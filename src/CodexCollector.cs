using System.Globalization;
using System.Text.Json;

/// <summary>
/// Codex rollout から、最初の実質的なユーザー依頼と最後の final_answer だけを残す。
/// commentary、ツール引数、ツール結果の本文は選択しない。
/// </summary>
internal static class CodexCollector
{
    internal static void Observe(ByteLog.LogLine line, CodexSelection selection)
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
            var timestamp = ReadTimestamp(root, "timestamp");
            if (timestamp is not null)
            {
                selection.Note(timestamp.Value);
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = StringProp(root, "type");
            if (type == "session_meta")
            {
                ReadSession(payload, selection);
                return;
            }

            if (type != "response_item" || StringProp(payload, "type") != "message")
            {
                return;
            }

            ReadMessage(payload, line, timestamp, selection);
        }
    }

    internal static string? SubstantiveUserText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        trimmed = StripBlock(trimmed, "environment_context").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (StartsWithBlock(trimmed, "user_instructions") ||
            StartsWithBlock(trimmed, "permissions") ||
            StartsWithBlock(trimmed, "sandbox_instructions") ||
            StartsWithBlock(trimmed, "skill") ||
            StartsWithBlock(trimmed, "skills") ||
            trimmed.StartsWith("<external_codex_apps", StringComparison.Ordinal) ||
            trimmed.StartsWith("# AGENTS.md", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    private static void ReadSession(JsonElement payload, CodexSelection selection)
    {
        var sessionId = StringProp(payload, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = StringProp(payload, "id");
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            selection.NativeId = sessionId;
        }

        var cwd = StringProp(payload, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            selection.Cwd = cwd;
        }

        if (payload.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object)
        {
            var repository = Normalize.RepositoryFromGitUrl(StringProp(git, "repository_url"));
            if (!string.IsNullOrWhiteSpace(repository))
            {
                selection.Repository = repository;
            }
        }
    }

    private static void ReadMessage(JsonElement payload, ByteLog.LogLine line, DateTimeOffset? timestamp, CodexSelection selection)
    {
        var role = StringProp(payload, "role");
        if (!payload.TryGetProperty("content", out var content))
        {
            return;
        }

        var text = Normalize.ReadText(content);
        if (role == "user")
        {
            if (selection.Request is not null)
            {
                return;
            }

            var substantive = SubstantiveUserText(text);
            if (substantive is null)
            {
                return;
            }

            selection.Request = new SelectedExcerpt(substantive, timestamp, line.Number, "response_item user message");
            return;
        }

        if (role != "assistant")
        {
            return;
        }

        var phase = StringProp(payload, "phase");
        if (phase.Equals("commentary", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (phase.Length > 0 && !phase.Equals("final_answer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var answer = text.Trim();
        if (answer.Length == 0)
        {
            return;
        }

        selection.Response = new SelectedExcerpt(answer, timestamp, line.Number, phase.Length == 0 ? "response_item assistant message" : "response_item final_answer");
    }

    private static string StripBlock(string text, string name)
    {
        var open = "<" + name + ">";
        var close = "</" + name + ">";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return text;
        }

        return text.Remove(start, end + close.Length - start);
    }

    private static bool StartsWithBlock(string text, string name) =>
        text.StartsWith("<" + name + ">", StringComparison.Ordinal) ||
        text.StartsWith("<" + name + " ", StringComparison.Ordinal);

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

internal sealed class CodexSelection
{
    public string? NativeId { get; set; }
    public string? Repository { get; set; }
    public string? Cwd { get; set; }
    public SelectedExcerpt? Request { get; set; }
    public SelectedExcerpt? Response { get; set; }
    public DateTimeOffset? FirstEvent { get; set; }
    public DateTimeOffset? LastEvent { get; set; }

    public void Note(DateTimeOffset timestamp)
    {
        if (FirstEvent is null || timestamp < FirstEvent)
        {
            FirstEvent = timestamp;
        }

        if (LastEvent is null || timestamp > LastEvent)
        {
            LastEvent = timestamp;
        }
    }
}
