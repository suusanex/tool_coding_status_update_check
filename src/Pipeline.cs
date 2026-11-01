using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// 収集、活動化、PR照会、台帳提案を順に行う。LLMとghは呼び出し側が差し替える。
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
        try
        {
            Ingest(request);
            var packets = BuildPackets(request.Store, request.Window);
            var views = await AnalyzeAsync(request, packets, cancellationToken);
            await RefreshTrackedAsync(request, views, cancellationToken);
            var concentrations = await ConcentrateAsync(request, views, cancellationToken);
            IReadOnlyList<LedgerChange> changes = [];
            IReadOnlyList<HoldNote> holds = [];
            if (views.Count > 0)
            {
                var cards = CardParser.LoadRepository(request.Config.LedgerRepository);
                (changes, holds) = await LedgerPlanner.PlanAsync(cards, views, request.Store, request.Model, cancellationToken);
            }

            var staged = StampBlobs(request.Config.LedgerRepository, changes);
            proposalPath = WriteProposal(request.Config, runId, staged, views);
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
            reportPath = WriteReport(request, runId, views, holds, concentrations, LedgerText(request.ApplyLedger, staged, applied, stop), usage);
            request.Store.FinishRun(runId, exit == 0 ? "succeeded" : "failed", reportPath, proposalPath);
            return new RunResult
            {
                RunId = runId,
                ReportPath = reportPath,
                AppliedCards = applied,
                StopReason = stop,
                ExitCode = exit,
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

    private static void Ingest(RunRequest request)
    {
        IngestRoot(request, request.Config.CodexSessionsDir, "codex", searchAllDirectories: true);
        if (!string.IsNullOrWhiteSpace(request.Config.CodexArchivedSessionsDir))
        {
            IngestRoot(request, request.Config.CodexArchivedSessionsDir, "codex", searchAllDirectories: true);
        }

        IngestRoot(request, request.Config.FacadeRunsDir, "facade", searchAllDirectories: false);
    }

    private static void IngestRoot(RunRequest request, string root, string source, bool searchAllDirectories)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var pattern = source == "codex" ? "rollout-*.jsonl" : "*.events.jsonl";
        var option = searchAllDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFiles(root, pattern, option).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            var cursor = request.Store.GetCursor(path);
            if (cursor is not null &&
                cursor.Size == info.Length &&
                cursor.ByteOffset == info.Length &&
                info.LastWriteTimeUtc < request.Window.FromUtc)
            {
                continue;
            }

            var read = ByteLog.Read(path, cursor?.ByteOffset ?? 0, cursor?.Size ?? 0, cursor?.PrefixHash, cursor?.NextLine ?? 1);
            if (read.Reset)
            {
                request.Store.DeleteObservationsForFile(path);
            }

            var carry = new ParseCarry
            {
                NativeId = read.Reset ? null : cursor?.NativeId,
                Repository = read.Reset ? null : cursor?.Repository,
                Cwd = read.Reset ? null : cursor?.Cwd,
                LastEvent = read.Reset ? null : cursor?.LastEventUtc,
                Terminal = !read.Reset && (cursor?.Terminal ?? false),
            };
            foreach (var line in read.Lines)
            {
                var draft = source == "codex"
                    ? CodexCollector.ParseLine(path, line, carry)
                    : FacadeCollector.ParseLine(path, line, carry);
                if (draft is not null)
                {
                    request.Store.UpsertObservation(draft);
                }
            }

            request.Store.SaveCursor(path, source, new FileCursor
            {
                ByteOffset = read.NewOffset,
                Size = read.Size,
                PrefixHash = read.PrefixHash,
                NextLine = read.NextLine,
                NativeId = carry.NativeId,
                Repository = carry.Repository,
                Cwd = carry.Cwd,
                LastEventUtc = carry.LastEvent,
                Terminal = carry.Terminal,
            });
        }
    }

    private static List<SourcePacket> BuildPackets(StateStore store, TimeWindow window)
    {
        var natives = store.ListNativeIdsInWindow(window.FromUtc, window.ToUtcExclusive);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packets = new List<SourcePacket>();
        foreach (var native in natives.Where(item => item.Source == "codex").OrderBy(item => item.NativeId, StringComparer.Ordinal))
        {
            var observations = store.ListByNative("codex", native.NativeId).ToList();
            var runIds = observations.SelectMany(item => item.Draft.RunIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var runId in runIds)
            {
                var linked = store.ListByNative("facade", runId);
                if (linked.Count == 0)
                {
                    continue;
                }

                observations.AddRange(linked);
                consumed.Add(runId);
            }

            packets.Add(MakePacket(observations, native.NativeId, "codex", runIds));
        }

        foreach (var native in natives.Where(item => item.Source == "facade").OrderBy(item => item.NativeId, StringComparer.Ordinal))
        {
            if (consumed.Contains(native.NativeId))
            {
                continue;
            }

            var observations = store.ListByNative("facade", native.NativeId);
            packets.Add(MakePacket(observations, native.NativeId, "facade", [native.NativeId]));
        }

        return packets;
    }

    private static SourcePacket MakePacket(IReadOnlyList<StoredObservation> observations, string nativeId, string source, IReadOnlyList<string> runIds)
    {
        var ordered = observations
            .OrderBy(item => item.Draft.Timestamp ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Draft.Ordinal)
            .ToList();
        return new SourcePacket
        {
            NativeId = nativeId,
            Source = source,
            Repository = ordered.Select(item => DetailString(item, "repository")).LastOrDefault(value => value is not null),
            Cwd = ordered.Select(item => DetailString(item, "cwd")).LastOrDefault(value => value is not null),
            LinkedRunIds = runIds,
            Observations = ordered,
        };
    }

    private static async Task<List<ActivityView>> AnalyzeAsync(RunRequest request, IReadOnlyList<SourcePacket> packets, CancellationToken cancellationToken)
    {
        var views = new ConcurrentBag<ActivityView>();
        var gate = new SemaphoreSlim(Math.Max(1, request.Config.MaxParallel));
        var tasks = packets.Select(async packet =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                foreach (var view in await AnalyzePacketAsync(request, packet, cancellationToken))
                {
                    views.Add(view);
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return views.OrderBy(view => view.Id, StringComparer.Ordinal).ToList();
    }

    private static async Task<IReadOnlyList<ActivityView>> AnalyzePacketAsync(RunRequest request, SourcePacket packet, CancellationToken cancellationToken)
    {
        var stored = request.Store.ListCurrentByNative(packet.NativeId);
        if (CanReuse(packet.Observations, stored))
        {
            return stored.Select(view => view.ReplacePulls(view.PullRequests, view.Stages, true)).ToList();
        }

        var substantive = packet.Observations.Where(item => Normalize.IsSubstantiveKind(item.Draft.Kind)).ToList();
        var selfObservations = substantive.Where(item => Normalize.IsSelfInvocation(item.Draft.Body)).ToList();
        var otherObservations = substantive.Where(item => !Normalize.IsSelfInvocation(item.Draft.Body)).ToList();
        var produced = new List<ActivityView>();
        if (selfObservations.Count > 0 && otherObservations.Count == 0)
        {
            produced.AddRange(await MaterializeAsync(request, packet, packet.Observations, SelfExtraction(packet.Observations), coverageFromModel: true, forceSelf: true, cancellationToken));
        }
        else if (selfObservations.Count > 0)
        {
            var selfPacket = CopyPacket(packet, selfObservations);
            produced.AddRange(await MaterializeAsync(request, selfPacket, selfObservations, SelfExtraction(selfObservations), coverageFromModel: true, forceSelf: true, cancellationToken));
            var rest = packet.Observations.Where(item => selfObservations.All(self => self.Id != item.Id)).ToList();
            produced.AddRange(await ExtractViewsAsync(request, CopyPacket(packet, rest), cancellationToken));
        }
        else
        {
            produced.AddRange(await ExtractViewsAsync(request, packet, cancellationToken));
        }

        var freshIds = produced.Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var view in produced)
        {
            request.Store.UpsertActivity(view);
            foreach (var pull in view.PullRequests)
            {
                request.Store.UpsertPull(pull, view.Id);
            }
        }

        if (produced.Count > 0)
        {
            foreach (var old in stored)
            {
                if (!freshIds.Contains(old.Id))
                {
                    request.Store.MarkReplaced(old.Id, produced[0].Id);
                }
            }
        }

        return produced;
    }

    private static async Task<IReadOnlyList<ActivityView>> ExtractViewsAsync(RunRequest request, SourcePacket packet, CancellationToken cancellationToken)
    {
        var limit = request.MaxPacketChars ?? Normalize.PacketCharLimit;
        var slices = PacketSplitter.Split(packet.Observations, limit);
        var extracted = new List<ExtractedActivity>();
        string? previous = null;
        for (var index = 0; index < slices.Count; index++)
        {
            var slice = CopyPacket(packet, slices[index]);
            var part = await request.Model.ExtractAsync(new ActivityPacket
            {
                PacketId = packet.NativeId + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                NativeId = slice.NativeId,
                Source = slice.Source,
                Repository = slice.Repository,
                Cwd = slice.Cwd,
                ExistingSummary = previous,
                Observations = slice.Observations,
                LinkedRunIds = slice.LinkedRunIds,
            }, cancellationToken);
            extracted.AddRange(part);
            previous = string.Join(" / ", part.Select(item => item.Summary));
        }

        return await MaterializeAsync(request, packet, packet.Observations, extracted, extracted.Count > 0, forceSelf: false, cancellationToken);
    }

    private static async Task<IReadOnlyList<ActivityView>> MaterializeAsync(
        RunRequest request,
        SourcePacket packet,
        IReadOnlyList<StoredObservation> observations,
        IReadOnlyList<ExtractedActivity> extracted,
        bool coverageFromModel,
        bool forceSelf,
        CancellationToken cancellationToken)
    {
        var grouped = ActivityGrouping.Group(observations, extracted);
        var omitted = observations.Any(HasOmittedTail);
        var coverage = coverageFromModel && !omitted;
        var views = new List<ActivityView>();
        foreach (var activity in grouped)
        {
            var snapshots = new List<PullRequestSnapshot>();
            foreach (var reference in activity.Refs.Where(reference => reference.Kind == "pull"))
            {
                snapshots.Add(await request.Pulls.GetAsync(reference.Owner, reference.Repository, reference.Number, cancellationToken));
            }

            var used = observations.Where(item => activity.ObservationIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
            var self = forceSelf || IsSelf(used);
            views.Add(ToView(activity, packet, snapshots, coverage, self ? "status_check_self" : "normal", true));
        }

        return views;
    }

    private static ActivityView ToView(GroupedActivity activity, SourcePacket packet, IReadOnlyList<PullRequestSnapshot> pulls, bool coverage, string kind, bool inWindow)
    {
        var stages = activity.Stages.ToList();
        foreach (var pull in pulls)
        {
            stages.Add(pull.State switch
            {
                PullRequestState.Merged => StageNames.PrMerged,
                PullRequestState.Open => StageNames.PrOpen,
                _ => StageNames.PrClosedUnmerged,
            });
        }

        var evidence = activity.ObservationIds.ToList();
        evidence.Add(packet.Source + ":" + packet.NativeId);
        foreach (var runId in packet.LinkedRunIds)
        {
            evidence.Add("run:" + runId);
        }

        return new ActivityView
        {
            Id = activity.Id,
            Summary = activity.Summary,
            Purpose = activity.Purpose,
            Repository = packet.Repository ?? "",
            Stages = stages,
            PeriodChange = activity.PeriodChange,
            CurrentPosition = activity.CurrentPosition,
            Kind = kind,
            InWindow = inWindow,
            Evidence = evidence,
            Uncertainty = activity.Uncertainty,
            PullRequests = pulls,
            IssueRefs = activity.Refs.Where(reference => reference.Kind == "issue").ToList(),
            ExplicitRemainingWork = activity.ExplicitRemainingWork,
            DiscoveredFrom = activity.DiscoveredFrom,
            NextAction = activity.NextAction,
            Confidence = activity.Confidence,
            Cwd = packet.Cwd,
            ObservationIds = activity.ObservationIds,
            NativeId = packet.NativeId,
            CoverageComplete = coverage,
        };
    }

    private static async Task RefreshTrackedAsync(RunRequest request, List<ActivityView> views, CancellationToken cancellationToken)
    {
        foreach (var tracked in request.Store.ListUnmergedTracked())
        {
            if (tracked.ActivityId is null || views.Any(view => view.Id == tracked.ActivityId))
            {
                var freshKnown = await request.Pulls.GetAsync(tracked.Snapshot.Owner, tracked.Snapshot.Repository, tracked.Snapshot.Number, cancellationToken);
                request.Store.UpsertPull(freshKnown, tracked.ActivityId);
                if (tracked.ActivityId is null)
                {
                    continue;
                }

                var index = views.FindIndex(view => view.Id == tracked.ActivityId);
                if (index < 0)
                {
                    continue;
                }

                views[index] = Overlay(views[index], freshKnown, true);
                request.Store.UpsertActivity(views[index]);
                continue;
            }

            var fresh = await request.Pulls.GetAsync(tracked.Snapshot.Owner, tracked.Snapshot.Repository, tracked.Snapshot.Number, cancellationToken);
            request.Store.UpsertPull(fresh, tracked.ActivityId);
            if (fresh.State == tracked.Snapshot.State && fresh.MergedAt == tracked.Snapshot.MergedAt)
            {
                continue;
            }

            var existing = request.Store.ListCurrentActivities().FirstOrDefault(view => view.Id == tracked.ActivityId);
            if (existing is null)
            {
                continue;
            }

            var updated = Overlay(existing, fresh, false);
            request.Store.UpsertActivity(updated);
            views.Add(updated);
        }
    }

    private static ActivityView Overlay(ActivityView view, PullRequestSnapshot fresh, bool inWindow)
    {
        var pulls = view.PullRequests.Select(pull => pull.Url.Equals(fresh.Url, StringComparison.OrdinalIgnoreCase) ? fresh : pull).ToList();
        if (pulls.All(pull => !pull.Url.Equals(fresh.Url, StringComparison.OrdinalIgnoreCase)))
        {
            pulls.Add(fresh);
        }

        var stages = view.Stages.Where(stage => stage is not (StageNames.PrOpen or StageNames.PrMerged or StageNames.PrClosedUnmerged)).ToList();
        foreach (var pull in pulls)
        {
            stages.Add(pull.State switch
            {
                PullRequestState.Merged => StageNames.PrMerged,
                PullRequestState.Open => StageNames.PrOpen,
                _ => StageNames.PrClosedUnmerged,
            });
        }

        return view.ReplacePulls(pulls, stages, inWindow);
    }

    private static async Task<IReadOnlyList<ConcentrationItem>> ConcentrateAsync(RunRequest request, IReadOnlyList<ActivityView> views, CancellationToken cancellationToken)
    {
        var eligible = views.Where(view => view.Kind != "status_check_self" && view.InWindow).ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        var allowed = eligible.Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
        var items = await request.Model.ConcentrateAsync(eligible, cancellationToken);
        return items.Where(item => allowed.Contains(item.ActivityId)).Take(10).ToList();
    }

    private static bool CanReuse(IReadOnlyList<StoredObservation> observations, IReadOnlyList<ActivityView> stored)
    {
        if (stored.Count == 0)
        {
            return false;
        }

        var packetIds = observations.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var storedIds = stored.SelectMany(item => item.ObservationIds).ToHashSet(StringComparer.Ordinal);
        if (!packetIds.SetEquals(storedIds))
        {
            return false;
        }

        return stored.All(view => view.CoverageComplete || view.Kind == "status_check_self" || view.ObservationIds.Any(id => HasOmittedTail(observations.First(item => item.Id == id))));
    }

    private static bool IsSelf(IReadOnlyList<StoredObservation> observations)
    {
        var substantive = observations.Where(item => Normalize.IsSubstantiveKind(item.Draft.Kind)).ToList();
        return substantive.Count > 0 && substantive.All(item => Normalize.IsSelfInvocation(item.Draft.Body));
    }

    private static IReadOnlyList<ExtractedActivity> SelfExtraction(IReadOnlyList<StoredObservation> observations) =>
    [
        new ExtractedActivity
        {
            ObservationIds = observations.Select(item => item.Id).ToList(),
            IndependentIssue = false,
            Summary = "このツールの分析実行",
            Purpose = "status check",
            Stages = [],
            PeriodChange = "分析コマンドを実行した。",
            CurrentPosition = "分析実行であり、他のProjectの完了には使わない。",
            ExplicitRemainingWork = "",
            Uncertainty = "",
            Confidence = 1,
            NextAction = "",
            UncertainMerge = false,
        },
    ];

    private static SourcePacket CopyPacket(SourcePacket packet, IReadOnlyList<StoredObservation> observations) => new()
    {
        NativeId = packet.NativeId,
        Source = packet.Source,
        Repository = packet.Repository,
        Cwd = packet.Cwd,
        LinkedRunIds = packet.LinkedRunIds,
        Observations = observations,
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

    private static string WriteReport(RunRequest request, string runId, IReadOnlyList<ActivityView> views, IReadOnlyList<HoldNote> holds, IReadOnlyList<ConcentrationItem> concentrations, string ledgerSummary, (int InputTokens, int OutputTokens, decimal Usd) usage)
    {
        var directory = Path.Combine(request.Config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.md");
        var text = ReportWriter.Write(new ReportModel
        {
            Window = request.Window,
            QueriedAt = request.Now,
            Activities = views,
            Holds = holds,
            Concentrations = concentrations,
            LedgerSummary = ledgerSummary,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            EstimatedUsd = usage.Usd,
        });
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteProposal(AppConfig config, string runId, IReadOnlyList<LedgerChange> changes, IReadOnlyList<ActivityView> views)
    {
        var directory = Path.Combine(config.ReportsDirectory, runId);
        Directory.CreateDirectory(directory);
        var involved = changes.SelectMany(change => change.Decisions).Select(decision => decision.ActivityId).ToHashSet(StringComparer.Ordinal);
        var pulls = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in views.Where(view => involved.Contains(view.Id)))
        {
            foreach (var pull in view.PullRequests)
            {
                if (!seen.Add(pull.Url))
                {
                    continue;
                }

                JsonLists.AddObject(pulls, new JsonObject { ["url"] = pull.Url, ["state"] = pull.State.ToString() });
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

        var root = new JsonObject
        {
            ["runId"] = runId,
            ["pulls"] = pulls,
            ["changes"] = changeArray,
        };
        var path = Path.Combine(directory, "proposal.json");
        File.WriteAllText(path, root.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private static bool HasOmittedTail(StoredObservation observation)
    {
        if (JsonNode.Parse(observation.Draft.DetailJson) is not JsonObject obj)
        {
            return false;
        }

        return obj["omittedTail"]?.GetValue<bool>() == true;
    }

    private static string? DetailString(StoredObservation observation, string name)
    {
        if (JsonNode.Parse(observation.Draft.DetailJson) is not JsonObject obj || obj[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

internal static class PacketSplitter
{
    internal static IReadOnlyList<IReadOnlyList<StoredObservation>> Split(IReadOnlyList<StoredObservation> observations, int maxChars)
    {
        var slices = new List<IReadOnlyList<StoredObservation>>();
        var current = new List<StoredObservation>();
        var size = 0;
        foreach (var observation in observations)
        {
            var length = observation.Draft.Body.Length + observation.Id.Length + 32;
            if (length > maxChars)
            {
                if (current.Count > 0)
                {
                    slices.Add(current);
                    current = [];
                    size = 0;
                }

                var budget = Math.Max(1, maxChars - observation.Id.Length - 32);
                var body = observation.Draft.Body;
                for (var start = 0; start < body.Length; start += budget)
                {
                    var sliceLength = Math.Min(budget, body.Length - start);
                    slices.Add([Slice(observation, body.Substring(start, sliceLength))]);
                }

                continue;
            }

            if (current.Count > 0 && size + length > maxChars)
            {
                slices.Add(current);
                current = [];
                size = 0;
            }

            current.Add(observation);
            size += length;
        }

        if (current.Count > 0)
        {
            slices.Add(current);
        }

        return slices.Count == 0 ? [[]] : slices;
    }

    private static StoredObservation Slice(StoredObservation observation, string body) => new()
    {
        Id = observation.Id,
        Draft = new ObservationDraft
        {
            Source = observation.Draft.Source,
            NativeId = observation.Draft.NativeId,
            FilePath = observation.Draft.FilePath,
            Ordinal = observation.Draft.Ordinal,
            Timestamp = observation.Draft.Timestamp,
            Kind = observation.Draft.Kind,
            Body = body,
            DetailJson = observation.Draft.DetailJson,
            Urls = observation.Draft.Urls,
            RunIds = observation.Draft.RunIds,
        },
    };
}

internal sealed class RunRequest
{
    public required AppConfig Config { get; init; }
    public required TimeWindow Window { get; init; }
    public required bool ApplyLedger { get; init; }
    public required IActivityModel Model { get; init; }
    public required IPullRequestSource Pulls { get; init; }
    public required StateStore Store { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public string? RunId { get; init; }
    public int? MaxPacketChars { get; init; }
}

internal sealed class RunResult
{
    public required string RunId { get; init; }
    public required string ReportPath { get; init; }
    public required IReadOnlyList<string> AppliedCards { get; init; }
    public string? StopReason { get; init; }
    public int ExitCode { get; init; }
}

internal sealed class SourcePacket
{
    public required string NativeId { get; init; }
    public required string Source { get; init; }
    public string? Repository { get; init; }
    public string? Cwd { get; init; }
    public required IReadOnlyList<string> LinkedRunIds { get; init; }
    public required IReadOnlyList<StoredObservation> Observations { get; init; }
}

internal sealed record ProposalPull(string Url, string State);

internal sealed record StoredProposal(string RunId, IReadOnlyList<ProposalPull> Pulls, IReadOnlyList<LedgerChange> Changes);
