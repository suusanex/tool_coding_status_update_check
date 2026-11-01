# コーディング状況の台帳更新

同一PCの Codex rollout から、各スレッドの最初の実質的な依頼と最後の通常応答だけを見て、`personal-project-driver` の該当 workstream を更新する。

このツールは case-coordinator を置き換えない。通常の反映で漏れた台帳の更新を、ローカルログから補う。コーディングエージェントの各スレッドは、ワークツリー上の一つの仕事を担当する前提にする。途中の実装、調査、ツール操作は分析しない。

ChatGPT Chat の履歴は見ない。CodexAgentFacade の events.jsonl も読まない。ログの量や agent の実行時間は、本人の作業時間として扱わない。

## QuickStart

.NET SDK 11 が必要。APIキーは専用の環境変数だけを読む。

```powershell
$env:OPENAI_API_KEY_STATUS_CHECK = "<key>"
dotnet run --file src/StatusCheck.cs -- estimate --last-days 7
dotnet run --file src/StatusCheck.cs -- preview --last-days 2
dotnet run --file src/StatusCheck.cs -- run --last-days 7
dotnet run --file src/StatusCheck.cs -- run --from 2026-10-30 --to 2026-11-01
dotnet run --file src/StatusCheck.cs -- apply --proposal <runId>
```

初回実行で `%USERPROFILE%\.coding-status-check\config.json` を作る。状態DBとレポートは `%USERPROFILE%\.coding-status-check\` に置く。ここには redact 済みの本文も残る。このリポジトリには commit しない。

`estimate` は API も gh も呼ばず、同じ選択と同じプロンプト組み立てで入力トークン数の見込みだけを出す。台帳も変えない。reconcile の回数は上限で、pull 状態は Open の仮値である。`preview` はレポートと提案だけを残し、台帳は変えない。`run` は検査を通った Project カードだけを台帳リポジトリへ commit する。`apply` は保存済み提案を、いまのカードといまの PR 状態で再検査してから書く。PR状態が提案時と違えば何も書かない。

`--last-days 2` はローカル日付の昨日と今日である。`--from` と `--to` は両端を含む。期間は対象スレッドを選ぶために使う。選ばれたスレッドの最初の依頼は、期間外でも使う。レポートに書く応答日時は、採用した通常応答の時刻である。

## 収集するもの

- Codex: `%USERPROFILE%\.codex\sessions` と、あれば `archived_sessions` の `rollout-*.jsonl`

人間向けの `.log`、`auth.json`、暗号化された reasoning、CodexAgentFacade の `*.events.jsonl` は読まない。

各スレッドから LLM へ渡す本文は次だけである。

- 最初の実質的なユーザー依頼。環境情報、指示ファイル、権限、skill だけのメッセージは依頼にしない
- 最後の通常応答。`phase=final_answer`、または phase が無い assistant message。commentary、途中応答、ツール引数、ツール結果は渡さない

通常応答が一度もないスレッドは台帳を更新しない。その状態を「結果未取得」とは書かない。以前の通常応答があるとき、新しい実行開始や途中ログだけでは、その応答を破棄しない。

JSON として読めない完結した行と、UTF-8 として読めないチャンクはレポートに残して読み飛ばす。実行は止めない。末尾に改行が無い行は、追記が終わるまで読まない。

## 台帳

`projects/*.md` の現在地だけを更新する。Project を特定するときは `projects/*.md` を全件読む。無関係な workstream、目的、front matter、参照は残す。

対応する PR がすべてマージ済みで、最後の通常応答に明示的な残作業がなければ、その workstream の block を削除する。ログ全文をモデルへ送ったことは条件にしない。Project カード自体は消さない。管理側への返却や受領は待たない。

明示的な残作業があるときは文言を残し、次のアクションを「管理側へこの残作業を戻す。」にする。最初の依頼にあった予定が最後の応答で完了していれば、その予定は残作業に戻さない。PR が無い作業も反映できる。メッセージ送信や PR のマージはしない。

意味の差分がなければ更新しない。既にある具体的な本文を、より薄い定型文では上書きしない。同じ workstream に複数の報告が当たっても、そこで停止せず、時刻と内容でまとめて照合する。

## 以前の状態からの切り替え

分析の入力が変わったため、各ログは一度先頭から読み直す。既存DBの古い observations は残すが、新しい判断には使わない。API設定と台帳の checkout は消さない。

## テスト

```powershell
dotnet run --file tests/StatusCheck.Tests.cs
```

LLM と `gh` はテスト内の偽物に差し替える。実ログの送信量は `estimate` で見る。検証のために API は呼ばない。
