#:property TargetFramework=net10.0
#:package Microsoft.Data.Sqlite@9.0.10
#:package OpenAI@2.14.0
#:include ../src/Config.cs
#:include ../src/TimeWindow.cs
#:include ../src/Models.cs
#:include ../src/Normalize.cs
#:include ../src/Redaction.cs
#:include ../src/ByteLog.cs
#:include ../src/Store.cs
#:include ../src/CodexCollector.cs
#:include ../src/GitHubClient.cs
#:include ../src/Ledger.cs
#:include ../src/OpenAiActivity.cs
#:include ../src/Report.cs
#:include ../src/Pipeline.cs

using System.Text;
using System.Text.Json.Nodes;

var failures = new List<string>();
await Case("config can be created and old facade path is ignored", ConfigFile);
await Case("first request and last answer are the only model text", SelectedPayload);
await Case("a thread with no normal response does not update the ledger", NoNormalResponse);
await Case("an old normal response survives later mid logs", ResponseSurvivesMidLogs);
await Case("explicit remaining work is kept and tool text is not", ExplicitRemaining);
await Case("an already matching card is not rewritten", AlreadyMatches);
await Case("richer ledger text is kept", RicherText);
await Case("a merged pull outside the window completes the workstream", RefreshMerge);
await Case("closed unmerged does not delete", ClosedUnmerged);
await Case("self invocation does not update and other work can be assessed", SelfAndDevelopment);
await Case("unmatched repository does not change the card", Unmatched);
await Case("two reports for one workstream do not stop the run", TwoReports);
await Case("rerun does not add another commit", Rerun);
await Case("apply writes the preview and rejects a changed pull", ApplyProposal);
await Case("one model failure does not stop the other thread", ModelFailure);
await Case("an incomplete line is resumed once", IncompleteLine);
await Case("invalid json does not stop the run", InvalidJsonContinues);
await Case("invalid utf-8 chunk is skipped", InvalidUtf8Chunk);
await Case("an old file mtime does not drop in-window events", OldMtimeInWindow);
await Case("an old observation body is not sent", OldObservation);
await Case("estimate counts the selected payload and does not call the model", EstimateOnly);
await Case("an open pull on another report blocks deletion", OpenPullBlocksDelete);
await Case("saved matching does not delete a coordinator update", CoordinatorUpdateSurvivesMerge);
await Case("another workstream report does not mix into completion", OtherWorkstreamStaysOut);
await Case("an empty field clears and null keeps the ledger", EmptyClearsNullKeeps);
await Case("a replaced rollout does not keep the old thread", ReplacedRolloutDropsOldThread);
await Case("an outside merge stays with an active card", OutsideMergeJoinsActiveCard);
await Case("an unselected pull does not hold the card", UnselectedPullDoesNotHold);
await Case("openai failure keeps status and detail", OpenAiFailureMessage);
await Case("a missing pull does not stop the run", MissingPullContinues);
await Case("a failed pull does not delete the workstream", FailedPullBlocksDelete);
await Case("one reconcile failure does not stop the other card", ReconcileFailureContinues);
await Case("an unreadable rollout does not stop the run", UnreadableRolloutContinues);
await Case("an unreadable card does not stop the run", UnreadableCardContinues);

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

Task ConfigFile()
{
    var name = "status-check-config-" + Guid.NewGuid().ToString("N") + ".json";
    var previous = Directory.GetCurrentDirectory();
    var directory = Path.Combine(Path.GetTempPath(), "status-check-config-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        Directory.SetCurrentDirectory(directory);
        var config = AppConfig.LoadOrCreate(name);
        var written = File.ReadAllText(Path.Combine(directory, name));
        Check(config.Model.Length > 0, "default config was empty");
        Check(!written.Contains("facadeRunsDir", StringComparison.Ordinal), "new config still records facade logs");
    }
    finally
    {
        Directory.SetCurrentDirectory(previous);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    var parsed = AppConfig.Parse("""
        {
          "stateDirectory": "state",
          "codexSessionsDir": "codex",
          "facadeRunsDir": "ignored",
          "ledgerRepository": "ledger"
        }
        """);
    Check(parsed.LedgerRepository == "ledger", "old config key was required");
    Check(parsed.CodexSessionsDir == "codex", "codex directory was dropped");
    return Task.CompletedTask;
}

async Task SelectedPayload()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard(null));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("<user_instructions>\nSECRET_INSTRUCTIONS\n</user_instructions>"),
        LogText.User("<environment_context>\nSECRET_ENVIRONMENT\n</environment_context>\nウィジェットを直して。"),
        LogText.Assistant("SECRET_COMMENTARY 途中です", "commentary"),
        LogText.Tool("SECRET_TOOL tool_call_update 残作業はツールだけに書く。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer"),
        LogText.User("追記の依頼は最初の依頼にしない。"));
    await world.Run(true);
    var thread = world.Store.GetThread("codex", "sess");
    Check(thread?.Request?.Text == "ウィジェットを直して。", "first request was " + thread?.Request?.Text);
    Check(thread?.Response?.Text.Contains("実装を完了した。", StringComparison.Ordinal) == true, "last answer missing");
    Check(thread?.Response?.Basis.Contains("final_answer", StringComparison.Ordinal) == true, "basis was " + thread?.Response?.Basis);
    Check(world.Model.Payloads.Count == 2, "call count " + world.Model.Payloads.Count);
    var payload = string.Join("\n", world.Model.Payloads);
    Check(payload.Contains("ウィジェットを直して。", StringComparison.Ordinal), "request was not sent");
    Check(payload.Contains("実装を完了した。", StringComparison.Ordinal), "answer was not sent");
    Check(!payload.Contains("SECRET_INSTRUCTIONS", StringComparison.Ordinal), "instructions were sent");
    Check(!payload.Contains("SECRET_ENVIRONMENT", StringComparison.Ordinal), "environment was sent");
    Check(!payload.Contains("SECRET_COMMENTARY", StringComparison.Ordinal), "commentary was sent");
    Check(!payload.Contains("SECRET_TOOL", StringComparison.Ordinal), "tool text was sent");
    Check(!payload.Contains("tool_call_update", StringComparison.Ordinal), "tool update was sent");
    Check(!payload.Contains("追記の依頼", StringComparison.Ordinal), "later request replaced the first");
    Check(world.CardText().Contains("実装を完了した。", StringComparison.Ordinal), "reported position was not written");
    Check(world.CardText().Contains("### 文書整理", StringComparison.Ordinal), "unrelated workstream was dropped");

    var calls = world.Model.Payloads.Count;
    var responseLength = thread!.Response!.Text.Length;
    world.AppendRollout("sess", LogText.Tool(new string('x', 20000) + " SECRET_TOOL_2 tool_call_update"));
    await world.Run(true);
    var again = world.Store.GetThread("codex", "sess");
    Check(again?.Response?.Text.Length == responseLength, "mid logs changed the selected answer");
    Check(world.Model.Payloads.Count == calls, "mid logs caused another model call");
    Check(!string.Join("\n", world.Model.Payloads).Contains("SECRET_TOOL_2", StringComparison.Ordinal), "later tool text was sent");
}

async Task NoNormalResponse()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var before = world.CardText();
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("実装を開始した。"),
        LogText.Assistant("SECRET_COMMENTARY まだ答えではない", "commentary"));
    var result = await world.Run(true);
    Check(world.CardText() == before, "a thread without a normal response changed the card");
    Check(world.Model.Payloads.Count == 0, "a thread without a normal response called the model");
    Check(world.Store.GetThread("codex", "sess")?.Response is null, "commentary became the response");
    var report = File.ReadAllText(result.ReportPath);
    Check(!report.Contains("結果未取得", StringComparison.Ordinal), "missing response was described as 結果未取得");
    Check(report.Contains("期間内の全作業を復元したものではない", StringComparison.Ordinal), "report claimed a full reconstruction");
}

async Task ResponseSurvivesMidLogs()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。", "2026-10-15T00:11:00Z"),
        LogText.Assistant("計画のみで、実装はしていない。", "final_answer", "2026-11-01T00:20:00Z"));
    await world.Run(true);
    var answer = world.Store.GetThread("codex", "sess")?.Response?.Text;
    world.AppendRollout("sess", LogText.Event("task_started"), LogText.Assistant("途中の commentary", "commentary"), LogText.Tool("SECRET_MID"));
    var result = await world.Run(true);
    var thread = world.Store.GetThread("codex", "sess");
    Check(thread?.Request?.Text == "ウィジェットを直して。", "the first request was dropped");
    Check(thread?.Response?.Text == answer, "the last normal response was replaced");
    Check(world.CardText().Contains("計画のみで、実装はしていない。", StringComparison.Ordinal), "plan response was not the current position");
    Check(world.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "a plan deleted the workstream");
    var report = File.ReadAllText(result.ReportPath);
    Check(!report.Contains("結果未取得", StringComparison.Ordinal), "kept response was described as 結果未取得");
    Check(report.Contains("2026-11-01T00:20:00", StringComparison.Ordinal), "report hid the adopted response time");
    Check(world.Model.Payloads.Any(payload => payload.Contains("ウィジェットを直して。", StringComparison.Ordinal)), "out-of-window request was dropped");
}

async Task ExplicitRemaining()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard(null));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Tool("残作業は秘密の索引を更新する。SECRET_REMAINING"),
        LogText.Assistant("実装を一段落させた。残作業は索引を更新する。 https://github.com/acme/widgets/pull/7", "final_answer"));
    await world.Run(true);
    var text = world.CardText();
    Check(text.Contains("残作業は索引を更新する。", StringComparison.Ordinal), "explicit remaining work was dropped");
    Check(text.Contains("管理側へこの残作業を戻す。", StringComparison.Ordinal), "next action was not the return to management");
    Check(text.Contains("### ウィジェット修正", StringComparison.Ordinal), "remaining work deleted the workstream");
    Check(!text.Contains("SECRET_REMAINING", StringComparison.Ordinal), "tool text became remaining work");
    Check(!string.Join("\n", world.Model.Payloads).Contains("SECRET_REMAINING", StringComparison.Ordinal), "tool remaining work was sent");
    Check(!text.Contains("未送信の本文", StringComparison.Ordinal), "unsent body was written into the ledger");
}

async Task AlreadyMatches()
{
    await using var world = World.Create();
    const string current = "計画のみで、実装はしていない。";
    world.WriteCard(OneCard("なし。", current));
    var before = world.CardText();
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant(current, "final_answer"));
    await world.Run(true);
    Check(world.CardText() == before, "matching card was rewritten");
    Check(world.Commits() == 1, "matching card was committed");
}

async Task RicherText()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard(null, workstreamCurrent: "詳細な進捗: ウィジェットの実装を続けている。レビューも反映済み。"));
    var before = world.CardText();
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("ウィジェットの実装を続けている。", "final_answer"));
    await world.Run(true);
    Check(world.CardText() == before, "richer text was replaced");
    Check(world.CardText().Contains("### 文書整理", StringComparison.Ordinal), "unrelated workstream changed");
    Check(world.CardText().Contains("ウィジェットを直す。", StringComparison.Ordinal), "purpose changed");
}

async Task RefreshMerge()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard(null));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer"));
    await world.Run(true);
    Check(world.Commits() == 2, "open pull did not update the card");
    Check(world.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "open pull deleted the workstream");
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    var result = await world.Run(true, window: World.Day(2));
    var text = world.CardText();
    Check(!text.Contains("### ウィジェット修正", StringComparison.Ordinal), "merged pull kept the workstream");
    Check(text.Contains("### 文書整理", StringComparison.Ordinal), "other workstream was removed");
    Check(text.Contains("ウィジェットを直す。", StringComparison.Ordinal), "purpose was removed");
    Check(text.Contains("別件の文書整理は未着手である。", StringComparison.Ordinal), "other current sentence was removed");
    Check(text.Contains(LedgerPlanner.CompletionFact, StringComparison.Ordinal), "completion fact missing");
    Check(File.ReadAllText(result.ReportPath).Contains("期間外", StringComparison.Ordinal), "merge outside the window was not reported");
    var commits = world.Commits();
    await world.Run(true, window: World.Day(2));
    Check(world.Commits() == commits, "completed workstream was committed again");
}

async Task ClosedUnmerged()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.ClosedUnmerged);
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装完了と報告する。 https://github.com/acme/widgets/pull/7", "final_answer"));
    var result = await world.Run(true);
    Check(world.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "closed unmerged deleted the workstream");
    Check(File.ReadAllText(result.ReportPath).Contains("未マージのClose", StringComparison.Ordinal), "closed state missing from the report");
    Check(!world.CardText().Contains("未送信の本文", StringComparison.Ordinal), "unsent body was written");
}

async Task SelfAndDevelopment()
{
    await using var self = World.Create();
    self.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    self.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    self.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("dotnet run --file src/StatusCheck.cs"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer"));
    await self.Run(true);
    Check(self.Model.Payloads.Count == 0, "self invocation called the model");
    Check(self.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "self invocation deleted a workstream");

    await using var dev = World.Create();
    dev.WriteCard(OneCard("なし。"));
    var before = dev.CardText();
    dev.WriteRollout(
        "sess",
        "https://github.com/suusanex/tool_coding_status_update_check",
        LogText.User("READMEの実装を開始した。"),
        LogText.Assistant("READMEの実装を開始した。", "final_answer"));
    await dev.Run(true);
    Check(dev.Model.Payloads.Any(payload => payload.Contains("READMEの実装を開始した。", StringComparison.Ordinal)), "tool development was skipped");
    Check(dev.CardText() == before, "unrelated development changed the widgets card");
}

async Task Unmatched()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var before = world.CardText();
    world.WriteRollout(
        "sess",
        "https://github.com/other/repo",
        LogText.User("実装を開始した。"),
        LogText.Assistant("実装を開始した。", "final_answer"));
    var result = await world.Run(true);
    Check(world.CardText() == before, "unmatched card changed");
    Check(File.ReadAllText(result.ReportPath).Contains("決められない", StringComparison.Ordinal), "hold missing");
    Check(world.Commits() == 1, "unmatched run committed");
}

async Task TwoReports()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout("sess-a", "https://github.com/acme/widgets", LogText.User("ウィジェットを直して。"), LogText.Assistant("先に調査した。", "final_answer", "2026-11-01T00:20:00Z"));
    world.WriteRollout("sess-b", "https://github.com/acme/widgets", LogText.User("ウィジェットを直して。"), LogText.Assistant("実装を開始した。", "final_answer", "2026-11-01T00:40:00Z"));
    var result = await world.Run(true);
    Check(result.ExitCode == 0, result.StopReason ?? "two reports failed");
    Check(world.CardText().Contains("実装を開始した。", StringComparison.Ordinal), "newer report was not applied");
    Check(world.Commits() == 2, "two reports made more than one ledger commit");
}

async Task Rerun()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("計画のみで、実装はしていない。", "final_answer"));
    await world.Run(false);
    Check(world.Commits() == 1, "preview committed");
    await world.Run(true);
    var commits = world.Commits();
    Check(commits == 2, "first apply did not commit");
    await world.Run(true);
    Check(world.Commits() == commits, "rerun committed again");
}

async Task ApplyProposal()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer"));
    var preview = await world.Run(false);
    Check(world.Commits() == 1, "preview committed");
    var applied = await world.Apply(preview.RunId);
    Check(applied.ExitCode == 0, applied.StopReason ?? "apply failed");
    Check(world.Commits() == 2, "apply did not commit");

    await using var changed = World.Create();
    changed.WriteCard(OneCard("なし。"));
    changed.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    changed.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer"));
    var proposal = await changed.Run(false);
    changed.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    try
    {
        await changed.Apply(proposal.RunId);
        Check(false, "changed pull state was applied");
    }
    catch (StatusCheckException ex)
    {
        Check(ex.Message.Contains("Pull request state changed", StringComparison.Ordinal), ex.Message);
    }

    Check(changed.Commits() == 1, "rejected apply committed");
}

async Task ModelFailure()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout(
        "bad",
        "https://github.com/acme/widgets",
        LogText.User("壊れた評価。"),
        LogText.Assistant("FAIL_MODEL 実装を完了した。", "final_answer"));
    world.WriteRollout(
        "good",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を開始した。", "final_answer", "2026-11-01T00:40:00Z"));
    var result = await world.Run(true);
    Check(result.ExitCode == 0, result.StopReason ?? "one model failure stopped the run");
    Check(result.SkippedRecords.Any(skip => skip.Subject == "codex:bad" && skip.Message.Contains("scripted model failure", StringComparison.Ordinal)), "model failure was not reported");
    Check(world.Store.GetThread("codex", "bad")?.Assessment is null, "failed assessment was stored");
    Check(world.CardText().Contains("実装を開始した。", StringComparison.Ordinal), "the other thread was not applied");
    Check(!world.CardText().Contains("FAIL_MODEL", StringComparison.Ordinal), "failed thread was written");
    var cursor = world.Store.GetCursor(world.RolloutPath("bad"));
    var length = new FileInfo(world.RolloutPath("bad")).Length;
    Check(cursor is not null && cursor.ByteOffset == length, "model failure rewound the cursor");
    Check(File.ReadAllText(result.ReportPath).Contains("scripted model failure", StringComparison.Ordinal), "report missed the model failure");
}

async Task IncompleteLine()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var path = world.RolloutPath("sess");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, LogText.Meta("sess", "https://github.com/acme/widgets") + "\n" + LogText.User("実装を開始した。"), World.FileEncoding);
    File.SetLastWriteTimeUtc(path, World.Mtime.UtcDateTime);
    await world.Run(true);
    Check(world.Store.GetThread("codex", "sess")?.Request is null, "incomplete line was consumed");
    File.AppendAllText(path, "\n", World.FileEncoding);
    File.SetLastWriteTimeUtc(path, World.Mtime.UtcDateTime);
    await world.Run(true);
    Check(world.Store.GetThread("codex", "sess")?.Request?.Text == "実装を開始した。", "resumed line was missed");
    Check(world.Commits() == 1, "a request without a response committed");
    await world.Run(true);
    Check(world.Store.GetThread("codex", "sess")?.Request?.Text == "実装を開始した。", "resumed line was duplicated");
}

async Task InvalidJsonContinues()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var path = world.RolloutPath("sess");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(
        path,
        LogText.Meta("sess", "https://github.com/acme/widgets") + "\n{not-json}\n" + LogText.User("ウィジェットを直して。") + "\n" + LogText.Assistant("実装を完了した。", "final_answer") + "\n",
        World.FileEncoding);
    File.SetLastWriteTimeUtc(path, World.Mtime.UtcDateTime);
    var first = await world.Run(false);
    Check(first.ExitCode == 0, "invalid json stopped the run: " + first.StopReason);
    Check(first.SkippedLogs.Any(skip => skip.LineNumber == 2 && skip.Path == path), "codex line was not skipped");
    Check(world.Store.GetThread("codex", "sess")?.Response?.Text.Contains("実装を完了した。", StringComparison.Ordinal) == true, "later answer was dropped");
    Check(File.ReadAllText(first.ReportPath).Contains("2 行目", StringComparison.Ordinal), "report missed the bad line");
    var second = await world.Run(false);
    Check(second.SkippedLogs.Count == 0, "skipped line was reported again");
    Check(world.Store.GetThread("codex", "sess")?.Request?.Text == "ウィジェットを直して。", "second run duplicated the request");
}

async Task InvalidUtf8Chunk()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var badPath = world.RolloutPath("bad");
    Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
    using (var stream = File.Create(badPath))
    {
        stream.Write(World.FileEncoding.GetBytes(LogText.Meta("sess-bad", "https://github.com/acme/widgets") + "\n"));
        stream.WriteByte(0xFF);
        stream.WriteByte((byte)'\n');
        stream.Write(World.FileEncoding.GetBytes(LogText.User("実装を開始した。") + "\n"));
    }

    File.SetLastWriteTimeUtc(badPath, World.Mtime.UtcDateTime);
    world.WriteRollout("later", "https://github.com/acme/widgets", LogText.User("実装を開始した。"), LogText.Assistant("計画のみで、実装はしていない。", "final_answer"));
    var first = await world.Run(false);
    Check(first.ExitCode == 0, "invalid utf-8 stopped the run: " + first.StopReason);
    Check(first.SkippedLogs.Count == 1 && first.SkippedLogs[0].LineNumber is null && first.SkippedLogs[0].Path == badPath, "utf-8 chunk was not reported as a file skip");
    Check(world.Store.GetThread("codex", "sess-bad") is null, "undecodable chunk was ingested");
    Check(world.Store.GetThread("codex", "later")?.Request?.Text == "実装を開始した。", "later file was not read");
    var cursor = world.Store.GetCursor(badPath);
    Check(cursor is not null && cursor.ByteOffset == new FileInfo(badPath).Length && cursor.NextLine == 4, "utf-8 cursor did not advance past the chunk");
    Check(File.ReadAllText(first.ReportPath).Contains("not valid UTF-8", StringComparison.Ordinal), "report missed the utf-8 skip");
    var second = await world.Run(false);
    Check(second.SkippedLogs.Count == 0, "utf-8 chunk was reported again");
}

async Task OldMtimeInWindow()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout("sess", "https://github.com/acme/widgets", LogText.User("実装を開始した。"), LogText.Assistant("計画のみで、実装はしていない。", "final_answer"));
    var path = world.RolloutPath("sess");
    File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
    await world.Run(true);
    Check(world.Store.GetThread("codex", "sess")?.Request?.Text == "実装を開始した。", "old mtime dropped an in-window event");
}

async Task OldObservation()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.Store.UpsertObservation(new ObservationDraft
    {
        Source = "codex",
        NativeId = "old",
        FilePath = "old.jsonl",
        Ordinal = 1,
        Timestamp = World.Mtime,
        Kind = "user_message",
        Body = "SENTINEL_OLD_OBSERVATION",
        DetailJson = "{}",
        Urls = [],
        RunIds = [],
    });
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("計画のみで、実装はしていない。", "final_answer"));
    await world.Run(false);
    Check(world.Model.Payloads.Count > 0, "new thread was not assessed");
    Check(!string.Join("\n", world.Model.Payloads).Contains("SENTINEL_OLD_OBSERVATION", StringComparison.Ordinal), "old observation was sent");
}

async Task EstimateOnly()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Tool(new string('x', 20000) + " SECRET_ESTIMATE tool_call_update"),
        LogText.Assistant("計画のみで、実装はしていない。", "final_answer"));
    var result = await world.Run(false, estimate: true);
    Check(result.ExitCode == 0, result.StopReason ?? "estimate failed");
    Check(result.Estimated, "estimate called the live path");
    Check(result.PlannedCalls >= 1, "estimate planned no calls");
    Check(result.EstimatedInputTokens > 0, "estimate returned no tokens");
    Check(world.Model.Payloads.Count == 0, "estimate called the model");
    Check(world.Commits() == 1, "estimate committed");
    var thread = world.Store.GetThread("codex", "sess");
    Check(thread?.Response?.Text.Contains("SECRET_ESTIMATE", StringComparison.Ordinal) == false, "estimate kept the tool body");
    Check((thread?.Request?.Text.Length ?? 0) + (thread?.Response?.Text.Length ?? 0) < 500, "selected text included the tool body");
    var report = File.ReadAllText(result.ReportPath);
    Check(report.Contains("APIは呼び出していない", StringComparison.Ordinal), "estimate report missed the dry-run notice");
    Check(report.Contains("途中ログは送信対象に含めていない", StringComparison.Ordinal), "estimate report missed the mid-log notice");
    Check(report.Contains("途中ログがpayloadに含まれる: いいえ", StringComparison.Ordinal), "tool update was treated as payload");
    Check(report.Contains("reconcile 呼び出しは上限である", StringComparison.Ordinal), "estimate hid that reconcile counts are an upper bound");
    Check(!report.Contains("SECRET_ESTIMATE", StringComparison.Ordinal), "estimate report included the tool body");
}

Task OpenPullBlocksDelete()
{
    var card = CardParser.Parse("projects/widgets.md", TwoCard(null));
    var open = Snapshot(7, PullRequestState.Open);
    var merged = Snapshot(8, PullRequestState.Merged);
    var older = ReportView("old", "先に出した。 https://github.com/acme/widgets/pull/7", "2026-11-01T00:20:00Z", open);
    var newer = ReportView("new", "実装を完了した。 https://github.com/acme/widgets/pull/8", "2026-11-01T00:40:00Z", merged);
    var blocked = LedgerPlanner.Apply(card, [older, newer], [new LedgerFieldUpdate("ウィジェット修正", "delete", null, null, null, "", ["codex:old", "codex:new"])]);
    Check(blocked is null, "open pull was deleted");

    var both = LedgerPlanner.Apply(
        card,
        [
            ReportView("old", "先に出した。 https://github.com/acme/widgets/pull/7", "2026-11-01T00:20:00Z", Snapshot(7, PullRequestState.Merged)),
            newer,
        ],
        [new LedgerFieldUpdate("ウィジェット修正", "delete", null, null, null, "", ["codex:old", "codex:new"])]);
    Check(both is not null && !both.NewText.Contains("### ウィジェット修正", StringComparison.Ordinal), "merged pulls kept the workstream");
    Check(both!.NewText.Contains("### 文書整理", StringComparison.Ordinal), "other workstream was removed with the merged pulls");
    return Task.CompletedTask;
}

async Task ReplacedRolloutDropsOldThread()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.WriteRollout(
        "old-session",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("計画のみで、実装はしていない。", "final_answer"));
    await world.Run(false);
    var path = world.RolloutPath("old-session");
    File.WriteAllText(
        path,
        string.Join("\n", LogText.Meta("new-session", "https://github.com/acme/widgets"), LogText.User("ウィジェットを直して。"), LogText.Assistant("実装を開始した。", "final_answer")) + "\n",
        World.FileEncoding);
    File.SetLastWriteTimeUtc(path, World.Mtime.UtcDateTime);
    await world.Run(false);
    Check(world.Store.GetThread("codex", "old-session") is null, "replaced file kept the old thread");
    Check(world.Store.GetThread("codex", "new-session")?.Response?.Text.Contains("実装を開始した。", StringComparison.Ordinal) == true, "replacement thread was not read");
}

async Task OutsideMergeJoinsActiveCard()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard(null));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout(
        "old",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer", "2026-11-01T00:20:00Z"));
    await world.Run(true);
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    world.WriteRollout(
        "new",
        "https://github.com/acme/widgets",
        LogText.User("文書の担当を確認して。", "2026-11-02T00:11:00Z"),
        LogText.Assistant("文書整理はまだである。", "final_answer", "2026-11-02T00:20:00Z"));
    await world.Run(true, window: World.Day(2));
    Check(
        world.Model.Payloads.Any(payload => payload.Contains("実装を完了した。", StringComparison.Ordinal) && payload.Contains("文書整理はまだである。", StringComparison.Ordinal)),
        "outside merged thread was dropped from the active card");
}

async Task UnselectedPullDoesNotHold()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("https://github.com/acme/widgets/pull/7"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    var other = Path.Combine(world.Ledger, "projects", "other.md");
    File.WriteAllText(
        other,
        """
        ---
        status: active
        repositories:
          - acme/other
        ---

        # Other

        ## 目的

        別件。

        ## 現在地

        別件を見ている。

        ## 進行中のworkstream

        ### 別件

        **現在地**

        別件を見ている。

        **次のアクション**

        続ける。

        **待ち・阻害要因**

        なし。

        **参照**

        https://github.com/acme/other/pull/9
        """,
        World.FileEncoding);
    LedgerGit.Run(world.Ledger, ["add", "--", "projects/other.md"]);
    LedgerGit.Run(world.Ledger, ["commit", "-m", "other"]);
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("別件 https://github.com/acme/other/pull/9 は対象外。ウィジェットを直して。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer"));
    var result = await world.Run(false);
    var report = File.ReadAllText(result.ReportPath);
    Check(!report.Contains("複数のProjectカード", StringComparison.Ordinal), "unselected pull forced a hold");
    Check(world.Model.Payloads.Any(payload => payload.Contains("projects/widgets.md", StringComparison.Ordinal)), "selected pull did not reach reconcile");
}

async Task CoordinatorUpdateSurvivesMerge()
{
    await using var world = World.Create();
    world.WriteCard(TwoCard(null));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7", "final_answer"));
    await world.Run(true);
    var path = Path.Combine(world.Ledger, "projects", "widgets.md");
    var rewritten = world.CardText().Replace("実装を完了した。 https://github.com/acme/widgets/pull/7", "実装済み。次は導入作業。", StringComparison.Ordinal);
    File.WriteAllText(path, rewritten, World.FileEncoding);
    LedgerGit.Run(world.Ledger, ["add", "--", "projects/widgets.md"]);
    LedgerGit.Run(world.Ledger, ["commit", "-m", "coordinator"]);
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero));
    await world.Run(true);
    var text = world.CardText();
    Check(text.Contains("### ウィジェット修正", StringComparison.Ordinal), "saved matching deleted the coordinator update");
    Check(text.Contains("導入作業", StringComparison.Ordinal), "coordinator text was removed");
    Check(world.Model.Payloads.Any(payload => payload.Contains("導入作業", StringComparison.Ordinal)), "merge skipped reconciliation with the current ledger");
}

Task OtherWorkstreamStaysOut()
{
    var card = CardParser.Parse("projects/widgets.md", TwoCard(null));
    var feature = ReportView("feature", "実装を完了した。 https://github.com/acme/widgets/pull/7", "2026-11-01T00:20:00Z", Snapshot(7, PullRequestState.Merged));
    var docs = ReportView("docs", "残作業は説明書の更新。", "2026-11-01T00:40:00Z", Snapshot(8, PullRequestState.Open));
    docs = new ThreadView
    {
        Thread = docs.Thread,
        Assessment = new ThreadAssessment
        {
            Summary = "残作業は説明書の更新。",
            Purpose = "",
            CurrentPosition = "残作業は説明書の更新。",
            ExplicitRemainingWork = "残作業は説明書の更新。",
            NextAction = "",
            Uncertainty = "",
            SelectedPullUrls = [],
        },
        Pulls = [],
    };
    var deleted = LedgerPlanner.Apply(card, [feature, docs], [new LedgerFieldUpdate("ウィジェット修正", "delete", null, null, null, "", ["codex:feature"])]);
    Check(deleted is not null && !deleted.NewText.Contains("### ウィジェット修正", StringComparison.Ordinal), "named merged report did not complete its workstream");
    Check(!deleted!.NewText.Contains("残作業は説明書の更新", StringComparison.Ordinal), "another workstream remaining work was mixed in");
    Check(deleted.NewText.Contains("### 文書整理", StringComparison.Ordinal), "the other workstream was removed");
    return Task.CompletedTask;
}

Task EmptyClearsNullKeeps()
{
    var card = CardParser.Parse("projects/widgets.md", OneCard("https://github.com/acme/widgets/pull/7").Replace("なし。", "認証エラーで停止。", StringComparison.Ordinal));
    var view = ReportView("sess", "認証エラーを解消し、実装を再開した。", "2026-11-01T00:20:00Z", Snapshot(7, PullRequestState.Open));
    var cleared = LedgerPlanner.Apply(card, [view], [new LedgerFieldUpdate("ウィジェット修正", "update", "認証エラーを解消し、実装を再開した。", null, "", "", ["codex:sess"])]);
    Check(cleared is not null && !cleared.NewText.Contains("認証エラーで停止", StringComparison.Ordinal), "empty waiting kept the old blocker");
    Check(cleared!.NewText.Contains("認証エラーを解消し、実装を再開した。", StringComparison.Ordinal), "new current was dropped");
    Check(cleared.NewText.Contains("実装を続ける。", StringComparison.Ordinal), "null next cleared the ledger");
    var kept = LedgerPlanner.Apply(card, [view], [new LedgerFieldUpdate("ウィジェット修正", "update", "認証エラーを解消し、実装を再開した。", null, null, "", ["codex:sess"])]);
    Check(kept is not null && kept.NewText.Contains("認証エラーで停止", StringComparison.Ordinal), "null waiting cleared the ledger");
    var parsed = ThreadPrompts.ParseReconcile("""
        {"updates":[{"workstream":"機能追加","action":"update","current":"再開した。","next":null,"waiting":"","reason":"","reportIds":["codex:a"]}]}
        """);
    Check(parsed.Count == 1 && parsed[0].Next is null && parsed[0].Waiting == "" && parsed[0].ReportIds!.Contains("codex:a"), "reconcile JSON lost null, empty, or report ids");
    return Task.CompletedTask;
}

static ThreadView ReportView(string id, string response, string at, PullRequestSnapshot pull)
{
    var timestamp = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);
    return new ThreadView
    {
        Thread = new ThreadRecord
        {
            Source = "codex",
            NativeId = id,
            FilePath = id + ".jsonl",
            Response = new SelectedExcerpt(response, timestamp, 1, "response_item final_answer"),
            LastEventUtc = timestamp,
            Fingerprint = id,
        },
        Assessment = new ThreadAssessment
        {
            Summary = response,
            Purpose = "",
            CurrentPosition = response,
            ExplicitRemainingWork = "",
            NextAction = "",
            Uncertainty = "",
            SelectedPullUrls = [pull.Url],
        },
        Pulls = [pull],
    };
}

static PullRequestSnapshot Snapshot(int number, PullRequestState state) =>
    new(
        "https://github.com/acme/widgets/pull/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "acme",
        "widgets",
        number,
        state,
        state == PullRequestState.Merged ? new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero) : null,
        null,
        "title");

Task OpenAiFailureMessage()
{
    var text = OpenAiActivityModel.FormatFailure("assess", "gpt-6.1-terra", 3, true, 429, "{\n  \"error\": { \"message\": \"Rate limit exceeded\" }\n}");
    Check(text.Contains("after 3 attempts.", StringComparison.Ordinal), text);
    Check(text.Contains("stage=assess", StringComparison.Ordinal), text);
    Check(text.Contains("model=gpt-6.1-terra", StringComparison.Ordinal), text);
    Check(text.Contains("status=429", StringComparison.Ordinal), text);
    Check(text.Contains("Rate limit exceeded", StringComparison.Ordinal), text);
    Check(!text.Contains('\n'), "detail kept a newline");
    var secret = OpenAiActivityModel.FormatFailure("assess", "m", 1, false, 400, "bad sk-" + new string('a', 20));
    Check(secret.Contains("[REDACTED]", StringComparison.Ordinal), secret);
    Check(!secret.Contains(new string('a', 20), StringComparison.Ordinal), "secret leaked");
    var empty = OpenAiActivityModel.FormatFailure("reconcile", "m", 1, false, 500, "  \n");
    Check(empty.Contains("detail=(none)", StringComparison.Ordinal), empty);
    var truncated = OpenAiActivityModel.FormatFailure("assess", "m", 1, true, 503, new string('x', 400));
    Check(truncated.Contains("xxx...", StringComparison.Ordinal), truncated);
    Check(!truncated.Contains(new string('x', 301), StringComparison.Ordinal), "detail was not capped");
    return Task.CompletedTask;
}

async Task MissingPullContinues()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Open);
    world.WriteRollout(
        "missing",
        "https://github.com/acme/widgets",
        LogText.User("存在しない番号。"),
        LogText.Assistant("番号だけの参照。 https://github.com/acme/widgets/pull/1", "final_answer"));
    world.WriteRollout(
        "good",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を開始した。 https://github.com/acme/widgets/pull/7", "final_answer", "2026-11-01T00:40:00Z"));
    var result = await world.Run(true);
    Check(result.ExitCode == 0, result.StopReason ?? "missing pull stopped the run");
    Check(result.SkippedRecords.Any(skip => skip.Subject.Contains("/pull/1", StringComparison.Ordinal)), "missing pull was not reported");
    Check(world.CardText().Contains("### ウィジェット修正", StringComparison.Ordinal), "missing pull deleted the workstream");
    Check(world.CardText().Contains("実装を開始した。", StringComparison.Ordinal), "the other thread was not applied");
    Check(File.ReadAllText(result.ReportPath).Contains("pull/1", StringComparison.Ordinal), "report missed the pull skip");
}

async Task FailedPullBlocksDelete()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var before = world.CardText();
    world.Pulls.Set("acme", "widgets", 7, PullRequestState.Merged, new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero));
    world.WriteRollout(
        "sess",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を完了した。 https://github.com/acme/widgets/pull/7 https://github.com/acme/widgets/pull/1", "final_answer"));
    var result = await world.Run(true);
    Check(result.ExitCode == 0, result.StopReason ?? "incomplete pull state stopped the run");
    Check(result.SkippedRecords.Any(skip => skip.Subject.Contains("/pull/1", StringComparison.Ordinal)), "failed pull was not reported");
    Check(world.CardText() == before, "incomplete pull state changed the card");
    Check(File.ReadAllText(result.ReportPath).Contains("取得できなかったPRがある。", StringComparison.Ordinal), "report hid the failed pull");
}

async Task ReconcileFailureContinues()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var otherPath = Path.Combine(world.Ledger, "projects", "other.md");
    var otherCard = OneCard("なし。").Replace("acme/widgets", "acme/other", StringComparison.Ordinal);
    File.WriteAllText(otherPath, otherCard.Replace("\r\n", "\n", StringComparison.Ordinal), World.FileEncoding);
    LedgerGit.Run(world.Ledger, ["add", "--", "projects/other.md"]);
    LedgerGit.Run(world.Ledger, ["commit", "-m", "add other"]);
    var otherBefore = File.ReadAllText(otherPath);
    world.WriteRollout(
        "other",
        "https://github.com/acme/other",
        LogText.User("別カード。"),
        LogText.Assistant("FAIL_RECONCILE 実装を開始した。", "final_answer"));
    world.WriteRollout(
        "good",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を開始した。", "final_answer"));
    var result = await world.Run(true);
    Check(result.ExitCode == 0, result.StopReason ?? "one reconcile failure stopped the run");
    Check(result.SkippedRecords.Any(skip => skip.Subject == "projects/other.md" && skip.Message.Contains("scripted reconcile failure", StringComparison.Ordinal)), "reconcile failure was not reported");
    Check(File.ReadAllText(otherPath) == otherBefore, "failed card was written");
    Check(world.CardText().Contains("実装を開始した。", StringComparison.Ordinal), "the other card was not applied");
}

async Task UnreadableRolloutContinues()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var badPath = world.RolloutPath("bad");
    Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
    File.WriteAllText(badPath, LogText.Meta("bad", "https://github.com/acme/widgets") + "\n" + LogText.User("読めない。") + "\n", World.FileEncoding);
    FileStream? locked = null;
    try
    {
        locked = new FileStream(badPath, FileMode.Open, FileAccess.Read, FileShare.None);
        world.WriteRollout(
            "good",
            "https://github.com/acme/widgets",
            LogText.User("ウィジェットを直して。"),
            LogText.Assistant("実装を開始した。", "final_answer"));
        var result = await world.Run(false);
        Check(result.ExitCode == 0, result.StopReason ?? "locked rollout stopped the run");
        Check(result.SkippedLogs.Any(skip => skip.Path == badPath && skip.LineNumber is null), "locked rollout was not skipped");
        Check(world.Store.GetThread("codex", "good")?.Response?.Text.Contains("実装を開始した。", StringComparison.Ordinal) == true, "later file was not read");
        Check(world.Store.GetCursor(badPath) is null, "locked rollout advanced the cursor");
    }
    finally
    {
        locked?.Dispose();
    }
}

async Task UnreadableCardContinues()
{
    await using var world = World.Create();
    world.WriteCard(OneCard("なし。"));
    var otherPath = Path.Combine(world.Ledger, "projects", "other.md");
    File.WriteAllText(otherPath, OneCard("なし。").Replace("acme/widgets", "acme/other", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal), World.FileEncoding);
    LedgerGit.Run(world.Ledger, ["add", "--", "projects/other.md"]);
    LedgerGit.Run(world.Ledger, ["commit", "-m", "add other"]);
    world.WriteRollout(
        "good",
        "https://github.com/acme/widgets",
        LogText.User("ウィジェットを直して。"),
        LogText.Assistant("実装を開始した。", "final_answer"));
    world.WriteRollout(
        "other",
        "https://github.com/acme/other",
        LogText.User("別カード。"),
        LogText.Assistant("別リポジトリの実装を開始した。", "final_answer"));
    FileStream? locked = null;
    try
    {
        locked = new FileStream(otherPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await world.Run(false);
        Check(result.ExitCode == 0, result.StopReason ?? "locked card stopped the run");
        Check(result.SkippedRecords.Any(skip => skip.Subject == "projects/other.md"), "locked card was not skipped");
        Check(world.Model.Payloads.Any(payload => payload.Contains("実装を開始した。", StringComparison.Ordinal)), "readable card was not assessed");
    }
    finally
    {
        locked?.Dispose();
    }
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static string OneCard(string reference, string? current = null)
{
    var body = current ?? "ウィジェットの実装を続けている。";
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

""" + body + """

## 識別キーワード

widgets

## 進行中のworkstream

### ウィジェット修正

**現在地**

""" + body + """

**次のアクション**

実装を続ける。

**待ち・阻害要因**

なし。

**参照**

""" + reference + "\n";
}

static string TwoCard(string? secondReference, string? workstreamCurrent = null)
{
    var first = "https://github.com/acme/widgets/pull/7";
    var second = secondReference ?? "なし。";
    var current = workstreamCurrent ?? "ウィジェットの実装を続けている。";
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

""" + current + """

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

    internal static string User(string text, string timestamp = "2026-11-01T00:11:00Z") =>
        Message("user", text, "", timestamp);

    internal static string Assistant(string text, string phase, string timestamp = "2026-11-01T00:20:00Z") =>
        Message("assistant", text, phase, timestamp);

    internal static string Tool(string secret)
    {
        var arguments = new JsonObject
        {
            ["prompt"] = secret,
            ["output"] = secret,
        };
        return new JsonObject
        {
            ["timestamp"] = "2026-11-01T00:15:00Z",
            ["type"] = "response_item",
            ["payload"] = new JsonObject
            {
                ["type"] = "function_call",
                ["name"] = "start_agent",
                ["call_id"] = "call-1",
                ["arguments"] = arguments.ToJsonString(),
            },
        }.ToJsonString();
    }

    internal static string Event(string type) =>
        new JsonObject
        {
            ["timestamp"] = "2026-11-01T00:30:00Z",
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = type },
        }.ToJsonString();

    private static string Message(string role, string text, string phase, string timestamp)
    {
        var payload = new JsonObject
        {
            ["type"] = "message",
            ["role"] = role,
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = role == "user" ? "input_text" : "output_text", ["text"] = text },
            },
        };
        if (phase.Length > 0)
        {
            payload["phase"] = phase;
        }

        return new JsonObject
        {
            ["timestamp"] = timestamp,
            ["type"] = "response_item",
            ["payload"] = payload,
        }.ToJsonString();
    }
}

internal sealed class World : IAsyncDisposable
{
    internal static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf8 = FileEncoding;
    internal static readonly DateTimeOffset Mtime = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeWindow Window = Day(1);
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    internal required string Root { get; init; }
    internal required AppConfig Config { get; init; }
    internal required StateStore Store { get; init; }
    internal required ScriptedModel Model { get; init; }
    internal required FakePulls Pulls { get; init; }
    internal string Ledger => Config.LedgerRepository;

    internal static TimeWindow Day(int day) =>
        TimeWindow.FromDates(new DateOnly(2026, 11, day), new DateOnly(2026, 11, day), TimeZoneInfo.Utc);

    internal static World Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "status-check-" + Guid.NewGuid().ToString("N"));
        var config = new AppConfig
        {
            StateDirectory = Path.Combine(root, "state"),
            CodexSessionsDir = Path.Combine(root, "sessions"),
            CodexArchivedSessionsDir = null,
            LedgerRepository = Path.Combine(root, "ledger"),
            Model = "scripted",
            ReasoningEffort = "medium",
            MaxParallel = 1,
            MaxRetries = 1,
        };
        Directory.CreateDirectory(Path.Combine(config.LedgerRepository, "projects"));
        Directory.CreateDirectory(config.CodexSessionsDir);
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

    internal string RolloutPath(string session) =>
        Path.Combine(Config.CodexSessionsDir, "2026", "11", "01", "rollout-" + session + ".jsonl");

    internal void WriteRollout(string session, string repositoryUrl, params string[] lines)
    {
        var path = RolloutPath(session);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines.Prepend(LogText.Meta(session, repositoryUrl))) + "\n", Utf8);
        File.SetLastWriteTimeUtc(path, Mtime.UtcDateTime);
    }

    internal void AppendRollout(string session, params string[] lines)
    {
        var path = RolloutPath(session);
        File.AppendAllText(path, string.Join("\n", lines) + "\n", Utf8);
        File.SetLastWriteTimeUtc(path, Mtime.UtcDateTime);
    }

    internal Task<RunResult> Run(bool apply, bool estimate = false, TimeWindow? window = null) =>
        StatusPipeline.RunAsync(new RunRequest
        {
            Config = Config,
            Window = window ?? Window,
            ApplyLedger = apply && !estimate,
            EstimateOnly = estimate,
            Model = Model,
            Pulls = Pulls,
            Store = Store,
            Now = Now,
        }, CancellationToken.None);

    internal Task<RunResult> Apply(string runId) =>
        StatusPipeline.ApplyAsync(Config, Store, runId, Pulls, CancellationToken.None);

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
    internal List<string> Payloads { get; } = [];

    public Task<ThreadAssessment> AssessAsync(string system, string user, CancellationToken cancellationToken)
    {
        Payloads.Add(system + "\n" + user);
        if (user.Contains("FAIL_MODEL", StringComparison.Ordinal))
        {
            throw new StatusCheckException("scripted model failure");
        }

        var response = Section(user, "response");
        return Task.FromResult(new ThreadAssessment
        {
            Summary = response.Length == 0 ? "依頼" : response,
            Purpose = "",
            CurrentPosition = response,
            ExplicitRemainingWork = Remaining(response),
            NextAction = "",
            Uncertainty = "",
            SelectedPullUrls = Urls(response),
        });
    }

    public Task<IReadOnlyList<LedgerFieldUpdate>> ReconcileAsync(string system, string user, CancellationToken cancellationToken)
    {
        Payloads.Add(system + "\n" + user);
        if (user.Contains("FAIL_RECONCILE", StringComparison.Ordinal))
        {
            throw new StatusCheckException("scripted reconcile failure");
        }

        if (!user.Contains("### ウィジェット修正", StringComparison.Ordinal) || user.Contains("導入作業", StringComparison.Ordinal))
        {
            return Task.FromResult<IReadOnlyList<LedgerFieldUpdate>>([]);
        }

        var response = Section(user, "response");
        var ids = ReportIds(user);
        if (response.Contains("残作業", StringComparison.Ordinal) || !AllMerged(user) || (!response.Contains("実装完了", StringComparison.Ordinal) && !response.Contains("完了した", StringComparison.Ordinal)))
        {
            return Task.FromResult<IReadOnlyList<LedgerFieldUpdate>>([new LedgerFieldUpdate("ウィジェット修正", "update", response, null, null, "", ids)]);
        }

        return Task.FromResult<IReadOnlyList<LedgerFieldUpdate>>([new LedgerFieldUpdate("ウィジェット修正", "delete", null, null, null, "", ids)]);
    }

    private static List<string> ReportIds(string user)
    {
        var normalized = user.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var ids = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if (!line.StartsWith("id: ", StringComparison.Ordinal))
            {
                continue;
            }

            var id = line[4..].Trim();
            if (id.Length > 0 && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string Section(string user, string label)
    {
        // StringBuilder.AppendLine は Windows では CRLF を出す。マーカーは LF で探す。
        var normalized = user.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var marker = label + ":\n";
        var start = normalized.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += marker.Length;
        var end = normalized.Length;
        foreach (var stop in new[] { "\npulls:", "\npullCandidates:", "\n---", "\nrequestAt:", "\nresponseAt:" })
        {
            var found = normalized.IndexOf(stop, start, StringComparison.Ordinal);
            if (found >= 0 && found < end)
            {
                end = found;
            }
        }

        return normalized[start..end].Trim();
    }

    private static string Remaining(string response)
    {
        foreach (var sentence in response.Split('。', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (sentence.Contains("残作業", StringComparison.Ordinal))
            {
                return sentence + "。";
            }
        }

        return "";
    }

    private static bool AllMerged(string user)
    {
        var normalized = user.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var pulls = normalized.Split('\n').Where(line => line.StartsWith("- https://", StringComparison.Ordinal)).ToList();
        return pulls.Count > 0 && pulls.All(line => line.TrimEnd().EndsWith("Merged", StringComparison.Ordinal));
    }

    private static List<string> Urls(string text)
    {
        var found = new List<string>();
        var index = 0;
        const string marker = "https://github.com/";
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var end = index;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            var url = text[index..end].TrimEnd('.', ')', ']', ',');
            if (!found.Contains(url, StringComparer.Ordinal))
            {
                found.Add(url);
            }

            index = end;
        }

        return found;
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
