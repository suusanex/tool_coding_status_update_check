using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Facade の events.jsonl だけを読む。人間向け .log は収集しない。
/// session_bound は終端ではなく、外部 agent の session id として保存する。
/// </summary>
internal static class FacadeCollector
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "encrypted_content", "authorization", "apiKey", "api_key", "token", "secret", "password", "rawArguments",
    };

    internal static ObservationDraft? ParseLine(string path, ByteLog.LogLine line, ParseCarry carry)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.Text);
        }
        catch (JsonException ex)
        {
            throw new StatusCheckException($"Facade log line {line.Number} in {path} is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var runId = StringProp(root, "runId");
            if (string.IsNullOrWhiteSpace(runId))
            {
                runId = Path.GetFileName(path).Replace(".events.jsonl", "", StringComparison.Ordinal);
            }

            carry.NativeId = runId;
            var timestamp = ReadTimestamp(root, "ts");
            if (timestamp is not null && (carry.LastEvent is null || timestamp > carry.LastEvent))
            {
                carry.LastEvent = timestamp;
            }

            var source = StringProp(root, "source");
            var type = StringProp(root, "type");
            if (!root.TryGetProperty("data", out var data))
            {
                data = default;
            }

            if (source == "facade" && type is "completed" or "failed" or "cancelled")
            {
                carry.Terminal = true;
            }

            if (source == "facade" && type == "started" && data.ValueKind == JsonValueKind.Object)
            {
                var cwd = StringProp(data, "workingDirectory");
                var prompt = StringProp(data, "prompt");
                var agent = StringProp(data, "agent");
                carry.Cwd = cwd;
                var text = "agent=" + agent + "\ncwd=" + cwd + "\nprompt=" + prompt;
                var detail = new JsonObject { ["agent"] = agent, ["cwd"] = cwd, ["hints"] = HintArray(text) };
                return Draft(path, line, runId, timestamp, "started", text, detail);
            }

            if (source == "facade" && type == "session_bound" && data.ValueKind == JsonValueKind.Object)
            {
                var sessionId = StringProp(data, "sessionId");
                var bindingSource = StringProp(data, "bindingSource");
                var text = "external_session=" + sessionId + "\nbinding_source=" + bindingSource;
                var detail = new JsonObject { ["externalSessionId"] = sessionId, ["bindingSource"] = bindingSource };
                return Draft(path, line, runId, timestamp, "session_bound", text, detail);
            }

            if (source == "facade" && type is "completed" or "failed" or "cancelled")
            {
                var output = data.ValueKind == JsonValueKind.Object ? StringProp(data, "outputText") : "";
                var reason = data.ValueKind == JsonValueKind.Object ? StringProp(data, "completionReason") : "";
                var text = "terminal=" + type + "\nreason=" + reason + "\noutput=" + output;
                var detail = new JsonObject { ["terminal"] = type, ["reason"] = reason, ["hints"] = HintArray(text) };
                return Draft(path, line, runId, timestamp, type, text, detail);
            }

            if (source == "agent" && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String)
            {
                var builder = new StringBuilder();
                Walk(data, "", builder, Normalize.StoredBodyLimit);
                var text = builder.ToString();
                if (!ContainsSignal(text))
                {
                    return null;
                }

                var detail = new JsonObject { ["eventType"] = type, ["hints"] = HintArray(text) };
                if (builder.Length >= Normalize.StoredBodyLimit)
                {
                    detail["omittedTail"] = true;
                }

                return Draft(path, line, runId, timestamp, "agent_excerpt", text, detail);
            }

            return null;
        }
    }

    private static bool ContainsSignal(string text)
    {
        if (text.Contains("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.Contains("残作業", StringComparison.Ordinal) ||
            text.Contains("残件", StringComparison.Ordinal) ||
            text.Contains("マージ後", StringComparison.Ordinal) ||
            text.Contains("実装完了", StringComparison.Ordinal) ||
            text.Contains("実装を開始", StringComparison.Ordinal);
    }

    private static void Walk(JsonElement element, string key, StringBuilder builder, int budget)
    {
        if (builder.Length >= budget || SensitiveKeys.Contains(key))
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.AppendLine(element.GetString());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, property.Name, builder, budget);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, key, builder, budget);
                }

                break;
        }
    }

    private static ObservationDraft Draft(string path, ByteLog.LogLine line, string runId, DateTimeOffset? timestamp, string kind, string text, JsonObject detail)
    {
        var scan = text.Length > Normalize.StoredBodyLimit ? text[..Normalize.StoredBodyLimit] : text;
        var stored = Normalize.StoreBody(scan, out var omittedTail);
        if (omittedTail || detail["omittedTail"]?.GetValue<bool>() == true)
        {
            detail["omittedTail"] = true;
        }

        return new ObservationDraft
        {
            Source = "facade",
            NativeId = runId,
            FilePath = path,
            Ordinal = line.Number,
            Timestamp = timestamp,
            Kind = kind,
            Body = stored,
            DetailJson = detail.ToJsonString(),
            Urls = Normalize.GitHubRefs(scan),
            RunIds = Normalize.RunIds(scan + "\n" + runId),
        };
    }

    private static JsonArray HintArray(string text)
    {
        var array = new JsonArray();
        foreach (var hint in Normalize.ResidualHints(text))
        {
            JsonLists.AddText(array, hint);
        }

        return array;
    }

    private static string StringProp(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        var text = StringProp(element, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }
}
