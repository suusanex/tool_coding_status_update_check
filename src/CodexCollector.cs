using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>
/// Codex rollout から、会話ターン（最初の実質的依頼・中間ターン・最後の通常応答・未応答の末尾依頼）、
/// GitHub PR/Issue 参照、および tool output の短い構造化抜粋（SignalExcerpt）を収集する。
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

            if (type != "response_item")
            {
                return;
            }

            var payloadType = StringProp(payload, "type");
            if (payloadType == "message")
            {
                ReadMessage(payload, line, timestamp, selection);
                return;
            }

            if (payloadType is "function_call" or "custom_tool_call")
            {
                ReadToolCall(payload, selection);
                return;
            }

            if (payloadType is "function_call_output" or "custom_tool_call_output")
            {
                ReadToolOutput(payload, line, timestamp, selection);
            }
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
            var substantive = SubstantiveUserText(text);
            if (substantive is null)
            {
                return;
            }

            if (selection.Request is null)
            {
                selection.Request = new SelectedExcerpt(substantive, timestamp, line.Number, "response_item user message");
            }

            selection.AddTurn("user", substantive, timestamp, line.Number, "response_item user message");
            selection.AddRefs(substantive, "user", line.Number);
            return;
        }

        if (role != "assistant")
        {
            return;
        }

        var phase = StringProp(payload, "phase");
        if (phase.Equals("commentary", StringComparison.OrdinalIgnoreCase))
        {
            selection.AddRefs(text, "commentary", line.Number);
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

        var basis = phase.Length == 0 ? "response_item assistant message" : "response_item final_answer";
        selection.Response = new SelectedExcerpt(answer, timestamp, line.Number, basis);
        selection.AddTurn("assistant", answer, timestamp, line.Number, basis);
        selection.AddRefs(answer, "assistant", line.Number);
    }

    private static void ReadToolCall(JsonElement payload, CodexSelection selection)
    {
        var callId = StringProp(payload, "call_id");
        var name = StringProp(payload, "name");
        if (!string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(name))
        {
            selection.RecordToolCall(callId, name);
        }
    }

    private static void ReadToolOutput(JsonElement payload, ByteLog.LogLine line, DateTimeOffset? timestamp, CodexSelection selection)
    {
        if (!payload.TryGetProperty("output", out var outputNode))
        {
            return;
        }

        var output = Normalize.ReadText(outputNode).Trim();
        if (output.Length == 0 || output.Contains("tool_call_update", StringComparison.Ordinal))
        {
            return;
        }

        var callId = StringProp(payload, "call_id");
        var toolName = selection.ResolveToolName(callId);
        if (string.Equals(toolName, "start_agent", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var nonCodeLines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !IsCodeOrFixtureLine(l));
        selection.AddRefs(string.Join("\n", nonCodeLines), "tool", line.Number);
        var signalText = ExtractActionableSignal(output);
        if (signalText is not null)
        {
            selection.AddSignal("tool_output", toolName, signalText, timestamp, line.Number);
        }
    }

    /// <summary>
    /// tool output から、状態・完了・残作業・阻害要因の根拠候補となる短い結果行だけを抽出する。
    /// ファイル全文やソースコード断片は除外する。
    /// </summary>
    internal static string? ExtractActionableSignal(string output)
    {
        if (output.Length > 8000 ||
            output.Contains("## 進行中のworkstream", StringComparison.Ordinal) ||
            output.Contains("internal static ", StringComparison.Ordinal) ||
            output.Contains("public sealed ", StringComparison.Ordinal))
        {
            return null;
        }

        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var relevant = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 ||
                line.StartsWith("Chunk ID:", StringComparison.Ordinal) ||
                line.StartsWith("Wall time:", StringComparison.Ordinal) ||
                IsCodeOrFixtureLine(line))
            {
                continue;
            }

            if (IsSignalLine(line))
            {
                relevant.Add(line);
                if (relevant.Count >= 4)
                {
                    break;
                }
            }
        }

        if (relevant.Count == 0)
        {
            return null;
        }

        return string.Join(" / ", relevant);
    }

    private static bool IsCodeOrFixtureLine(string line) =>
        line.Contains("Assert.", StringComparison.Ordinal) ||
        line.Contains("Check(", StringComparison.Ordinal) ||
        line.Contains("\"https://", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("'https://", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("//", StringComparison.Ordinal);

    private static bool IsSignalLine(string line) =>
        line.Contains("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("残作業", StringComparison.Ordinal) ||
        line.Contains("未対応", StringComparison.Ordinal) ||
        line.Contains("完了", StringComparison.Ordinal) ||
        line.Contains("マージ", StringComparison.Ordinal) ||
        line.Contains("成功", StringComparison.Ordinal) ||
        line.Contains("失敗", StringComparison.Ordinal) ||
        line.Contains("エラー", StringComparison.Ordinal) ||
        line.Contains("合格", StringComparison.Ordinal) ||
        line.Contains("Merged", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Pull request", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Passed!", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Failed!", StringComparison.OrdinalIgnoreCase) ||
        line.Contains(" passed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains(" failed", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("passed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("GraphQL:", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("requires approval", StringComparison.OrdinalIgnoreCase);

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
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private int _userCount;
    private int _assistantCount;

    public string? NativeId { get; set; }
    public string? Repository { get; set; }
    public string? Cwd { get; set; }
    public SelectedExcerpt? Request { get; set; }
    public SelectedExcerpt? Response { get; set; }
    public List<ThreadTurn> Turns { get; } = [];
    public List<SignalExcerpt> Signals { get; } = [];
    public List<ExtractedRef> ExtractedRefs { get; } = [];
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

    public void AddTurn(string role, string text, DateTimeOffset? timestamp, long ordinal, string basis)
    {
        if (role == "user")
        {
            _userCount++;
            Turns.Add(new ThreadTurn("u" + _userCount.ToString(CultureInfo.InvariantCulture), role, text, timestamp, ordinal, basis));
        }
        else
        {
            _assistantCount++;
            Turns.Add(new ThreadTurn("a" + _assistantCount.ToString(CultureInfo.InvariantCulture), role, text, timestamp, ordinal, basis));
        }
    }

    public void SeedTurns(IReadOnlyList<ThreadTurn> storedTurns)
    {
        Turns.Clear();
        _userCount = 0;
        _assistantCount = 0;
        foreach (var turn in storedTurns)
        {
            if (turn.Role == "user")
            {
                _userCount++;
            }
            else if (turn.Role == "assistant")
            {
                _assistantCount++;
            }

            Turns.Add(turn);
        }
    }

    public void RecordToolCall(string callId, string toolName)
    {
        _toolNames[callId] = toolName;
    }

    public string ResolveToolName(string callId) =>
        !string.IsNullOrWhiteSpace(callId) && _toolNames.TryGetValue(callId, out var name) ? name : "tool_output";

    public void AddRefs(string text, string sourceKind, long ordinal)
    {
        foreach (var item in Normalize.GitHubRefs(text))
        {
            if (ExtractedRefs.Any(existing => existing.Ref.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase) && existing.SourceKind == sourceKind))
            {
                continue;
            }

            ExtractedRefs.Add(new ExtractedRef(item, sourceKind, ordinal));
        }

        if (sourceKind is "user" or "assistant")
        {
            foreach (var pull in Normalize.PullCandidates(text, Repository, explicitOnly: true))
            {
                if (ExtractedRefs.Any(existing => existing.Ref.Url.Equals(pull.Url, StringComparison.OrdinalIgnoreCase) && existing.SourceKind == sourceKind))
                {
                    continue;
                }

                ExtractedRefs.Add(new ExtractedRef(pull, sourceKind, ordinal));
            }

            foreach (var issue in Normalize.IssueCandidates(text, Repository))
            {
                if (ExtractedRefs.Any(existing => existing.Ref.Url.Equals(issue.Url, StringComparison.OrdinalIgnoreCase) && existing.SourceKind == sourceKind))
                {
                    continue;
                }

                ExtractedRefs.Add(new ExtractedRef(issue, sourceKind, ordinal));
            }
        }
    }

    public void AddSignal(string sourceKind, string toolName, string rawText, DateTimeOffset? timestamp, long ordinal)
    {
        var text = Normalize.TruncateWithFlag(rawText, Normalize.SignalExcerptLimit, out var truncated);
        if (text.Length == 0)
        {
            return;
        }

        if (Signals.Any(existing => existing.Text.Equals(text, StringComparison.Ordinal)))
        {
            return;
        }

        if (Signals.Count >= Normalize.MaxSignalsPerThread)
        {
            Signals.RemoveAt(0);
        }

        var nextIndex = Signals.Count + 1;
        Signals.Add(new SignalExcerpt(
            "sig" + nextIndex.ToString(CultureInfo.InvariantCulture),
            sourceKind,
            toolName,
            text,
            timestamp,
            ordinal,
            truncated));

        for (var i = 0; i < Signals.Count; i++)
        {
            var expectedId = "sig" + (i + 1).ToString(CultureInfo.InvariantCulture);
            if (Signals[i].SignalId != expectedId)
            {
                Signals[i] = Signals[i] with { SignalId = expectedId };
            }
        }
    }
}
