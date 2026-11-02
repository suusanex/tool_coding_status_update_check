using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// 収集・台帳反映で共有するデータ。解析入力はスレッドの選択済み本文だけである。
/// </summary>
internal enum PullRequestState
{
    Open,
    Merged,
    ClosedUnmerged,
}

internal sealed record PullRequestSnapshot(
    string Url,
    string Owner,
    string Repository,
    int Number,
    PullRequestState State,
    DateTimeOffset? MergedAt,
    DateTimeOffset? ClosedAt,
    string Title);

internal interface IPullRequestSource
{
    Task<PullRequestSnapshot> GetAsync(string owner, string repository, int number, CancellationToken cancellationToken);
}

internal sealed record GitHubRef(string Url, string Owner, string Repository, int Number, string Kind);

/// <summary>
/// 旧解析が残した観測行。新しい解析はこれを LLM 入力にしない。
/// </summary>
internal sealed class ObservationDraft
{
    public required string Source { get; init; }
    public required string NativeId { get; init; }
    public required string FilePath { get; init; }
    public required long Ordinal { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public required string Kind { get; init; }
    public required string Body { get; init; }
    public required string DetailJson { get; init; }
    public required IReadOnlyList<GitHubRef> Urls { get; init; }
    public required IReadOnlyList<string> RunIds { get; init; }
}

internal sealed class StoredObservation
{
    public required string Id { get; init; }
    public required ObservationDraft Draft { get; init; }
}

/// <summary>
/// ログから機械的に選んだ、最初の依頼または最後の通常応答。
/// </summary>
internal sealed record SelectedExcerpt(string Text, DateTimeOffset? Timestamp, long Ordinal, string Basis);

/// <summary>
/// 1スレッドの台帳反映に使う選択結果。ツール本文は持たない。
/// </summary>
internal sealed class ThreadRecord
{
    public required string Source { get; init; }
    public required string NativeId { get; init; }
    public required string FilePath { get; init; }
    public string? Repository { get; init; }
    public string? Cwd { get; init; }
    public SelectedExcerpt? Request { get; init; }
    public SelectedExcerpt? Response { get; init; }
    public DateTimeOffset? FirstEventUtc { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }
    public string Fingerprint { get; init; } = "";
    public ThreadAssessment? Assessment { get; init; }
    public string? MatchedPath { get; init; }
    public string? MatchedWorkstream { get; init; }
    public string? ReconcileFingerprint { get; init; }

    public bool Overlaps(TimeWindow window)
    {
        if (LastEventUtc is null)
        {
            return false;
        }

        var first = FirstEventUtc ?? LastEventUtc.Value;
        return first < window.ToUtcExclusive && LastEventUtc.Value >= window.FromUtc;
    }

    public bool HasResponse => !string.IsNullOrWhiteSpace(Response?.Text);
}

/// <summary>
/// 選択済みの依頼と最後の応答から得た、台帳へ書く意味。
/// </summary>
internal sealed class ThreadAssessment
{
    public required string Summary { get; init; }
    public required string Purpose { get; init; }
    public required string CurrentPosition { get; init; }
    public required string ExplicitRemainingWork { get; init; }
    public required string NextAction { get; init; }
    public required string Uncertainty { get; init; }
    public required IReadOnlyList<string> SelectedPullUrls { get; init; }

    public string ToJson()
    {
        var pulls = new JsonArray();
        foreach (var url in SelectedPullUrls)
        {
            JsonLists.AddText(pulls, url);
        }

        return new JsonObject
        {
            ["summary"] = Summary,
            ["purpose"] = Purpose,
            ["currentPosition"] = CurrentPosition,
            ["explicitRemainingWork"] = ExplicitRemainingWork,
            ["nextAction"] = NextAction,
            ["uncertainty"] = Uncertainty,
            ["selectedPullUrls"] = pulls,
        }.ToJsonString();
    }

    public static ThreadAssessment FromJson(string json)
    {
        var obj = JsonNode.Parse(json) as JsonObject ?? throw new StatusCheckException("Assessment JSON is not an object.");
        return new ThreadAssessment
        {
            Summary = obj["summary"]?.GetValue<string>() ?? "",
            Purpose = obj["purpose"]?.GetValue<string>() ?? "",
            CurrentPosition = obj["currentPosition"]?.GetValue<string>() ?? "",
            ExplicitRemainingWork = obj["explicitRemainingWork"]?.GetValue<string>() ?? "",
            NextAction = obj["nextAction"]?.GetValue<string>() ?? "",
            Uncertainty = obj["uncertainty"]?.GetValue<string>() ?? "",
            SelectedPullUrls = ReadStrings(obj["selectedPullUrls"] as JsonArray),
        };
    }

    private static List<string> ReadStrings(JsonArray? array)
    {
        var values = new List<string>();
        if (array is null)
        {
            return values;
        }

        foreach (var node in array)
        {
            var text = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }
}

/// <summary>
/// 台帳の一つの workstream への判断。current、next、waiting は完成後の値。
/// null は変更しない。空文字はその欄を空にする。reportIds は判断に使った報告だけ。
/// </summary>
internal sealed record LedgerFieldUpdate(
    string Workstream,
    string Action,
    string? Current,
    string? Next,
    string? Waiting,
    string Reason,
    IReadOnlyList<string>? ReportIds = null);

internal interface IActivityModel
{
    Task<ThreadAssessment> AssessAsync(string system, string user, CancellationToken cancellationToken);

    Task<IReadOnlyList<LedgerFieldUpdate>> ReconcileAsync(string system, string user, CancellationToken cancellationToken);
}

internal static class JsonLists
{
    /// <summary>
    /// file-based app では JsonArray.Add(string) が実行時に失敗する。文字列は JsonNode として足す。
    /// </summary>
    internal static void AddText(JsonArray array, string value)
    {
        JsonNode node = JsonValue.Create(value)!;
        array.Add(node);
    }

    internal static void AddObject(JsonArray array, JsonObject value)
    {
        JsonNode node = value;
        array.Add(node);
    }
}

/// <summary>
/// 処理の失敗。Fatal が false のときは対象1件を飛ばして実行を続けられる。
/// 費用上限や前提の欠落など、続きの対象にも同じ失敗が起きるものは Fatal にする。
/// </summary>
internal sealed class StatusCheckException : Exception
{
    public StatusCheckException(string message, bool fatal = false) : base(message)
    {
        Fatal = fatal;
    }

    public StatusCheckException(string message, Exception inner, bool fatal = false) : base(message, inner)
    {
        Fatal = fatal;
    }

    public bool Fatal { get; }
}

/// <summary>
/// 改行まで読めたログ行が JSON ではない。実行は止めず、この行だけを飛ばす。
/// </summary>
internal sealed class InvalidLogLineException : Exception
{
    public InvalidLogLineException(string message) : base(message)
    {
    }
}

/// <summary>
/// 読めなかったログの位置。本文は残さない。
/// </summary>
internal sealed record IngestSkip(string Path, long? LineNumber, string Message)
{
    public string ToLog()
    {
        if (LineNumber is long line)
        {
            return "skipped log line " + line.ToString(CultureInfo.InvariantCulture) + " in " + Path + ": " + Message;
        }

        return "skipped log file " + Path + ": " + Message;
    }

    public string ToReport()
    {
        if (LineNumber is long line)
        {
            return Path + " の " + line.ToString(CultureInfo.InvariantCulture) + " 行目: " + Message;
        }

        return Path + ": " + Message;
    }
}

/// <summary>
/// ログ以外で、対象1件だけ失敗して飛ばした処理。実行は続ける。
/// </summary>
internal sealed record ProcessingSkip(string Subject, string Message)
{
    public string ToLog() => "skipped " + Subject + ": " + Message;

    public string ToReport() => Subject + ": " + Message;
}
