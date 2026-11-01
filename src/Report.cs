using System.Text;

/// <summary>
/// 日本語の活動レポート。期間中の変化と、照会時刻のPR状態を分けて書く。
/// </summary>
internal static class ReportWriter
{
    internal static string Write(ReportModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 活動状況");
        builder.AppendLine();
        builder.AppendLine("- 期間: " + model.Window.FromDate.ToString("yyyy-MM-dd") + " から " + model.Window.ToDate.ToString("yyyy-MM-dd") + " （タイムゾーン: " + model.Window.TimeZoneId + "）");
        builder.AppendLine("- 照会時刻: " + StateStore.FormatTime(model.QueriedAt));
        builder.AppendLine("- 収集対象: Codex rollout と CodexAgentFacade の events.jsonl");
        builder.AppendLine("- ChatGPT Chat の履歴は収集していない。");
        builder.AppendLine();
        builder.AppendLine("## 活動");
        builder.AppendLine();
        foreach (var activity in model.Activities.Where(activity => activity.Kind != "status_check_self"))
        {
            AppendActivity(builder, activity, model.Window);
        }

        if (model.Activities.All(activity => activity.Kind == "status_check_self"))
        {
            builder.AppendLine("期間内の対象活動はない。");
            builder.AppendLine();
        }

        builder.AppendLine("## このツール自身の実行");
        builder.AppendLine();
        var self = model.Activities.Where(activity => activity.Kind == "status_check_self").ToList();
        if (self.Count == 0)
        {
            builder.AppendLine("なし。");
            builder.AppendLine();
        }

        foreach (var activity in self)
        {
            AppendActivity(builder, activity, model.Window);
        }

        builder.AppendLine("## 保留");
        builder.AppendLine();
        if (model.Holds.Count == 0)
        {
            builder.AppendLine("なし。");
        }
        else
        {
            foreach (var hold in model.Holds)
            {
                builder.AppendLine("- " + hold.ActivityId + ": " + hold.Reason);
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 集中した活動");
        builder.AppendLine();
        if (model.Concentrations.Count == 0)
        {
            builder.AppendLine("観測された集中先はない。");
        }
        else
        {
            foreach (var item in model.Concentrations.Take(10))
            {
                builder.AppendLine("- " + item.Title + "（" + item.ActivityId + "）: " + item.Reason);
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 台帳");
        builder.AppendLine();
        builder.AppendLine(model.LedgerSummary);
        builder.AppendLine();
        builder.AppendLine("## 利用量");
        builder.AppendLine();
        builder.AppendLine("- input tokens: " + model.InputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- output tokens: " + model.OutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 概算USD: " + model.EstimatedUsd.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 概算は標準価格（入力100万トークンあたり1USD、272Kトークン超は2USD、出力は5USD、長文は7.5USD）で、キャッシュ割引は含まない。");
        return builder.ToString();
    }

    private static void AppendActivity(StringBuilder builder, ActivityView activity, TimeWindow window)
    {
        builder.AppendLine("### " + activity.Summary);
        builder.AppendLine();
        builder.AppendLine("- repository: " + (string.IsNullOrWhiteSpace(activity.Repository) ? "不明" : activity.Repository));
        builder.AppendLine("- 段階: " + (activity.Stages.Count == 0 ? "不明" : string.Join("、", activity.Stages.Select(StageNames.Label))));
        builder.AppendLine("- 期間中の変化: " + (activity.InWindow ? activity.PeriodChange : "窓内のログ追記はない。"));
        builder.AppendLine("- 現在地: " + activity.CurrentPosition);
        builder.AppendLine("- 照会時点のPR: " + PullSummary(activity));
        builder.AppendLine("- マージ時刻と期間: " + MergeTiming(activity, window));
        builder.AppendLine("- 根拠: " + string.Join("、", activity.Evidence));
        builder.AppendLine("- 不確実さ: " + (string.IsNullOrWhiteSpace(activity.Uncertainty) ? "記録なし" : activity.Uncertainty));
        builder.AppendLine("- 確信度: " + activity.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(activity.DiscoveredFrom))
        {
            builder.AppendLine("- 発見元: " + activity.DiscoveredFrom);
        }

        builder.AppendLine();
    }

    private static string PullSummary(ActivityView activity)
    {
        if (activity.PullRequests.Count == 0)
        {
            return "なし";
        }

        return string.Join("、", activity.PullRequests.Select(pull => pull.Url + " は " + (pull.State switch
        {
            PullRequestState.Merged => "マージ済み",
            PullRequestState.Open => "未マージ",
            _ => "未マージのClose",
        })));
    }

    private static string MergeTiming(ActivityView activity, TimeWindow window)
    {
        var merged = activity.PullRequests.Where(pull => pull.State == PullRequestState.Merged && pull.MergedAt is not null).ToList();
        if (merged.Count == 0)
        {
            return "照会時点で期間内のマージ時刻は取れていない。";
        }

        return string.Join(" ", merged.Select(pull => window.Contains(pull.MergedAt!.Value)
            ? pull.Url + " のマージ時刻は期間内。"
            : pull.Url + " のマージ時刻は期間外のため、期間中の成果には含めない。"));
    }
}

internal sealed class ReportModel
{
    public required TimeWindow Window { get; init; }
    public required DateTimeOffset QueriedAt { get; init; }
    public required IReadOnlyList<ActivityView> Activities { get; init; }
    public required IReadOnlyList<HoldNote> Holds { get; init; }
    public required IReadOnlyList<ConcentrationItem> Concentrations { get; init; }
    public required string LedgerSummary { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required decimal EstimatedUsd { get; init; }
}
