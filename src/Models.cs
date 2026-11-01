using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// 収集・分析・台帳反映で共有するデータ。JSONの入出力は呼び出し側がDOMで行う。
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
/// 活動の根拠は observation 全体ではなく、保存済み本文上の引用区間である。
/// </summary>
internal sealed record EvidenceSpan(string ObservationId, string Quote, int Start, int Length);

internal sealed record EvidenceQuote(string ObservationId, string Quote);

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

internal sealed class ActivityPacket
{
    public required string PacketId { get; init; }
    public required string NativeId { get; init; }
    public required string Source { get; init; }
    public string? Repository { get; init; }
    public string? Cwd { get; init; }
    public string? ExistingSummary { get; set; }
    public required IReadOnlyList<StoredObservation> Observations { get; init; }
    public required IReadOnlyList<string> LinkedRunIds { get; init; }
}

internal sealed class ExtractedActivity
{
    public required IReadOnlyList<string> ObservationIds { get; init; }
    public required bool IndependentIssue { get; init; }
    public required string Summary { get; init; }
    public required string Purpose { get; init; }
    public required IReadOnlyList<string> Stages { get; init; }
    public required string PeriodChange { get; init; }
    public required string CurrentPosition { get; init; }
    public required string ExplicitRemainingWork { get; init; }
    public required string Uncertainty { get; init; }
    public required double Confidence { get; init; }
    public required string NextAction { get; init; }
    public required bool UncertainMerge { get; init; }
    public IReadOnlyList<EvidenceQuote> EvidenceQuotes { get; init; } = [];
}

internal sealed record WorkstreamChoice(string? Heading, bool Uncertain);

internal sealed record WorkstreamChoiceRequest(string Repository, string Summary, IReadOnlyList<string> Headings);

internal sealed record ConcentrationItem(string ActivityId, string Title, string Reason);

internal interface IActivityModel
{
    Task<IReadOnlyList<ExtractedActivity>> ExtractAsync(ActivityPacket packet, CancellationToken cancellationToken);

    Task<WorkstreamChoice> ChooseWorkstreamAsync(WorkstreamChoiceRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConcentrationItem>> ConcentrateAsync(IReadOnlyList<ActivityView> activities, CancellationToken cancellationToken);
}

internal sealed class ActivityView
{
    public required string Id { get; init; }
    public required string Summary { get; init; }
    public required string Purpose { get; init; }
    public required string Repository { get; init; }
    public required IReadOnlyList<string> Stages { get; init; }
    public required string PeriodChange { get; init; }
    public required string CurrentPosition { get; init; }
    public required string Kind { get; init; }
    public required bool InWindow { get; init; }
    public required IReadOnlyList<string> Evidence { get; init; }
    public required string Uncertainty { get; init; }
    public required IReadOnlyList<PullRequestSnapshot> PullRequests { get; init; }
    public required IReadOnlyList<GitHubRef> IssueRefs { get; init; }
    public string? ExplicitRemainingWork { get; init; }
    public string? DiscoveredFrom { get; init; }
    public required string NextAction { get; init; }
    public double Confidence { get; init; }
    public string? Cwd { get; init; }
    public required IReadOnlyList<string> ObservationIds { get; init; }
    public string? NativeId { get; init; }
    public bool CoverageComplete { get; init; }

    public string ToJson()
    {
        var pulls = new JsonArray();
        foreach (var pull in PullRequests)
        {
            JsonLists.AddObject(pulls, new JsonObject
            {
                ["url"] = pull.Url,
                ["owner"] = pull.Owner,
                ["repository"] = pull.Repository,
                ["number"] = pull.Number,
                ["state"] = pull.State.ToString(),
                ["mergedAt"] = pull.MergedAt?.ToString("O"),
                ["closedAt"] = pull.ClosedAt?.ToString("O"),
                ["title"] = pull.Title,
            });
        }

        var issues = new JsonArray();
        foreach (var issue in IssueRefs)
        {
            JsonLists.AddObject(issues, new JsonObject
            {
                ["url"] = issue.Url,
                ["owner"] = issue.Owner,
                ["repository"] = issue.Repository,
                ["number"] = issue.Number,
                ["kind"] = issue.Kind,
            });
        }

        var stages = new JsonArray();
        foreach (var stage in Stages)
        {
            JsonLists.AddText(stages, stage);
        }

        var evidence = new JsonArray();
        foreach (var item in Evidence)
        {
            JsonLists.AddText(evidence, item);
        }

        var observationIds = new JsonArray();
        foreach (var id in ObservationIds)
        {
            JsonLists.AddText(observationIds, id);
        }

        var obj = new JsonObject
        {
            ["id"] = Id,
            ["summary"] = Summary,
            ["purpose"] = Purpose,
            ["repository"] = Repository,
            ["stages"] = stages,
            ["periodChange"] = PeriodChange,
            ["currentPosition"] = CurrentPosition,
            ["kind"] = Kind,
            ["uncertainty"] = Uncertainty,
            ["explicitRemainingWork"] = ExplicitRemainingWork ?? "",
            ["discoveredFrom"] = DiscoveredFrom ?? "",
            ["nextAction"] = NextAction,
            ["confidence"] = Confidence,
            ["cwd"] = Cwd ?? "",
            ["nativeId"] = NativeId ?? "",
            ["coverageComplete"] = CoverageComplete,
            ["pulls"] = pulls,
            ["issues"] = issues,
            ["evidence"] = evidence,
            ["observationIds"] = observationIds,
        };
        return obj.ToJsonString();
    }

    public static ActivityView FromJson(string json, bool inWindow)
    {
        var obj = JsonNode.Parse(json) as JsonObject ?? throw new StatusCheckException("Activity JSON is not an object.");
        var pulls = new List<PullRequestSnapshot>();
        if (obj["pulls"] is JsonArray pullArray)
        {
            foreach (var node in pullArray)
            {
                if (node is not JsonObject pull)
                {
                    continue;
                }

                pulls.Add(new PullRequestSnapshot(
                    RequiredString(pull, "url"),
                    RequiredString(pull, "owner"),
                    RequiredString(pull, "repository"),
                    pull["number"]?.GetValue<int>() ?? 0,
                    Enum.Parse<PullRequestState>(RequiredString(pull, "state")),
                    ParseTime(pull["mergedAt"]?.GetValue<string>()),
                    ParseTime(pull["closedAt"]?.GetValue<string>()),
                    pull["title"]?.GetValue<string>() ?? ""));
            }
        }

        var issues = new List<GitHubRef>();
        if (obj["issues"] is JsonArray issueArray)
        {
            foreach (var node in issueArray)
            {
                if (node is not JsonObject issue)
                {
                    continue;
                }

                issues.Add(new GitHubRef(
                    RequiredString(issue, "url"),
                    RequiredString(issue, "owner"),
                    RequiredString(issue, "repository"),
                    issue["number"]?.GetValue<int>() ?? 0,
                    issue["kind"]?.GetValue<string>() ?? "issue"));
            }
        }

        return new ActivityView
        {
            Id = RequiredString(obj, "id"),
            Summary = RequiredString(obj, "summary"),
            Purpose = obj["purpose"]?.GetValue<string>() ?? "",
            Repository = obj["repository"]?.GetValue<string>() ?? "",
            Stages = ReadStrings(obj["stages"] as JsonArray),
            PeriodChange = obj["periodChange"]?.GetValue<string>() ?? "",
            CurrentPosition = obj["currentPosition"]?.GetValue<string>() ?? "",
            Kind = obj["kind"]?.GetValue<string>() ?? "normal",
            InWindow = inWindow,
            Evidence = ReadStrings(obj["evidence"] as JsonArray),
            Uncertainty = obj["uncertainty"]?.GetValue<string>() ?? "",
            PullRequests = pulls,
            IssueRefs = issues,
            ExplicitRemainingWork = EmptyToNull(obj["explicitRemainingWork"]?.GetValue<string>()),
            DiscoveredFrom = EmptyToNull(obj["discoveredFrom"]?.GetValue<string>()),
            NextAction = obj["nextAction"]?.GetValue<string>() ?? "",
            Confidence = obj["confidence"]?.GetValue<double>() ?? 0,
            Cwd = EmptyToNull(obj["cwd"]?.GetValue<string>()),
            ObservationIds = ReadStrings(obj["observationIds"] as JsonArray),
            NativeId = EmptyToNull(obj["nativeId"]?.GetValue<string>()),
            CoverageComplete = obj["coverageComplete"]?.GetValue<bool>() ?? false,
        };
    }

    internal ActivityView ReplacePulls(IReadOnlyList<PullRequestSnapshot> pulls, IReadOnlyList<string> stages, bool inWindow) => new()
    {
        Id = Id,
        Summary = Summary,
        Purpose = Purpose,
        Repository = Repository,
        Stages = stages,
        PeriodChange = PeriodChange,
        CurrentPosition = CurrentPosition,
        Kind = Kind,
        InWindow = inWindow,
        Evidence = Evidence,
        Uncertainty = Uncertainty,
        PullRequests = pulls,
        IssueRefs = IssueRefs,
        ExplicitRemainingWork = ExplicitRemainingWork,
        DiscoveredFrom = DiscoveredFrom,
        NextAction = NextAction,
        Confidence = Confidence,
        Cwd = Cwd,
        ObservationIds = ObservationIds,
        NativeId = NativeId,
        CoverageComplete = CoverageComplete,
    };

    private static string RequiredString(JsonObject obj, string name)
    {
        var value = obj[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new StatusCheckException($"Activity JSON is missing {name}.");
        }

        return value;
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

    private static DateTimeOffset? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static string? EmptyToNull(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
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

internal sealed class StatusCheckException : Exception
{
    public StatusCheckException(string message) : base(message)
    {
    }

    public StatusCheckException(string message, Exception inner) : base(message, inner)
    {
    }
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

internal static class StageNames
{
    internal const string Consultation = "consultation";
    internal const string PlanOnly = "plan_only";
    internal const string ImplementationStarted = "implementation_started";
    internal const string ImplementationReportedComplete = "implementation_reported_complete";
    internal const string PrCreated = "pr_created";
    internal const string Deferred = "deferred";
    internal const string Blocked = "blocked";
    internal const string NewIssue = "new_issue";
    internal const string ManagementReturnUnconfirmed = "management_return_unconfirmed";
    internal const string PrOpen = "pr_open";
    internal const string PrMerged = "pr_merged";
    internal const string PrClosedUnmerged = "pr_closed_unmerged";

    internal static readonly HashSet<string> Reported =
    [
        Consultation,
        PlanOnly,
        ImplementationStarted,
        ImplementationReportedComplete,
        PrCreated,
        Deferred,
        Blocked,
        NewIssue,
        ManagementReturnUnconfirmed,
    ];

    internal static string Label(string stage) => stage switch
    {
        Consultation => "相談・調査のみ",
        PlanOnly => "計画のみ",
        ImplementationStarted => "実装開始",
        ImplementationReportedComplete => "実装完了の報告",
        PrCreated => "PR作成",
        PrOpen => "未マージ",
        PrMerged => "マージ済み",
        PrClosedUnmerged => "未マージのClose",
        Deferred => "明示的な後回し",
        Blocked => "blocked",
        NewIssue => "新規課題",
        ManagementReturnUnconfirmed => "管理側返却は未確認",
        _ => stage,
    };
}
