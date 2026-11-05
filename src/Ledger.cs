using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// personal-project-driver の Project カードを局所更新する。
/// workstream 完了は block の削除であり、Project カード自体は消さない。
/// </summary>
internal static partial class CardParser
{
    internal static IReadOnlyList<ProjectCard> LoadRepository(string repository, ICollection<ProcessingSkip>? skips = null)
    {
        var directory = Path.Combine(repository, "projects");
        if (!Directory.Exists(directory))
        {
            throw new StatusCheckException("Ledger projects directory was not found: " + directory);
        }

        var cards = new List<ProjectCard>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.md").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(repository, path).Replace('\\', '/');
            try
            {
                cards.Add(Parse(relative, File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 1枚が読めなくても、他の Project カードの照合は続ける。
                skips?.Add(new ProcessingSkip(relative, ex.Message));
            }
        }

        return cards;
    }

    internal static ProjectCard Parse(string relativePath, string text)
    {
        var front = FrontMatter(text);
        var statusMatch = StatusPattern().Match(front);
        var status = statusMatch.Success ? statusMatch.Groups[1].Value.Trim() : "";
        var repositories = RepositoryPattern().Matches(front).Select(match => match.Groups[1].Value).ToList();
        var h1 = H1Pattern().Match(text);
        var workstreams = Workstreams(text);
        return new ProjectCard
        {
            RelativePath = relativePath,
            Text = text,
            Status = status,
            Repositories = repositories,
            H1 = h1.Success ? h1.Groups[1].Value.Trim() : "",
            Purpose = SectionBody(text, "目的"),
            Current = SectionBody(text, "現在地"),
            Keywords = ContainsSection(text, "識別キーワード") ? SectionBody(text, "識別キーワード") : null,
            Workstreams = workstreams,
        };
    }

    internal static string ReplacePieces(string text, string? newCurrentBody, IReadOnlyList<(int Start, int Length, string Replacement)> edits)
    {
        var updated = text;
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            updated = updated[..edit.Start] + edit.Replacement + updated[(edit.Start + edit.Length)..];
        }

        if (newCurrentBody is not null)
        {
            updated = ReplaceSectionBody(updated, "現在地", newCurrentBody);
        }

        if (CardParser.Parse("check", updated).Workstreams.Count == 0 && updated.Contains("## 進行中のworkstream", StringComparison.Ordinal))
        {
            updated = ReplaceSectionBody(updated, "進行中のworkstream", "なし。");
        }

        return updated;
    }

    internal static void Validate(ProjectCard before, ProjectCard after, IReadOnlySet<string> touched)
    {
        if (before.H1 != after.H1 || before.Status != after.Status || before.Purpose != after.Purpose || before.Keywords != after.Keywords)
        {
            throw new StatusCheckException("Ledger validation failed for preserved sections in " + before.RelativePath);
        }

        if (!before.Repositories.SequenceEqual(after.Repositories))
        {
            throw new StatusCheckException("Ledger validation failed for repositories in " + before.RelativePath);
        }

        if (!HeadingPattern().Matches(before.Text).Select(match => match.Groups[1].Value.Trim()).SequenceEqual(
                HeadingPattern().Matches(after.Text).Select(match => match.Groups[1].Value.Trim())))
        {
            throw new StatusCheckException("Ledger validation failed because a section was added or removed in " + before.RelativePath);
        }

        foreach (var workstream in before.Workstreams)
        {
            if (touched.Contains(workstream.Heading))
            {
                continue;
            }

            var kept = after.Workstreams.FirstOrDefault(candidate => candidate.Heading == workstream.Heading);
            if (kept is null || kept.Raw != workstream.Raw)
            {
                throw new StatusCheckException("Ledger validation failed because an unrelated workstream changed in " + before.RelativePath);
            }
        }
    }

    internal static string RenderWorkstream(string heading, string current, string next, string waiting, string references, string newline)
    {
        return "### " + heading + newline
            + newline + "**現在地**" + newline + newline + current.Trim() + newline
            + newline + "**次のアクション**" + newline + newline + next.Trim() + newline
            + newline + "**待ち・阻害要因**" + newline + newline + waiting.Trim() + newline
            + newline + "**参照**" + newline + newline + references.Trim() + newline + newline;
    }

    internal static string RewriteCurrent(string oldCurrent, string completedWorkstreamCurrent, string fact, string newline)
    {
        var blocked = Normalize.SplitSentences(completedWorkstreamCurrent)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length >= 8)
            .ToList();
        var kept = new List<string>();
        foreach (var sentence in Normalize.SplitSentences(oldCurrent))
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var drop = blocked.Any(block => trimmed.Contains(block, StringComparison.Ordinal) || block.Contains(trimmed, StringComparison.Ordinal));
            if (!drop)
            {
                kept.Add(trimmed);
            }
        }

        if (!kept.Any(sentence => sentence.Contains("明示的な残作業は記録されていない", StringComparison.Ordinal)))
        {
            kept.Add(fact);
        }

        return string.Join(newline, kept);
    }

    private static string ReplaceSectionBody(string text, string heading, string body)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var pattern = new Regex("(?m)^## " + Regex.Escape(heading) + @"[^\n]*\n");
        var match = pattern.Match(text);
        if (!match.Success)
        {
            throw new StatusCheckException("Section was not found: " + heading);
        }

        var next = HeadingPattern().Match(text, match.Index + match.Length);
        var end = next.Success ? next.Index : text.Length;
        var rendered = newline + body.Trim() + newline + newline;
        return text[..(match.Index + match.Length)] + rendered + text[end..];
    }

    private static string FrontMatter(string text)
    {
        var start = text.IndexOf("---", StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        var end = text.IndexOf("---", start + 3, StringComparison.Ordinal);
        return end < 0 ? "" : text[start..end];
    }

    private static string SectionBody(string text, string heading)
    {
        var pattern = new Regex("(?m)^## " + Regex.Escape(heading) + @"[^\n]*\n");
        var match = pattern.Match(text);
        if (!match.Success)
        {
            return "";
        }

        var next = HeadingPattern().Match(text, match.Index + match.Length);
        var end = next.Success ? next.Index : text.Length;
        return text[(match.Index + match.Length)..end].Trim();
    }

    internal static bool ContainsSection(string text, string heading) =>
        Regex.IsMatch(text, "(?m)^## " + Regex.Escape(heading) + @"\s*$");

    private static IReadOnlyList<WorkstreamSlice> Workstreams(string text)
    {
        var section = Regex.Match(text, @"(?m)^## 進行中のworkstream[^\n]*\n");
        if (!section.Success)
        {
            return [];
        }

        var nextSection = HeadingPattern().Match(text, section.Index + section.Length);
        var end = nextSection.Success ? nextSection.Index : text.Length;
        var body = text[(section.Index + section.Length)..end];
        if (body.Trim() == "なし。")
        {
            return [];
        }

        var slices = new List<WorkstreamSlice>();
        var matches = Regex.Matches(text, @"(?m)^### (.+)$");
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (match.Index < section.Index || match.Index >= end)
            {
                continue;
            }

            var sliceEnd = i + 1 < matches.Count && matches[i + 1].Index < end ? matches[i + 1].Index : end;
            var raw = text[match.Index..sliceEnd];
            slices.Add(new WorkstreamSlice
            {
                Heading = match.Groups[1].Value.Trim(),
                Start = match.Index,
                Length = sliceEnd - match.Index,
                Raw = raw,
                Current = Field(raw, "現在地"),
                Next = Field(raw, "次のアクション"),
                Waiting = Field(raw, "待ち・阻害要因"),
                References = Field(raw, "参照"),
            });
        }

        return slices;
    }

    private static string Field(string raw, string label)
    {
        var marker = "**" + label + "**";
        var start = raw.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += marker.Length;
        var next = raw.IndexOf("\n**", start, StringComparison.Ordinal);
        var body = next < 0 ? raw[start..] : raw[start..next];
        return body.Trim();
    }

    [GeneratedRegex(@"(?m)^## .+$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"(?m)^# (.+)$")]
    private static partial Regex H1Pattern();

    [GeneratedRegex(@"status:\s*(\S+)")]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"-\s+([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)")]
    private static partial Regex RepositoryPattern();
}

internal sealed class ProjectCard
{
    public required string RelativePath { get; init; }
    public required string Text { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<string> Repositories { get; init; }
    public required string H1 { get; init; }
    public required string Purpose { get; init; }
    public required string Current { get; init; }
    public string? Keywords { get; init; }
    public required IReadOnlyList<WorkstreamSlice> Workstreams { get; init; }
}

internal sealed class WorkstreamSlice
{
    public required string Heading { get; init; }
    public required int Start { get; init; }
    public required int Length { get; init; }
    public required string Raw { get; init; }
    public required string Current { get; init; }
    public required string Next { get; init; }
    public required string Waiting { get; init; }
    public required string References { get; init; }
}

internal sealed class LedgerDecision
{
    public required string Key { get; init; }
    public required string ActivityId { get; init; }
    public required string Workstream { get; init; }
    public required string Action { get; init; }
    public required string RelativePath { get; init; }
}

internal sealed class LedgerChange
{
    public required string RelativePath { get; init; }
    public required string BlobBefore { get; init; }
    public required string NewText { get; init; }
    public required IReadOnlyList<LedgerDecision> Decisions { get; init; }
    public IReadOnlyList<ProposalPull> Pulls { get; init; } = [];
}

internal sealed class HoldNote
{
    public required string ActivityId { get; init; }
    public required string Reason { get; init; }
    public string ReasonCode { get; init; } = "";
    public IReadOnlyList<string> CandidateCards { get; init; } = [];
}

internal static class LedgerPlanner
{
    internal const string CompletionFact = "対応PRはマージ済みで、明示的な残作業は記録されていない。";

    internal static LedgerChange? Apply(
        ProjectCard card,
        IReadOnlyList<ThreadView> reports,
        IReadOnlyList<LedgerFieldUpdate> updates) =>
        ApplyWithDiagnostics(card, reports, updates, out _, out _);

    internal static LedgerChange? ApplyWithDiagnostics(
        ProjectCard card,
        IReadOnlyList<ThreadView> reports,
        IReadOnlyList<LedgerFieldUpdate> updates,
        out IReadOnlyList<WorkstreamDiagnostic> diagnostics,
        out IReadOnlyList<string> unselectedReportIds)
    {
        var diagList = new List<WorkstreamDiagnostic>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in updates.OrderBy(update => update.Workstream == LedgerTargets.ProjectCurrent ? 1 : 0))
        {
            if (update.ReportIds is not null)
            {
                foreach (var id in update.ReportIds)
                {
                    selectedIds.Add(id);
                }
            }
        }

        unselectedReportIds = reports
            .Select(report => report.Thread.Source + ":" + report.Thread.NativeId)
            .Where(id => !selectedIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var newline = card.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var edits = new Dictionary<string, (int Start, int Length, string Replacement)>(StringComparer.Ordinal);
        var decisions = new List<LedgerDecision>();
        var pullStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in updates.OrderBy(update => update.Workstream == LedgerTargets.ProjectCurrent ? 1 : 0))
        {
            if (string.IsNullOrWhiteSpace(update.Workstream))
            {
                continue;
            }

            if (update.Action.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (update.Workstream == LedgerTargets.ProjectCurrent)
                {
                    diagList.Add(new WorkstreamDiagnostic(LedgerTargets.ProjectCurrent, "none", "guard_checked",
                        "reconcile_project_current_invalid", "予約対象はaction=updateのみ許可される。", update.ReportIds ?? [], Applied: false));
                    continue;
                }
                diagList.Add(new WorkstreamDiagnostic(
                    update.Workstream,
                    "none",
                    "reconciled",
                    "reconcile_action_none",
                    string.IsNullOrWhiteSpace(update.Reason) ? "モデルが変更不要（action=none）と判断した。" : update.Reason,
                    update.ReportIds ?? [],
                    Applied: false));
                continue;
            }

            if (update.Workstream == LedgerTargets.ProjectCurrent)
            {
                var projectReports = ReportsFor(reports, update);
                var knownIds = reports.Select(report => report.Thread.Source + ":" + report.Thread.NativeId).ToHashSet(StringComparer.Ordinal);
                var ids = update.ReportIds ?? [];
                if (!update.Action.Equals("update", StringComparison.OrdinalIgnoreCase) ||
                    ids.Count == 0 || ids.Any(id => !knownIds.Contains(id)) || projectReports.Count == 0 || !CardParser.ContainsSection(card.Text, "現在地") ||
                    string.IsNullOrWhiteSpace(update.Current))
                {
                    diagList.Add(new WorkstreamDiagnostic(LedgerTargets.ProjectCurrent, update.Action, "guard_checked",
                        "reconcile_project_current_invalid", "Project現在地の更新は既知の報告IDと空でない完成後の現在地が必要。", ids, Applied: false));
                    continue;
                }

                var proposed = update.Current.Trim();
                var currentBeforeProjectUpdate = current ?? card.Current;
                var rendered = !string.IsNullOrEmpty(currentBeforeProjectUpdate) && currentBeforeProjectUpdate.Contains(proposed, StringComparison.Ordinal) && proposed.Length < currentBeforeProjectUpdate.Length
                    ? currentBeforeProjectUpdate
                    : proposed;
                // A card-level summary must not erase facts that still belong to other active workstreams.
                var retainedFacts = card.Workstreams.Where(workstream => !touched.Contains(workstream.Heading))
                    .SelectMany(workstream => Normalize.SplitSentences(workstream.Current))
                    .Select(sentence => sentence.Trim())
                    .Where(sentence => sentence.Length >= 8 && card.Current.Contains(sentence, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal);
                var factsOwnedByUpdatedWorkstreams = card.Workstreams.Where(workstream => touched.Contains(workstream.Heading))
                    .SelectMany(workstream => Normalize.SplitSentences(workstream.Current))
                    .Select(sentence => sentence.Trim()).ToHashSet(StringComparer.Ordinal);
                foreach (var oldFact in Normalize.SplitSentences(card.Current).Select(sentence => sentence.Trim()).Where(sentence => sentence.Length >= 8))
                {
                    if (factsOwnedByUpdatedWorkstreams.Contains(oldFact) || IsSupersededSummaryFact(oldFact, proposed)) continue;
                    if (!rendered.Contains(oldFact, StringComparison.Ordinal)) rendered = rendered.TrimEnd() + " " + oldFact;
                }
                foreach (var fact in retainedFacts)
                {
                    if (!rendered.Contains(fact, StringComparison.Ordinal)) rendered = rendered.TrimEnd() + " " + fact;
                }
                if (!string.IsNullOrWhiteSpace(currentBeforeProjectUpdate) && currentBeforeProjectUpdate.Contains(LedgerPlanner.CompletionFact, StringComparison.Ordinal) &&
                    !rendered.Contains(LedgerPlanner.CompletionFact, StringComparison.Ordinal))
                {
                    rendered = rendered.TrimEnd() + " " + LedgerPlanner.CompletionFact;
                }
                var latestProjectReport = Latest(projectReports);
                var remaining = latestProjectReport?.Assessment?.ExplicitRemainingWork;
                if (latestProjectReport?.Assessment?.RemainingWorkUnverified != true &&
                    !string.IsNullOrWhiteSpace(remaining) && !rendered.Contains(remaining, StringComparison.Ordinal))
                {
                    rendered = rendered.TrimEnd() + " 残作業: " + remaining.Trim();
                }
                if (rendered != currentBeforeProjectUpdate)
                {
                    current = rendered;
                    var projectActivityId = LatestActivity(projectReports);
                    decisions.Add(new LedgerDecision
                    {
                        Key = Normalize.ShortHash(projectActivityId + "\n" + card.RelativePath + "\n" + LedgerTargets.ProjectCurrent + "\nupdate"),
                        ActivityId = projectActivityId,
                        Workstream = LedgerTargets.ProjectCurrent,
                        Action = "update",
                        RelativePath = card.RelativePath,
                    });
                    foreach (var report in projectReports.SelectMany(report => report.Pulls)) pullStates[report.Url] = report.State.ToString();
                }
                diagList.Add(new WorkstreamDiagnostic(LedgerTargets.ProjectCurrent, "update", rendered == currentBeforeProjectUpdate ? "reconciled" : "proposed",
                    rendered == currentBeforeProjectUpdate ? "reconcile_noop_richer_existing" : "update_proposed",
                    "Projectカードの現在地へ、照合した報告の進捗要約を反映する。", ids, Applied: false));
                continue;
            }

            var slice = card.Workstreams.FirstOrDefault(workstream => workstream.Heading == update.Workstream);
            if (slice is null)
            {
                diagList.Add(new WorkstreamDiagnostic(
                    update.Workstream,
                    update.Action,
                    "workstream_matched",
                    "reconcile_workstream_mismatch",
                    "指定されたworkstream見出し（" + update.Workstream + "）がProjectカードに存在しない。",
                    update.ReportIds ?? [],
                    Applied: false));
                continue;
            }

            var action = update.Action.ToLowerInvariant();
            if (action is not ("update" or "delete"))
            {
                diagList.Add(new WorkstreamDiagnostic(slice.Heading, update.Action, "guard_checked", "reconcile_invalid_action",
                    "actionはupdate/none/deleteのいずれかでなければならない。", update.ReportIds ?? [], Applied: false));
                continue;
            }
            var relevant = ReportsFor(reports, update);
            if (action == "delete")
            {
                var guard = CheckDeleteGuard(relevant);
                if (!guard.Allowed)
                {
                    diagList.Add(new WorkstreamDiagnostic(
                        slice.Heading,
                        "delete",
                        "guard_checked",
                        guard.ReasonCode,
                        guard.ReasonDetail,
                        update.ReportIds ?? [],
                        Applied: false));
                    continue;
                }

                edits[slice.Heading] = (slice.Start, slice.Length, "");
                current = CardParser.RewriteCurrent(current ?? card.Current, slice.Current, CompletionFact, newline);
                diagList.Add(new WorkstreamDiagnostic(
                    slice.Heading,
                    "delete",
                    "proposed",
                    "guard_passed",
                    string.IsNullOrWhiteSpace(update.Reason) ? guard.ReasonDetail : update.Reason,
                    update.ReportIds ?? [],
                    Applied: false));
            }
            else
            {
                var latest = Latest(relevant);
                var renderedCurrent = ResolveField(update.Current, slice.Current);
                var renderedNext = ResolveField(update.Next, slice.Next);
                var renderedWaiting = ResolveField(update.Waiting, slice.Waiting);
                if (string.IsNullOrWhiteSpace(latest?.Assessment?.ExplicitRemainingWork) &&
                    FieldKept(update.Current, renderedCurrent, slice.Current) &&
                    FieldKept(update.Next, renderedNext, slice.Next) &&
                    FieldKept(update.Waiting, renderedWaiting, slice.Waiting))
                {
                    var urls = relevant.SelectMany(report => report.Pulls.Select(pull => pull.Url));
                    if (urls.All(url => slice.References.Contains(url, StringComparison.Ordinal)))
                    {
                        var noopCode = slice.Current.Length > renderedCurrent.Length
                            ? "reconcile_noop_richer_existing"
                            : "reconcile_noop_already_reflected";
                        diagList.Add(new WorkstreamDiagnostic(
                            slice.Heading,
                            "update",
                            "guard_checked",
                            noopCode,
                            "既存の記載に包含されているため変更不要（no-op）。",
                            update.ReportIds ?? [],
                            Applied: false));
                        continue;
                    }
                }

                if (renderedCurrent.Length > 0 &&
                    slice.Current.Length > renderedCurrent.Length &&
                    slice.Current.Contains(renderedCurrent, StringComparison.Ordinal))
                {
                    renderedCurrent = slice.Current;
                }

                if (!string.IsNullOrWhiteSpace(latest?.Assessment?.ExplicitRemainingWork))
                {
                    renderedWaiting = latest!.Assessment!.ExplicitRemainingWork;
                    if (update.Next is null || string.IsNullOrWhiteSpace(update.Next))
                    {
                        renderedNext = "管理側へこの残作業を戻す。";
                    }
                }

                var references = AppendUrls(slice.References, relevant.SelectMany(report => report.Pulls.Select(pull => pull.Url)), newline);
                edits[slice.Heading] = (slice.Start, slice.Length, CardParser.RenderWorkstream(slice.Heading, renderedCurrent, renderedNext, renderedWaiting, references, newline));
                diagList.Add(new WorkstreamDiagnostic(
                    slice.Heading,
                    "update",
                    "proposed",
                    "update_proposed",
                    string.IsNullOrWhiteSpace(update.Reason) ? "現在地の差分を反映する。" : update.Reason,
                    update.ReportIds ?? [],
                    Applied: false));
            }

            touched.Add(slice.Heading);
            foreach (var pull in relevant.SelectMany(report => report.Pulls))
            {
                pullStates[pull.Url] = pull.State.ToString();
            }

            var activityId = LatestActivity(relevant);
            var pullKey = string.Join(",", relevant.SelectMany(report => report.Pulls).OrderBy(pull => pull.Url, StringComparer.Ordinal).Select(pull => pull.Url + ":" + pull.State));
            decisions.Add(new LedgerDecision
            {
                Key = Normalize.ShortHash(activityId + "\n" + card.RelativePath + "\n" + slice.Heading + "\n" + action + "\n" + pullKey),
                ActivityId = activityId,
                Workstream = slice.Heading,
                Action = action,
                RelativePath = card.RelativePath,
            });
        }

        diagnostics = diagList;
        if (decisions.Count == 0)
        {
            return null;
        }

        var updated = CardParser.ReplacePieces(card.Text, current, edits.Values.ToList());
        if (updated == card.Text)
        {
            return null;
        }

        var after = CardParser.Parse(card.RelativePath, updated);
        CardParser.Validate(card, after, touched);
        foreach (var decision in decisions.Where(decision => decision.Action == "delete"))
        {
            if (after.Workstreams.Any(workstream => workstream.Heading == decision.Workstream))
            {
                throw new StatusCheckException("Completed workstream was not removed: " + decision.Workstream);
            }
        }

        if (decisions.Any(decision => decision.Action == "delete"))
        {
            var others = card.Workstreams.Where(workstream => !touched.Contains(workstream.Heading)).ToList();
            foreach (var other in others)
            {
                foreach (var sentence in Normalize.SplitSentences(other.Current))
                {
                    var trimmed = sentence.Trim();
                    if (trimmed.Length >= 8 && card.Current.Contains(trimmed, StringComparison.Ordinal) && !after.Current.Contains(trimmed, StringComparison.Ordinal))
                    {
                        throw new StatusCheckException("Project current position dropped text that belongs to another workstream.");
                    }
                }
            }
        }

        return new LedgerChange
        {
            RelativePath = card.RelativePath,
            BlobBefore = "",
            NewText = updated,
            Decisions = decisions,
            Pulls = pullStates.Select(pair => new ProposalPull(pair.Key, pair.Value)).ToList(),
        };
    }

    internal static bool CanDelete(IReadOnlyList<ThreadView> reports) =>
        CheckDeleteGuard(reports).Allowed;

    internal static (bool Allowed, string ReasonCode, string ReasonDetail) CheckDeleteGuard(IReadOnlyList<ThreadView> reports)
    {
        if (reports.Count == 0)
        {
            return (false, "guard_rejected_no_reports", "判断対象のreportが指定されていないため完了削除しない。");
        }

        if (reports.Any(report => ThreadPrompts.IsPromptTruncated(report.Thread)))
        {
            return (false, "guard_rejected_body_truncated", "対象スレッドの本文が上限で切り詰められているため完了削除しない。");
        }

        // 取得できなかった PR があると、見えている PR だけでは完了にできない。
        if (reports.Any(report => report.PullLookupFailed))
        {
            return (false, "guard_rejected_pr_lookup_failed", "取得できなかったPRがあるため完了削除しない。");
        }

        if (reports.Any(report => !string.IsNullOrWhiteSpace(report.Assessment?.ExplicitRemainingWork)))
        {
            return (false, "guard_rejected_remaining_work", "未解消の明示的な残作業があるため完了削除しない。");
        }

        if (reports.Any(report => report.Assessment?.RemainingWorkUnverified == true))
        {
            return (false, "guard_rejected_unverified_remaining_quote", "残作業の引用元を検証できず判断不能のため完了削除しない。");
        }

        if (reports.Any(report => report.Thread.HasTrailingUnansweredUserTurn))
        {
            return (false, "guard_rejected_unanswered_user_turn", "最後の通常応答後に未応答のユーザー発言があるため完了削除しない。");
        }

        var pulls = reports.SelectMany(report => report.Pulls).ToList();
        if (pulls.Count == 0)
        {
            return (false, "guard_rejected_no_pr", "対応するPRがないため完了削除しない。");
        }

        if (!pulls.All(pull => pull.State == PullRequestState.Merged))
        {
            return (false, "guard_rejected_pr_not_merged", "未マージのPRが残っているため完了削除しない。");
        }

        return (true, "guard_passed", "関連PRがすべてMergedで未解消の残作業がないため完了削除を許可した。");
    }

    /// <summary>
    /// 更新案が名指しした報告だけを完了判定と欄の上書きに使う。他の報告は足さない。
    /// </summary>
    private static IReadOnlyList<ThreadView> ReportsFor(IReadOnlyList<ThreadView> reports, LedgerFieldUpdate update)
    {
        var ids = update.ReportIds;
        if (ids is null || ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return reports.Where(report => wanted.Contains(report.Thread.Source + ":" + report.Thread.NativeId)).ToList();
    }

    private static ThreadView? Latest(IReadOnlyList<ThreadView> reports) =>
        reports
            .OrderByDescending(report => report.Thread.Response?.Timestamp ?? DateTimeOffset.MinValue)
            .ThenByDescending(report => report.Thread.LastEventUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    private static string LatestActivity(IReadOnlyList<ThreadView> reports)
    {
        var latest = Latest(reports);
        return latest is null ? "" : latest.Thread.Source + ":" + latest.Thread.NativeId;
    }

    private static string ResolveField(string? proposed, string existing) =>
        proposed is null ? existing : proposed.Trim();

    /// <summary>
    /// null は変更しない。空文字は「既に同じ」にはしない。
    /// </summary>
    private static bool FieldKept(string? proposed, string rendered, string existing) =>
        proposed is null || (rendered.Length > 0 && existing.Contains(rendered, StringComparison.Ordinal));

    private static bool IsSupersededSummaryFact(string oldFact, string proposed)
    {
        // A state transition alone does not prove that two sentences describe the same subject.
        // Preserve explicitly scoped old facts unless the proposed sentence repeats that scope.
        var oldScope = Regex.Match(oldFact, @"^(.{1,30}?)(?:は|が|の|:|：)").Groups[1].Value.Trim();
        foreach (var proposedFact in Normalize.SplitSentences(proposed).Select(sentence => sentence.Trim()))
        {
            var proposedScope = Regex.Match(proposedFact, @"^(.{1,30}?)(?:は|が|の|:|：)").Groups[1].Value.Trim();
            if (oldScope.Length > 0 && !string.Equals(oldScope, proposedScope, StringComparison.Ordinal)) continue;
            if (oldScope.Length == 0 && proposedScope.Length > 0) continue;
            if (IsOpposingSummaryState(oldFact, proposedFact)) return true;
        }

        return false;
    }

    private static bool IsOpposingSummaryState(string oldFact, string proposed)
    {
        var opposingStates = new (string[] Negative, string[] Positive)[]
        {
            (["未実装", "実装していない", "実装なし", "未着手"], ["実装済み", "実装した", "実装完了", "実装を完了", "実装を開始", "実装開始"]),
            (["PR未作成", "PRはまだ作成していない", "PR作成前", "PR作成待ち"], ["PR作成済み", "PRを作成", "PR作成した", "PR作成完了"]),
            (["未統合", "未マージ", "マージ待ち"], ["マージ済み", "マージされた", "Merged"]),
            (["未デプロイ", "デプロイ前", "デプロイ待ち"], ["デプロイ済み", "デプロイした", "本番反映済み"]),
            (["未受入", "受入待ち", "承認待ち"], ["受入済み", "承認済み", "承認された"]),
            (["CI失敗", "テスト失敗", "テスト未実施"], ["CI成功", "テスト成功", "テスト通過"]),
        };
        foreach (var (negative, positive) in opposingStates)
        {
            if (negative.Any(marker => oldFact.Contains(marker, StringComparison.Ordinal)) && positive.Any(marker => proposed.Contains(marker, StringComparison.Ordinal)) ||
                positive.Any(marker => oldFact.Contains(marker, StringComparison.Ordinal)) && negative.Any(marker => proposed.Contains(marker, StringComparison.Ordinal))) return true;
        }

        return false;
    }

    private static string AppendUrls(string existing, IEnumerable<string> urls, string newline)
    {
        var text = existing.Trim();
        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal))
        {
            if (!text.Contains(url, StringComparison.Ordinal))
            {
                text = string.IsNullOrEmpty(text) ? url : text + newline + url;
            }
        }

        return text;
    }
}

internal static class LedgerGit
{
    internal static void EnsureClean(string repository)
    {
        var staged = Run(repository, ["diff", "--cached", "--name-only"]);
        if (!string.IsNullOrWhiteSpace(staged))
        {
            throw new StatusCheckException("The ledger repository has staged files. Apply stopped without writing.");
        }

        var dirty = Run(repository, ["status", "--porcelain", "--", "projects"]);
        if (!string.IsNullOrWhiteSpace(dirty))
        {
            throw new StatusCheckException("The ledger projects directory has uncommitted changes. Apply stopped without writing.");
        }
    }

    internal static string Hash(string repository, string relativePath)
    {
        var hash = Run(repository, ["hash-object", "--", relativePath.Replace('/', Path.DirectorySeparatorChar)]).Trim();
        if (hash.Length == 0)
        {
            throw new StatusCheckException("git hash-object returned an empty hash for " + relativePath);
        }

        return hash;
    }

    internal static string Commit(string repository, IReadOnlyList<string> relativePaths, string message)
    {
        var arguments = new List<string> { "add", "--" };
        arguments.AddRange(relativePaths.Select(path => path.Replace('/', Path.DirectorySeparatorChar)));
        Run(repository, arguments);
        var messagePath = Path.Combine(Path.GetTempPath(), "status-check-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(messagePath, message + "\n", new UTF8Encoding(false));
        try
        {
            Run(repository, ["commit", "-F", messagePath]);
        }
        finally
        {
            File.Delete(messagePath);
        }

        return Run(repository, ["rev-parse", "HEAD"]).Trim();
    }

    internal static string Run(string repository, IReadOnlyList<string> arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stderr = stderrTask.GetAwaiter().GetResult();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new StatusCheckException("git " + string.Join(' ', arguments) + " failed: " + stderr.Trim());
        }

        return stdout;
    }
}
