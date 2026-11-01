using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// 選択済みの依頼と最後の応答だけを解析し、台帳の差分があるときだけ更新する。
/// LLM と gh は呼び出し側が差し替える。estimate は API を呼ばない。
/// </summary>
internal static class StatusPipeline
{
    internal const string CommitMessage = "活動状況の照合結果をProjectカードへ反映する";

    internal static async Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken)
    {
        var runId = string.IsNullOrWhiteSpace(request.RunId) ? Guid.NewGuid().ToString("D") : request.RunId!;
        request.Store.InsertRun(runId, request.Window);
        string? reportPath = null;
        string? proposalPath = null;
        var skips = new List<IngestSkip>();
        try
        {
            skips.AddRange(Ingest(request));
            var threads = request.Store.ListThreads();
            if (request.EstimateOnly)
            {
                var estimate = DescribeEstimate(request, threads);
                reportPath = WriteEstimate(request, runId, estimate, skips);
                request.Store.FinishRun(runId, "succeeded", reportPath, null);
                return new RunResult
                {
                    RunId = runId,
                    ReportPath = reportPath,
                    AppliedCards = [],
                    ExitCode = 0,
                    SkippedLogs = skips,
                    EstimatedInputTokens = estimate.InputTokens,
                    PlannedCalls = estimate.AssessCalls + estimate.ReconcileCalls,
                    Estimated = true,
                };
            }

            var cards = CardParser.LoadRepository(request.Config.LedgerRepository);
            var plan = await PlanAsync(request, threads, cards, cancellationToken);
            var staged = StampBlobs(request.Config.LedgerRepository, plan.Changes);
            proposalPath = WriteProposal(request.Config, runId, staged);
            var applied = new List<string>();
            string? stop = null;
            var exit = 0;
            if (request.ApplyLedger && staged.Count > 0)
            {
                try
                {
                    applied.AddRange(CommitChanges(request.Config.LedgerRepository, staged, request.Store, runId));
                }
                catch (StatusCheckException ex)
                {
                    stop = ex.Message;
                    exit = 1;
                }
            }

            var usage = request.Store.UsageTotals(runId);
            reportPath = WriteReport(request, runId, plan, LedgerText(request.ApplyLedger, staged, applied, stop), usage, skips);
            request.Store.FinishRun(runId, exit == 0 ? "succeeded" : "failed", reportPath, proposalPath);
            return new RunResult
            {
                RunId = runId,
                ReportPath = reportPath,
                AppliedCards = applied,
                StopReason = stop,
                ExitCode = exit,
                SkippedLogs = skips,
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
                AppliedCards = [],
                StopReason = message,
                ExitCode = 1,
                SkippedLogs = skips,
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

        var applied = CommitChanges(config.LedgerRepository, proposal.Changes, store, runId);
        return new RunResult { RunId = runId, ReportPath = "", AppliedCards = applied, StopReason = null, ExitCode = 0 };
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
            var info = new FileInfo(path);
            var cursor = request.Store.GetCursor(path);
            var versionReset = cursor is null || cursor.ScanVersion < Normalize.InputModelVersion;
            if (!versionReset &&
                cursor is not null &&
                cursor.Size == info.Length &&
                cursor.ByteOffset == info.Length &&
                info.LastWriteTimeUtc < request.Window.FromUtc)
            {
                continue;
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
                return;
            }

            selection.NativeId = string.IsNullOrWhiteSpace(cursor.NativeId)
                ? Path.GetFileNameWithoutExtension(path)
                : cursor.NativeId;
        }

        var record = ToRecord("codex", path, selection.NativeId, selection.Repository, selection.Cwd, selection.Request, selection.Response, selection.FirstEvent, selection.LastEvent, request.Store, rebuild, seeded);
        request.Store.UpsertThread(record);
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
    }

    private static ThreadRecord ToRecord(
        string source,
        string path,
        string nativeId,
        string? repository,
        string? cwd,
        SelectedExcerpt? request,
        SelectedExcerpt? response,
        DateTimeOffset? first,
        DateTimeOffset? last,
        StateStore store,
        bool rebuild,
        bool seeded)
    {
        var previous = store.GetThread(source, nativeId);
        var authoritative = rebuild || seeded;
        var requestExcerpt = Cap(request) ?? (authoritative ? null : previous?.Request);
        var responseExcerpt = Cap(response) ?? (authoritative ? null : previous?.Response);
        var fingerprint = Normalize.SelectionFingerprint(requestExcerpt, responseExcerpt);
        var same = previous is not null && previous.Fingerprint.Length > 0 && previous.Fingerprint == fingerprint;
        return new ThreadRecord
        {
            Source = source,
            NativeId = nativeId,
            FilePath = path,
            Repository = string.IsNullOrWhiteSpace(repository) ? previous?.Repository : repository,
            Cwd = string.IsNullOrWhiteSpace(cwd) ? previous?.Cwd : cwd,
            Request = requestExcerpt,
            Response = responseExcerpt,
            FirstEventUtc = first ?? previous?.FirstEventUtc,
            LastEventUtc = last ?? previous?.LastEventUtc,
            Fingerprint = fingerprint,
            Assessment = same ? previous!.Assessment : null,
            MatchedPath = same ? previous!.MatchedPath : null,
            MatchedWorkstream = same ? previous!.MatchedWorkstream : null,
            ReconcileFingerprint = same ? previous!.ReconcileFingerprint : null,
        };
    }

    private static SelectedExcerpt? Cap(SelectedExcerpt? excerpt)
    {
        if (excerpt is null)
        {
            return null;
        }

        var text = Redaction.Apply(excerpt.Text).Trim();
        if (text.Length > Normalize.StoredBodyLimit)
        {
            text = text[..Normalize.StoredBodyLimit];
        }

        return excerpt with { Text = text };
    }

    private static async Task<AnalysisPlan> PlanAsync(RunRequest request, IReadOnlyList<ThreadRecord> threads, IReadOnlyList<ProjectCard> cards, CancellationToken cancellationToken)
    {
        var holds = new List<HoldNote>();
        var reports = new List<ThreadView>();
        var eligible = threads.Where(thread => thread.Overlaps(request.Window) && thread.HasResponse && !IsSelf(thread)).ToList();
        var gate = new SemaphoreSlim(Math.Max(1, request.Config.MaxParallel));
        var assessed = new System.Collections.Concurrent.ConcurrentBag<ThreadRecord>();
        var tasks = eligible.Select(async thread =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                assessed.Add(await AssessOneAsync(request, thread, cancellationToken));
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
            var candidates = Candidates(thread);
            var match = MatchCard(thread, candidates, cards);
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

        foreach (var tracked in await RefreshOutsideWindowAsync(request, threads, eligible, cards, cancellationToken))
        {
            if (groups.ContainsKey(tracked.Card.RelativePath))
            {
                continue;
            }

            if (tracked.Thread.Assessment is null || string.IsNullOrWhiteSpace(tracked.Thread.MatchedWorkstream))
            {
                continue;
            }

            groups[tracked.Card.RelativePath] = (tracked.Card, [tracked.Thread]);
        }

        var snapshots = await QueryPullsAsync(
            request,
            groups.Values.SelectMany(group => group.Threads.SelectMany(thread => Candidates(thread).Select(reference => (reference, thread.Source + ":" + thread.NativeId)))),
            cancellationToken);
        var changes = new List<LedgerChange>();
        foreach (var group in groups.Values)
        {
            var views = group.Threads.Select(thread => new ThreadView
            {
                Thread = thread,
                Assessment = thread.Assessment,
                Pulls = ChoosePulls(thread, snapshots),
            }).ToList();
            reports.AddRange(views);
            if (views.Any(view => view.Assessment is null))
            {
                continue;
            }

            if (TryFastComplete(group.Card, views, out var fast))
            {
                if (fast is not null)
                {
                    changes.Add(fast);
                }

                Remember(request.Store, group.Card, views, fast);
                continue;
            }

            var hash = ReconcileHash(group.Card.Text, views);
            if (views.All(view => view.Thread.ReconcileFingerprint == hash))
            {
                continue;
            }

            var updates = await ModelOf(request).ReconcileAsync(ThreadPrompts.ReconcileSystem, ThreadPrompts.ReconcileUser(group.Card, views), cancellationToken);
            var change = LedgerPlanner.Apply(group.Card, views, updates);
            if (change is not null)
            {
                changes.Add(change);
            }

            Remember(request.Store, group.Card, views, change);
        }

        return new AnalysisPlan(reports, holds, changes);
    }

    private static async Task<ThreadRecord> AssessOneAsync(RunRequest request, ThreadRecord thread, CancellationToken cancellationToken)
    {
        if (thread.Assessment is not null)
        {
            return thread;
        }

        var candidates = Candidates(thread);
        var assessment = await ModelOf(request).AssessAsync(ThreadPrompts.AssessSystem, ThreadPrompts.AssessUser(thread, candidates), cancellationToken);
        var allowed = candidates.Select(candidate => candidate.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = assessment.SelectedPullUrls.Where(url => allowed.Contains(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count == 0 && candidates.Count == 1)
        {
            selected.Add(candidates[0].Url);
        }

        var remaining = assessment.ExplicitRemainingWork.Trim();
        if (remaining.Length == 0 || thread.Response is null || !thread.Response.Text.Contains(remaining, StringComparison.Ordinal))
        {
            remaining = "";
        }

        var confirmed = new ThreadAssessment
        {
            Summary = assessment.Summary,
            Purpose = assessment.Purpose,
            CurrentPosition = assessment.CurrentPosition,
            ExplicitRemainingWork = remaining,
            NextAction = assessment.NextAction,
            Uncertainty = assessment.Uncertainty,
            SelectedPullUrls = selected,
        };
        var saved = Copy(thread, confirmed);
        request.Store.UpsertThread(saved);
        return saved;
    }

    private static bool TryFastComplete(ProjectCard card, IReadOnlyList<ThreadView> views, out LedgerChange? change)
    {
        change = null;
        if (views.Count != 1 || !LedgerPlanner.CanDelete(views))
        {
            return false;
        }

        var view = views[0];
        var heading = view.Thread.MatchedWorkstream;
        if (string.IsNullOrWhiteSpace(heading) || card.Workstreams.All(workstream => workstream.Heading != heading))
        {
            return false;
        }

        change = LedgerPlanner.Apply(card, views, [new LedgerFieldUpdate(heading, "delete", "", "", "", "")]);
        return true;
    }

    private static void Remember(StateStore store, ProjectCard card, IReadOnlyList<ThreadView> views, LedgerChange? change)
    {
        var basis = change?.NewText ?? card.Text;
        var hash = ReconcileHash(basis, views);
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
        CancellationToken cancellationToken)
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
            catch (StatusCheckException)
            {
                continue;
            }

            request.Store.UpsertPull(fresh, tracked.ActivityId);
            if (fresh.State != PullRequestState.Merged || string.IsNullOrWhiteSpace(tracked.ActivityId) || inside.Contains(tracked.ActivityId))
            {
                continue;
            }

            var parts = tracked.ActivityId.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var thread = threads.FirstOrDefault(item => item.Source == parts[0] && item.NativeId == parts[1]);
            if (thread?.Assessment is null || !string.IsNullOrWhiteSpace(thread.Assessment.ExplicitRemainingWork))
            {
                continue;
            }

            var card = cards.FirstOrDefault(item => item.RelativePath == thread.MatchedPath)
                ?? cards.FirstOrDefault(item => item.Text.Contains(fresh.Url, StringComparison.OrdinalIgnoreCase));
            if (card is null)
            {
                continue;
            }

            extra.Add((card, thread));
        }

        return extra;
    }

    private static async Task<Dictionary<string, PullRequestSnapshot>> QueryPullsAsync(
        RunRequest request,
        IEnumerable<(GitHubRef Reference, string ActivityId)> pairs,
        CancellationToken cancellationToken)
    {
        var snapshots = new Dictionary<string, PullRequestSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs.GroupBy(item => item.Reference.Url, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            if (snapshots.ContainsKey(pair.Reference.Url))
            {
                continue;
            }

            var snapshot = await PullsOf(request).GetAsync(pair.Reference.Owner, pair.Reference.Repository, pair.Reference.Number, cancellationToken);
            snapshots[snapshot.Url] = snapshot;
            request.Store.UpsertPull(snapshot, pair.ActivityId);
        }

        return snapshots;
    }

    private static IActivityModel ModelOf(RunRequest request) =>
        request.Model ?? throw new StatusCheckException("A model is required.");

    private static IPullRequestSource PullsOf(RunRequest request) =>
        request.Pulls ?? throw new StatusCheckException("A pull request source is required.");

    private static EstimateReport DescribeEstimate(RunRequest request, IReadOnlyList<ThreadRecord> threads)
    {
        IReadOnlyList<ProjectCard> cards;
        try
        {
            cards = CardParser.LoadRepository(request.Config.LedgerRepository);
        }
        catch (StatusCheckException)
        {
            cards = [];
        }

        var overlapping = threads.Where(thread => thread.Overlaps(request.Window)).ToList();
        var prompts = new List<(string Stage, string System, string User)>();
        var groups = new Dictionary<string, (ProjectCard Card, List<ThreadRecord> Threads)>(StringComparer.Ordinal);
        foreach (var thread in overlapping.Where(thread => thread.HasResponse && !IsSelf(thread)))
        {
            if (thread.Assessment is null)
            {
                prompts.Add(("assess", ThreadPrompts.AssessSystem, ThreadPrompts.AssessUser(thread, Candidates(thread))));
            }

            var match = MatchCard(thread, Candidates(thread), cards);
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
                Pulls = Candidates(thread).Select(candidate => new PullRequestSnapshot(candidate.Url, candidate.Owner, candidate.Repository, candidate.Number, PullRequestState.Open, null, null, "")).ToList(),
            }).ToList();
            var hash = ReconcileHash(group.Card.Text, views);
            if (views.All(view => view.Assessment is not null && view.Thread.ReconcileFingerprint == hash))
            {
                continue;
            }

            prompts.Add(("reconcile", ThreadPrompts.ReconcileSystem, ThreadPrompts.ReconcileUser(group.Card, views)));
        }

        var payloadChars = prompts.Sum(prompt => prompt.System.Length + prompt.User.Length);
        return new EstimateReport(
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
    }

    private static (ProjectCard? Card, HoldNote? Hold) MatchCard(ThreadRecord thread, IReadOnlyList<GitHubRef> candidates, IReadOnlyList<ProjectCard> cards)
    {
        var activityId = thread.Source + ":" + thread.NativeId;
        var urls = candidates.Select(candidate => candidate.Url).ToList();
        var urlCards = cards.Where(card => urls.Any(url => card.Text.Contains(url, StringComparison.OrdinalIgnoreCase))).ToList();
        if (urlCards.Count == 1)
        {
            return (urlCards[0], null);
        }

        if (urlCards.Count > 1)
        {
            return (null, new HoldNote { ActivityId = activityId, Reason = "PRのURLが複数のProjectカードに現れている。" });
        }

        if (!string.IsNullOrWhiteSpace(thread.Repository))
        {
            var repoCards = cards.Where(card => card.Repositories.Contains(thread.Repository, StringComparer.OrdinalIgnoreCase)).ToList();
            if (repoCards.Count == 1)
            {
                return (repoCards[0], null);
            }

            return (null, new HoldNote { ActivityId = activityId, Reason = "repositoryに一致するProjectカードを一意に決められない。" });
        }

        return (null, new HoldNote { ActivityId = activityId, Reason = "repositoryもURLも一致しない。" });
    }

    private static IReadOnlyList<GitHubRef> Candidates(ThreadRecord thread)
    {
        var text = (thread.Request?.Text ?? "") + "\n" + (thread.Response?.Text ?? "");
        var found = Normalize.PullCandidates(text, thread.Repository).ToList();
        if (thread.Assessment is not null)
        {
            foreach (var url in thread.Assessment.SelectedPullUrls)
            {
                var parsed = Normalize.GitHubRefs(url).FirstOrDefault(reference => reference.Kind == "pull");
                if (parsed is not null && found.All(item => !item.Url.Equals(parsed.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(parsed);
                }
            }
        }

        return found;
    }

    private static IReadOnlyList<PullRequestSnapshot> ChoosePulls(ThreadRecord thread, IReadOnlyDictionary<string, PullRequestSnapshot> snapshots)
    {
        var candidates = Candidates(thread);
        var selected = thread.Assessment?.SelectedPullUrls ?? [];
        var chosen = candidates.Where(candidate => selected.Contains(candidate.Url, StringComparer.OrdinalIgnoreCase)).ToList();
        if (chosen.Count == 0 && candidates.Count == 1)
        {
            chosen = [candidates[0]];
        }

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

    private static string ReconcileHash(string cardText, IReadOnlyList<ThreadView> views)
    {
        var body = cardText + "\n" + string.Join(
            "\n",
            views.OrderBy(view => view.Thread.NativeId, StringComparer.Ordinal)
                .Select(view => view.Thread.Fingerprint + "\n" + (view.Assessment?.ToJson() ?? "") + "\n" + string.Join(",", view.Pulls.OrderBy(pull => pull.Url, StringComparer.Ordinal).Select(pull => pull.Url + ":" + pull.State))));
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

    private static List<string> CommitChanges(string repository, IReadOnlyList<LedgerChange> changes, StateStore store, string runId)
    {
        if (changes.Count == 0)
        {
            return [];
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

        return changes.Select(change => change.RelativePath).ToList();
    }

    private static string WriteReport(RunRequest request, string runId, AnalysisPlan plan, string ledgerSummary, (int InputTokens, int OutputTokens, decimal Usd) usage, IReadOnlyList<IngestSkip> skips)
    {
        var directory = Path.Combine(request.Config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.md");
        var text = ReportWriter.Write(new ReportModel
        {
            Window = request.Window,
            QueriedAt = request.Now,
            Threads = plan.Reports,
            Holds = plan.Holds,
            LedgerSummary = ledgerSummary,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            EstimatedUsd = usage.Usd,
            SkippedLogs = skips,
        });
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteEstimate(RunRequest request, string runId, EstimateReport estimate, IReadOnlyList<IngestSkip> skips)
    {
        var directory = Path.Combine(request.Config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.md");
        File.WriteAllText(path, ReportWriter.WriteEstimate(request.Window, request.Now, estimate, skips), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
}

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
    public required IReadOnlyList<string> AppliedCards { get; init; }
    public string? StopReason { get; init; }
    public int ExitCode { get; init; }
    public IReadOnlyList<IngestSkip> SkippedLogs { get; init; } = [];
    public int EstimatedInputTokens { get; init; }
    public int PlannedCalls { get; init; }
    public bool Estimated { get; init; }
}

internal sealed record ProposalPull(string Url, string State);

internal sealed record StoredProposal(string RunId, IReadOnlyList<ProposalPull> Pulls, IReadOnlyList<LedgerChange> Changes);

internal sealed record AnalysisPlan(IReadOnlyList<ThreadView> Reports, IReadOnlyList<HoldNote> Holds, IReadOnlyList<LedgerChange> Changes);

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
