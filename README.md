# コーディング状況の台帳更新

同一PCの Codex rollout から、各スレッドの対話ターンと結果根拠を読み取り、`personal-project-driver` の該当 workstream を更新する。

このツールは case-coordinator を置き換えない。通常の反映で漏れた台帳の更新を、ローカルログから補う。コーディングエージェントの各スレッドは、ワークツリー上の一つの仕事を担当する前提にする。長大な途中のツール操作ログやファイル全文はモデルへ送らない。

ChatGPT Chat の履歴は見ない。CodexAgentFacade の events.jsonl も読まない。ログの量や agent の実行時間は、本人の作業時間として扱わない。

## QuickStart

.NET SDK 11 が必要。APIキーは専用の環境変数だけを読む。

```powershell
$env:OPENAI_API_KEY_STATUS_CHECK = "<key>"
dotnet run --file src/StatusCheck.cs -- estimate --last-days 7
dotnet run --file src/StatusCheck.cs -- estimate --last-days 7 --mode C
dotnet run --file src/StatusCheck.cs -- preview --last-days 2
dotnet run --file src/StatusCheck.cs -- run --last-days 7
dotnet run --file src/StatusCheck.cs -- run --from 2026-10-30 --to 2026-11-01
dotnet run --file src/StatusCheck.cs -- apply --proposal <runId>
```

初回実行で `%USERPROFILE%\.coding-status-check\config.json` を作る。状態DBとレポートは `%USERPROFILE%\.coding-status-check\` に置く。各実行は `reports/<runId>/report.md` と段階別診断トレース `reports/<runId>/trace.json` を出力する。ここには redact 済みの本文も残る。このリポジトリには commit しない。

`estimate` は API も gh も呼ばず、同じ選択と同じプロンプト組み立てで入力トークン数の見込みと段階別診断（`trace.json`）だけを出す。台帳も変えない。reconcile の回数は上限で、pull 状態は Open の仮値である。`preview` はレポート・診断トレース・提案だけを残し、台帳は変えない。`run` は検査を通った Project カードだけを台帳リポジトリへ commit する。`apply` は保存済み提案を、いまのカードといまの PR 状態で再検査してから書く。PR状態が提案時と違えば何も書かない。

`run` または `apply` が1枚以上のカードを commit した場合、同じ `reports/<runId>/` に `application.json` を原子的に作成する。receipt には `runId`、適用時刻（UTC）、適用カードパス、commit SHA を記録する。preview の report/proposal は変更せず、receipt は実際に適用が成功した事実を別ファイルとして残す。変更カードがない実行では receipt を作らない。

各CLI実行は `command`、`runId`、コンパイル済みアーティファクトの MVID、実行時カレントディレクトリ、入力モード、台帳 checkout を表示する。MVID はロードされたバイナリの識別子であり、現在のソースや Git HEAD の識別ではない。preview の出力は未適用であることと、対応する `apply --proposal <runId>` コマンドを明記する。古い report と新しい実行の出力を混同しないよう、runId とアーティファクト表示を確認する。

標準的な流れは `estimate` → `preview` で内容を確認 → 必要なら同じ runId を `apply` する。直接 commit する意図がある場合だけ `run` を使う。`apply` は必ず既存の proposal runId を指定する。

`--last-days 2` はローカル日付の昨日と今日である。`--from` と `--to` は両端を含む。期間は対象スレッドを選ぶために使う。選ばれたスレッドの最初の依頼は、期間外でも使う。レポートに書く応答日時は、採用した通常応答の時刻である。

## 入力モード（`--mode` / `inputMode`）

`config.json` の `"inputMode"` または CLI の `--mode <A|A+URL|A+Match|B|C>` で入力・照合方式を切り替えられる（既定値は `C`）。

- `A`: 最初の実質的なユーザー依頼と最後の通常応答のみを渡す従来方式。
- `A+URL`: `A` に加え、`Issue #N` と明示的な `PR #N` を分離抽出し、commentary / tool output 内の同一リポジトリ GitHub PR/Issue URL も機械抽出する。
- `A+Match`: `A+URL` に加え、同一リポジトリを共有する複数 Project カード間で Issue 番号・識別キーワード・workstream 見出し・進行中 workstream の有無によるカード特定を行う。
- `B`: `A+Match` に加え、スレッド内の時系列順のユーザー発言・通常応答（`turns`）と、最後の通常応答後の未応答ユーザー発言（`trailingUnansweredUserTurn`）を渡す。
- `C`（既定）: `B` に加え、tool output から状態・PR・テスト結果等の短い根拠候補行（`signals`、1件最大300文字・スレッドあたり最大8件）を渡す。

## 収集するもの

- Codex: `%USERPROFILE%\.codex\sessions` と、あれば `archived_sessions` の `rollout-*.jsonl`

人間向けの `.log`、`auth.json`、暗号化された reasoning、CodexAgentFacade の `*.events.jsonl` は読まない。

- ユーザー発言は環境情報、指示ファイル、権限、skill だけのメッセージを除外して時系列順に保持する。
- 通常応答は `phase=final_answer`、または phase が無い assistant message を時系列順に保持する（commentary は応答本文としては使わない）。
- 最後の通常応答の後に未応答のユーザー発言がある場合、`trailingUnansweredUserTurn` として検出し、完了削除（`delete`）を抑止する。

通常応答が一度もないスレッドは台帳を更新しない。その状態を「結果未取得」とは書かない。以前の通常応答があるとき、新しい実行開始や途中ログだけでは、その応答を破棄しない。

JSON として読めない完結した行と、UTF-8 として読めないチャンクはレポートに残して読み飛ばす。実行は止めない。末尾に改行が無い行は、追記が終わるまで読まない。

読めないログファイルや Project カード、1スレッドの評価、1件の PR 状態取得、1枚のカード照合が失敗した場合も、対象と理由をレポートに残して飛ばす。実行は止めない。費用の上限に達した場合と、台帳への書き込み検査に失敗した場合は止める。

## 台帳と診断トレース

`projects/*.md` の現在地と進行中 workstream の欄だけを更新する。workstream が一つもないカードも対象になり、モデルが `__project_current__` を返した場合は、そのカードの Project 全体の「現在地」を更新する。Project を特定するときは `projects/*.md` を全件読む。無関係な workstream、目的、front matter、参照は残す。

workstream 削除は独立した条件をすべて満たす場合だけ行う。対応する PR がすべてマージ済みであることに加え、未解消の明示的な残作業・未応答のユーザー訂正や追加依頼・残作業引用の検証失敗（`remainingWorkUnverified`）がないことが必要である。PR がマージ済みでも残作業があれば削除しない。削除は保存済みの対応付けだけでは行わず、いまの台帳本文と照合する。Project カード自体は消さない。管理側への返却や受領は待たない。

照合が名指しした報告だけを、その workstream の完了判定と欄の更新に使う。現在地、次のアクション、待ちは完成後の値で、変えない欄は null、空にする欄は空文字である。カードに紐づいた報告のうち、どの workstream の `reportIds` にも選ばれなかった報告は `reconcile_report_unselected_no_reason` としてレポートと `trace.json` に記録する。

明示的な Issue/PR URL などから関連カードが候補に出ていても、照合結果で選ばれていないカードへの更新は推測しない。レポートの保留・候補理由を確認し、関連カードへの反映が必要なら、そのカードを含む期間で次回 `preview` を行って再確認する。

明示的な残作業があるときは、該当 turn / signal からの逐語引用を確認したうえで文言を残し、次のアクションを「管理側へこの残作業を戻す。」にする。モデルが残作業を返したが本文中に逐語一致が確認できない場合は、台帳本文へは書かず `guard_rejected_unverified_remaining_quote` として完了削除を止める。最初の依頼や中間ターンにあった予定が後続のターンで明示的に完了していれば、その予定は残作業に戻さない。また、本来の作業完了後に別件の使い方質問へ一般的な案内（「〜するには〜してください」等）を返した場合は残作業として誤抽出しない。

## 以前の状態からの切り替え

分析の入力モデル・プロンプト版数・モードが変わった場合は、fingerprint により評価キャッシュが自動的に無効化され、ログは先頭から再走査される。既存DBの古い observations は残すが、新しい判断には使わない。API設定と台帳の checkout は消さない。

## テスト

```powershell
dotnet run --file tests/StatusCheck.Tests.cs
```

LLM と `gh` はテスト内の偽物に差し替える。実ログの送信量は `estimate` で見る。検証のために API は呼ばない。
