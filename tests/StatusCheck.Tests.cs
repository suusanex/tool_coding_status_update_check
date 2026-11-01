#:property TargetFramework=net10.0
#:package Microsoft.Data.Sqlite@9.0.10
#:include ../src/Config.cs
#:include ../src/TimeWindow.cs
#:include ../src/Models.cs
#:include ../src/Normalize.cs
#:include ../src/Redaction.cs
#:include ../src/ByteLog.cs
#:include ../src/Store.cs
#:include ../src/CodexCollector.cs
#:include ../src/FacadeCollector.cs
#:include ../src/GitHubClient.cs
#:include ../src/ActivityGrouping.cs
#:include ../src/Ledger.cs
#:include ../src/Report.cs
#:include ../src/Pipeline.cs

using System.Text;
using System.Text.Json.Nodes;

var failures = new List<string>();
await Case("grouping keeps overlapping independent issue", GroupingOverlap);
await Case("grouping rejects an unconfirmed quote", GroupingUnconfirmed);
await Case("grouping keeps disjoint quotes", GroupingDisjoint);
await Case("plan, start, and reported completion stay distinct", StageSentences);
await Case("parent and facade stay one activity and keep an independent issue", ParentFacade);
await Case("overlapping quote does not write the independent issue twice", OverlapHold);
await Case("merged corresponding PR ignores an open reference URL", RefreshMerge);
await Case("explicit remaining work stays on the workstream", ExplicitRemaining);
await Case("remaining work past the display excerpt blocks completion", TailRemaining);
await Case("incomplete coverage does not delete", IncompleteCoverage);
await Case("model failure keeps the cursor and does not commit", ModelFailure);
await Case("a remaining-work quote that more PRs are needed blocks completion", MultiplePrRemaining);
await Case("an unmerged pull identifier blocks completion", UnmergedIdentifier);
await Case("unmatched repository does not change the card", Unmatched);
await Case("rerun does not add another commit", Rerun);
await Case("an incomplete line is resumed once", IncompleteLine);
await Case("closed unmerged is not merged", ClosedUnmerged);
await Case("self invocation is excluded and tool development is kept", SelfAndDevelopment);

if (failures.Count > 0)
{
    Console.Error.WriteLine(failures.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " failed");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine("passed");
}

async Task Case(string name, Func<Task> body)
{
    try
    {
        await body();
        Console.WriteLine("ok " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
        Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
    }
}

Task GroupingOverlap()
{
    var body = "ウィジェットの実装を完了し、その過程で索引バグを見つけた。";
    var grouped = ActivityGrouping.Group([Obs("obs1", body)], [Extracted("obs1", false, "親作業", "", "implementation_reported_complete"), Extracted("obs1", true, "独立課題", body, "new_issue"), Extracted("obs1", true, "独立課題", body, "new_issue")]);
    var independent = grouped.Single(item => item.IndependentIssue);
    var parent = grouped.Single(item => !item.IndependentIssue);
    Check(grouped.Count == 2, "expected two activities");
    Check(independent.DiscoveredFrom == parent.Id, "discoveredFrom was not the parent");
    Check(!parent.Uncertainty.Contains("確認できない", StringComparison.Ordinal), "overlap was treated as unconfirmed");
    Check(parent.Id != independent.Id, "shared evidence collapsed the activity id");
    return Task.CompletedTask;
}

Task GroupingUnconfirmed()
{
    var body = "親の実装を完了した。";
    var grouped = ActivityGrouping.Group([Obs("obs1", body)], [Extracted("obs1", false, "親作業", "", "implementation_reported_complete"), Extracted("obs1", true, "独立課題", "本文に存在しない引用", "new_issue")]);
    Check(grouped.Count == 1, "unconfirmed split was kept");
    Check(grouped[0].Uncertainty.Contains("確認できない", StringComparison.Ordinal), "missing uncertainty");
    return Task.CompletedTask;
}

Task GroupingDisjoint()
{
    var body = "親の実装を完了した。別課題: 索引が古い。";
    var grouped = ActivityGrouping.Group([Obs("obs1", body)], [Extracted("obs1", false, "親作業", "", "implementation_reported_complete"), Extracted("obs1", true, "独立課題", "別課題: 索引が古い。", "new_issue")]);
    Check(grouped.Count == 2, "disjoint split was dropped");
    Check(grouped.Single(item => item.IndependentIssue).DiscoveredFrom == grouped.Single(item => !item.IndependentIssue).Id, "discoveredFrom missing");
    return Task.CompletedTask;
}

async Task StageSentences()
{
    await using var plan = World.Create();
    plan.WriteCard(OneCard("なし。"));
    plan.WriteRollout("実装には着手していない。計画だけを作った。");
    await plan.Run(true);
    Check(plan.CardText().Contains("計画のみで、実装開始は記録されていない。", StringComparison.Ordinal), "plan sentence missing");

    await using var started = World.Create();
    started.WriteCard(OneCard("なし。"));
    started.WriteRollout("実装を開始した。");
    await started.Run(true);
    Check(started.CardText().Contains("実装開始が記録されている。", StringComparison.Ordinal), "start sentence missing");

    await using var done = World.Create();
    done.WriteCard(OneCard("なし。"));
    done.WriteRollout("実装完了と報告した。");
    await done.Run(true);
    Check(done.CardText().Contains("実装完了と報告されている。マージ済みPRは記録されていない。", StringComparison.Ordinal), "completion sentence missing");
}

async Task ParentFacade()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard("https://github.com/acme/widgets/pull/8"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.Pulls.Set("acme", "widgets", 8, PullRequestState.Open);
    var runId = "20260831T143122Z-72d77c1d";
    world.WriteRollout("実装を開始した。 https://github.com/acme/widgets/pull/7\n別課題: 索引の不具合を見つけた。 https://github.com/acme/widgets/pull/8\n" + runId, "sess-parent");
    world.WriteFacade(runId, "delegate the parent task");
    await world.Run(true);
    var activities = world.Store.ListCurrentActivities();
    Check(activities.Count == 2, "activity count " + activities.Count);
    Check(activities.All(item => item.NativeId == "sess-parent"), "facade run became its own activity");
    var independent = activities.Single(item => item.DiscoveredFrom is not null);
    Check(activities.Any(item => item.Id == independent.DiscoveredFrom), "parent link missing");
    var facadeIds = world.Store.ListByNative("facade", runId).Select(item => item.Id).ToList();
    Check(facadeIds.Count > 0 && facadeIds.All(id => activities.Any(item => item.ObservationIds.Contains(id, StringComparer.Ordinal))), "facade observation was not absorbed");
}

async Task OverlapHold()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout("同一文課題: ウィジェットの実装を完了し、索引バグを見つけた。 https://github.com/acme/widgets/pull/7");
    var result = await world.Run(true);
    var activities = world.Store.ListCurrentActivities();
    Check(activities.Count == 2, "overlap did not keep both activities");
    Check(activities.Count(item => item.DiscoveredFrom is not null) == 1, "independent issue was folded back");
    Check(File.ReadAllText(result.ReportPath).Contains("同じworkstreamへは重ねて書かない", StringComparison.Ordinal), "hold was not reported");
    Check(world.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "workstream was removed");
}

async Task RefreshMerge()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard(null, "https://github.com/acme/widgets/pull/9"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout("実装完了と報告する。 https://github.com/acme/widgets/pull/7");
    await world.Run(true);
    var beforeDelete = world.CardText();
    var document = Between(beforeDelete, "### 文書整理", "");
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    var commits = world.Commits();
    var second = await world.Run(true);
    var text = world.CardText();
    Check(!text.Contains("### ウィジェット修正", StringComparison.Ordinal), "merged workstream remained");
    Check(text.Contains("### 文書整理", StringComparison.Ordinal), "other workstream was removed");
    Check(text.Contains(document, StringComparison.Ordinal), "other workstream changed");
    Check(text.Contains("管理側の受領は完了条件にしない。", StringComparison.Ordinal), "purpose changed");
    Check(text.Contains("別件の文書整理は未着手である。", StringComparison.Ordinal), "other current sentence was dropped");
    Check(text.Contains("対応PRはマージ済みで、明示的な残作業は記録されていない。", StringComparison.Ordinal), "completion fact missing");
    Check(File.ReadAllText(second.ReportPath).Contains("期間外", StringComparison.Ordinal), "out-of-window merge was not labeled");
    Check(world.Commits() == commits + 1, "delete was not committed");
    var third = world.Commits();
    await world.Run(true);
    Check(world.Commits() == third, "third run added a commit");
}

async Task ExplicitRemaining()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    world.WriteRollout("実装完了と報告する。 https://github.com/acme/widgets/pull/7\n残作業は索引を更新する。");
    await world.Run(true);
    var text = world.CardText();
    Check(text.Contains("### ウィジェット修正", StringComparison.Ordinal), "remaining work deleted the workstream");
    Check(text.Contains("残作業は索引を更新する。", StringComparison.Ordinal), "quote missing");
    Check(text.Contains("管理側へこの残作業を戻す。", StringComparison.Ordinal), "next action missing");
    Check(CountOf(text, "残作業は索引を更新する。") >= 2, "quote was not kept in both fields");
}

async Task TailRemaining()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    world.WriteRollout(new string('あ', 4500) + "\n残作業は索引を更新する。\nhttps://github.com/acme/widgets/pull/7");
    await world.Run(true);
    var stored = world.Store.ListByNative("codex", "sess").Single(item => item.Draft.Kind == "user_message");
    Check(stored.Draft.Body.Length > 4000, "stored body was truncated to the display excerpt");
    Check(world.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "tail remaining work was ignored");
    Check(world.CardText().Contains("残作業は索引を更新する。", StringComparison.Ordinal), "tail quote missing");
}

async Task IncompleteCoverage()
{
    await using var world = World.Create();
    var directory = Path.Combine(world.Ledger, "projects");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "widgets.md"), TwoCard(null), World.FileEncoding);
    var cards = CardParser.LoadRepository(world.Ledger);
    var pull = Snapshot(7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    var activity = View("act-coverage", [pull], coverage: false);
    var (changes, _) = await LedgerPlanner.PlanAsync(cards, [activity], world.Store, world.Model, CancellationToken.None);
    Check(changes.Count == 1, "coverage case produced no update");
    Check(changes[0].NewText.Contains("### ウィジェット修正", StringComparison.Ordinal), "workstream deleted without coverage");
    Check(changes[0].NewText.Contains("未送信の本文があるため、明示的な残作業がないとは扱わない。", StringComparison.Ordinal), "coverage sentence missing");
}

async Task ModelFailure()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var commits = world.Commits();
    world.WriteRollout("FAIL_MODEL 実装を開始した。");
    var result = await world.Run(true);
    Check(result.ExitCode == 1, "failure exit was " + result.ExitCode);
    Check(world.Commits() == commits, "failure committed the ledger");
    var path = Directory.EnumerateFiles(world.Config.CodexSessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories).Single();
    var cursor = world.Store.GetCursor(path);
    Check(cursor is not null && cursor.ByteOffset == new FileInfo(path).Length, "cursor was not left at EOF");
    Check(world.Store.ListByNative("codex", "sess").Count > 0, "observations were dropped");
}

async Task MultiplePrRemaining()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    world.WriteRollout("実装完了と報告する。 https://github.com/acme/widgets/pull/7\n残作業はこの作業は複数PRが必要。");
    await world.Run(true);
    var text = world.CardText();
    Check(text.Contains("### ウィジェット修正", StringComparison.Ordinal), "multiple-PR remaining work deleted the workstream");
    Check(text.Contains("残作業はこの作業は複数PRが必要。", StringComparison.Ordinal), "multiple-PR quote missing");
    Check(text.Contains("管理側へこの残作業を戻す。", StringComparison.Ordinal), "next action missing");
}

async Task UnmergedIdentifier()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    world.Pulls.Set("acme", "widgets", 8, PullRequestState.Open);
    world.WriteRollout("実装完了と報告する。 https://github.com/acme/widgets/pull/7 https://github.com/acme/widgets/pull/8");
    await world.Run(true);
    var text = world.CardText();
    Check(text.Contains("### ウィジェット修正", StringComparison.Ordinal), "unmerged identifier deleted the workstream");
    Check(text.Contains("https://github.com/acme/widgets/pull/8 は未マージ。", StringComparison.Ordinal), "open identifier was not described");
    Check(!text.Contains("対応PRが未特定である。", StringComparison.Ordinal), "unidentified wording was written");
    Check(!text.Contains("必要PR", StringComparison.Ordinal), "identifiers were called required PRs");
}

async Task Unmatched()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var before = world.CardText();
    world.WriteRollout("実装を開始した。", repositoryUrl: "https://github.com/other/repo");
    var result = await world.Run(true);
    Check(world.CardText() == before, "unmatched card changed");
    var report = File.ReadAllText(result.ReportPath);
    Check(report.Contains("決められない", StringComparison.Ordinal) || report.Contains("一致しない", StringComparison.Ordinal), "hold missing");
    Check(world.Commits() == 1, "unmatched run committed");
}

async Task Rerun()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout("計画だけを作った。着手していない。");
    await world.Run(false);
    Check(world.Commits() == 1, "preview committed");
    await world.Run(true);
    var commits = world.Commits();
    Check(commits == 2, "first apply did not commit");
    await world.Run(true);
    Check(world.Commits() == commits, "rerun committed again");
    Check(world.Store.ListCurrentActivities().Count == 1, "rerun duplicated activities");
}

async Task IncompleteLine()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var path = world.RolloutPath();
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var meta = LogText.Meta("sess", "https://github.com/acme/widgets");
    File.WriteAllText(path, meta + "\n" + LogText.Message("実装を開始した。"), World.FileEncoding);
    File.SetLastWriteTimeUtc(path, World.Mtime.UtcDateTime);
    await world.Run(true);
    Check(world.Store.ListByNative("codex", "sess").Count(item => item.Draft.Kind == "user_message") == 0, "incomplete line was consumed");
    File.AppendAllText(path, "\n", World.FileEncoding);
    File.SetLastWriteTimeUtc(path, World.Mtime.UtcDateTime);
    await world.Run(true);
    Check(world.Store.ListByNative("codex", "sess").Count(item => item.Draft.Kind == "user_message") == 1, "resumed line was duplicated or missed");
    Check(world.Store.ListCurrentActivities().Count == 1, "resume duplicated current activities");
    var commits = world.Commits();
    await world.Run(true);
    Check(world.Commits() == commits, "resume rerun committed again");
}

async Task ClosedUnmerged()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.ClosedUnmerged);
    world.WriteRollout("実装完了と報告する。 https://github.com/acme/widgets/pull/7");
    await world.Run(true);
    var text = world.CardText();
    Check(text.Contains("未マージのClose", StringComparison.Ordinal), "closed unmerged text missing");
    Check(text.Contains("### ウィジェット修正", StringComparison.Ordinal), "closed unmerged deleted the workstream");
}

async Task SelfAndDevelopment()
{
    await using var self = World.Create();
    self.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    self.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    self.WriteRollout("dotnet run --file src/StatusCheck.cs\nhttps://github.com/acme/widgets/pull/7");
    await self.Run(true);
    Check(self.Store.ListCurrentActivities().All(item => item.Kind == "status_check_self"), "self kind missing");
    Check(self.Model.ConcentratedIds.Count == 0, "self invocation was concentrated");
    Check(self.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "self invocation deleted a workstream");

    await using var dev = World.Create();
    dev.WriteCard(OneCard("なし。"));
    dev.WriteRollout("READMEの実装を開始した。", cwd: @"D:\Data\git\tool_coding_status_update_check", repositoryUrl: "https://github.com/suusanex/tool_coding_status_update_check");
    await dev.Run(true);
    var ids = dev.Store.ListCurrentActivities().Select(item => item.Id).ToList();
    Check(ids.Count == 1 && dev.Model.ConcentratedIds.Contains(ids[0]), "tool development was excluded from concentration");
    Check(dev.Commits() == 1, "tool development committed another project");
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static int CountOf(string text, string value)
{
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += value.Length;
    }

    return count;
}

static string Between(string text, string start, string end)
{
    var from = text.IndexOf(start, StringComparison.Ordinal);
    if (from < 0)
    {
        throw new InvalidOperationException("marker missing: " + start);
    }

    var to = string.IsNullOrEmpty(end) ? text.Length : text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
    if (to < 0)
    {
        to = text.Length;
    }

    return text[from..to];
}

static StoredObservation Obs(string id, string body) => new()
{
    Id = id,
    Draft = new ObservationDraft
    {
        Source = "codex",
        NativeId = "sess",
        FilePath = "rollout.jsonl",
        Ordinal = 1,
        Kind = "user_message",
        Body = body,
        DetailJson = "{}",
        Urls = Normalize.GitHubRefs(body),
        RunIds = [],
    },
};

static ExtractedActivity Extracted(string id, bool independent, string summary, string quote, string stage) => new()
{
    ObservationIds = [id],
    IndependentIssue = independent,
    Summary = summary,
    Purpose = "",
    Stages = [stage],
    PeriodChange = "",
    CurrentPosition = "",
    ExplicitRemainingWork = "",
    Uncertainty = "",
    Confidence = 1,
    NextAction = "",
    UncertainMerge = false,
    EvidenceQuotes = quote.Length == 0 ? [] : [new EvidenceQuote(id, quote)],
};

static PullRequestSnapshot Snapshot(int number, PullRequestState state, DateTimeOffset? mergedAt)
{
    var url = "https://github.com/acme/widgets/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return new PullRequestSnapshot(url, "acme", "widgets", number, state, mergedAt, null, "title");
}

static ActivityView View(string id, IReadOnlyList<PullRequestSnapshot> pulls, bool coverage) => new()
{
    Id = id,
    Summary = "実装完了",
    Purpose = "",
    Repository = "acme/widgets",
    Stages = [StageNames.ImplementationReportedComplete],
    PeriodChange = "",
    CurrentPosition = "",
    Kind = "normal",
    InWindow = true,
    Evidence = [],
    Uncertainty = "",
    PullRequests = pulls,
    IssueRefs = [],
    ExplicitRemainingWork = null,
    NextAction = "",
    ObservationIds = ["obs"],
    CoverageComplete = coverage,
};

static string OneCard(string reference) => """
---
status: active
repositories:
  - acme/widgets
---

# Widgets

## 目的

ウィジェットを直す。管理側の受領は完了条件にしない。

## 現在地

ウィジェットの実装を続けている。

## 識別キーワード

widgets

## 進行中のworkstream

### ウィジェット修正

**現在地**

ウィジェットの実装を続けている。

**次のアクション**

実装を続ける。

**待ち・阻害要因**

なし。

**参照**

""" + reference + "\n";

static string TwoCard(string? secondReference, string? extraFirstReference = null)
{
    var first = "https://github.com/acme/widgets/pull/7" + (extraFirstReference is null ? "" : "\n" + extraFirstReference);
    var second = secondReference ?? "なし。";
    return """
---
status: active
repositories:
  - acme/widgets
---

# Widgets

## 目的

ウィジェットを直す。管理側の受領は完了条件にしない。

## 現在地

ウィジェットの実装を続けている。別件の文書整理は未着手である。

## 識別キーワード

widgets

## 進行中のworkstream

### ウィジェット修正

**現在地**

ウィジェットの実装を続けている。

**次のアクション**

実装を続ける。

**待ち・阻害要因**

なし。

**参照**

""" + first + """

### 文書整理

**現在地**

別件の文書整理は未着手である。

**次のアクション**

文書の担当を確認する。

**待ち・阻害要因**

なし。

**参照**

""" + second + "\n";
}

internal static class LogText
{
    internal static string Meta(string session, string repositoryUrl, string cwd = @"D:\work")
    {
        var line = new JsonObject
        {
            ["timestamp"] = "2026-11-01T00:10:00Z",
            ["type"] = "session_meta",
            ["payload"] = new JsonObject
            {
                ["id"] = session,
                ["session_id"] = session,
                ["cwd"] = cwd,
                ["git"] = new JsonObject
                {
                    ["repository_url"] = repositoryUrl,
                    ["branch"] = "main",
                },
            },
        };
        return line.ToJsonString();
    }

    internal static string Message(string text)
    {
        var line = new JsonObject
        {
            ["timestamp"] = "2026-11-01T00:11:00Z",
            ["type"] = "response_item",
            ["payload"] = new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = text },
                },
            },
        };
        return line.ToJsonString();
    }
}

internal sealed class World : IAsyncDisposable
{
    internal static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8 = FileEncoding;
    internal static readonly DateTimeOffset Mtime = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeWindow Window = TimeWindow.FromDates(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    internal required string Root { get; init; }
    internal required AppConfig Config { get; init; }
    internal required StateStore Store { get; init; }
    internal required ScriptedModel Model { get; init; }
    internal required FakePulls Pulls { get; init; }
    internal string Ledger => Config.LedgerRepository;

    internal static World Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "status-check-" + Guid.NewGuid().ToString("N"));
        var config = new AppConfig
        {
            StateDirectory = Path.Combine(root, "state"),
            CodexSessionsDir = Path.Combine(root, "sessions"),
            CodexArchivedSessionsDir = null,
            FacadeRunsDir = Path.Combine(root, "facade"),
            LedgerRepository = Path.Combine(root, "ledger"),
            Model = "scripted",
            ReasoningEffort = "medium",
            MaxParallel = 1,
            MaxRetries = 1,
        };
        Directory.CreateDirectory(Path.Combine(config.LedgerRepository, "projects"));
        Directory.CreateDirectory(config.CodexSessionsDir);
        Directory.CreateDirectory(config.FacadeRunsDir);
        LedgerGit.Run(config.LedgerRepository, ["init", "-b", "main"]);
        LedgerGit.Run(config.LedgerRepository, ["config", "user.email", "status-check@example.com"]);
        LedgerGit.Run(config.LedgerRepository, ["config", "user.name", "status-check"]);
        LedgerGit.Run(config.LedgerRepository, ["config", "commit.gpgsign", "false"]);
        LedgerGit.Run(config.LedgerRepository, ["config", "core.autocrlf", "false"]);
        return new World
        {
            Root = root,
            Config = config,
            Store = new StateStore(config.DatabasePath),
            Model = new ScriptedModel(),
            Pulls = new FakePulls(),
        };
    }

    internal void WriteCard(string text)
    {
        var path = Path.Combine(Ledger, "projects", "widgets.md");
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal), Utf8);
        LedgerGit.Run(Ledger, ["add", "--", "projects/widgets.md"]);
        if (Commits() == 0)
        {
            LedgerGit.Run(Ledger, ["commit", "-m", "initial"]);
        }
    }

    internal string CardText() => File.ReadAllText(Path.Combine(Ledger, "projects", "widgets.md"));

    internal int Commits()
    {
        try
        {
            return int.Parse(LedgerGit.Run(Ledger, ["rev-list", "--count", "HEAD"]).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (StatusCheckException)
        {
            return 0;
        }
    }

    internal string RolloutPath() => Path.Combine(Config.CodexSessionsDir, "2026", "11", "01", "rollout-test.jsonl");

    internal void WriteRollout(string message, string session = "sess", string cwd = @"D:\work", string repositoryUrl = "https://github.com/acme/widgets")
    {
        var path = RolloutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LogText.Meta(session, repositoryUrl, cwd) + "\n" + LogText.Message(message) + "\n", Utf8);
        File.SetLastWriteTimeUtc(path, Mtime.UtcDateTime);
    }

    internal void WriteFacade(string runId, string prompt)
    {
        var line = new JsonObject
        {
            ["ts"] = "2026-11-01T00:12:00Z",
            ["runId"] = runId,
            ["source"] = "facade",
            ["type"] = "started",
            ["data"] = new JsonObject
            {
                ["agent"] = "codex",
                ["workingDirectory"] = @"D:\work",
                ["prompt"] = prompt,
            },
        };
        var path = Path.Combine(Config.FacadeRunsDir, runId + ".events.jsonl");
        File.WriteAllText(path, line.ToJsonString() + "\n", Utf8);
        File.SetLastWriteTimeUtc(path, Mtime.UtcDateTime);
    }

    internal Task<RunResult> Run(bool apply) =>
        StatusPipeline.RunAsync(new RunRequest
        {
            Config = Config,
            Window = Window,
            ApplyLedger = apply,
            Model = Model,
            Pulls = Pulls,
            Store = Store,
            Now = Now,
        }, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        Store.Dispose();
        await Task.CompletedTask;
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ScriptedModel : IActivityModel
{
    internal List<string> ConcentratedIds { get; } = [];

    public Task<IReadOnlyList<ExtractedActivity>> ExtractAsync(ActivityPacket packet, CancellationToken cancellationToken)
    {
        var extracted = new List<ExtractedActivity>();
        foreach (var observation in packet.Observations)
        {
            var body = observation.Draft.Body;
            if (body.Contains("FAIL_MODEL", StringComparison.Ordinal))
            {
                throw new StatusCheckException("scripted model failure");
            }

            if (body.Contains("同一文課題:", StringComparison.Ordinal))
            {
                extracted.Add(Make(observation.Id, false, "親作業", StagesFor(body), "", []));
                extracted.Add(Make(observation.Id, true, "独立課題", ["new_issue"], "", [new EvidenceQuote(observation.Id, body.Trim())]));
                continue;
            }

            if (body.Contains("別課題:", StringComparison.Ordinal))
            {
                var index = body.IndexOf("別課題:", StringComparison.Ordinal);
                var parentQuote = body[..index].Trim();
                var childQuote = body[index..].Trim();
                extracted.Add(Make(observation.Id, false, "親作業", StagesFor(parentQuote), "", [new EvidenceQuote(observation.Id, parentQuote)]));
                extracted.Add(Make(observation.Id, true, "独立課題", ["new_issue"], "", [new EvidenceQuote(observation.Id, childQuote)]));
                continue;
            }

            var remaining = Normalize.SplitSentences(body).FirstOrDefault(sentence => sentence.Contains("残作業", StringComparison.Ordinal)) ?? "";
            extracted.Add(Make(observation.Id, false, SummaryFor(body), StagesFor(body), remaining, []));
        }

        return Task.FromResult<IReadOnlyList<ExtractedActivity>>(extracted);
    }

    public Task<WorkstreamChoice> ChooseWorkstreamAsync(WorkstreamChoiceRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkstreamChoice(null, true));

    public Task<IReadOnlyList<ConcentrationItem>> ConcentrateAsync(IReadOnlyList<ActivityView> activities, CancellationToken cancellationToken)
    {
        ConcentratedIds.AddRange(activities.Select(activity => activity.Id));
        return Task.FromResult<IReadOnlyList<ConcentrationItem>>([]);
    }

    private static ExtractedActivity Make(string id, bool independent, string summary, IReadOnlyList<string> stages, string remaining, IReadOnlyList<EvidenceQuote> quotes) => new()
    {
        ObservationIds = [id],
        IndependentIssue = independent,
        Summary = summary,
        Purpose = "",
        Stages = stages,
        PeriodChange = summary,
        CurrentPosition = "",
        ExplicitRemainingWork = remaining,
        Uncertainty = "",
        Confidence = 1,
        NextAction = "",
        UncertainMerge = false,
        EvidenceQuotes = quotes,
    };

    private static IReadOnlyList<string> StagesFor(string text)
    {
        if (text.Contains("実装完了", StringComparison.Ordinal))
        {
            return ["implementation_reported_complete"];
        }

        if (text.Contains("実装を開始", StringComparison.Ordinal))
        {
            return ["implementation_started"];
        }

        if (text.Contains("計画だけ", StringComparison.Ordinal) || text.Contains("計画のみ", StringComparison.Ordinal))
        {
            return ["plan_only"];
        }

        return ["consultation"];
    }

    private static string SummaryFor(string text)
    {
        if (text.Contains("実装完了", StringComparison.Ordinal))
        {
            return "実装完了の報告";
        }

        if (text.Contains("実装を開始", StringComparison.Ordinal))
        {
            return "実装開始";
        }

        if (text.Contains("計画だけ", StringComparison.Ordinal) || text.Contains("計画のみ", StringComparison.Ordinal))
        {
            return "計画のみ";
        }

        return "相談";
    }
}

internal sealed class FakePulls : IPullRequestSource
{
    private readonly Dictionary<string, PullRequestSnapshot> _items = new(StringComparer.OrdinalIgnoreCase);

    internal void Set(string owner, string repository, int number, PullRequestState state, DateTimeOffset? mergedAt = null)
    {
        var url = "https://github.com/" + owner + "/" + repository + "/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _items[url] = new PullRequestSnapshot(url, owner, repository, number, state, mergedAt, state == PullRequestState.ClosedUnmerged ? mergedAt ?? DateTimeOffset.UnixEpoch : null, "title");
    }

    public Task<PullRequestSnapshot> GetAsync(string owner, string repository, int number, CancellationToken cancellationToken)
    {
        var url = "https://github.com/" + owner + "/" + repository + "/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!_items.TryGetValue(url, out var snapshot))
        {
            throw new StatusCheckException("Missing pull " + url);
        }

        return Task.FromResult(snapshot);
    }
}
