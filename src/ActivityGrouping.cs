/// <summary>
/// モデル出力を活動へそろえる。作業の同一性は引用の重なりでは決めない。
/// 確認できた独立課題は引用が重なっても残し、同じ作業は親活動へまとめる。
/// </summary>
internal static class ActivityGrouping
{
    internal static IReadOnlyList<GroupedActivity> Group(IReadOnlyList<StoredObservation> observations, IReadOnlyList<ExtractedActivity> extracted)
    {
        var bodies = observations.ToDictionary(item => item.Id, item => item.Draft.Body, StringComparer.Ordinal);
        var accepted = new List<(ExtractedActivity Item, List<EvidenceSpan> Spans)>();
        var rejectedItems = new List<ExtractedActivity>();
        foreach (var item in extracted)
        {
            if (!item.IndependentIssue && !item.UncertainMerge)
            {
                continue;
            }

            var spans = ConfirmSpans(item, bodies);
            if (spans is null)
            {
                rejectedItems.Add(item);
                continue;
            }

            accepted.Add((item, spans));
        }

        var holes = accepted.SelectMany(pair => pair.Spans).ToList();
        var primarySpans = new List<EvidenceSpan>();
        foreach (var observation in observations)
        {
            var observationHoles = holes.Where(span => span.ObservationId == observation.Id).ToList();
            var complement = Complement(observation.Id, observation.Draft.Body, observationHoles).ToList();
            if (complement.Count == 0 && observationHoles.Count > 0)
            {
                primarySpans.AddRange(Piece(observation.Id, observation.Draft.Body, 0, observation.Draft.Body.Length));
            }
            else
            {
                primarySpans.AddRange(complement);
            }
        }

        var primaryItems = extracted.Where(item => !item.IndependentIssue && !item.UncertainMerge).ToList();
        var grouped = new List<GroupedActivity>();
        GroupedActivity? primary = null;
        if (primarySpans.Count > 0 || accepted.Count == 0)
        {
            var notes = new List<string>();
            if (rejectedItems.Count > 0)
            {
                notes.Add("独立課題の引用を確認できないため親活動に残した。");
            }

            if (primaryItems.Count == 0 && extracted.Count > 0 && accepted.Count == 0)
            {
                notes.Add("モデルが親活動を返さなかった。");
            }

            primary = Build(primaryItems.Concat(rejectedItems).ToList(), primarySpans, false, null, notes);
            grouped.Add(primary);
        }

        foreach (var pair in accepted)
        {
            grouped.Add(Build([pair.Item], pair.Spans, pair.Item.IndependentIssue, primary?.Id, []));
        }

        var distinct = new List<GroupedActivity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activity in grouped)
        {
            if (seen.Add(activity.Id))
            {
                distinct.Add(activity);
            }
        }

        return distinct;
    }

    private static GroupedActivity Build(IReadOnlyList<ExtractedActivity> items, IReadOnlyList<EvidenceSpan> spans, bool independent, string? discoveredFrom, IReadOnlyList<string> notes)
    {
        var quoteText = string.Join('\n', spans.Select(span => span.Quote));
        var bodies = spans.Select(span => span.Quote).ToList();
        string? remaining = null;
        var stages = new List<string>();
        var summaries = new List<string>();
        var purposes = new List<string>();
        var changes = new List<string>();
        var positions = new List<string>();
        var uncertainties = new List<string>(notes);
        var actions = new List<string>();
        var confidence = 0d;
        foreach (var item in items)
        {
            remaining ??= Normalize.ConfirmQuote(item.ExplicitRemainingWork, bodies);
            foreach (var stage in item.Stages)
            {
                if (StageNames.Reported.Contains(stage) && !stages.Contains(stage, StringComparer.Ordinal))
                {
                    stages.Add(stage);
                }
            }

            if (!string.IsNullOrWhiteSpace(item.Summary))
            {
                summaries.Add(item.Summary.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.Purpose))
            {
                purposes.Add(item.Purpose.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.PeriodChange))
            {
                changes.Add(item.PeriodChange.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.CurrentPosition))
            {
                positions.Add(item.CurrentPosition.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.Uncertainty))
            {
                uncertainties.Add(item.Uncertainty.Trim());
            }

            if (!string.IsNullOrWhiteSpace(item.NextAction))
            {
                actions.Add(item.NextAction.Trim());
            }

            confidence = Math.Max(confidence, item.Confidence);
        }

        var refs = Normalize.GitHubRefs(quoteText);
        return new GroupedActivity
        {
            Id = Normalize.ActivityId(independent ? "independent\n" + quoteText : "primary", spans),
            ObservationIds = spans.Select(span => span.ObservationId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Spans = spans,
            Summary = summaries.Count == 0 ? "活動の要約は未確定。" : string.Join(" / ", summaries.Distinct(StringComparer.Ordinal)),
            Purpose = purposes.FirstOrDefault() ?? "",
            Stages = stages,
            PeriodChange = changes.FirstOrDefault() ?? "",
            CurrentPosition = positions.FirstOrDefault() ?? "",
            ExplicitRemainingWork = remaining,
            Uncertainty = string.Join(" ", uncertainties.Distinct(StringComparer.Ordinal)),
            Confidence = confidence,
            NextAction = actions.FirstOrDefault() ?? "",
            DiscoveredFrom = discoveredFrom,
            IndependentIssue = independent,
            Refs = refs,
        };
    }

    private static List<EvidenceSpan>? ConfirmSpans(ExtractedActivity item, IReadOnlyDictionary<string, string> bodies)
    {
        if (item.EvidenceQuotes.Count == 0)
        {
            return null;
        }

        var spans = new List<EvidenceSpan>();
        foreach (var quote in item.EvidenceQuotes)
        {
            if (!bodies.TryGetValue(quote.ObservationId, out var body))
            {
                return null;
            }

            var text = quote.Quote.Trim();
            var start = text.Length == 0 ? -1 : body.IndexOf(text, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            var span = new EvidenceSpan(quote.ObservationId, text, start, text.Length);
            if (Overlaps([span], spans))
            {
                continue;
            }

            spans.Add(span);
        }

        return spans;
    }

    private static bool Overlaps(IEnumerable<EvidenceSpan> left, IEnumerable<EvidenceSpan> right)
    {
        foreach (var one in left)
        {
            foreach (var other in right)
            {
                if (!string.Equals(one.ObservationId, other.ObservationId, StringComparison.Ordinal))
                {
                    continue;
                }

                var oneEnd = one.Start + one.Length;
                var otherEnd = other.Start + other.Length;
                if (one.Start < otherEnd && other.Start < oneEnd)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<EvidenceSpan> Complement(string observationId, string body, IEnumerable<EvidenceSpan> holes)
    {
        var cursor = 0;
        foreach (var hole in holes.OrderBy(span => span.Start).ThenBy(span => span.Length))
        {
            if (hole.Start > cursor)
            {
                foreach (var span in Piece(observationId, body, cursor, hole.Start))
                {
                    yield return span;
                }
            }

            cursor = Math.Max(cursor, hole.Start + hole.Length);
        }

        if (cursor <= body.Length)
        {
            foreach (var span in Piece(observationId, body, cursor, body.Length))
            {
                yield return span;
            }
        }
    }

    private static IEnumerable<EvidenceSpan> Piece(string observationId, string body, int start, int end)
    {
        if (end <= start)
        {
            yield break;
        }

        var raw = body[start..end];
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        var offset = raw.IndexOf(trimmed, StringComparison.Ordinal);
        yield return new EvidenceSpan(observationId, trimmed, start + offset, trimmed.Length);
    }
}

internal sealed class GroupedActivity
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> ObservationIds { get; init; }
    public required IReadOnlyList<EvidenceSpan> Spans { get; init; }
    public required string Summary { get; init; }
    public required string Purpose { get; init; }
    public required IReadOnlyList<string> Stages { get; init; }
    public required string PeriodChange { get; init; }
    public required string CurrentPosition { get; init; }
    public string? ExplicitRemainingWork { get; init; }
    public required string Uncertainty { get; init; }
    public double Confidence { get; init; }
    public required string NextAction { get; init; }
    public string? DiscoveredFrom { get; init; }
    public bool IndependentIssue { get; init; }
    public required IReadOnlyList<GitHubRef> Refs { get; init; }
}
