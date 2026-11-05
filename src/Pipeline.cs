using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// 選択済みの入力を解析し、台帳の差分があるときだけ更新する。
/// LLM と gh は呼び出し側が差し替える。estimate は API を呼ばない。
/// </summary>
internal static partial class StatusPipeline
{
    internal const string CommitMessage = "活動状況の照合結果をProjectカードへ反映する";

    internal static async Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken)
    {
        var runId = string.IsNullOrWhiteSpace(request.RunId) ? Guid.NewGuid().ToString("D") : request.RunId!;
        request.Store.InsertRun(runId, request.Window);
        string? reportPath = null;
        string? proposalPath = null;
        string? tracePath = null;
        var skips = new List<IngestSkip>();
        var recordSkips = new List<ProcessingSkip>();
        try
        {
            skips.AddRange(Ingest(request));
            var storedThreads = request.Store.ListThreads();
            var threads = SyncFingerprints(request, storedThreads);
            if (request.EstimateOnly)
            {
                var (estimate, estimateTrace) = DescribeEstimate(request, runId, threads, skips, recordSkips);
                tracePath = WriteTrace(request.Config, runId, estimateTrace);
                reportPath = WriteEstimate(request, runId, estimate, skips, recordSkips);
                request.Store.FinishRun(runId, "succeeded", reportPath, null);
                return new RunResult
                {
                    RunId = runId,
                    ReportPath = reportPath,
                    TracePath = tracePath,
                    Trace = estimateTrace,
                    AppliedCards = [],
                    ExitCode = 0,
                    SkippedLogs = skips,
                    SkippedRecords = recordSkips,
                    EstimatedInputTokens = estimate.InputTokens,
                    PlannedCalls = estimate.AssessCalls + estimate.ReconcileCalls,
                    Estimated = true,
                };
            }

            var cards = CardParser.LoadRepository(request.Config.LedgerRepository, recordSkips);
            var plan = await PlanAsync(request, threads, cards, skips, recordSkips, cancellationToken);
            var staged = StampBlobs(request.Config.LedgerRepository, plan.Changes);
            proposalPath = WriteProposal(request.Config, runId, staged);
            var applied = new List<string>();
            string? applicationReceiptPath = null;
            string? stop = null;
            var exit = 0;
            string? commitSha = null;
            if (request.ApplyLedger && staged.Count > 0)
            {
                try
                {
                    var commitResult = CommitChangesWithSha(request.Config.LedgerRepository, staged, request.Store, runId);
                    applied.AddRange(commitResult.AppliedPaths);
                    commitSha = commitResult.CommitSha;
                    if (applied.Count > 0) applicationReceiptPath = WriteApplicationReceipt(request.Config, runId, applied, commitSha);
                }
                catch (StatusCheckException ex)
                {
                    stop = ex.Message;
                    exit = 1;
                }
            }

            FinalizeTrace(plan.Trace, request.ApplyLedger, staged, applied, commitSha, stop);
            tracePath = WriteTrace(request.Config, runId, plan.Trace);
            var usage = request.Store.UsageTotals(runId);
            reportPath = WriteReport(request, runId, plan, LedgerText(request.ApplyLedger, staged, applied, stop), usage, skips, recordSkips);
            request.Store.FinishRun(runId, exit == 0 ? "succeeded" : "failed", reportPath, proposalPath);
            return new RunResult
            {
                RunId = runId,
                ReportPath = reportPath,
                TracePath = tracePath,
                Trace = plan.Trace,
                AppliedCards = applied,
                ApplicationReceiptPath = applicationReceiptPath,
                StopReason = stop,
                ExitCode = exit,
                SkippedLogs = skips,
                SkippedRecords = recordSkips,
            };
        }
        catch (Exception ex) when (ex is StatusCheckException or AggregateException)
        {
            var message = ex is AggregateException aggregate
                ? (aggregate.Flatten().InnerExceptions.FirstOrDefault()?.Message ?? ex.Message)
                : ex.Message;
            request.Store.FinishRun(runId, "failed", reportPath, proposalPath);
            return new RunResult
            {
                RunId = runId,
                ReportPath = reportPath ?? "",
                TracePath = tracePath,
                AppliedCards = [],
                StopReason = message,
                ExitCode = 1,
                SkippedLogs = skips,
                SkippedRecords = recordSkips,
            };
        }
    }

    internal static async Task<RunResult> ApplyAsync(AppConfig config, StateStore store, string runId, IPullRequestSource pulls, CancellationToken cancellationToken)
    {
        var proposalPath = Path.Combine(config.ReportsDirectory, runId, "proposal.json");
        if (!File.Exists(proposalPath))
        {
            throw new StatusCheckException("Proposal was not found: " + proposalPath);
        }

        var proposal = ReadProposal(File.ReadAllText(proposalPath));
        if (proposal.Changes.Count == 0)
        {
            return new RunResult { RunId = runId, ReportPath = "", AppliedCards = [], StopReason = null, ExitCode = 0 };
        }

        LedgerGit.EnsureClean(config.LedgerRepository);
        foreach (var change in proposal.Changes)
        {
            var hash = LedgerGit.Hash(config.LedgerRepository, change.RelativePath);
            if (!string.Equals(hash, change.BlobBefore, StringComparison.Ordinal))
            {
                throw new StatusCheckException("Ledger card changed after the proposal was created: " + change.RelativePath);
            }
        }

        foreach (var pull in proposal.Pulls)
        {
            var reference = Normalize.GitHubRefs(pull.Url).FirstOrDefault(item => item.Kind == "pull");
            if (reference is null)
            {
                throw new StatusCheckException("Proposal pull URL is not a GitHub pull request: " + pull.Url);
            }

            var fresh = await pulls.GetAsync(reference.Owner, reference.Repository, reference.Number, cancellationToken);
            if (!string.Equals(fresh.State.ToString(), pull.State, StringComparison.Ordinal))
            {
                throw new StatusCheckException("Pull request state changed since the proposal: " + pull.Url);
            }
        }

        var commitResult = CommitChangesWithSha(config.LedgerRepository, proposal.Changes, store, runId);
        var receipt = commitResult.AppliedPaths.Count > 0 ? WriteApplicationReceipt(config, runId, commitResult.AppliedPaths, commitResult.CommitSha) : null;
        return new RunResult { RunId = runId, ReportPath = "", AppliedCards = commitResult.AppliedPaths, ApplicationReceiptPath = receipt, StopReason = null, ExitCode = 0 };
    }

    private static IReadOnlyList<ThreadRecord> SyncFingerprints(RunRequest request, IReadOnlyList<ThreadRecord> stored)
    {
        var synced = new List<ThreadRecord>(stored.Count);
        foreach (var thread in stored)
        {
            var expected = Normalize.SelectionFingerprint(
                thread.Request,
                thread.Response,
                thread.Turns,
                thread.Signals,
                thread.ExtractedRefs,
                request.Config.InputMode,
                request.Config.Model,
                request.Config.ReasoningEffort);
            if (thread.Fingerprint == expected)
            {
                synced.Add(thread);
                continue;
            }

            var updated = new ThreadRecord
            {
                Source = thread.Source,
                NativeId = thread.NativeId,
                FilePath = thread.FilePath,
                Repository = thread.Repository,
                Cwd = thread.Cwd,
                Request = thread.Request,
                Response = thread.Response,
                Turns = thread.Turns,
                Signals = thread.Signals,
                ExtractedRefs = thread.ExtractedRefs,
                Truncated = thread.Truncated,
                FirstEventUtc = thread.FirstEventUtc,
                LastEventUtc = thread.LastEventUtc,
                Fingerprint = expected,
                Assessment = null,
                MatchedPath = null,
                MatchedWorkstream = null,
                ReconcileFingerprint = null,
            };
            request.Store.UpsertThread(updated);
            synced.Add(updated);
        }

        return synced;
    }

    private static List<IngestSkip> Ingest(RunRequest request)
    {
        var skips = new List<IngestSkip>();
        IngestRoot(request, request.Config.CodexSessionsDir, "codex", searchAllDirectories: true, skips);
        if (!string.IsNullOrWhiteSpace(request.Config.CodexArchivedSessionsDir))
        {
            IngestRoot(request, request.Config.CodexArchivedSessionsDir, "codex", searchAllDirectories: true, skips);
        }

        return skips;
    }

    private static void IngestRoot(RunRequest request, string root, string source, bool searchAllDirectories, List<IngestSkip> skips)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var pattern = "rollout-*.jsonl";
        var option = searchAllDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFiles(root, pattern, option).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                IngestFile(request, path, source, skips);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 1ファイルが読めなくても、他のログは取り込む。
                skips.Add(new IngestSkip(path, null, ex.Message));
            }
        }
    }

    private static void IngestFile(RunRequest request, string path, string source, List<IngestSkip> skips)
    {
        var info = new FileInfo(path);
        var cursor = request.Store.GetCursor(path);
        var versionReset = cursor is null || cursor.ScanVersion < Normalize.InputModelVersion;
        if (!versionReset &&
            cursor is not null &&
            cursor.Size == info.Length &&
            cursor.ByteOffset == info.Length &&
            info.LastWriteTimeUtc < request.Window.FromUtc)
        {
            return;
        }

        var read = ByteLog.Read(
            path,
            versionReset ? 0 : cursor?.ByteOffset ?? 0,
            versionReset ? 0 : cursor?.Size ?? 0,
            versionReset ? null : cursor?.PrefixHash,
            versionReset ? 1 : cursor?.NextLine ?? 1);
        if (read.DecodeError is not null)
        {
            skips.Add(new IngestSkip(path, null, read.DecodeError));
        }

        var working = cursor ?? new FileCursor();
        var rebuild = versionReset || read.Reset;
        ReadCodex(request, path, read, working, rebuild, skips);

        request.Store.SaveCursor(path, source, new FileCursor
        {
            ByteOffset = read.NewOffset,
            Size = read.Size,
            PrefixHash = read.PrefixHash,
            NextLine = read.NextLine,
            NativeId = working.NativeId,
            Repository = working.Repository,
            Cwd = working.Cwd,
            LastEventUtc = working.LastEventUtc,
            Terminal = working.Terminal,
            ScanVersion = Normalize.InputModelVersion,
        });
    }

    private static void ReadCodex(RunRequest request, string path, ByteLog.ReadResult read, FileCursor cursor, bool rebuild, List<IngestSkip> skips)
    {
        var selection = new CodexSelection();
        var seeded = !rebuild && !string.IsNullOrWhiteSpace(cursor.NativeId);
        if (seeded)
        {
            SeedCodex(selection, request.Store.GetThread("codex", cursor.NativeId!));
        }

        foreach (var line in read.Lines)
        {
            try
            {
                CodexCollector.Observe(line, selection);
            }
            catch (InvalidLogLineException ex)
            {
                skips.Add(new IngestSkip(path, line.Number, ex.Message));
            }
        }

        if (string.IsNullOrWhiteSpace(selection.NativeId))
        {
            if (selection.Request is null && selection.Response is null && selection.FirstEvent is null)
            {
                // 置き換わったファイルにセッションが無いとき、古いスレッドを残すと台帳へ返り続ける。
                if (read.Reset)
                {
                    request.Store.DeleteOtherThreads("codex", path, null);
                }

                return;
            }

            selection.NativeId = string.IsNullOrWhiteSpace(cursor.NativeId)
                ? Path.GetFileNameWithoutExtension(path)
                : cursor.NativeId;
        }

        var record = ToRecord("codex", path, selection, request.Config, request.Store, rebuild, seeded);
        request.Store.UpsertThread(record);
        if (read.Reset)
        {
            request.Store.DeleteOtherThreads("codex", path, record.NativeId);
        }

        cursor.NativeId = record.NativeId;
        cursor.Repository = record.Repository;
        cursor.Cwd = record.Cwd;
        cursor.LastEventUtc = record.LastEventUtc;
    }

    private static void SeedCodex(CodexSelection selection, ThreadRecord? stored)
    {
        if (stored is null)
        {
            return;
        }

        selection.NativeId = stored.NativeId;
        selection.Repository = stored.Repository;
        selection.Cwd = stored.Cwd;
        selection.Request = stored.Request;
        selection.Response = stored.Response;
        selection.FirstEvent = stored.FirstEventUtc;
        selection.LastEvent = stored.LastEventUtc;
        selection.SeedTurns(stored.Turns);
        selection.Signals.AddRange(stored.Signals);
        selection.ExtractedRefs.AddRange(stored.ExtractedRefs);
    }

    private static ThreadRecord ToRecord(
        string source,
        string path,
        CodexSelection selection,
        AppConfig config,
        StateStore store,
        bool rebuild,
        bool seeded)
    {
        var nativeId = selection.NativeId!;
        var previous = store.GetThread(source, nativeId);
        var authoritative = rebuild || seeded;
        var requestExcerpt = Cap(selection.Request, out var reqTrunc) ?? (authoritative ? null : previous?.Request);
        var responseExcerpt = Cap(selection.Response, out var respTrunc) ?? (authoritative ? null : previous?.Response);
        var turnsTrunc = false;
        var turns = selection.Turns.Count > 0
            ? CapTurns(selection.Turns, out turnsTrunc)
            : (authoritative ? [] : previous?.Turns ?? []);

        var signals = selection.Signals.Count > 0 || authoritative
            ? selection.Signals.ToList()
            : (previous?.Signals.ToList() ?? []);
        var refs = selection.ExtractedRefs.Count > 0 || authoritative
            ? selection.ExtractedRefs.ToList()
            : (previous?.ExtractedRefs.ToList() ?? []);
        var truncated = reqTrunc ||
            respTrunc ||
            turnsTrunc ||
            (requestExcerpt?.Truncated ?? false) ||
            (responseExcerpt?.Truncated ?? false) ||
            turns.Any(t => t.Truncated) ||
            (!authoritative && (previous?.Truncated ?? false));
        var fingerprint = Normalize.SelectionFingerprint(
            requestExcerpt,
            responseExcerpt,
            turns,
            signals,
            refs,
            config.InputMode,
            config.Model,
            config.ReasoningEffort);
        var same = previous is not null && previous.Fingerprint.Length > 0 && previous.Fingerprint == fingerprint;
        return new ThreadRecord
        {
            Source = source,
            NativeId = nativeId,
            FilePath = path,
            Repository = string.IsNullOrWhiteSpace(selection.Repository) ? previous?.Repository : selection.Repository,
            Cwd = string.IsNullOrWhiteSpace(selection.Cwd) ? previous?.Cwd : selection.Cwd,
            Request = requestExcerpt,
            Response = responseExcerpt,
            Turns = turns,
            Signals = signals,
            ExtractedRefs = refs,
            Truncated = truncated,
            FirstEventUtc = selection.FirstEvent ?? previous?.FirstEventUtc,
            LastEventUtc = selection.LastEvent ?? previous?.LastEventUtc,
            Fingerprint = fingerprint,
            Assessment = same ? previous!.Assessment : null,
            MatchedPath = same ? previous!.MatchedPath : null,
            MatchedWorkstream = same ? previous!.MatchedWorkstream : null,
            ReconcileFingerprint = same ? previous!.ReconcileFingerprint : null,
        };
    }

    private static SelectedExcerpt? Cap(SelectedExcerpt? excerpt, out bool truncated)
    {
        if (excerpt is null)
        {
            truncated = false;
            return null;
        }

        var text = Normalize.StoreBody(excerpt.Text, out truncated);
        return excerpt with { Text = text, Truncated = excerpt.Truncated || truncated };
    }

    private static IReadOnlyList<ThreadTurn> CapTurns(IReadOnlyList<ThreadTurn> turns, out bool anyTruncated)
    {
        anyTruncated = false;
        var list = new List<ThreadTurn>(turns.Count);
        foreach (var turn in turns)
        {
            var text = Normalize.StoreBody(turn.Text, out var truncated);
            if (truncated || turn.Truncated)
            {
                anyTruncated = true;
            }

            list.Add(turn with { Text = text, Truncated = turn.Truncated || truncated });
        }

        return list;
    }

    private static List<PreSelectionDiagnostic> BuildPreSelectionDiagnostics(
        RunRequest request,
        IReadOnlyList<ThreadRecord> threads,
        IReadOnlyList<IngestSkip> skips)
    {
        var list = new List<PreSelectionDiagnostic>();
        foreach (var skip in skips)
        {
            list.Add(new PreSelectionDiagnostic(
                skip.Path,
                skip.Path,
                "discovered",
                "log_read_error",
                skip.ToReport()));
        }

        foreach (var thread in threads)
        {
            var activityId = thread.Source + ":" + thread.NativeId;
            if (!thread.Overlaps(request.Window))
            {
                list.Add(new PreSelectionDiagnostic(
                    activityId,
                    thread.FilePath,
                    "discovered",
                    "out_of_window",
                    "イベント時刻が対象期間外のため除外。",
                    thread.FirstEventUtc,
                    thread.LastEventUtc));
                continue;
            }

            if (!thread.HasResponse)
            {
                list.Add(new PreSelectionDiagnostic(
                    activityId,
                    thread.FilePath,
                    "discovered",
                    "no_normal_response",
                    "通常のassistant応答が存在しないため除外。",
                    thread.FirstEventUtc,
                    thread.LastEventUtc));
                continue;
            }

            if (IsSelf(thread))
            {
                list.Add(new PreSelectionDiagnostic(
                    activityId,
                    thread.FilePath,
                    "discovered",
                    "self_invocation",
                    "StatusCheck自身の実行であるため除外。",
                    thread.FirstEventUtc,
                    thread.LastEventUtc));
            }
        }

        return list;
    }

    private static async Task<AnalysisPlan> PlanAsync(
        RunRequest request,
        IReadOnlyList<ThreadRecord> threads,
        IReadOnlyList<ProjectCard> cards,
        IReadOnlyList<IngestSkip> ingestSkips,
        List<ProcessingSkip> skipped,
        CancellationToken cancellationToken)
    {
        var mode = InputModes.NormalizeMode(request.Config.InputMode);
        var preSelection = BuildPreSelectionDiagnostics(request, threads, ingestSkips);
        var threadDiags = new ConcurrentDictionary<string, ThreadDiagnostic>(StringComparer.Ordinal);
        var holds = new List<HoldNote>();
        var reports = new List<ThreadView>();
        var eligible = threads.Where(thread => thread.Overlaps(request.Window) && thread.HasResponse && !IsSelf(thread)).ToList();

        foreach (var thread in eligible)
        {
            var id = thread.Source + ":" + thread.NativeId;
            var candPulls = Candidates(thread, mode);
            var candIssues = IssueCandidates(thread, mode);
            threadDiags[id] = new ThreadDiagnostic
            {
                ActivityId = id,
                FilePath = thread.FilePath,
                Repository = thread.Repository,
                Cwd = thread.Cwd,
                Stage = "selected",
                ReasonCode = thread.Truncated ? "body_truncated" : "selected",
                ReasonDetail = thread.Truncated ? "対象として選択（本文に上限切り詰めあり）。" : "対象として選択。",
                Truncated = thread.Truncated,
                HasTrailingUnansweredUserTurn = thread.HasTrailingUnansweredUserTurn,
                Request = thread.Request,
                Response = thread.Response,
                Turns = ThreadPrompts.SelectTurnsForPrompt(thread.Turns),
                Signals = thread.Signals,
                CandidatePullUrls = candPulls.Select(c => c.Url).ToList(),
                CandidateIssueUrls = candIssues.Select(c => c.Url).ToList(),
            };
        }

        var gate = new SemaphoreSlim(Math.Max(1, request.Config.MaxParallel));
        var skipGate = new object();
        var assessed = new ConcurrentBag<ThreadRecord>();
        var tasks = eligible.Select(async thread =>
        {
            var id = thread.Source + ":" + thread.NativeId;
            await gate.WaitAsync(cancellationToken);
            try
            {
                var diag = threadDiags[id];
                var updated = await AssessOneAsync(request, thread, diag, cancellationToken);
                assessed.Add(updated);
            }
            catch (StatusCheckException ex) when (!ex.Fatal)
            {
                if (threadDiags.TryGetValue(id, out var diag))
                {
                    diag.Stage = "assessed";
                    diag.ReasonCode = "assess_failed";
                    diag.ReasonDetail = ex.Message;
                }

                // 1スレッドの評価失敗で、他のスレッドの照合を止めない。
                RecordSkip(skipped, skipGate, id, ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        var byId = assessed.ToDictionary(thread => thread.Source + ":" + thread.NativeId, StringComparer.Ordinal);
        var groups = new Dictionary<string, (ProjectCard Card, List<ThreadRecord> Threads)>(StringComparer.Ordinal);
        foreach (var thread in byId.Values.OrderBy(thread => thread.NativeId, StringComparer.Ordinal))
        {
            var id = thread.Source + ":" + thread.NativeId;
            var selectedPulls = SelectedCandidates(thread, mode);
            var issueRefs = IssueCandidates(thread, mode);
            var match = MatchCard(thread, selectedPulls, issueRefs, cards, mode);
            if (threadDiags.TryGetValue(id, out var diag))
            {
                diag.MatchedCardPath = match.Card?.RelativePath;
                diag.MatchReasonCode = match.ReasonCode;
                diag.MatchReasonDetail = match.ReasonDetail;
                diag.CandidateCardPaths = match.CandidateCards;
                diag.Stage = match.Card is null ? "held" : "project_matched";
                diag.ReasonCode = match.ReasonCode;
                diag.ReasonDetail = match.ReasonDetail;
            }

            if (match.Card is null)
            {
                if (match.Hold is not null)
                {
                    holds.Add(match.Hold);
                }

                reports.Add(new ThreadView { Thread = thread, Assessment = thread.Assessment, Pulls = [] });
                continue;
            }

            if (!groups.TryGetValue(match.Card.RelativePath, out var group))
            {
                group = (match.Card, []);
            }

            group.Threads.Add(thread);
            groups[match.Card.RelativePath] = group;
        }

        var deferredRefreshIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tracked in await RefreshOutsideWindowAsync(request, threads, eligible, cards, skipped, skipGate, cancellationToken, deferredRefreshIds))
        {
            var id = tracked.Thread.Source + ":" + tracked.Thread.NativeId;
            if (!threadDiags.ContainsKey(id))
            {
                threadDiags[id] = new ThreadDiagnostic
                {
                    ActivityId = id,
                    FilePath = tracked.Thread.FilePath,
                    Repository = tracked.Thread.Repository,
                    Cwd = tracked.Thread.Cwd,
                    Stage = "project_matched",
                    ReasonCode = "matched_by_tracked_pr",
                    ReasonDetail = "期間外スレッドの追跡PRマージにより再照合対象に追加。",
                    Truncated = tracked.Thread.Truncated,
                    HasTrailingUnansweredUserTurn = tracked.Thread.HasTrailingUnansweredUserTurn,
                    AssessCacheHit = true,
                    Request = tracked.Thread.Request,
                    Response = tracked.Thread.Response,
                    Turns = ThreadPrompts.SelectTurnsForPrompt(tracked.Thread.Turns),
                    Signals = tracked.Thread.Signals,
                    Assessment = tracked.Thread.Assessment,
                    MatchedCardPath = tracked.Card.RelativePath,
                    MatchReasonCode = "matched_by_tracked_pr",
                    MatchReasonDetail = "期間外スレッドの追跡PRマージにより再照合対象に追加。",
                    CandidateCardPaths = [tracked.Card.RelativePath],
                };
            }

            if (groups.TryGetValue(tracked.Card.RelativePath, out var existing))
            {
                if (existing.Threads.All(thread => thread.Source != tracked.Thread.Source || thread.NativeId != tracked.Thread.NativeId))
                {
                    existing.Threads.Add(tracked.Thread);
                }

                continue;
            }

            if (tracked.Thread.Assessment is null)
            {
                continue;
            }

            groups[tracked.Card.RelativePath] = (tracked.Card, [tracked.Thread]);
        }

        var failedPulls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = await QueryPullsAsync(
            request,
            groups.Values.SelectMany(group => group.Threads.SelectMany(thread => SelectedCandidates(thread, mode).Select(reference => (reference, thread.Source + ":" + thread.NativeId)))),
            failedPulls,
            skipped,
            skipGate,
            deferredRefreshIds,
            cancellationToken);
        var changes = new List<LedgerChange>();
        var cardDiags = new List<CardDiagnostic>();
        foreach (var group in groups.Values.OrderBy(g => g.Card.RelativePath, StringComparer.Ordinal))
        {
            var views = group.Threads.Select(thread =>
            {
                var pulls = ChoosePulls(thread, snapshots, mode);
                var lookupFailed = SelectedCandidates(thread, mode).Any(candidate => failedPulls.Contains(candidate.Url));
                var id = thread.Source + ":" + thread.NativeId;
                if (threadDiags.TryGetValue(id, out var diag))
                {
                    diag.Pulls = pulls;
                    diag.PullLookupFailed = lookupFailed;
                }

                return new ThreadView
                {
                    Thread = thread,
                    Assessment = thread.Assessment,
                    Pulls = pulls,
                    PullLookupFailed = lookupFailed,
                };
            }).ToList();
            reports.AddRange(views);
            if (views.Any(view => view.Assessment is null))
            {
                continue;
            }

            var reconcilePrompt = ThreadPrompts.ReconcileUser(group.Card, views, mode);
            var hash = ReconcileHash(group.Card.Text, views, request.Config);
            if (views.All(view => view.Thread.ReconcileFingerprint == hash))
            {
                var referenceSuggestions = CrossCardReferenceSuggestions(group.Card, views, cards);
                cardDiags.Add(new CardDiagnostic
                {
                    RelativePath = group.Card.RelativePath,
                    ReconcileCacheHit = true,
                    ReconcileHash = hash,
                    Stage = "reconciled",
                    ReasonCode = "reconcile_cached",
                    ReasonDetail = "カード本文・評価結果・PR状態のfingerprintが前回と一致したためcacheを再利用。",
                    ReconcilePrompt = reconcilePrompt,
                    WorkstreamDiagnostics = referenceSuggestions,
                });
                PersistDeferredRefreshPulls(request.Store, views, snapshots, deferredRefreshIds);
                foreach (var view in views)
                {
                    if (threadDiags.TryGetValue(view.Thread.Source + ":" + view.Thread.NativeId, out var tDiag))
                    {
                        tDiag.Stage = "reconciled";
                        tDiag.ReasonCode = "reconcile_cached";
                        tDiag.ReasonDetail = "Reconcile cache再利用により変更なし。";
                    }
                }

                continue;
            }

            try
            {
                var updates = await ModelOf(request).ReconcileAsync(ThreadPrompts.ReconcileSystem, reconcilePrompt, cancellationToken);
                var change = LedgerPlanner.ApplyWithDiagnostics(group.Card, views, updates, out var wsDiags, out var unselectedIds);
                var referenceSuggestions = CrossCardReferenceSuggestions(group.Card, views, cards);
                if (change is not null)
                {
                    changes.Add(change);
                }

                Remember(request.Store, group.Card, views, change, request.Config);
                PersistDeferredRefreshPulls(request.Store, views, snapshots, deferredRefreshIds);
                var cardReasonCode = change is not null ? "proposed" : (wsDiags.FirstOrDefault()?.ReasonCode ?? "reconcile_no_updates");
                var cardReasonDetail = change is not null
                    ? "台帳変更案を作成した。"
                    : (wsDiags.FirstOrDefault()?.ReasonDetail ?? "モデルから変更対象のworkstream更新が返されなかった。");

                cardDiags.Add(new CardDiagnostic
                {
                    RelativePath = group.Card.RelativePath,
                    ReconcileCacheHit = false,
                    ReconcileHash = hash,
                    Stage = change is not null ? "proposed" : "guard_checked",
                    ReasonCode = cardReasonCode,
                    ReasonDetail = cardReasonDetail,
                    ReconcilePrompt = reconcilePrompt,
                    Updates = updates,
                    WorkstreamDiagnostics = wsDiags.Concat(referenceSuggestions).ToList(),
                    UnselectedReportIds = unselectedIds,
                });

                UpdateThreadDiagnosticsAfterReconcile(views, change, wsDiags, unselectedIds, threadDiags);
            }
            catch (StatusCheckException ex) when (!ex.Fatal)
            {
                cardDiags.Add(new CardDiagnostic
                {
                    RelativePath = group.Card.RelativePath,
                    ReconcileCacheHit = false,
                    ReconcileHash = hash,
                    Stage = "reconciled",
                    ReasonCode = "reconcile_failed",
                    ReasonDetail = ex.Message,
                    ReconcilePrompt = reconcilePrompt,
                    WorkstreamDiagnostics = CrossCardReferenceSuggestions(group.Card, views, cards),
                });
                // 1枚の照合失敗で、他のカードの更新を止めない。
                RecordSkip(skipped, skipGate, group.Card.RelativePath, ex.Message);
            }
        }

        var trace = new RunTrace
        {
            RunId = string.IsNullOrWhiteSpace(request.RunId) ? "run" : request.RunId!,
            InputMode = mode,
            Model = request.Config.Model,
            ReasoningEffort = request.Config.ReasoningEffort,
            PromptVersion = Normalize.PromptVersion,
            InputModelVersion = Normalize.InputModelVersion,
            WindowFromUtc = request.Window.FromUtc,
            WindowToUtcExclusive = request.Window.ToUtcExclusive,
            QueriedAtUtc = request.Now,
            PreSelection = preSelection,
            Threads = threadDiags.Values.OrderBy(t => t.ActivityId, StringComparer.Ordinal).ToList(),
            Cards = cardDiags,
        };

        return new AnalysisPlan(reports, holds, changes, trace);
    }

    private static void UpdateThreadDiagnosticsAfterReconcile(
        IReadOnlyList<ThreadView> views,
        LedgerChange? change,
        IReadOnlyList<WorkstreamDiagnostic> wsDiags,
        IReadOnlyList<string> unselectedIds,
        ConcurrentDictionary<string, ThreadDiagnostic> threadDiags)
    {
        var unselectedSet = unselectedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var view in views)
        {
            var id = view.Thread.Source + ":" + view.Thread.NativeId;
            if (!threadDiags.TryGetValue(id, out var diag))
            {
                continue;
            }

            if (unselectedSet.Contains(id))
            {
                diag.Stage = "reconciled";
                diag.ReasonCode = "reconcile_report_unselected_no_reason";
                diag.ReasonDetail = "未選択・理由未提供（ReconcileのreportIdsに含まれなかった）。";
                continue;
            }

            var cited = wsDiags.FirstOrDefault(ws => ws.ReportIds.Contains(id, StringComparer.Ordinal));
            if (cited is not null)
            {
                diag.Stage = cited.Stage;
                diag.ReasonCode = cited.ReasonCode;
                diag.ReasonDetail = cited.ReasonDetail;
            }
            else if (change is not null)
            {
                diag.Stage = "proposed";
                diag.ReasonCode = "proposed";
                diag.ReasonDetail = "台帳更新案に含まれた。";
            }
        }
    }

    private static void FinalizeTrace(
        RunTrace trace,
        bool applyLedger,
        IReadOnlyList<LedgerChange> staged,
        IReadOnlyList<string> appliedCards,
        string? commitSha,
        string? stopReason)
    {
        var appliedSet = appliedCards.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stagedSet = staged.Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var card in trace.Cards)
        {
            if (!stagedSet.Contains(card.RelativePath))
            {
                card.ProposalStatus = "none";
                continue;
            }

            if (stopReason is not null)
            {
                card.Stage = "guard_checked";
                card.ReasonCode = "apply_validation_failed";
                card.ReasonDetail = stopReason;
                card.ProposalStatus = "apply_validation_failed";
            }
            else if (applyLedger && appliedSet.Contains(card.RelativePath))
            {
                card.Stage = "applied";
                card.ReasonCode = "applied";
                card.ReasonDetail = "台帳へコミット適用済み。";
                card.ProposalStatus = "applied";
                card.CommitSha = commitSha;
                card.WorkstreamDiagnostics = card.WorkstreamDiagnostics
                    .Select(ws => ws.Stage == "proposed" ? ws with { Stage = "applied", Applied = true } : ws)
                    .ToList();
            }
            else
            {
                card.Stage = "proposed";
                card.ReasonCode = "proposed_preview_only";
                card.ReasonDetail = "previewのため提案のみ作成し台帳未変更。";
                card.ProposalStatus = "proposed_preview_only";
            }
        }

        foreach (var thread in trace.Threads)
        {
            if (thread.MatchedCardPath is null || !stagedSet.Contains(thread.MatchedCardPath) || thread.Stage != "proposed")
            {
                continue;
            }

            if (stopReason is not null)
            {
                thread.ReasonCode = "apply_validation_failed";
                thread.ReasonDetail = stopReason;
            }
            else if (applyLedger && appliedSet.Contains(thread.MatchedCardPath))
            {
                thread.Stage = "applied";
                thread.ReasonCode = "applied";
                thread.ReasonDetail = "台帳へコミット適用済み。";
            }
            else
            {
                thread.Stage = "proposed";
                thread.ReasonCode = "proposed_preview_only";
                thread.ReasonDetail = "previewのため提案のみ作成。";
            }
        }
    }

    private static async Task<ThreadRecord> AssessOneAsync(
        RunRequest request,
        ThreadRecord thread,
        ThreadDiagnostic diag,
        CancellationToken cancellationToken)
    {
        var mode = InputModes.NormalizeMode(request.Config.InputMode);
        var candidates = Candidates(thread, mode);
        var systemPrompt = ThreadPrompts.GetAssessSystem(mode);
        var userPrompt = ThreadPrompts.AssessUser(thread, candidates, mode);
        diag.AssessPrompt = userPrompt;

        if (thread.Assessment is not null)
        {
            diag.AssessCacheHit = true;
            diag.Assessment = thread.Assessment;
            diag.Stage = "assessed";
            diag.ReasonCode = "assess_cached";
            diag.ReasonDetail = "前回と同じfingerprintのためAssessment cacheを再利用。";
            return thread;
        }

        var assessment = await ModelOf(request).AssessAsync(systemPrompt, userPrompt, cancellationToken);
        var allowed = candidates.Select(candidate => candidate.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = assessment.SelectedPullUrls.Where(url => allowed.Contains(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count == 0 && candidates.Count == 1)
        {
            if (!InputModes.UseImprovedUrlExtraction(mode) ||
                (thread.Response is not null && thread.Response.Text.Contains(candidates[0].Url, StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(candidates[0].Url);
            }
        }

        var (remaining, sourceId, quoteVerified, unverified) = VerifyRemainingWork(thread, assessment, mode);
        var confirmed = new ThreadAssessment
        {
            Summary = assessment.Summary,
            Purpose = assessment.Purpose,
            CurrentPosition = assessment.CurrentPosition,
            ExplicitRemainingWork = remaining,
            RemainingWorkSourceId = sourceId,
            RemainingWorkQuoteVerified = quoteVerified,
            RemainingWorkUnverified = unverified,
            NextAction = assessment.NextAction,
            Uncertainty = assessment.Uncertainty,
            SelectedPullUrls = selected,
        };
        diag.AssessCacheHit = false;
        diag.Assessment = confirmed;
        diag.Stage = "assessed";
        diag.ReasonCode = unverified ? "remaining_quote_unverified" : "assess_succeeded";
        diag.ReasonDetail = unverified
            ? "Assessmentは完了したが、モデルが返したexplicitRemainingWorkの引用元を本文で確認できないため判断不能として記録。"
            : "Assessment完了。";

        var saved = Copy(thread, confirmed);
        request.Store.UpsertThread(saved);
        return saved;
    }

    internal static (string Remaining, string SourceId, bool QuoteVerified, bool Unverified) VerifyRemainingWork(
        ThreadRecord thread,
        ThreadAssessment rawAssessment,
        string? inputMode)
    {
        var mode = InputModes.NormalizeMode(inputMode);
        var remaining = rawAssessment.ExplicitRemainingWork.Trim();
        if (remaining.Length == 0)
        {
            return ("", "", true, false);
        }

        if (!InputModes.UseChronologicalTurns(mode))
        {
            if (thread.Response is not null && thread.Response.Text.Contains(remaining, StringComparison.Ordinal))
            {
                return (remaining, "response", true, false);
            }

            if (mode == InputModes.A)
            {
                return ("", "", false, false);
            }

            return ("", rawAssessment.RemainingWorkSourceId, false, true);
        }

        var wantedId = rawAssessment.RemainingWorkSourceId.Trim();
        var lastAssistantOrdinal = thread.Turns.LastOrDefault(t => t.Role == "assistant")?.Ordinal;
        if (!string.IsNullOrWhiteSpace(wantedId))
        {
            var turn = thread.Turns.FirstOrDefault(t => string.Equals(t.TurnId, wantedId, StringComparison.OrdinalIgnoreCase));
            if (turn is not null && turn.Text.Contains(remaining, StringComparison.Ordinal))
            {
                if (turn.Role == "assistant" && turn.Ordinal == lastAssistantOrdinal && IsUsageTipAfterCompletion(thread, remaining))
                {
                    return ("", "", true, false);
                }

                if (!IsResolvedInLaterTurn(thread.Turns, turn.Ordinal, remaining))
                {
                    return (remaining, turn.TurnId, true, false);
                }

                return ("", "", true, false);
            }

            if (InputModes.UseSignalExcerpts(mode))
            {
                var sig = thread.Signals.FirstOrDefault(s => string.Equals(s.SignalId, wantedId, StringComparison.OrdinalIgnoreCase));
                if (sig is not null && sig.Text.Contains(remaining, StringComparison.Ordinal))
                {
                    if (!IsResolvedInLaterTurn(thread.Turns, sig.Ordinal, remaining))
                    {
                        return (remaining, sig.SignalId, true, false);
                    }

                    return ("", "", true, false);
                }
            }
        }

        for (var i = thread.Turns.Count - 1; i >= 0; i--)
        {
            var turn = thread.Turns[i];
            if (turn.Text.Contains(remaining, StringComparison.Ordinal))
            {
                if (turn.Role == "assistant" && turn.Ordinal == lastAssistantOrdinal && IsUsageTipAfterCompletion(thread, remaining))
                {
                    return ("", "", true, false);
                }

                if (!IsResolvedInLaterTurn(thread.Turns, turn.Ordinal, remaining))
                {
                    return (remaining, turn.TurnId, true, false);
                }

                return ("", "", true, false);
            }
        }

        if (thread.Response is not null && thread.Response.Text.Contains(remaining, StringComparison.Ordinal))
        {
            if (IsUsageTipAfterCompletion(thread, remaining))
            {
                return ("", "", true, false);
            }

            return (remaining, "response", true, false);
        }

        if (thread.Request is not null && thread.Request.Text.Contains(remaining, StringComparison.Ordinal))
        {
            if (!IsResolvedInLaterTurn(thread.Turns, thread.Request.Ordinal, remaining))
            {
                return (remaining, "request", true, false);
            }

            return ("", "", true, false);
        }

        if (InputModes.UseSignalExcerpts(mode))
        {
            for (var i = thread.Signals.Count - 1; i >= 0; i--)
            {
                var sig = thread.Signals[i];
                if (sig.Text.Contains(remaining, StringComparison.Ordinal))
                {
                    if (!IsResolvedInLaterTurn(thread.Turns, sig.Ordinal, remaining))
                    {
                        return (remaining, sig.SignalId, true, false);
                    }

                    return ("", "", true, false);
                }
            }
        }

        // モデルが残作業を返したが、どの選択済みターン・シグナルにも逐語一致しない場合は黙って「残作業なし」とせず判断不能フラグを立てる。
        return ("", wantedId, false, true);
    }

    /// <summary>
    /// 中間ターンで本来の作業が完了報告された後、別件の使い方質問に対する一般的な案内文（「今後の参考として〜」「--option を指定できる」等）を
    /// 残作業として誤抽出した場合を抑止する。
    /// </summary>
    private static bool IsUsageTipAfterCompletion(ThreadRecord thread, string remaining)
    {
        if (thread.Turns.Count < 4)
        {
            return false;
        }

        string[] remainingObligationTokens =
        [
            "残作業", "残件", "未対応", "未完了", "阻害",
            "本番", "デプロイ", "マイグレーション", "リリース",
            "反映して", "適用して", "修正して", "更新して", "作成して", "追加して", "削除して", "必要",
        ];
        if (remainingObligationTokens.Any(token => remaining.Contains(token, StringComparison.Ordinal)))
        {
            return false;
        }

        var earlierCompleted = thread.Turns.Take(thread.Turns.Count - 2).Any(t =>
            t.Role == "assistant" &&
            (t.Text.Contains("完了しました", StringComparison.Ordinal) ||
             t.Text.Contains("完了した", StringComparison.Ordinal) ||
             t.Text.Contains("完了し", StringComparison.Ordinal) ||
             t.Text.Contains("マージしました", StringComparison.Ordinal) ||
             t.Text.Contains("マージした", StringComparison.Ordinal) ||
             t.Text.Contains("差し替えました", StringComparison.Ordinal) ||
             t.Text.Contains("反映しました", StringComparison.Ordinal)));
        if (!earlierCompleted)
        {
            return false;
        }

        var lastUser = thread.Turns.LastOrDefault(t => t.Role == "user");
        var isHowToQuestion = lastUser is not null &&
            (lastUser.Text.Contains("どうしたら", StringComparison.Ordinal) ||
             lastUser.Text.Contains("どのように", StringComparison.Ordinal) ||
             lastUser.Text.Contains("使い方", StringComparison.Ordinal) ||
             lastUser.Text.Contains("方法", StringComparison.Ordinal) ||
             lastUser.Text.Contains("オプション", StringComparison.Ordinal) ||
             lastUser.Text.Contains("コマンド", StringComparison.Ordinal) ||
             lastUser.Text.Contains("教えて", StringComparison.Ordinal));
        var isOptionalReferenceSentence =
            remaining.Contains("今後の参考として", StringComparison.Ordinal) ||
            remaining.Contains("試す場合", StringComparison.Ordinal) ||
            remaining.Contains("確認したい場合", StringComparison.Ordinal) ||
            remaining.Contains("--", StringComparison.Ordinal) ||
            ((remaining.EndsWith("できる。", StringComparison.Ordinal) || remaining.EndsWith("できます。", StringComparison.Ordinal)) &&
             (remaining.Contains("場合は", StringComparison.Ordinal) || remaining.Contains("には", StringComparison.Ordinal)));
        return isHowToQuestion && isOptionalReferenceSentence;
    }

    private static bool IsResolvedInLaterTurn(IReadOnlyList<ThreadTurn> turns, long sourceOrdinal, string remaining)
    {
        var subjectTokens = ExtractCardTokens(remaining)
            .Where(token => token is not ("残作業" or "残件" or "未対応" or "未完了" or "作業" or "対応"))
            .ToList();
        foreach (var later in turns.Where(t => t.Role == "assistant" && t.Ordinal > sourceOrdinal))
        {
            if (later.Text.Contains("残作業はありません", StringComparison.Ordinal) ||
                later.Text.Contains("残作業はなし", StringComparison.Ordinal))
            {
                return true;
            }

            if (subjectTokens.Count > 0 &&
                subjectTokens.All(token => later.Text.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
                (later.Text.Contains("完了", StringComparison.Ordinal) ||
                 later.Text.Contains("解消", StringComparison.Ordinal) ||
                 later.Text.Contains("反映済", StringComparison.Ordinal) ||
                 later.Text.Contains("適用済", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static void Remember(StateStore store, ProjectCard card, IReadOnlyList<ThreadView> views, LedgerChange? change, AppConfig config)
    {
        var basis = change?.NewText ?? card.Text;
        var hash = ReconcileHash(basis, views, config);
        foreach (var view in views)
        {
            if (view.Assessment is null)
            {
                continue;
            }

            var decision = change?.Decisions.FirstOrDefault(item => item.ActivityId == view.Thread.Source + ":" + view.Thread.NativeId);
            store.SaveAssessment(view.Thread.Source, view.Thread.NativeId, view.Assessment, card.RelativePath, decision?.Workstream ?? view.Thread.MatchedWorkstream, hash);
        }
    }

    private static async Task<IReadOnlyList<(ProjectCard Card, ThreadRecord Thread)>> RefreshOutsideWindowAsync(
        RunRequest request,
        IReadOnlyList<ThreadRecord> threads,
        IReadOnlyList<ThreadRecord> eligible,
        IReadOnlyList<ProjectCard> cards,
        List<ProcessingSkip> skipped,
        object skipGate,
        CancellationToken cancellationToken,
        HashSet<string> deferredRefreshIds)
    {
        var inside = eligible.Select(thread => thread.Source + ":" + thread.NativeId).ToHashSet(StringComparer.Ordinal);
        var extra = new List<(ProjectCard, ThreadRecord)>();
        foreach (var tracked in request.Store.ListUnmergedTracked())
        {
            PullRequestSnapshot fresh;
            try
            {
                fresh = await PullsOf(request).GetAsync(tracked.Snapshot.Owner, tracked.Snapshot.Repository, tracked.Snapshot.Number, cancellationToken);
            }
            catch (StatusCheckException ex) when (!ex.Fatal)
            {
                RecordSkip(skipped, skipGate, tracked.Snapshot.Url, ex.Message);
                continue;
            }

            if (fresh.State != PullRequestState.Merged)
            {
                request.Store.UpsertPull(fresh, tracked.ActivityId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(tracked.ActivityId) || inside.Contains(tracked.ActivityId))
            {
                continue;
            }

            var parts = tracked.ActivityId.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var thread = threads.FirstOrDefault(item => item.Source == parts[0] && item.NativeId == parts[1]);
            if (thread?.Assessment is null)
            {
                continue;
            }

            var card = cards.FirstOrDefault(item => item.RelativePath == thread.MatchedPath)
                ?? cards.FirstOrDefault(item => item.Text.Contains(fresh.Url, StringComparison.OrdinalIgnoreCase));
            if (card is null)
            {
                continue;
            }

            deferredRefreshIds.Add(tracked.ActivityId);
            extra.Add((card, thread));
        }

        return extra;
    }

    private static async Task<Dictionary<string, PullRequestSnapshot>> QueryPullsAsync(
        RunRequest request,
        IEnumerable<(GitHubRef Reference, string ActivityId)> pairs,
        HashSet<string> failedUrls,
        List<ProcessingSkip> skipped,
        object skipGate,
        IReadOnlySet<string> deferredRefreshIds,
        CancellationToken cancellationToken)
    {
        var snapshots = new Dictionary<string, PullRequestSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs.GroupBy(item => item.Reference.Url, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            if (snapshots.ContainsKey(pair.Reference.Url) || failedUrls.Contains(pair.Reference.Url))
            {
                continue;
            }

            try
            {
                var snapshot = await PullsOf(request).GetAsync(pair.Reference.Owner, pair.Reference.Repository, pair.Reference.Number, cancellationToken);
                snapshots[snapshot.Url] = snapshot;
                if (!deferredRefreshIds.Contains(pair.ActivityId)) request.Store.UpsertPull(snapshot, pair.ActivityId);
            }
            catch (StatusCheckException ex) when (!ex.Fatal)
            {
                // 存在しない PR や gh の失敗は、その URL だけ飛ばす。
                failedUrls.Add(pair.Reference.Url);
                RecordSkip(skipped, skipGate, pair.Reference.Url, ex.Message);
            }
        }

        return snapshots;
    }

    private static void PersistDeferredRefreshPulls(
        StateStore store,
        IReadOnlyList<ThreadView> views,
        IReadOnlyDictionary<string, PullRequestSnapshot> snapshots,
        IReadOnlySet<string> deferredRefreshIds)
    {
        foreach (var view in views)
        {
            var activityId = view.Thread.Source + ":" + view.Thread.NativeId;
            if (!deferredRefreshIds.Contains(activityId)) continue;
            foreach (var pull in view.Pulls)
            {
                if (snapshots.TryGetValue(pull.Url, out var snapshot)) store.UpsertPull(snapshot, activityId);
            }
        }
    }

    internal static IReadOnlyList<WorkstreamDiagnostic> CrossCardReferenceSuggestions(
        ProjectCard matchedCard,
        IReadOnlyList<ThreadView> reports,
        IReadOnlyList<ProjectCard> cards)
    {
        var suggestions = new List<WorkstreamDiagnostic>();
        foreach (var report in reports)
        {
            var text = string.Join("\n", new[] { report.Thread.Request?.Text, report.Thread.Response?.Text }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Concat(report.Thread.Turns.Select(turn => turn.Text))
                .Concat(report.Thread.Signals.Select(signal => signal.Text)));
            var explicitRefs = Normalize.GitHubRefs(text);
            var activityId = report.Thread.Source + ":" + report.Thread.NativeId;
            foreach (var other in cards.Where(card => card.RelativePath != matchedCard.RelativePath && card.Repositories.Count > 0))
            {
                // Only flag an explicit cross-repository URL already written in another card; no semantic matching or reassignment.
                if (other.Repositories.Any(repository => matchedCard.Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase))) continue;
                foreach (var reference in explicitRefs.Where(reference => other.Text.Contains(reference.Url, StringComparison.OrdinalIgnoreCase) &&
                             !other.Repositories.Contains(reference.Owner + "/" + reference.Repository, StringComparer.OrdinalIgnoreCase)))
                {
                    suggestions.Add(new WorkstreamDiagnostic(
                        "__cross_card_reference__", "suggestion", "candidate", "cross_card_explicit_reference_candidate",
                        "報告 " + activityId + " が明示した " + reference.Url + " は別repositoryのカード " + other.RelativePath + " にも明示参照されている。自動で再割当て・更新は行っていない。",
                        [activityId], Applied: false));
                }
            }
        }

        return suggestions.DistinctBy(item => item.ReasonDetail, StringComparer.Ordinal).ToList();
    }

    private static void RecordSkip(List<ProcessingSkip> skipped, object gate, string subject, string message)
    {
        lock (gate)
        {
            skipped.Add(new ProcessingSkip(subject, message));
        }
    }

    private static IActivityModel ModelOf(RunRequest request) =>
        request.Model ?? throw new StatusCheckException("A model is required.", fatal: true);

    private static IPullRequestSource PullsOf(RunRequest request) =>
        request.Pulls ?? throw new StatusCheckException("A pull request source is required.", fatal: true);

    private static (EstimateReport Report, RunTrace Trace) DescribeEstimate(
        RunRequest request,
        string runId,
        IReadOnlyList<ThreadRecord> threads,
        IReadOnlyList<IngestSkip> ingestSkips,
        List<ProcessingSkip> skipped)
    {
        var mode = InputModes.NormalizeMode(request.Config.InputMode);
        IReadOnlyList<ProjectCard> cards;
        try
        {
            cards = CardParser.LoadRepository(request.Config.LedgerRepository, skipped);
        }
        catch (StatusCheckException)
        {
            cards = [];
        }

        var preSelection = BuildPreSelectionDiagnostics(request, threads, ingestSkips);
        var threadDiags = new List<ThreadDiagnostic>();
        var overlapping = threads.Where(thread => thread.Overlaps(request.Window)).ToList();
        var prompts = new List<(string Stage, string System, string User)>();
        var groups = new Dictionary<string, (ProjectCard Card, List<ThreadRecord> Threads)>(StringComparer.Ordinal);
        foreach (var thread in overlapping.Where(thread => thread.HasResponse && !IsSelf(thread)))
        {
            var candidates = Candidates(thread, mode);
            var issueRefs = IssueCandidates(thread, mode);
            var assessPrompt = ThreadPrompts.AssessUser(thread, candidates, mode);
            if (thread.Assessment is null)
            {
                prompts.Add(("assess", ThreadPrompts.GetAssessSystem(mode), assessPrompt));
            }

            var match = MatchCard(thread, SelectedCandidates(thread, mode), issueRefs, cards, mode);
            var diag = new ThreadDiagnostic
            {
                ActivityId = thread.Source + ":" + thread.NativeId,
                FilePath = thread.FilePath,
                Repository = thread.Repository,
                Cwd = thread.Cwd,
                Stage = match.Card is null ? "held" : "project_matched",
                ReasonCode = match.ReasonCode,
                ReasonDetail = match.ReasonDetail,
                Truncated = thread.Truncated,
                HasTrailingUnansweredUserTurn = thread.HasTrailingUnansweredUserTurn,
                AssessCacheHit = thread.Assessment is not null,
                Request = thread.Request,
                Response = thread.Response,
                Turns = ThreadPrompts.SelectTurnsForPrompt(thread.Turns),
                Signals = thread.Signals,
                CandidatePullUrls = candidates.Select(c => c.Url).ToList(),
                CandidateIssueUrls = issueRefs.Select(c => c.Url).ToList(),
                AssessPrompt = assessPrompt,
                Assessment = thread.Assessment,
                MatchedCardPath = match.Card?.RelativePath,
                MatchReasonCode = match.ReasonCode,
                MatchReasonDetail = match.ReasonDetail,
                CandidateCardPaths = match.CandidateCards,
            };
            threadDiags.Add(diag);

            if (match.Card is null)
            {
                continue;
            }

            if (!groups.TryGetValue(match.Card.RelativePath, out var group))
            {
                group = (match.Card, []);
            }

            group.Threads.Add(thread);
            groups[match.Card.RelativePath] = group;
        }

        foreach (var group in groups.Values)
        {
            var views = group.Threads.Select(thread => new ThreadView
            {
                Thread = thread,
                Assessment = thread.Assessment,
                Pulls = SelectedCandidates(thread, mode).Select(candidate => new PullRequestSnapshot(candidate.Url, candidate.Owner, candidate.Repository, candidate.Number, PullRequestState.Open, null, null, "")).ToList(),
            }).ToList();
            var hash = ReconcileHash(group.Card.Text, views, request.Config);
            if (views.All(view => view.Assessment is not null && view.Thread.ReconcileFingerprint == hash))
            {
                continue;
            }

            prompts.Add(("reconcile", ThreadPrompts.ReconcileSystem, ThreadPrompts.ReconcileUser(group.Card, views, mode)));
        }

        var payloadChars = prompts.Sum(prompt => prompt.System.Length + prompt.User.Length);
        var report = new EstimateReport(
            overlapping.Count,
            overlapping.Count(thread => thread.HasResponse),
            overlapping.Count(thread => !thread.HasResponse),
            overlapping.Sum(thread => thread.Request?.Text.Length ?? 0),
            overlapping.Sum(thread => thread.Response?.Text.Length ?? 0),
            prompts.Count(prompt => prompt.Stage == "assess"),
            prompts.Count(prompt => prompt.Stage == "reconcile"),
            payloadChars,
            prompts.Sum(prompt => Pricing.EstimateInputTokens(prompt.System.Length + prompt.User.Length)),
            overlapping,
            prompts.Any(prompt => prompt.User.Contains("tool_call_update", StringComparison.Ordinal)));
        var trace = new RunTrace
        {
            RunId = runId,
            InputMode = mode,
            Model = request.Config.Model,
            ReasoningEffort = request.Config.ReasoningEffort,
            PromptVersion = Normalize.PromptVersion,
            InputModelVersion = Normalize.InputModelVersion,
            WindowFromUtc = request.Window.FromUtc,
            WindowToUtcExclusive = request.Window.ToUtcExclusive,
            QueriedAtUtc = request.Now,
            PreSelection = preSelection,
            Threads = threadDiags,
            Cards = [],
        };
        return (report, trace);
    }

    internal static CardMatchResult MatchCard(
        ThreadRecord thread,
        IReadOnlyList<GitHubRef> candidates,
        IReadOnlyList<GitHubRef> issueRefs,
        IReadOnlyList<ProjectCard> cards,
        string? inputMode = null)
    {
        var mode = InputModes.NormalizeMode(inputMode);
        var activityId = thread.Source + ":" + thread.NativeId;

        if (InputModes.UseImprovedCardMatching(mode) && !string.IsNullOrWhiteSpace(thread.NativeId) && thread.NativeId.Length >= 8)
        {
            var threadRef = "codex://threads/" + thread.NativeId;
            var threadCards = cards.Where(card => card.Text.Contains(threadRef, StringComparison.OrdinalIgnoreCase)).ToList();
            if (threadCards.Count == 1)
            {
                return new CardMatchResult(threadCards[0], null, "matched_by_thread_ref", "カード本文のスレッド参照に一致した。", [threadCards[0].RelativePath]);
            }

            if (threadCards.Count > 1)
            {
                var paths = threadCards.Select(c => c.RelativePath).ToList();
                var reason = "スレッド参照が複数のProjectカードに現れている（" + string.Join(", ", paths) + "）。";
                return new CardMatchResult(null, new HoldNote { ActivityId = activityId, Reason = reason, ReasonCode = "hold_conflicting_urls", CandidateCards = paths }, "hold_conflicting_urls", reason, paths);
            }
        }

        var urls = candidates.Select(candidate => candidate.Url).ToList();
        var urlCards = cards.Where(card => urls.Any(url => card.Text.Contains(url, StringComparison.OrdinalIgnoreCase))).ToList();
        if (urlCards.Count == 1)
        {
            return new CardMatchResult(urlCards[0], null, "matched_by_url", "PRのURLがProjectカードに一致した。", [urlCards[0].RelativePath]);
        }

        if (urlCards.Count > 1)
        {
            var paths = urlCards.Select(c => c.RelativePath).ToList();
            var reason = "PRのURLが複数のProjectカードに現れている。";
            return new CardMatchResult(null, new HoldNote { ActivityId = activityId, Reason = reason, ReasonCode = "hold_conflicting_urls", CandidateCards = paths }, "hold_conflicting_urls", reason, paths);
        }

        if (!InputModes.UseImprovedCardMatching(mode))
        {
            if (!string.IsNullOrWhiteSpace(thread.Repository))
            {
                var repoCards = cards.Where(card => card.Repositories.Contains(thread.Repository, StringComparer.OrdinalIgnoreCase)).ToList();
                if (repoCards.Count == 1)
                {
                    return new CardMatchResult(repoCards[0], null, "matched_by_repo_unique", "repositoryが単一のProjectカードに一致した。", [repoCards[0].RelativePath]);
                }

                var paths = repoCards.Select(c => c.RelativePath).ToList();
                var code = repoCards.Count == 0 ? "hold_no_project_candidate" : "hold_ambiguous_project_candidates";
                var reason = "repositoryに一致するProjectカードを一意に決められない。";
                return new CardMatchResult(null, new HoldNote { ActivityId = activityId, Reason = reason, ReasonCode = code, CandidateCards = paths }, code, reason, paths);
            }

            return new CardMatchResult(null, new HoldNote { ActivityId = activityId, Reason = "repositoryもURLも一致しない。", ReasonCode = "hold_no_repository" }, "hold_no_repository", "repositoryもURLも一致しない。", []);
        }

        if (issueRefs.Count > 0)
        {
            var issueUrls = issueRefs.Select(i => i.Url).ToList();
            var issueCards = cards.Where(card => issueUrls.Any(url => card.Text.Contains(url, StringComparison.OrdinalIgnoreCase))).ToList();
            if (issueCards.Count == 1)
            {
                return new CardMatchResult(issueCards[0], null, "matched_by_card_context", "IssueのURLがProjectカード本文に一致した。", [issueCards[0].RelativePath]);
            }
        }

        var candidateRepos = new List<string>();
        if (!string.IsNullOrWhiteSpace(thread.Repository))
        {
            candidateRepos.Add(thread.Repository);
        }
        else
        {
            foreach (var pull in candidates)
            {
                var pullRepo = pull.Owner + "/" + pull.Repository;
                if (!candidateRepos.Contains(pullRepo, StringComparer.OrdinalIgnoreCase))
                {
                    candidateRepos.Add(pullRepo);
                }
            }

            foreach (var issue in issueRefs)
            {
                var issueRepo = issue.Owner + "/" + issue.Repository;
                if (!candidateRepos.Contains(issueRepo, StringComparer.OrdinalIgnoreCase))
                {
                    candidateRepos.Add(issueRepo);
                }
            }
        }

        if (candidateRepos.Count > 0)
        {
            var repoCards = !string.IsNullOrWhiteSpace(thread.Repository)
                ? cards.Where(card => card.Repositories.Contains(thread.Repository, StringComparer.OrdinalIgnoreCase)).ToList()
                : cards.Where(card => card.Repositories.Any(r => candidateRepos.Contains(r, StringComparer.OrdinalIgnoreCase))).ToList();

            if (repoCards.Count == 1)
            {
                return new CardMatchResult(repoCards[0], null, "matched_by_repo_unique", "repositoryが単一のProjectカードに一致した。", [repoCards[0].RelativePath]);
            }

            if (repoCards.Count == 0)
            {
                var repoLabel = thread.Repository ?? string.Join(", ", candidateRepos);
                var reason = "repositoryに一致するProjectカードを一意に決められない（候補0件: " + repoLabel + "）。";
                return new CardMatchResult(
                    null,
                    new HoldNote { ActivityId = activityId, Reason = reason, ReasonCode = "hold_no_project_candidate", CandidateCards = [] },
                    "hold_no_project_candidate",
                    reason,
                    []);
            }

            return DisambiguateRepoCards(activityId, thread, issueRefs, repoCards);
        }

        return new CardMatchResult(
            null,
            new HoldNote { ActivityId = activityId, Reason = "repositoryもURLも一致しない。", ReasonCode = "hold_no_repository" },
            "hold_no_repository",
            "repositoryもURLも一致しない。",
            []);
    }

    private static CardMatchResult DisambiguateRepoCards(
        string activityId,
        ThreadRecord thread,
        IReadOnlyList<GitHubRef> issueRefs,
        IReadOnlyList<ProjectCard> repoCards)
    {
        var paths = repoCards.Select(c => c.RelativePath).ToList();
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine(thread.Request?.Text ?? "");
        contextBuilder.AppendLine(thread.Response?.Text ?? "");
        if (thread.Assessment is not null)
        {
            contextBuilder.AppendLine(thread.Assessment.Summary);
            contextBuilder.AppendLine(thread.Assessment.Purpose);
            contextBuilder.AppendLine(thread.Assessment.CurrentPosition);
        }

        foreach (var turn in thread.Turns)
        {
            contextBuilder.AppendLine(turn.Text);
        }

        var threadText = contextBuilder.ToString();
        var primaryTokensByCard = repoCards.ToDictionary(
            card => card.RelativePath,
            card => ExtractCardTokens((card.H1 ?? "") + "\n" + (card.Keywords ?? "")),
            StringComparer.Ordinal);
        var wsTokensByCard = repoCards.ToDictionary(
            card => card.RelativePath,
            card => ExtractCardTokens(string.Join("\n", card.Workstreams.Select(w => w.Heading))),
            StringComparer.Ordinal);
        var bodyTokensByCard = repoCards.ToDictionary(
            card => card.RelativePath,
            card => ExtractCardTokens(card.Purpose + "\n" + card.Current + "\n" + string.Join("\n", card.Workstreams.Select(w => w.Current))),
            StringComparer.Ordinal);

        var scores = new List<(ProjectCard Card, int Score, List<string> MatchedSignals)>();
        foreach (var card in repoCards)
        {
            var score = 0;
            var signals = new List<string>();

            foreach (var issue in issueRefs)
            {
                var numStr = issue.Number.ToString(CultureInfo.InvariantCulture);
                if (card.Text.Contains("Issue #" + numStr, StringComparison.OrdinalIgnoreCase) ||
                    card.Text.Contains("/issues/" + numStr, StringComparison.OrdinalIgnoreCase) ||
                    card.Workstreams.Any(w => w.Heading.Contains("#" + numStr, StringComparison.Ordinal)))
                {
                    score += 10;
                    signals.Add("Issue#" + numStr);
                }
            }

            foreach (var token in primaryTokensByCard[card.RelativePath])
            {
                if (repoCards.Count(other => primaryTokensByCard[other.RelativePath].Contains(token)) == 1 &&
                    threadText.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 4;
                    signals.Add("kw:" + token);
                }
            }

            foreach (var token in wsTokensByCard[card.RelativePath])
            {
                if (repoCards.Count(other => wsTokensByCard[other.RelativePath].Contains(token)) == 1 &&
                    threadText.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                    signals.Add("ws:" + token);
                }
            }

            var bodyBonus = 0;
            foreach (var token in bodyTokensByCard[card.RelativePath])
            {
                if (token.Length >= 4 &&
                    repoCards.Count(other => bodyTokensByCard[other.RelativePath].Contains(token)) == 1 &&
                    threadText.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    bodyBonus += 2;
                    signals.Add("ctx:" + token);
                    if (bodyBonus >= 4)
                    {
                        break;
                    }
                }
            }

            score += bodyBonus;
            scores.Add((card, score, signals));
        }

        var ordered = scores.OrderByDescending(item => item.Score).ToList();
        if (repoCards.Count == 1)
        {
            return new CardMatchResult(repoCards[0], null, "matched_by_repository",
                "同一repositoryに一致するProjectカードが1枚だけのため照合した。", paths);
        }

        if (ordered[0].Score >= 3 && ordered[0].Score >= ordered[1].Score + 2)
        {
            return new CardMatchResult(
                ordered[0].Card,
                null,
                "matched_by_card_context",
                "カード文脈照合により特定（" + string.Join(", ", ordered[0].MatchedSignals.Take(4)) + "）。",
                paths);
        }

        var activeCards = repoCards.Where(c => c.Workstreams.Count > 0).ToList();
        if (activeCards.Count == 1)
        {
            var inactiveScore = scores.Where(s => s.Card.Workstreams.Count == 0).Max(s => s.Score);
            if (inactiveScore == 0)
            {
                return new CardMatchResult(
                    activeCards[0],
                    null,
                    "matched_by_card_context",
                    "同一repository内で進行中workstreamを持つ唯一のProjectカードに一致した。",
                    paths);
            }
        }

        var holdReason = "repositoryに一致するProjectカードを一意に決められない（複数候補: " + string.Join(", ", paths) + "）。";
        return new CardMatchResult(
            null,
            new HoldNote
            {
                ActivityId = activityId,
                Reason = holdReason,
                ReasonCode = "hold_ambiguous_project_candidates",
                CandidateCards = paths,
            },
            "hold_ambiguous_project_candidates",
            holdReason,
            paths);
    }

    private static HashSet<string> ExtractCardTokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return set;
        }

        foreach (Match match in CardTokenPattern().Matches(text))
        {
            var token = match.Value.Trim();
            if (token.Length < 3 || IsStopToken(token))
            {
                continue;
            }

            set.Add(token);
        }

        return set;
    }

    private static bool IsStopToken(string token) =>
        token is "Issue" or "issue" or "PR" or "main" or "git" or "GitHub" or "https" or "com" or "suusanex"
            or "プロジェクト" or "エージェント" or "システム" or "ツール" or "スクリプト" or "テスト" or "確認" or "対応" or "改善" or "作成" or "整理" or "実装" or "検討" or "運用" or "管理";

    /// <summary>
    /// 評価が PR を選んだ後は、その URL だけをカード照合と gh 照会に使う。
    /// 未評価のときは本文から集めた候補を返す。
    /// </summary>
    private static IReadOnlyList<GitHubRef> SelectedCandidates(ThreadRecord thread, string? inputMode = null)
    {
        var mode = InputModes.NormalizeMode(inputMode);
        var candidates = Candidates(thread, mode);
        if (thread.Assessment is null)
        {
            return candidates;
        }

        var selected = thread.Assessment.SelectedPullUrls;
        if (!InputModes.UseImprovedUrlExtraction(mode))
        {
            if (selected is null || selected.Count == 0)
            {
                return candidates;
            }

            var chosenLegacy = candidates.Where(candidate => selected.Contains(candidate.Url, StringComparer.OrdinalIgnoreCase)).ToList();
            return chosenLegacy.Count == 0 ? candidates : chosenLegacy;
        }

        if (selected is null || selected.Count == 0)
        {
            return [];
        }

        return candidates.Where(candidate => selected.Contains(candidate.Url, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    internal static IReadOnlyList<GitHubRef> Candidates(ThreadRecord thread, string? inputMode = null)
    {
        var mode = InputModes.NormalizeMode(inputMode);
        var improved = InputModes.UseImprovedUrlExtraction(mode);
        var text = (thread.Request?.Text ?? "") + "\n" + (thread.Response?.Text ?? "");
        var found = Normalize.PullCandidates(text, thread.Repository, explicitOnly: improved).ToList();
        var seen = found.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (improved)
        {
            if (InputModes.UseChronologicalTurns(mode))
            {
                foreach (var turn in thread.Turns)
                {
                    foreach (var pull in Normalize.PullCandidates(turn.Text, thread.Repository, explicitOnly: true))
                    {
                        if (seen.Add(pull.Url))
                        {
                            found.Add(pull);
                        }
                    }
                }
            }

            foreach (var extracted in thread.ExtractedRefs.Where(r => r.Ref.Kind == "pull"))
            {
                if (extracted.SourceKind is "tool" or "commentary" && !IsCompatibleToolRef(extracted.Ref, thread.Repository))
                {
                    continue;
                }

                if (seen.Add(extracted.Ref.Url))
                {
                    found.Add(extracted.Ref);
                }
            }
        }

        if (thread.Assessment is not null)
        {
            foreach (var url in thread.Assessment.SelectedPullUrls)
            {
                var parsed = Normalize.GitHubRefs(url).FirstOrDefault(reference => reference.Kind == "pull");
                if (parsed is not null && seen.Add(parsed.Url))
                {
                    found.Add(parsed);
                }
            }
        }

        return found;
    }

    internal static IReadOnlyList<GitHubRef> IssueCandidates(ThreadRecord thread, string? inputMode = null)
    {
        var mode = InputModes.NormalizeMode(inputMode);
        if (!InputModes.UseImprovedUrlExtraction(mode))
        {
            return [];
        }

        var text = (thread.Request?.Text ?? "") + "\n" + (thread.Response?.Text ?? "");
        var found = Normalize.IssueCandidates(text, thread.Repository).ToList();
        var seen = found.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (InputModes.UseChronologicalTurns(mode))
        {
            foreach (var turn in thread.Turns)
            {
                foreach (var issue in Normalize.IssueCandidates(turn.Text, thread.Repository))
                {
                    if (seen.Add(issue.Url))
                    {
                        found.Add(issue);
                    }
                }
            }
        }

        foreach (var extracted in thread.ExtractedRefs.Where(r => r.Ref.Kind == "issue"))
        {
            if (extracted.SourceKind is "tool" or "commentary" && !IsCompatibleToolRef(extracted.Ref, thread.Repository))
            {
                continue;
            }

            if (seen.Add(extracted.Ref.Url))
            {
                found.Add(extracted.Ref);
            }
        }

        return found;
    }

    private static bool IsCompatibleToolRef(GitHubRef reference, string? threadRepository) =>
        !string.IsNullOrWhiteSpace(threadRepository) &&
        string.Equals(threadRepository, reference.Owner + "/" + reference.Repository, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PullRequestSnapshot> ChoosePulls(ThreadRecord thread, IReadOnlyDictionary<string, PullRequestSnapshot> snapshots, string? inputMode = null)
    {
        var chosen = SelectedCandidates(thread, inputMode);
        var pulls = new List<PullRequestSnapshot>();
        foreach (var candidate in chosen)
        {
            if (snapshots.TryGetValue(candidate.Url, out var snapshot))
            {
                pulls.Add(snapshot);
            }
        }

        return pulls;
    }

    private static bool IsSelf(ThreadRecord thread) =>
        Normalize.IsSelfInvocation(thread.Request?.Text) || Normalize.IsSelfInvocation(thread.Response?.Text);

    private static string ReconcileHash(string cardText, IReadOnlyList<ThreadView> views, AppConfig config)
    {
        var mode = InputModes.NormalizeMode(config.InputMode);
        var body = Normalize.InputModelVersion.ToString(CultureInfo.InvariantCulture)
            + "\n" + Normalize.PromptVersion
            + "\n" + mode
            + "\n" + config.Model
            + "\n" + config.ReasoningEffort
            + "\n" + cardText
            + "\n" + string.Join(
                "\n",
                views.OrderBy(view => view.Thread.NativeId, StringComparer.Ordinal)
                    .Select(view => view.Thread.Fingerprint + "\n" + (view.Assessment?.ToJson() ?? "") + "\n" + (view.PullLookupFailed ? "failed" : "ok") + "\n" + string.Join(",", view.Pulls.OrderBy(pull => pull.Url, StringComparer.Ordinal).Select(pull => pull.Url + ":" + pull.State))));
        return Normalize.Sha256Hex(body);
    }

    private static ThreadRecord Copy(ThreadRecord thread, ThreadAssessment assessment) => new()
    {
        Source = thread.Source,
        NativeId = thread.NativeId,
        FilePath = thread.FilePath,
        Repository = thread.Repository,
        Cwd = thread.Cwd,
        Request = thread.Request,
        Response = thread.Response,
        Turns = thread.Turns,
        Signals = thread.Signals,
        ExtractedRefs = thread.ExtractedRefs,
        Truncated = thread.Truncated,
        FirstEventUtc = thread.FirstEventUtc,
        LastEventUtc = thread.LastEventUtc,
        Fingerprint = thread.Fingerprint,
        Assessment = assessment,
        MatchedPath = thread.MatchedPath,
        MatchedWorkstream = thread.MatchedWorkstream,
        ReconcileFingerprint = null,
    };

    private static List<LedgerChange> StampBlobs(string repository, IReadOnlyList<LedgerChange> changes)
    {
        var stamped = new List<LedgerChange>();
        foreach (var change in changes)
        {
            stamped.Add(new LedgerChange
            {
                RelativePath = change.RelativePath,
                BlobBefore = LedgerGit.Hash(repository, change.RelativePath),
                NewText = change.NewText,
                Decisions = change.Decisions,
                Pulls = change.Pulls,
            });
        }

        return stamped;
    }

    private static (List<string> AppliedPaths, string? CommitSha) CommitChangesWithSha(string repository, IReadOnlyList<LedgerChange> changes, StateStore store, string runId)
    {
        if (changes.Count == 0)
        {
            return ([], null);
        }

        LedgerGit.EnsureClean(repository);
        foreach (var change in changes)
        {
            var hash = LedgerGit.Hash(repository, change.RelativePath);
            if (!string.Equals(hash, change.BlobBefore, StringComparison.Ordinal))
            {
                throw new StatusCheckException("Ledger card changed after the proposal was created: " + change.RelativePath);
            }
        }

        foreach (var change in changes)
        {
            var path = Path.Combine(repository, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(path, change.NewText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var sha = LedgerGit.Commit(repository, changes.Select(change => change.RelativePath).ToList(), CommitMessage);
        foreach (var change in changes)
        {
            foreach (var decision in change.Decisions)
            {
                store.SaveDecision(decision.Key, runId, decision.RelativePath, decision.Workstream, decision.Action, decision.ActivityId, sha);
            }
        }

        return (changes.Select(change => change.RelativePath).ToList(), sha);
    }

    private static string WriteReport(RunRequest request, string runId, AnalysisPlan plan, string ledgerSummary, (int InputTokens, int OutputTokens, decimal Usd) usage, IReadOnlyList<IngestSkip> skips, IReadOnlyList<ProcessingSkip> recordSkips)
    {
        var directory = Path.Combine(request.Config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.md");
        var text = ReportWriter.Write(new ReportModel
        {
            Command = request.ApplyLedger ? "run" : "preview",
            Window = request.Window,
            QueriedAt = request.Now,
            InputMode = request.Config.InputMode,
            Threads = plan.Reports,
            Holds = plan.Holds,
            LedgerSummary = ledgerSummary,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            EstimatedUsd = usage.Usd,
            SkippedLogs = skips,
            SkippedRecords = recordSkips,
            Trace = plan.Trace,
        });
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteTrace(AppConfig config, string runId, RunTrace trace)
    {
        var directory = Path.Combine(config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "trace.json");
        File.WriteAllText(path, trace.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteApplicationReceipt(AppConfig config, string runId, IReadOnlyList<string> appliedPaths, string? commitSha)
    {
        var directory = Path.Combine(config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "application.json");
        var temporary = path + ".tmp";
        static string Quote(string value) => "\"" + System.Text.Json.JsonEncodedText.Encode(value).ToString() + "\"";
        var json = "{\n  \"runId\": " + Quote(runId)
            + ",\n  \"appliedAtUtc\": " + Quote(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            + ",\n  \"commitSha\": " + (commitSha is null ? "null" : Quote(commitSha))
            + ",\n  \"appliedPaths\": [" + string.Join(", ", appliedPaths.Select(Quote)) + "]\n}\n";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static string WriteEstimate(RunRequest request, string runId, EstimateReport estimate, IReadOnlyList<IngestSkip> skips, IReadOnlyList<ProcessingSkip> recordSkips)
    {
        var directory = Path.Combine(request.Config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.md");
        File.WriteAllText(path, ReportWriter.WriteEstimate(request.Window, request.Now, estimate, skips, recordSkips, request.Config.InputMode), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteProposal(AppConfig config, string runId, IReadOnlyList<LedgerChange> changes)
    {
        var directory = Path.Combine(config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var pulls = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            foreach (var pull in change.Pulls)
            {
                if (!seen.Add(pull.Url))
                {
                    continue;
                }

                JsonLists.AddObject(pulls, new JsonObject { ["url"] = pull.Url, ["state"] = pull.State });
            }
        }

        var changeArray = new JsonArray();
        foreach (var change in changes)
        {
            var decisions = new JsonArray();
            foreach (var decision in change.Decisions)
            {
                JsonLists.AddObject(decisions, new JsonObject
                {
                    ["key"] = decision.Key,
                    ["activityId"] = decision.ActivityId,
                    ["workstream"] = decision.Workstream,
                    ["action"] = decision.Action,
                    ["relativePath"] = decision.RelativePath,
                });
            }

            JsonLists.AddObject(changeArray, new JsonObject
            {
                ["relativePath"] = change.RelativePath,
                ["blobBefore"] = change.BlobBefore,
                ["newText"] = change.NewText,
                ["decisions"] = decisions,
            });
        }

        var path = Path.Combine(directory, "proposal.json");
        File.WriteAllText(path, new JsonObject { ["runId"] = runId, ["pulls"] = pulls, ["changes"] = changeArray }.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static StoredProposal ReadProposal(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new StatusCheckException("Proposal JSON is not an object.");
        var pulls = new List<ProposalPull>();
        if (root["pulls"] is JsonArray pullArray)
        {
            foreach (var node in pullArray)
            {
                if (node is JsonObject pull)
                {
                    pulls.Add(new ProposalPull(pull["url"]?.GetValue<string>() ?? "", pull["state"]?.GetValue<string>() ?? ""));
                }
            }
        }

        var changes = new List<LedgerChange>();
        if (root["changes"] is JsonArray changeArray)
        {
            foreach (var node in changeArray)
            {
                if (node is not JsonObject change)
                {
                    continue;
                }

                var decisions = new List<LedgerDecision>();
                if (change["decisions"] is JsonArray decisionArray)
                {
                    foreach (var decisionNode in decisionArray)
                    {
                        if (decisionNode is not JsonObject decision)
                        {
                            continue;
                        }

                        decisions.Add(new LedgerDecision
                        {
                            Key = decision["key"]?.GetValue<string>() ?? "",
                            ActivityId = decision["activityId"]?.GetValue<string>() ?? "",
                            Workstream = decision["workstream"]?.GetValue<string>() ?? "",
                            Action = decision["action"]?.GetValue<string>() ?? "",
                            RelativePath = decision["relativePath"]?.GetValue<string>() ?? "",
                        });
                    }
                }

                changes.Add(new LedgerChange
                {
                    RelativePath = change["relativePath"]?.GetValue<string>() ?? "",
                    BlobBefore = change["blobBefore"]?.GetValue<string>() ?? "",
                    NewText = change["newText"]?.GetValue<string>() ?? "",
                    Decisions = decisions,
                });
            }
        }

        return new StoredProposal(root["runId"]?.GetValue<string>() ?? "", pulls, changes);
    }

    private static string LedgerText(bool apply, IReadOnlyList<LedgerChange> staged, IReadOnlyList<string> applied, string? stop)
    {
        if (stop is not null)
        {
            return "台帳は書いていない。停止理由: " + stop;
        }

        if (!apply)
        {
            return staged.Count == 0 ? "変更はない。" : "preview のため台帳は変更していない。";
        }

        return applied.Count == 0 ? "変更はない。" : "適用したカード: " + string.Join("、", applied);
    }

    [GeneratedRegex(@"[A-Za-z0-9_.-]{3,}|[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex CardTokenPattern();
}

internal sealed record CardMatchResult(
    ProjectCard? Card,
    HoldNote? Hold,
    string ReasonCode,
    string ReasonDetail,
    IReadOnlyList<string> CandidateCards);

internal sealed class RunRequest
{
    public required AppConfig Config { get; init; }
    public required TimeWindow Window { get; init; }
    public required bool ApplyLedger { get; init; }
    public IActivityModel? Model { get; init; }
    public IPullRequestSource? Pulls { get; init; }
    public required StateStore Store { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public string? RunId { get; init; }
    public bool EstimateOnly { get; init; }
}

internal sealed class RunResult
{
    public required string RunId { get; init; }
    public required string ReportPath { get; init; }
    public string? TracePath { get; init; }
    public RunTrace? Trace { get; init; }
    public required IReadOnlyList<string> AppliedCards { get; init; }
    public string? ApplicationReceiptPath { get; init; }
    public string? StopReason { get; init; }
    public int ExitCode { get; init; }
    public IReadOnlyList<IngestSkip> SkippedLogs { get; init; } = [];
    public IReadOnlyList<ProcessingSkip> SkippedRecords { get; init; } = [];
    public int EstimatedInputTokens { get; init; }
    public int PlannedCalls { get; init; }
    public bool Estimated { get; init; }
}

internal sealed record ProposalPull(string Url, string State);

internal sealed record StoredProposal(string RunId, IReadOnlyList<ProposalPull> Pulls, IReadOnlyList<LedgerChange> Changes);

internal sealed record AnalysisPlan(IReadOnlyList<ThreadView> Reports, IReadOnlyList<HoldNote> Holds, IReadOnlyList<LedgerChange> Changes, RunTrace Trace);

internal sealed record EstimateReport(
    int Threads,
    int WithResponse,
    int WithoutResponse,
    int RequestChars,
    int ResponseChars,
    int AssessCalls,
    int ReconcileCalls,
    int PayloadChars,
    int InputTokens,
    IReadOnlyList<ThreadRecord> Selected,
    bool PayloadContainsToolUpdates);
