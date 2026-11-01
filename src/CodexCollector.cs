using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Codex rollout JSONL から、session_meta と会話・tool の対応だけを抜く。
/// reasoning の encrypted_content、account id、base_instructions は残さない。
/// </summary>
internal static class CodexCollector
{
    internal static ObservationDraft? ParseLine(string path, ByteLog.LogLine line, ParseCarry carry)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.Text);
        }
        catch (JsonException ex)
        {
            throw new InvalidLogLineException(ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            var type = StringProp(root, "type");
            var timestamp = ReadTimestamp(root, "timestamp");
            if (timestamp is not null && (carry.LastEvent is null || timestamp > carry.LastEvent))
            {
                carry.LastEvent = timestamp;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (type == "session_meta")
            {
                return ParseSession(path, line, payload, timestamp, carry);
            }

            var nativeId = carry.NativeId ?? Path.GetFileNameWithoutExtension(path);
            if (type == "response_item")
            {
                return ParseResponse(path, line, payload, timestamp, nativeId, carry);
            }

            if (type == "event_msg" && StringProp(payload, "type") == "task_started")
            {
                var turnId = StringProp(payload, "turn_id");
                return Draft(path, line, "codex", nativeId, timestamp, "task_started", "task_started turn_id=" + turnId, new JsonObject { ["turnId"] = turnId }, carry);
            }

            return null;
        }
    }

    private static ObservationDraft ParseSession(string path, ByteLog.LogLine line, JsonElement payload, DateTimeOffset? timestamp, ParseCarry carry)
    {
        var sessionId = StringProp(payload, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = StringProp(payload, "id");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Path.GetFileNameWithoutExtension(path);
        }

        var cwd = StringProp(payload, "cwd");
        string? repository = null;
        string? branch = null;
        if (payload.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object)
        {
            repository = Normalize.RepositoryFromGitUrl(StringProp(git, "repository_url"));
            branch = StringProp(git, "branch");
        }

        carry.NativeId = sessionId;
        carry.Cwd = cwd;
        carry.Repository = repository;
        var body = "cwd=" + cwd + "\nrepository=" + repository + "\nbranch=" + branch;
        var detail = new JsonObject
        {
            ["cwd"] = cwd,
            ["repository"] = repository,
            ["branch"] = branch,
        };
        return Draft(path, line, "codex", sessionId, timestamp, "session_meta", body, detail, carry);
    }

    private static ObservationDraft? ParseResponse(string path, ByteLog.LogLine line, JsonElement payload, DateTimeOffset? timestamp, string nativeId, ParseCarry carry)
    {
        var payloadType = StringProp(payload, "type");
        if (payloadType == "message")
        {
            var role = StringProp(payload, "role");
            if (role is not ("user" or "assistant"))
            {
                return null;
            }

            if (!payload.TryGetProperty("content", out var content))
            {
                return null;
            }

            var text = Normalize.ReadText(content);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var hints = Normalize.ResidualHints(text);
            var detail = new JsonObject { ["role"] = role, ["hints"] = ToArray(hints) };
            return Draft(path, line, "codex", nativeId, timestamp, role + "_message", text, detail, carry, text);
        }

        if (payloadType == "function_call")
        {
            var name = StringProp(payload, "name");
            var callId = StringProp(payload, "call_id");
            var arguments = payload.TryGetProperty("arguments", out var args) ? ElementText(args) : "";
            var text = "tool " + name + " call_id=" + callId + "\n" + arguments;
            var detail = new JsonObject { ["name"] = name, ["callId"] = callId, ["hints"] = ToArray(Normalize.ResidualHints(text)) };
            return Draft(path, line, "codex", nativeId, timestamp, "function_call", text, detail, carry, text);
        }

        if (payloadType == "function_call_output")
        {
            var callId = StringProp(payload, "call_id");
            var output = payload.TryGetProperty("output", out var outputElement) ? ElementText(outputElement) : "";
            var text = "tool_result call_id=" + callId + "\n" + output;
            var detail = new JsonObject { ["callId"] = callId, ["hints"] = ToArray(Normalize.ResidualHints(text)) };
            return Draft(path, line, "codex", nativeId, timestamp, "function_call_output", text, detail, carry, text);
        }

        return null;
    }

    private static ObservationDraft Draft(string path, ByteLog.LogLine line, string source, string nativeId, DateTimeOffset? timestamp, string kind, string body, JsonObject detail, ParseCarry carry, string? urlSource = null)
    {
        var sourceText = urlSource ?? body;
        if (sourceText.Length > Normalize.StoredBodyLimit)
        {
            sourceText = sourceText[..Normalize.StoredBodyLimit];
        }

        var stored = Normalize.StoreBody(body, out var omittedTail);
        if (omittedTail)
        {
            detail["omittedTail"] = true;
        }

        detail["hints"] ??= ToArray(Normalize.ResidualHints(sourceText));
        return new ObservationDraft
        {
            Source = source,
            NativeId = nativeId,
            FilePath = path,
            Ordinal = line.Number,
            Timestamp = timestamp,
            Kind = kind,
            Body = stored,
            DetailJson = detail.ToJsonString(),
            Urls = Normalize.GitHubRefs(sourceText),
            RunIds = Normalize.RunIds(sourceText),
        };
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            JsonLists.AddText(array, value);
        }

        return array;
    }

    private static string ElementText(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();

    private static string StringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

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

internal sealed class ParseCarry
{
    public string? NativeId { get; set; }
    public string? Repository { get; set; }
    public string? Cwd { get; set; }
    public DateTimeOffset? LastEvent { get; set; }
    public bool Terminal { get; set; }
}
