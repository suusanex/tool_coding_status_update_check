using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// 入力方式・抽出・対応付け改善の段階切り替え。
/// A: 現行基準（最初の依頼 + 最後の通常応答、既存のURL抽出・単一repo対応付け）
/// A+URL: A + PR/Issue区別とtool出力からのURL抽出、未選択URLの自動確定抑止
/// A+Match: A+URL + Projectカード文脈（スレッド参照・Issue URL・見出し・キーワード・進行中workstream）による対応付けと保留理由分離
/// B: A+Match + 会話の時系列ターン保持、中間ターン方向修正、残作業出典検証・明示解消追跡、未応答ユーザー発話による削除抑止
/// C: B + tool output / commentary にのみ存在する非URL根拠の短抜粋 SignalExcerpt
/// </summary>
internal static class InputModes
{
    public const string A = "A";
    public const string AUrl = "A+URL";
    public const string AMatch = "A+Match";
    public const string B = "B";
    public const string C = "C";
    public const string Default = C;

    public static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return Default;
        }

        return mode.Trim().ToUpperInvariant() switch
        {
            "A" => A,
            "A+URL" or "A_URL" => AUrl,
            "A+MATCH" or "A_MATCH" or "A_URL_MATCH" => AMatch,
            "B" or "B_URL_MATCH" => B,
            "C" or "B+C" or "B_URL_MATCH_SIGNAL" => C,
            _ => throw new StatusCheckException("Unsupported inputMode: " + mode),
        };
    }

    public static bool UseImprovedUrlExtraction(string? mode) =>
        NormalizeMode(mode) != A;

    public static bool UseImprovedCardMatching(string? mode) =>
        NormalizeMode(mode) is AMatch or B or C;

    public static bool UseChronologicalTurns(string? mode) =>
        NormalizeMode(mode) is B or C;

    public static bool UseSignalExcerpts(string? mode) =>
        NormalizeMode(mode) == C;
}

/// <summary>
/// 収集・台帳反映で共有するデータ。
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
/// ログ内のどこから抽出された GitHub 参照（pull / issue）かを保持する。
/// </summary>
internal sealed record ExtractedRef(GitHubRef Ref, string SourceKind, long Ordinal);

/// <summary>
/// 時系列順の実質的ユーザー発話または通常 assistant 応答。
/// </summary>
internal sealed record ThreadTurn(
    string TurnId,
    string Role,
    string Text,
    DateTimeOffset? Timestamp,
    long Ordinal,
    string Basis,
    bool Truncated = false);

/// <summary>
/// tool output や commentary にのみ存在する作業結果・残作業・解消結果・エラー等の短い構造化抜粋（根拠候補）。
/// </summary>
internal sealed record SignalExcerpt(
    string SignalId,
    string SourceKind,
    string ToolName,
    string Text,
    DateTimeOffset? Timestamp,
    long Ordinal,
    bool Truncated = false);

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
internal sealed record SelectedExcerpt(string Text, DateTimeOffset? Timestamp, long Ordinal, string Basis, bool Truncated = false);

/// <summary>
/// 1スレッドの台帳反映に使う選択結果。
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
    public IReadOnlyList<ThreadTurn> Turns { get; init; } = [];
    public IReadOnlyList<SignalExcerpt> Signals { get; init; } = [];
    public IReadOnlyList<ExtractedRef> ExtractedRefs { get; init; } = [];
    public bool Truncated { get; init; }
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

    public bool HasTrailingUnansweredUserTurn =>
        Turns.Count > 0 && string.Equals(Turns[^1].Role, "user", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 選択済みの入力から得た、台帳へ書く意味。
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
    public string RemainingWorkSourceId { get; init; } = "";
    public bool RemainingWorkQuoteVerified { get; init; } = true;
    public bool RemainingWorkUnverified { get; init; }

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
            ["remainingWorkSourceId"] = RemainingWorkSourceId,
            ["remainingWorkQuoteVerified"] = RemainingWorkQuoteVerified,
            ["remainingWorkUnverified"] = RemainingWorkUnverified,
        }.ToJsonString();
    }

    public static ThreadAssessment FromJson(string json)
    {
        var obj = JsonNode.Parse(json) as JsonObject ?? throw new StatusCheckException("Assessment JSON is not an object.");
        var remaining = obj["explicitRemainingWork"]?.GetValue<string>() ?? "";
        return new ThreadAssessment
        {
            Summary = obj["summary"]?.GetValue<string>() ?? "",
            Purpose = obj["purpose"]?.GetValue<string>() ?? "",
            CurrentPosition = obj["currentPosition"]?.GetValue<string>() ?? "",
            ExplicitRemainingWork = remaining,
            NextAction = obj["nextAction"]?.GetValue<string>() ?? "",
            Uncertainty = obj["uncertainty"]?.GetValue<string>() ?? "",
            SelectedPullUrls = ReadStrings(obj["selectedPullUrls"] as JsonArray),
            RemainingWorkSourceId = obj["remainingWorkSourceId"]?.GetValue<string>() ?? "",
            RemainingWorkQuoteVerified = obj["remainingWorkQuoteVerified"]?.GetValue<bool>() ?? true,
            RemainingWorkUnverified = obj["remainingWorkUnverified"]?.GetValue<bool>() ?? false,
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

/// <summary>
/// 選択前除外（期間外・通常応答なし・自己実行・ログ読取エラー）の診断記録。
/// </summary>
internal sealed record PreSelectionDiagnostic(
    string Subject,
    string FilePath,
    string Stage,
    string ReasonCode,
    string ReasonDetail,
    DateTimeOffset? FirstEventUtc = null,
    DateTimeOffset? LastEventUtc = null);

/// <summary>
/// 選択されたスレッドごとの実入力・判断結果・対応付け診断記録。
/// </summary>
internal sealed class ThreadDiagnostic
{
    public required string ActivityId { get; init; }
    public required string FilePath { get; init; }
    public string? Repository { get; init; }
    public string? Cwd { get; init; }
    public required string Stage { get; set; }
    public required string ReasonCode { get; set; }
    public required string ReasonDetail { get; set; }
    public bool Truncated { get; init; }
    public bool HasTrailingUnansweredUserTurn { get; init; }
    public bool AssessCacheHit { get; set; }
    public SelectedExcerpt? Request { get; init; }
    public SelectedExcerpt? Response { get; init; }
    public IReadOnlyList<ThreadTurn> Turns { get; init; } = [];
    public IReadOnlyList<SignalExcerpt> Signals { get; init; } = [];
    public IReadOnlyList<string> CandidatePullUrls { get; set; } = [];
    public IReadOnlyList<string> CandidateIssueUrls { get; set; } = [];
    public string AssessPrompt { get; set; } = "";
    public ThreadAssessment? Assessment { get; set; }
    public string? MatchedCardPath { get; set; }
    public string? MatchReasonCode { get; set; }
    public string? MatchReasonDetail { get; set; }
    public IReadOnlyList<string> CandidateCardPaths { get; set; } = [];
    public IReadOnlyList<PullRequestSnapshot> Pulls { get; set; } = [];
    public bool PullLookupFailed { get; set; }
}

/// <summary>
/// カード内の各 workstream 更新・削除提案および Guard 判定の診断記録。
/// </summary>
internal sealed record WorkstreamDiagnostic(
    string Workstream,
    string ProposedAction,
    string Stage,
    string ReasonCode,
    string ReasonDetail,
    IReadOnlyList<string> ReportIds,
    bool Applied);

/// <summary>
/// Project カード単位の Reconcile 実入力・判断結果・未選択 report・Guard・適用結果の診断記録。
/// </summary>
internal sealed class CardDiagnostic
{
    public required string RelativePath { get; init; }
    public bool ReconcileCacheHit { get; set; }
    public string ReconcileHash { get; set; } = "";
    public required string Stage { get; set; }
    public required string ReasonCode { get; set; }
    public required string ReasonDetail { get; set; }
    public string ReconcilePrompt { get; set; } = "";
    public IReadOnlyList<LedgerFieldUpdate> Updates { get; set; } = [];
    public IReadOnlyList<WorkstreamDiagnostic> WorkstreamDiagnostics { get; set; } = [];
    public IReadOnlyList<string> UnselectedReportIds { get; set; } = [];
    public string ProposalStatus { get; set; } = "none";
    public string? CommitSha { get; set; }
}

/// <summary>
/// 1回の実行全体の段階別診断と、切り詰め・選択・redact 後の実際の判断入力・判断結果・変更理由・適用結果の保存構造。
/// </summary>
internal sealed class RunTrace
{
    public required string RunId { get; init; }
    public required string InputMode { get; init; }
    public required string Model { get; init; }
    public required string ReasoningEffort { get; init; }
    public required string PromptVersion { get; init; }
    public required int InputModelVersion { get; init; }
    public required DateTimeOffset WindowFromUtc { get; init; }
    public required DateTimeOffset WindowToUtcExclusive { get; init; }
    public required DateTimeOffset QueriedAtUtc { get; init; }
    public IReadOnlyList<PreSelectionDiagnostic> PreSelection { get; init; } = [];
    public IReadOnlyList<ThreadDiagnostic> Threads { get; init; } = [];
    public IReadOnlyList<CardDiagnostic> Cards { get; init; } = [];

    public string ToJson()
    {
        var stageCounts = new JsonObject();
        var reasonCounts = new JsonObject();
        void CountItem(string stage, string reason)
        {
            stageCounts[stage] = (stageCounts[stage]?.GetValue<int>() ?? 0) + 1;
            reasonCounts[reason] = (reasonCounts[reason]?.GetValue<int>() ?? 0) + 1;
        }

        var preArray = new JsonArray();
        foreach (var item in PreSelection)
        {
            CountItem(item.Stage, item.ReasonCode);
            JsonLists.AddObject(preArray, new JsonObject
            {
                ["subject"] = item.Subject,
                ["filePath"] = item.FilePath,
                ["stage"] = item.Stage,
                ["reasonCode"] = item.ReasonCode,
                ["reasonDetail"] = item.ReasonDetail,
                ["firstEventUtc"] = item.FirstEventUtc is null ? null : StateStore.FormatTime(item.FirstEventUtc.Value),
                ["lastEventUtc"] = item.LastEventUtc is null ? null : StateStore.FormatTime(item.LastEventUtc.Value),
            });
        }

        var threadArray = new JsonArray();
        foreach (var thread in Threads)
        {
            CountItem(thread.Stage, thread.ReasonCode);
            var turns = new JsonArray();
            foreach (var turn in thread.Turns)
            {
                JsonLists.AddObject(turns, new JsonObject
                {
                    ["turnId"] = turn.TurnId,
                    ["role"] = turn.Role,
                    ["timestamp"] = turn.Timestamp is null ? null : StateStore.FormatTime(turn.Timestamp.Value),
                    ["ordinal"] = turn.Ordinal,
                    ["basis"] = turn.Basis,
                    ["truncated"] = turn.Truncated,
                    ["text"] = turn.Text,
                });
            }

            var signals = new JsonArray();
            foreach (var signal in thread.Signals)
            {
                JsonLists.AddObject(signals, new JsonObject
                {
                    ["signalId"] = signal.SignalId,
                    ["sourceKind"] = signal.SourceKind,
                    ["toolName"] = signal.ToolName,
                    ["timestamp"] = signal.Timestamp is null ? null : StateStore.FormatTime(signal.Timestamp.Value),
                    ["ordinal"] = signal.Ordinal,
                    ["truncated"] = signal.Truncated,
                    ["text"] = signal.Text,
                });
            }

            var candPulls = new JsonArray();
            foreach (var url in thread.CandidatePullUrls)
            {
                JsonLists.AddText(candPulls, url);
            }

            var candIssues = new JsonArray();
            foreach (var url in thread.CandidateIssueUrls)
            {
                JsonLists.AddText(candIssues, url);
            }

            var candCards = new JsonArray();
            foreach (var path in thread.CandidateCardPaths)
            {
                JsonLists.AddText(candCards, path);
            }

            var pulls = new JsonArray();
            foreach (var pull in thread.Pulls)
            {
                JsonLists.AddObject(pulls, new JsonObject
                {
                    ["url"] = pull.Url,
                    ["state"] = pull.State.ToString(),
                    ["mergedAt"] = pull.MergedAt is null ? null : StateStore.FormatTime(pull.MergedAt.Value),
                    ["closedAt"] = pull.ClosedAt is null ? null : StateStore.FormatTime(pull.ClosedAt.Value),
                    ["title"] = pull.Title,
                });
            }

            JsonLists.AddObject(threadArray, new JsonObject
            {
                ["activityId"] = thread.ActivityId,
                ["filePath"] = thread.FilePath,
                ["repository"] = thread.Repository,
                ["cwd"] = thread.Cwd,
                ["stage"] = thread.Stage,
                ["reasonCode"] = thread.ReasonCode,
                ["reasonDetail"] = thread.ReasonDetail,
                ["truncated"] = thread.Truncated,
                ["hasTrailingUnansweredUserTurn"] = thread.HasTrailingUnansweredUserTurn,
                ["assessCacheHit"] = thread.AssessCacheHit,
                ["requestText"] = thread.Request?.Text,
                ["responseText"] = thread.Response?.Text,
                ["turns"] = turns,
                ["signals"] = signals,
                ["candidatePullUrls"] = candPulls,
                ["candidateIssueUrls"] = candIssues,
                ["assessPrompt"] = thread.AssessPrompt,
                ["assessment"] = thread.Assessment is null ? null : JsonNode.Parse(thread.Assessment.ToJson()),
                ["matchedCardPath"] = thread.MatchedCardPath,
                ["matchReasonCode"] = thread.MatchReasonCode,
                ["matchReasonDetail"] = thread.MatchReasonDetail,
                ["candidateCardPaths"] = candCards,
                ["pulls"] = pulls,
                ["pullLookupFailed"] = thread.PullLookupFailed,
            });
        }

        var cardArray = new JsonArray();
        foreach (var card in Cards)
        {
            var updates = new JsonArray();
            foreach (var update in card.Updates)
            {
                var reportIds = new JsonArray();
                if (update.ReportIds is not null)
                {
                    foreach (var id in update.ReportIds)
                    {
                        JsonLists.AddText(reportIds, id);
                    }
                }

                JsonLists.AddObject(updates, new JsonObject
                {
                    ["workstream"] = update.Workstream,
                    ["action"] = update.Action,
                    ["current"] = update.Current,
                    ["next"] = update.Next,
                    ["waiting"] = update.Waiting,
                    ["reason"] = update.Reason,
                    ["reportIds"] = reportIds,
                });
            }

            var wsDiags = new JsonArray();
            foreach (var ws in card.WorkstreamDiagnostics)
            {
                var reportIds = new JsonArray();
                foreach (var id in ws.ReportIds)
                {
                    JsonLists.AddText(reportIds, id);
                }

                JsonLists.AddObject(wsDiags, new JsonObject
                {
                    ["workstream"] = ws.Workstream,
                    ["proposedAction"] = ws.ProposedAction,
                    ["stage"] = ws.Stage,
                    ["reasonCode"] = ws.ReasonCode,
                    ["reasonDetail"] = ws.ReasonDetail,
                    ["reportIds"] = reportIds,
                    ["applied"] = ws.Applied,
                });
            }

            var unselected = new JsonArray();
            foreach (var id in card.UnselectedReportIds)
            {
                JsonLists.AddText(unselected, id);
            }

            JsonLists.AddObject(cardArray, new JsonObject
            {
                ["relativePath"] = card.RelativePath,
                ["reconcileCacheHit"] = card.ReconcileCacheHit,
                ["reconcileHash"] = card.ReconcileHash,
                ["stage"] = card.Stage,
                ["reasonCode"] = card.ReasonCode,
                ["reasonDetail"] = card.ReasonDetail,
                ["reconcilePrompt"] = card.ReconcilePrompt,
                ["updates"] = updates,
                ["workstreamDiagnostics"] = wsDiags,
                ["unselectedReportIds"] = unselected,
                ["proposalStatus"] = card.ProposalStatus,
                ["commitSha"] = card.CommitSha,
            });
        }

        return new JsonObject
        {
            ["runId"] = RunId,
            ["inputMode"] = InputMode,
            ["model"] = Model,
            ["reasoningEffort"] = ReasoningEffort,
            ["promptVersion"] = PromptVersion,
            ["inputModelVersion"] = InputModelVersion,
            ["windowFromUtc"] = StateStore.FormatTime(WindowFromUtc),
            ["windowToUtcExclusive"] = StateStore.FormatTime(WindowToUtcExclusive),
            ["queriedAtUtc"] = StateStore.FormatTime(QueriedAtUtc),
            ["stageCounts"] = stageCounts,
            ["reasonCounts"] = reasonCounts,
            ["preSelection"] = preArray,
            ["threads"] = threadArray,
            ["cards"] = cardArray,
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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
