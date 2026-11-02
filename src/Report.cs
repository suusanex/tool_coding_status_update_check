using System.Text;

/// <summary>
/// 採用した依頼と最後の通常応答だけを書く。期間内の全作業を復元したとは書かない。
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
        builder.AppendLine("- 収集対象: Codex rollout。CodexAgentFacade の events.jsonl は読まない。");
        builder.AppendLine("- 各スレッドからは、最初の実質的な依頼と最後の通常応答だけを採用した。期間内の全作業を復元したものではない。");
        builder.AppendLine("- ChatGPT Chat の履歴は収集していない。");
        builder.AppendLine();
        AppendSkips(builder, model.SkippedLogs);
        AppendRecordSkips(builder, model.SkippedRecords);
        builder.AppendLine("## 採用した報告");
        builder.AppendLine();
        var reported = model.Threads.Where(view => view.Thread.HasResponse).ToList();
        if (reported.Count == 0)
        {
            builder.AppendLine("通常応答のある対象スレッドはない。");
            builder.AppendLine();
        }

        foreach (var view in reported)
        {
            AppendThread(builder, view, model.Window);
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

    internal static string WriteEstimate(TimeWindow window, DateTimeOffset queriedAt, EstimateReport estimate, IReadOnlyList<IngestSkip> skips, IReadOnlyList<ProcessingSkip> recordSkips)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 送信量の推定");
        builder.AppendLine();
        builder.AppendLine("APIは呼び出していない。これは llm_usage の実績ではない。出力トークンは推定していない。途中ログは送信対象に含めていない。");
        builder.AppendLine();
        builder.AppendLine("- 期間: " + window.FromDate.ToString("yyyy-MM-dd") + " から " + window.ToDate.ToString("yyyy-MM-dd") + " （タイムゾーン: " + window.TimeZoneId + "）");
        builder.AppendLine("- 照会時刻: " + StateStore.FormatTime(queriedAt));
        builder.AppendLine("- 対象スレッド: " + estimate.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 通常応答あり: " + estimate.WithResponse.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 通常応答なし: " + estimate.WithoutResponse.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 最初の依頼の文字数: " + estimate.RequestChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 最後の応答の文字数: " + estimate.ResponseChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- assess 呼び出し: " + estimate.AssessCalls.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- reconcile 呼び出し: " + estimate.ReconcileCalls.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- payload 文字数: " + estimate.PayloadChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 入力トークン推定: " + estimate.InputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AppendLine("- 入力トークン推定は文字数を4で割った値であり、出力は含まない。");
        builder.AppendLine("- reconcile 呼び出しは上限である。未評価のスレッドは選択前の候補 URL で見積もる。pull 状態は Open の仮値であり、gh は呼ばない。");
        builder.AppendLine("- 途中ログがpayloadに含まれる: " + (estimate.PayloadContainsToolUpdates ? "はい" : "いいえ"));
        builder.AppendLine();
        AppendSkips(builder, skips);
        AppendRecordSkips(builder, recordSkips);
        builder.AppendLine("## 採用した応答");
        builder.AppendLine();
        foreach (var thread in estimate.Selected.Where(thread => thread.HasResponse))
        {
            builder.AppendLine("- " + thread.Source + ":" + thread.NativeId + " 応答 " + Format(thread.Response?.Timestamp) + " / " + (thread.Response?.Text.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "文字");
        }

        return builder.ToString();
    }

    private static void AppendSkips(StringBuilder builder, IReadOnlyList<IngestSkip> skips)
    {
        builder.AppendLine("## 読めなかったログ");
        builder.AppendLine();
        if (skips.Count == 0)
        {
            builder.AppendLine("なし。");
        }
        else
        {
            foreach (var skip in skips)
            {
                builder.AppendLine("- " + skip.ToReport());
            }
        }

        builder.AppendLine();
    }

    private static void AppendRecordSkips(StringBuilder builder, IReadOnlyList<ProcessingSkip> skips)
    {
        builder.AppendLine("## 飛ばした対象");
        builder.AppendLine();
        if (skips.Count == 0)
        {
            builder.AppendLine("なし。");
        }
        else
        {
            foreach (var skip in skips)
            {
                builder.AppendLine("- " + skip.ToReport());
            }
        }

        builder.AppendLine();
    }

    private static void AppendThread(StringBuilder builder, ThreadView view, TimeWindow window)
    {
        var thread = view.Thread;
        var summary = string.IsNullOrWhiteSpace(view.Assessment?.Summary) ? thread.NativeId : view.Assessment!.Summary;
        builder.AppendLine("### " + summary);
        builder.AppendLine();
        builder.AppendLine("- id: " + thread.Source + ":" + thread.NativeId);
        builder.AppendLine("- repository: " + (string.IsNullOrWhiteSpace(thread.Repository) ? "不明" : thread.Repository));
        builder.AppendLine("- 最初の依頼: " + Format(thread.Request?.Timestamp) + " / " + (thread.Request?.Basis ?? ""));
        builder.AppendLine("- 採用した応答: " + Format(thread.Response?.Timestamp) + " / " + (thread.Response?.Basis ?? ""));
        builder.AppendLine("- 現在地: " + (string.IsNullOrWhiteSpace(view.Assessment?.CurrentPosition) ? "記録なし" : view.Assessment!.CurrentPosition));
        builder.AppendLine("- 明示的な残作業: " + (string.IsNullOrWhiteSpace(view.Assessment?.ExplicitRemainingWork) ? "なし" : view.Assessment!.ExplicitRemainingWork));
        builder.AppendLine("- 照会時点のPR: " + PullSummary(view));
        if (view.PullLookupFailed)
        {
            builder.AppendLine("- 取得できなかったPRがある。");
        }
        foreach (var line in MergeTiming(view, window))
        {
            builder.AppendLine("- " + line);
        }

        if (!string.IsNullOrWhiteSpace(view.Assessment?.Uncertainty))
        {
            builder.AppendLine("- 不確実さ: " + view.Assessment.Uncertainty);
        }

        builder.AppendLine();
    }

    private static string PullSummary(ThreadView view)
    {
        if (view.Pulls.Count == 0)
        {
            return "なし";
        }

        return string.Join("、", view.Pulls.Select(pull => pull.Url + " は " + (pull.State switch
        {
            PullRequestState.Merged => "マージ済み",
            PullRequestState.Open => "未マージ",
            _ => "未マージのClose",
        })));
    }

    private static IEnumerable<string> MergeTiming(ThreadView view, TimeWindow window)
    {
        foreach (var pull in view.Pulls.Where(pull => pull.State == PullRequestState.Merged && pull.MergedAt is not null))
        {
            yield return window.Contains(pull.MergedAt!.Value)
                ? pull.Url + " のマージ時刻は期間内。"
                : pull.Url + " のマージ時刻は期間外。";
        }
    }

    private static string Format(DateTimeOffset? timestamp) =>
        timestamp is null ? "不明" : StateStore.FormatTime(timestamp.Value);
}

internal sealed class ReportModel
{
    public required TimeWindow Window { get; init; }
    public required DateTimeOffset QueriedAt { get; init; }
    public required IReadOnlyList<ThreadView> Threads { get; init; }
    public required IReadOnlyList<HoldNote> Holds { get; init; }
    public required string LedgerSummary { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required decimal EstimatedUsd { get; init; }
    public IReadOnlyList<IngestSkip> SkippedLogs { get; init; } = [];
    public IReadOnlyList<ProcessingSkip> SkippedRecords { get; init; } = [];
}
