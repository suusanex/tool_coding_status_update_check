using System.Globalization;
using System.Text;

/// <summary>
/// 採用した報告・段階別診断・保留・台帳反映結果を書く。期間内の全作業を復元したとは書かない。
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
        builder.AppendLine("- 入力方式: " + InputModes.NormalizeMode(model.InputMode) + "。期間内の全作業を復元したものではない。");
        builder.AppendLine("- ChatGPT Chat の履歴は収集していない。");
        builder.AppendLine();
        AppendSkips(builder, model.SkippedLogs);
        AppendRecordSkips(builder, model.SkippedRecords);
        AppendDiagnostics(builder, model.Trace);
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
                var codePrefix = string.IsNullOrWhiteSpace(hold.ReasonCode) ? "" : "[" + hold.ReasonCode + "] ";
                builder.AppendLine("- " + hold.ActivityId + ": " + codePrefix + hold.Reason);
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 台帳");
        builder.AppendLine();
        builder.AppendLine(model.LedgerSummary);
        builder.AppendLine();
        builder.AppendLine("## 利用量");
        builder.AppendLine();
        builder.AppendLine("- input tokens: " + model.InputTokens.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- output tokens: " + model.OutputTokens.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- 概算USD: " + model.EstimatedUsd.ToString("0.0000", CultureInfo.InvariantCulture));
        builder.AppendLine("- 概算は標準価格（入力100万トークンあたり1USD、272Kトークン超は2USD、出力は5USD、長文は7.5USD）で、キャッシュ割引は含まない。");
        return builder.ToString();
    }

    internal static string WriteEstimate(TimeWindow window, DateTimeOffset queriedAt, EstimateReport estimate, IReadOnlyList<IngestSkip> skips, IReadOnlyList<ProcessingSkip> recordSkips, string? inputMode = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 送信量の推定");
        builder.AppendLine();
        builder.AppendLine("APIは呼び出していない。これは llm_usage の実績ではない。出力トークンは推定していない。途中ログは送信対象に含めていない。");
        builder.AppendLine();
        builder.AppendLine("- 期間: " + window.FromDate.ToString("yyyy-MM-dd") + " から " + window.ToDate.ToString("yyyy-MM-dd") + " （タイムゾーン: " + window.TimeZoneId + "）");
        builder.AppendLine("- 照会時刻: " + StateStore.FormatTime(queriedAt));
        builder.AppendLine("- 入力方式: " + InputModes.NormalizeMode(inputMode));
        builder.AppendLine("- 対象スレッド: " + estimate.Threads.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- 通常応答あり: " + estimate.WithResponse.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- 通常応答なし: " + estimate.WithoutResponse.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- 最初の依頼の文字数: " + estimate.RequestChars.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- 最後の応答の文字数: " + estimate.ResponseChars.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- assess 呼び出し: " + estimate.AssessCalls.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- reconcile 呼び出し: " + estimate.ReconcileCalls.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- payload 文字数: " + estimate.PayloadChars.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("- 入力トークン推定: " + estimate.InputTokens.ToString(CultureInfo.InvariantCulture));
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
            builder.AppendLine("- " + thread.Source + ":" + thread.NativeId + " 応答 " + Format(thread.Response?.Timestamp) + " / " + (thread.Response?.Text.Length ?? 0).ToString(CultureInfo.InvariantCulture) + "文字");
        }

        return builder.ToString();
    }

    private static void AppendDiagnostics(StringBuilder builder, RunTrace? trace)
    {
        if (trace is null)
        {
            return;
        }

        builder.AppendLine("## 段階別診断");
        builder.AppendLine();
        var preGroups = trace.PreSelection.GroupBy(item => item.ReasonCode, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in preGroups)
        {
            builder.AppendLine("- 選択前除外 `" + group.Key + "`: " + group.Count().ToString(CultureInfo.InvariantCulture) + "件");
        }

        var threadGroups = trace.Threads.GroupBy(item => item.Stage + " / " + item.ReasonCode, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in threadGroups)
        {
            builder.AppendLine("- スレッド到達 `" + group.Key + "`: " + group.Count().ToString(CultureInfo.InvariantCulture) + "件");
        }

        foreach (var card in trace.Cards)
        {
            builder.AppendLine("- カード `" + card.RelativePath + "`: stage=`" + card.Stage + "` reason=`" + card.ReasonCode + "` (" + card.ReasonDetail + ")");
            foreach (var ws in card.WorkstreamDiagnostics)
            {
                builder.AppendLine("  - workstream `" + ws.Workstream + "` (" + ws.ProposedAction + "): stage=`" + ws.Stage + "` reason=`" + ws.ReasonCode + "` applied=" + (ws.Applied ? "true" : "false") + " — " + ws.ReasonDetail);
            }

            if (card.UnselectedReportIds.Count > 0)
            {
                builder.AppendLine("  - 未選択report (`reconcile_report_unselected_no_reason` / 未選択・理由未提供): " + string.Join(", ", card.UnselectedReportIds));
            }
        }

        builder.AppendLine();
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
        builder.AppendLine("- ターン数: " + thread.Turns.Count.ToString(CultureInfo.InvariantCulture) + (thread.HasTrailingUnansweredUserTurn ? " （末尾に未応答のユーザー発話あり）" : ""));
        if (thread.Truncated)
        {
            builder.AppendLine("- 本文切り詰め: あり (`body_truncated`)");
        }

        builder.AppendLine("- 現在地: " + (string.IsNullOrWhiteSpace(view.Assessment?.CurrentPosition) ? "記録なし" : view.Assessment!.CurrentPosition));
        builder.AppendLine("- 明示的な残作業: " + (string.IsNullOrWhiteSpace(view.Assessment?.ExplicitRemainingWork) ? "なし" : view.Assessment!.ExplicitRemainingWork));
        if (view.Assessment?.RemainingWorkUnverified == true)
        {
            builder.AppendLine("- 残作業の引用検証: 未検証 (`remaining_quote_unverified` — 完了削除を保留)");
        }

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
    public string InputMode { get; init; } = InputModes.Default;
    public required IReadOnlyList<ThreadView> Threads { get; init; }
    public required IReadOnlyList<HoldNote> Holds { get; init; }
    public required string LedgerSummary { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required decimal EstimatedUsd { get; init; }
    public IReadOnlyList<IngestSkip> SkippedLogs { get; init; } = [];
    public IReadOnlyList<ProcessingSkip> SkippedRecords { get; init; } = [];
    public RunTrace? Trace { get; init; }
}
