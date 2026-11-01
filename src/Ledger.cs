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
    internal static IReadOnlyList<ProjectCard> LoadRepository(string repository)
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
            cards.Add(Parse(relative, File.ReadAllText(path)));
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

    private static bool ContainsSection(string text, string heading) =>
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
}

internal sealed class HoldNote
{
    public required string ActivityId { get; init; }
    public required string Reason { get; init; }
}

internal static class LedgerPlanner
{
    internal const string CompletionFact = "対応PRはマージ済みで、明示的な残作業は記録されていない。";

    internal static async Task<(IReadOnlyList<LedgerChange> Changes, IReadOnlyList<HoldNote> Holds)> PlanAsync(
        IReadOnlyList<ProjectCard> cards,
        IReadOnlyList<ActivityView> activities,
        StateStore store,
        IActivityModel model,
        CancellationToken cancellationToken)
    {
        var edits = new Dictionary<string, (ProjectCard Card, List<(int Start, int Length, string Replacement)> Edits, string? Current, HashSet<string> Touched, List<LedgerDecision> Decisions)>(StringComparer.Ordinal);
        var holds = new List<HoldNote>();
        foreach (var activity in activities.Where(activity => activity.Kind != "status_check_self").OrderBy(activity => string.IsNullOrEmpty(activity.DiscoveredFrom) ? 0 : 1).ThenBy(activity => activity.Id, StringComparer.Ordinal))
        {
            var match = await MatchAsync(activity, cards, store, model, cancellationToken);
            if (match.Card is null || match.Workstream is null)
            {
                if (match.Hold is not null)
                {
                    holds.Add(match.Hold);
                }

                continue;
            }

            var card = match.Card;
            var slice = match.Workstream;
            var action = Decide(activity);
            if (!edits.TryGetValue(card.RelativePath, out var bucket))
            {
                bucket = (card, [], null, [], []);
                edits[card.RelativePath] = bucket;
            }

            if (!bucket.Touched.Add(slice.Heading))
            {
                if (!string.IsNullOrEmpty(activity.DiscoveredFrom))
                {
                    holds.Add(new HoldNote { ActivityId = activity.Id, Reason = "独立課題は別活動として残したが、同じworkstreamへは重ねて書かない。" });
                    continue;
                }

                throw new StatusCheckException("Multiple activities selected the same workstream: " + slice.Heading);
            }

            var newline = card.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            if (action == "delete")
            {
                bucket.Edits.Add((slice.Start, slice.Length, ""));
                bucket.Current = CardParser.RewriteCurrent(bucket.Current ?? card.Current, slice.Current, CompletionFact, newline);
            }
            else
            {
                var rendered = RenderUpdate(activity, slice, newline);
                bucket.Edits.Add((slice.Start, slice.Length, rendered));
            }

            var key = Normalize.ShortHash(activity.Id + "\n" + card.RelativePath + "\n" + slice.Heading + "\n" + action + "\n" + string.Join(",", activity.PullRequests.OrderBy(pull => pull.Url, StringComparer.Ordinal).Select(pull => pull.Url + ":" + pull.State)));
            bucket.Decisions.Add(new LedgerDecision
            {
                Key = key,
                ActivityId = activity.Id,
                Workstream = slice.Heading,
                Action = action,
                RelativePath = card.RelativePath,
            });
            edits[card.RelativePath] = bucket;
        }

        var changes = new List<LedgerChange>();
        foreach (var entry in edits.Values)
        {
            if (entry.Decisions.Count == 0)
            {
                continue;
            }

            var updated = CardParser.ReplacePieces(entry.Card.Text, entry.Current, entry.Edits);
            if (updated == entry.Card.Text)
            {
                continue;
            }

            var after = CardParser.Parse(entry.Card.RelativePath, updated);
            CardParser.Validate(entry.Card, after, entry.Touched);
            if (entry.Decisions.Any(decision => decision.Action == "delete"))
            {
                foreach (var decision in entry.Decisions.Where(decision => decision.Action == "delete"))
                {
                    if (after.Workstreams.Any(workstream => workstream.Heading == decision.Workstream))
                    {
                        throw new StatusCheckException("Completed workstream was not removed: " + decision.Workstream);
                    }
                }

                var others = entry.Card.Workstreams.Where(workstream => entry.Decisions.All(decision => decision.Action != "delete" || decision.Workstream != workstream.Heading)).ToList();
                var newCurrent = after.Current;
                foreach (var other in others)
                {
                    foreach (var sentence in Normalize.SplitSentences(other.Current))
                    {
                        var trimmed = sentence.Trim();
                        if (trimmed.Length >= 8 && entry.Card.Current.Contains(trimmed, StringComparison.Ordinal) && !newCurrent.Contains(trimmed, StringComparison.Ordinal))
                        {
                            throw new StatusCheckException("Project current position dropped text that belongs to another workstream.");
                        }
                    }
                }
            }

            changes.Add(new LedgerChange
            {
                RelativePath = entry.Card.RelativePath,
                BlobBefore = "",
                NewText = updated,
                Decisions = entry.Decisions,
            });
        }

        return (changes, holds);
    }

    /// <summary>
    /// 削除は、送り切った本文に確認できる残作業がなく、活動の根拠から取った pull がすべてマージ済みのときだけ行う。
    /// workstream 本文の URL 件数やキーワードでは完了範囲を決めない。
    /// </summary>
    private static string Decide(ActivityView activity)
    {
        if (activity.CoverageComplete &&
            string.IsNullOrWhiteSpace(activity.ExplicitRemainingWork) &&
            activity.PullRequests.Count > 0 &&
            activity.PullRequests.All(pull => pull.State == PullRequestState.Merged))
        {
            return "delete";
        }

        return "update";
    }

    private static string RenderUpdate(ActivityView activity, WorkstreamSlice slice, string newline)
    {
        var references = AppendUrls(slice.References, activity.PullRequests.Select(pull => pull.Url).Concat(activity.IssueRefs.Select(issue => issue.Url)), newline);
        var pulls = activity.PullRequests;
        string current;
        string next;
        string waiting = slice.Waiting;
        if (!string.IsNullOrWhiteSpace(activity.ExplicitRemainingWork))
        {
            current = activity.ExplicitRemainingWork!;
            next = "管理側へこの残作業を戻す。";
            waiting = activity.ExplicitRemainingWork!;
        }
        else if (!activity.CoverageComplete && pulls.Count > 0 && pulls.All(pull => pull.State == PullRequestState.Merged))
        {
            current = "未送信の本文があるため、明示的な残作業がないとは扱わない。";
            next = slice.Next;
        }
        else if (pulls.Count > 0)
        {
            current = string.Join(newline, pulls.OrderBy(pull => pull.Url, StringComparer.Ordinal).Select(DescribePull));
            next = pulls.All(pull => pull.State == PullRequestState.Merged)
                ? (string.IsNullOrWhiteSpace(activity.NextAction) ? slice.Next : activity.NextAction)
                : (string.IsNullOrWhiteSpace(slice.Next) ? "未マージのPRを確認する。" : slice.Next);
        }
        else if (activity.Stages.Contains(StageNames.PlanOnly) && !activity.Stages.Contains(StageNames.ImplementationStarted))
        {
            current = "計画のみで、実装開始は記録されていない。";
            next = string.IsNullOrWhiteSpace(activity.NextAction) ? slice.Next : activity.NextAction;
        }
        else if (activity.Stages.Contains(StageNames.ImplementationReportedComplete))
        {
            current = "実装完了と報告されている。マージ済みPRは記録されていない。";
            next = string.IsNullOrWhiteSpace(activity.NextAction) ? slice.Next : activity.NextAction;
        }
        else if (activity.Stages.Contains(StageNames.ImplementationStarted))
        {
            current = string.IsNullOrWhiteSpace(activity.CurrentPosition) ? "実装開始が記録されている。" : activity.CurrentPosition;
            next = string.IsNullOrWhiteSpace(activity.NextAction) ? slice.Next : activity.NextAction;
        }
        else
        {
            current = string.IsNullOrWhiteSpace(activity.CurrentPosition) ? slice.Current : activity.CurrentPosition;
            next = string.IsNullOrWhiteSpace(activity.NextAction) ? slice.Next : activity.NextAction;
        }

        return CardParser.RenderWorkstream(slice.Heading, current, next, waiting, references, newline);
    }

    private static string DescribePull(PullRequestSnapshot pull)
    {
        var when = pull.MergedAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        return pull.State switch
        {
            PullRequestState.Merged => pull.Url + " はマージ済み" + (when is null ? "。" : "（" + when + "）。"),
            PullRequestState.Open => pull.Url + " は未マージ。",
            _ => pull.Url + " は未マージのClose。",
        };
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

    private static async Task<(ProjectCard? Card, WorkstreamSlice? Workstream, HoldNote? Hold)> MatchAsync(
        ActivityView activity,
        IReadOnlyList<ProjectCard> cards,
        StateStore store,
        IActivityModel model,
        CancellationToken cancellationToken)
    {
        var urls = activity.PullRequests.Select(pull => pull.Url).Concat(activity.IssueRefs.Select(issue => issue.Url)).Where(url => url.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var urlCards = cards.Where(card => urls.Any(url => card.Text.Contains(url, StringComparison.OrdinalIgnoreCase))).ToList();
        ProjectCard? card = null;
        if (urlCards.Count == 1)
        {
            card = urlCards[0];
        }
        else if (urlCards.Count > 1)
        {
            return (null, null, new HoldNote { ActivityId = activity.Id, Reason = "IssueまたはPRのURLが複数のProjectカードに現れている。" });
        }
        else if (!string.IsNullOrWhiteSpace(activity.Repository))
        {
            var repoCards = cards.Where(candidate => candidate.Repositories.Contains(activity.Repository, StringComparer.OrdinalIgnoreCase)).ToList();
            if (repoCards.Count == 1)
            {
                card = repoCards[0];
            }
            else
            {
                return (null, null, new HoldNote { ActivityId = activity.Id, Reason = "repositoryに一致するProjectカードを一意に決められない。" });
            }
        }
        else
        {
            return (null, null, new HoldNote { ActivityId = activity.Id, Reason = "repositoryもURLも一致しない。" });
        }

        var referenced = card.Workstreams.Where(workstream => urls.Any(url => workstream.Raw.Contains(url, StringComparison.OrdinalIgnoreCase))).ToList();
        if (referenced.Count == 0 && store.HasDeleteDecision(activity.Id, card.RelativePath))
        {
            return (null, null, null);
        }

        WorkstreamSlice? slice = referenced.Count == 1 ? referenced[0] : null;
        if (slice is null && referenced.Count == 0 && card.Workstreams.Count == 1)
        {
            slice = card.Workstreams[0];
        }

        if (slice is null && card.Workstreams.Count > 1)
        {
            var choice = await model.ChooseWorkstreamAsync(new WorkstreamChoiceRequest(activity.Repository, activity.Summary, card.Workstreams.Select(workstream => workstream.Heading).ToList()), cancellationToken);
            if (!choice.Uncertain && choice.Heading is not null)
            {
                slice = card.Workstreams.FirstOrDefault(workstream => workstream.Heading == choice.Heading);
            }
        }

        if (slice is null)
        {
            if (store.HasDeleteDecision(activity.Id, card.RelativePath))
            {
                return (null, null, null);
            }

            return (null, null, new HoldNote { ActivityId = activity.Id, Reason = "対応するworkstreamを一意に決められない。" });
        }

        return (card, slice, null);
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
