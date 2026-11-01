# コーディング状況の台帳更新

同一PCの Codex rollout と CodexAgentFacade の run log を集め、課題の進み方と GitHub PR の現在状態を見て、`personal-project-driver` の該当 workstream を更新する。

ChatGPT Chat の履歴は見ない。ログの量や agent の実行時間は、本人の作業時間として扱わない。

## QuickStart

.NET SDK 11 が必要。APIキーは専用の環境変数だけを読む。

```powershell
$env:OPENAI_API_KEY_STATUS_CHECK = "<key>"
dotnet run --file src/StatusCheck.cs -- preview --last-days 2
dotnet run --file src/StatusCheck.cs -- run --last-days 7
dotnet run --file src/StatusCheck.cs -- run --from 2026-10-30 --to 2026-11-01
dotnet run --file src/StatusCheck.cs -- apply --proposal <runId>
```

初回実行で `%USERPROFILE%\.coding-status-check\config.json` を作る。状態DBとレポートは `%USERPROFILE%\.coding-status-check\` に置く。ここには redact 済みの本文も残る。このリポジトリには commit しない。

`preview` はレポートと提案だけを残し、台帳は変えない。`run` は検査を通った Project カードだけを台帳リポジトリへ commit する。`apply` は保存済み提案を、いまのカードといまの PR 状態で再検査してから書く。PR状態が提案時と違えば何も書かない。

`--last-days 2` はローカル日付の昨日と今日である。`--from` と `--to` は両端を含む。

## 収集するもの

- Codex: `%USERPROFILE%\.codex\sessions` と、あれば `archived_sessions` の `rollout-*.jsonl`
- Facade: `CODEX_AGENT_FACADE_LOG_DIR`、無ければ `%USERPROFILE%\.codex-agent-facade\runs` の `*.events.jsonl`

人間向けの Facade `.log`、`auth.json`、暗号化された reasoning は読まない。

## 台帳で workstream を消す条件

活動の根拠から取り出した pull がすべてマージ済みで、保存済み本文から確認できた明示的な残作業がなく、本文をモデルへ送り切っているときだけ、その workstream の block を削除する。Project カード自体は消さない。管理側への返却や受領は待たない。

本文を送り切っていないときは削除しない。それは残作業なしではない。workstream 本文にある URL は、カードと workstream を選ぶために使う。完了判定には、その活動の根拠から取り出した pull の GitHub 状態だけを使う。複数PRがまだ必要だという記述は、モデルが残作業の引用として返したものを台帳に残す。

明示的な残作業があるときは文言を残し、次のアクションを「管理側へこの残作業を戻す。」にする。メッセージ送信やPRのマージはしない。

## テスト

```powershell
dotnet run --file tests/StatusCheck.Tests.cs
```

LLM と `gh` はテスト内の偽物に差し替える。実ログでの preview は `OPENAI_API_KEY_STATUS_CHECK` があるときだけ別途行う。
