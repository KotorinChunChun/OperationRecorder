# 記録から操作を再現する

OperationRecorder 0.1.5以降は、記録をリプレイ用configへ変換し、コマンドまたはps1から実行できる。configを指定しただけで通常起動時に実行されることはない。

毎回クラス名やキャプションが変わるアプリでは、0.1.6以降の[複数記録から条件を作る機能](replay-learning.md)でCodex CLIに比較させ、正規表現/条件式の案を作成できる。

## 作成・確認・実行

配布フォルダーのPowerShell 7から実行する。

```powershell
# 元記録を変更せずconfigへ変換する
.\OperationRecorder.exe --replay import --source 'C:\OperationRecords\session-example' --out 'C:\Replay\job.json' | Write-Output

# configを編集した後、入力を送らず不足や不正値を確認する
.\OperationRecorder.exe --replay check --config 'C:\Replay\job.json' | Write-Output

# 開始条件に従って一回実行する
.\OperationRecorder.exe --replay run --config 'C:\Replay\job.json' --logs 'C:\Replay\logs' | Write-Output

# 繰り返し/キー/アプリ起動を監視する
.\OperationRecorder.exe --replay watch --config 'C:\Replay\job.json' | Write-Output
```

ps1からも同じ処理を呼べる。既定のModeは入力を送らない`check`。

```powershell
pwsh -NoProfile -File .\scripts\Invoke-OperationReplay.ps1 -Config 'C:\Replay\job.json' -Mode check
pwsh -NoProfile -File .\scripts\Invoke-OperationReplay.ps1 -Config 'C:\Replay\job.json' -Mode watch
```

起動中の通常OperationRecorderと再生は競合するため、記録を保存して通常アプリを終了してから実行する。チェックと変換は常駐中でも行える。別のリプレイが実行/監視中なら新しい実行を拒否する。

元記録にEXEの絶対パス、安定したUI要素、入力値が無い場合、変換結果は`draft`となる。config内の`targets`や該当手順を補正する。passwordは環境変数で補完し、通常入力でも保存していなかった値は補完が必要。未対応手順は、意図を確認して`skip:true`と`skipReason`を明示するまで実行できない。

## 条件を監視して待つ

実行中は「ウィンドウを探しています」「クラスを探しています」「該当するUI要素を探しています」「該当画像になるのを待っています」と状態を表示する。一致が安定するまで操作せず、操作後の画像条件がある場合も監視する。

画像待機の表示には、現在の一致度と設定した条件値も出す。

`timeoutMs`は手順の待機上限。既定30000（30秒）、0は明示した無期限待ち。手順ごとの`timeoutMs`で上書きできる。`stableMs`（既定200）だけ一致が続くことを確認する。`triggerTimeoutMs`は開始条件の待機上限で、0なら監視を続ける。

通常のキャンセルはCtrl+C、非常停止は既定Ctrl+Alt+Pause。開始キーとの重複/予約競合を検出したら入力前に終了する。前面が指定対象から外れたとき、画面外/無効な対象、複数候補、ロック画面でも停止する。条件を無視した続行や、失敗した操作の自動再試行はしない。

## 開始条件

`triggers`のいずれかが成立したら実行する。`run`は一回、`watch`は`maxRuns`回（0ならキャンセルまで）監視する。開始要求が実行中に来ても、後から蓄積分を連続実行しない。

| type | 指定例 | 動作 |
|---|---|---|
| immediate | `{"type":"immediate"}` | 起動後の初回だけ |
| at | `{"type":"at","at":"2026-11-02T09:00:00+09:00","lateGraceSeconds":30}` | UTCまたはoffset付きの指定日時。遅刻幅を超えた日時だけなら拒否 |
| interval | `{"type":"interval","intervalSeconds":300}` | 監視開始/前回完了から指定秒数後 |
| daily | `{"type":"daily","time":"09:00","timeZone":"Tokyo Standard Time","lateGraceSeconds":30}` | 指定timezoneの日次時刻。DSTの同じ日を二度実行せず、存在しない/過ぎた時刻は追実行しない |
| hotkey | `{"type":"hotkey","key":"Ctrl+Alt+F20"}` | 指定キー。OS登録できなければ実行しない |
| application | `{"type":"application","target":"window1"}` | bindingのEXEに対応するウィンドウが現れたとき。最初から存在する場合も初回発火し、同じプロセスのままでは再発火しない |

`--now`は開始条件を待たず、一回目を直ちに開始する明示指定。実際の操作前には`startDelayMs`（既定1000）を待つ。

**監視プロセスが動作している間の機能であり、Windowsの予約へ自動登録しない。** PC停止/スリープ/ログアウトをまたぐ実行、PCの自動復帰は保証しない。既存の外部ジョブから同じps1/EXEを起動する場合も、ログイン済みの対話デスクトップが必要。サービスや「ログオンしていなくても実行」の無人セッションでは入力できない。

## 対象・解像度の違い

`targets`はEXEの絶対パス、`title`、`classPrefix`を指定する。記録時のPID/HWNDは使い回さない。`processId`は現在の特定インスタンスへ拘束するときだけ指定する。タイトルが変化するアプリでは`titleMatch:"contains"`と安定した部分を指定できる。タイトルを空にしても候補が複数なら停止する。

各手順は`element`のAutomationId/ControlTypeを優先する。意味のない数値だけのIDは変換時に除き、名前＋種類を補う。ウィンドウ位置やサイズが変わっても現在の要素を探索し、その内側の`x/y`（0～1の比率）へ入力する。UIAで見つからないからといって座標へ自動降格しない。

UIAが無い画面で座標を使う場合だけ、`positionMode:"window"`として`x/y/endX/endY`をウィンドウ全体に対する比率へ明示補正する。変換時のelement比率のまま切り替えない。記録時の寸法、`maxAspectChange`、DPIを確認し、差が条件を超えれば停止する。`allowDpiChange:true`はDPI差を許す明示指定。古い記録にはDPIが無い場合があるため、UIAを優先する。

ドラッグの終点が元のUI要素の外にある場合も、座標を勝手に切り詰めず不正として知らせる。ウィンドウ比率へ補正するか、新しい記録/対象指定へ直す。

## 画像条件・入力値

`beforeImage`/`afterImage`は`file`、画像内の`crop`（x/y/w/h）、`threshold`（0～1、既定0.98）を指定する。記録画像から当時のウィンドウ部分が分かるときは、自動で条件を作る。現在のウィンドウ画像と縮尺を合わせて色差を比較する。

これは画面全体からボタンを推測してクリックする機能ではない。動く時計、動画、テーマ変更、可変の入力内容、大幅なレイアウト変更で一致しない場合は待機/停止する。必要な手順の画像だけを条件にし、閾値や参照画像を調整する。画像条件を外しても対象/要素の照合は残る。

`input`はUIAのValuePatternで値を設定する。`text:{"literal":"通常の入力値"}`または`text:{"environment":"REPLAY_VALUE"}`の一方だけを使う。実際のpassword欄も環境変数参照を必須とする。UIAで値を設定できない欄、取得できない元入力、IME/ゲーム等の生キーイベントの完全な再現には対応しない。

`launch`手順はbindingのEXEと明示した`arguments`配列を起動する。記録したコマンドラインを勝手に実行しない。UACの自動承認や自動昇格は行わない。

## 結果

標準出力は結果JSON、状態表示は標準エラーへ出す。終了0は完了、1は実行失敗、2は引数/config不足、3はキャンセル。変換の`draft`は変換自体の完了を示し、操作可能という意味ではない。

事後画像条件がないクリックやキーの完了は、入力の送信完了を示す。外部アプリの保存や通信など、その後の処理完了まで確認したい手順には、事後画像または次の待機手順を設定する。

既定ログは`%LOCALAPPDATA%\OperationRecorder\replay-logs\run-<日時>-<GUID>`。`--logs`で保存先を変更でき、実行ごとに新しいフォルダーを作る。`progress.jsonl`には実行回数・手順・状態・時刻・理由、`result.json`には完了回数と最後の実行の完了手順数を残す。入力値や環境変数の値はログへ出さない。

キャンセルや失敗時でも、最後の操作が外部アプリ側で既に反映されている場合がある。UIA提供元への処理中呼び出しを強制的に巻き戻すことはできない。ログを確認して画面状態を合わせてから手動で再開する。自動的な続行・巻き戻し・多重再試行は行わない。元記録のJSONが変わった場合もconfigを作り直す。
