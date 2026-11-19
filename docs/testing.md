# コマンドラインからの動作テスト

OperationRecorder 0.1.5以降は、画面を手で操作せず、PowerShellから機能試験を実行できる。GUIと同じ編集セッション、設定・テンプレート・出力サービス、実フォームのコントロール、実EXEを通す。Computer Useは必要ない。

## 一括実行

ウィンドウの表示・切り替えはCoreで重複抑止・操作関連付け・手順文・リプレイ変換を確認する。`WindowObservationTests`は専用の別PID fixtureへパイプ命令だけを送り、実WinEventを確認する。キー/マウス入力は送らない。

```powershell
pwsh -NoProfile -File .\dev\scripts\Test-OperationRecorder.ps1 -Suite WindowEvents
```

この専用suiteはfixtureのビルドと環境設定も行い、利用者のOperationRecorderを停止しない。通常のDesktop一括試験では、従来どおり起動中アプリや前面の安全確認が必要になる。xUnitのケースを直接選択する場合は`OPREC_PASSIVE_WINDOW_TESTS=1`とビルド済みfixtureの絶対パス`OPREC_FIXTURE_EXE`を明示する。

Windowsと.NET 10 SDK、PowerShell 7（`pwsh`）を使用する。

```powershell
cd C:\develop\KotorinToys\tools\OperationRecorder
pwsh -NoProfile -File .\dev\scripts\Test-OperationRecorder.ps1 -Suite Core
```

| 引数 | 内容 |
|---|---|
| `-Suite Core` | 既定。単体/API、STAの実フォーム、実EXEのCLI・起動管理を実行する。OSの入力送信はしない |
| `-Suite Desktop` | 専用の別プロセスを対象に、実記録エンジンとWindowsの撮影・入力連携を検証する |
| `-Suite WindowEvents` | 表示・切り替えの判断/出力と、専用fixtureからの実WinEventを検証する。OS入力は送らない |
| `-Suite WindowGraph` | 親子/所有/世代の純粋試験と、専用窓のNative構造確認。撮影・入力なし |
| `-Suite Snapshot` | 専用WinFormsの通常記録と単発取得を並行し、親子・所有窓、OS標準ダイアログ、ダミーパスワード欄を検証 |
| `-Suite ApprovedRoi` | 専用WinFormsで固定領域の公開、動的領域の黒化、未知画面と認証の拒否を、外部PowerShellと実WGCで検証 |
| `-Suite PasswordEvidence` | 専用WinFormsで標準・カスタム欄の黒化、欄外の保存、前後ACK、移動と登録不一致の拒否を検証 |
| `-Suite WindowTransition` | 専用WinFormsで別HWNDへの工程引継ぎ、マスク済みWGC証拠、設定候補、未承認の遷移の停止を検証 |
| `-Suite Wgc` | WGC境界と専用窓、schema2負の受入。WGC_BLOCKEDは終了コード3で未受入として記録 |
| `-Suite All` | Coreに続けてDesktopを実行する |
| `-Configuration Debug\|Release` | 既定はRelease |
| `-OutputDirectory <フォルダー>` | 対象版の`dev/history/vX.Y.Z/logs/`以下へ結果を保存する。省略時はその下の`automated-<GUID>` |
| `-NoBuild` | 既にビルドした同じ構成を使う |
| `-List` | [ケース対応表](../dev/history/v0.1.13/test-cases.json)をJSONで表示する。ビルド・試験は実行しない |

```powershell
# ケース一覧だけ表示
pwsh -NoProfile -File .\dev\scripts\Test-OperationRecorder.ps1 -List

# 保存場所を指定して全層を実行
pwsh -NoProfile -File .\dev\scripts\Test-OperationRecorder.ps1 -Suite All `
  -OutputDirectory .\dev\history\v0.1.13\logs\automated-local
```

DesktopはWindowsの対話デスクトップと前面ウィンドウの一致を必要とする。通常のOperationRecorderが起動中の場合は、既存の記録やフックへ干渉するため実行を拒否し、既存プロセスを終了しない。専用fixtureだけへ入力を送り、撮影するディスプレイはfixtureが覆う。条件を満たせない場合は`blocked`となる。Coreの合格だけでDesktopの実施済みを意味しない。

`Snapshot` はPowerShell 7.6とWGCが使える対話デスクトップで実行する。別プロセスのPowerShellから本番コードの取得口を呼び出し、専用WinFormsアプリの親子・所有ウィンドウ、OS標準のMessageBoxとOK操作、ダミーパスワード欄を確認する。通常Recorderの継続は専用アプリの前後のウィンドウ操作ログで検証する。

[リプレイ](replay.md)も同じ入口で試験する。Coreはconfig変換・原本不変・待機上限・単調時計・日時/繰り返し/キー/アプリ条件・CLI/ps1の入力なし経路、DesktopはUIAによる再探索・前後画像の待機・通常/password入力・実キー・開始/非常停止キー・実EXE/ps1からの再生を検証する。専用fixtureの結果は、すべての外部アプリや物理DPI環境での成功を意味しない。

0.1.6の`ReplayLearningTests`は、正規表現と条件式、共有データの入力値除外、原本/操作の保持、複数根拠・確認後編集・出典変更を扱う。Codex CLIの外部モデルや実UIは呼び出さない。[複数記録からの条件作成](replay-learning.md)の実モデルによる生成・再生は別の確認範囲となる。ケースの存在と、その版で実際に動作テストを実行したかは分けて版別報告へ記録する。

## 結果の読み方

実行フォルダーにはTRX、標準出力・エラー等の生ログ、`summary.json`を保存する。生ログはGit管理の対象外とし、結果の要約は版別の実装報告へ残す。

```json
{
  "schemaVersion": 1,
  "version": "0.1.13",
  "status": "passed",
  "exitCode": 0,
  "startedUtc": "...",
  "endedUtc": "...",
  "suites": [
    { "name": "Core", "status": "passed", "total": 0, "passed": 0,
      "failed": 0, "skipped": 0, "trx": "...", "reason": null }
  ]
}
```

| 終了コード | 意味 |
|---|---|
| 0 | 指定したsuiteをすべて実行して合格 |
| 1 | ビルド失敗、試験失敗、想定外のskip |
| 2 | runnerの引数エラー |
| 3 | DesktopまたはWgcの環境条件を満たさず実行できない |

`total`等の件数は実行結果の値を使う。上の0は構造を示す例。`status`は`passed`、`failed`、`blocked`であり、未実行や環境不足を合格へ置き換えない。TRXの`testName`から失敗したケースとTheoryの引数を特定できる。

runnerが引数の値に誤りを見つけると、終了コード2を返す。未知のスイッチや値の欠落をPowerShell自身が引数束縛で拒否した場合は、スクリプト本体の実行前に終了コード1となる。

## 個別の再実行

外部証拠APIの非GUI試験はCoreに含む。専用Native試験は、通常の業務アプリを使わず、非アクティブの専用fixtureだけへUIA Invokeを送る。PNGの画素、実MP4の復号画素、対象識別を検証する。動画試験にはlibx264付きFFmpegの絶対パスが必要で、未設定は成功へ置き換えず失敗とする。

```powershell
$env:OPREC_EVIDENCE_TEST_FFMPEG = 'C:/tools/ffmpeg/bin/ffmpeg.exe'
dotnet test ./tests/OperationRecorderApp.Tests/OperationRecorderApp.Tests.csproj -c Release --artifacts-path ./artifacts/automation-runner --filter 'FullyQualifiedName~ExternalEvidenceNativeTests' --logger 'trx;LogFileName=evidence-native.trx' --results-directory ./dev/history/v0.1.13/logs/evidence-native
```

UIA停止の期限は、同じタスクの待機中止だけでなく、実際に別workerを無限待機/保存直後停止させて確認する。秘密は合成ダミー文字列だけを使う。[API契約](external-evidence.md)の全体マスク/geometry-onlyという検証範囲を維持し、従来の人向け記録や実業務アプリの機密保護を検証したとは扱わない。

```powershell
# UIの値受渡し・確認分岐を非表示STAで検証
dotnet test .\tests\OperationRecorderApp.Tests\OperationRecorderApp.Tests.csproj `
  --artifacts-path .\artifacts\automation-ui `
  --filter 'FullyQualifiedName~UiWiringTests|FullyQualifiedName~AppInteractionTests' `
  --logger 'trx;LogFileName=ui-wiring.trx' `
  --results-directory .\dev\history\v0.1.13\logs\ui

# 1ケースだけ指定（Theoryはすべての引数行を実行）
dotnet test .\tests\OperationRecorderApp.Tests\OperationRecorderApp.Tests.csproj `
  --filter 'FullyQualifiedName=OperationRecorderApp.Tests.UiWiringTests.操作キーの画面変更は有効な値だけ保存してメニュー表示へ反映する'
```

Desktopテストを直接起動する場合はrunnerと同じfixture/環境条件が必要になるため、通常は`-Suite Desktop`を使う。runnerは`OPREC_DESKTOP_TESTS=1`と`OPREC_FIXTURE_EXE`を専用の実行に渡す。

## 試験層と機能の対応

| 機能 | 主な実行経路 | 検証する境界 |
|---|---|---|
| 記録の開始・1手取消・取り直し・停止・保存・再開 | `RecordingLifecycleTests`、Desktop | controllerの実状態遷移、対象再利用、イベントdispatch、保存中/破棄後の操作、失敗通知。Coreではruntimeを差替え、Desktopでは実Recorder |
| Window/App/Active/Desktop/AllDesktops、対象の決定、同一プロセス、ディスプレイ | `TargetRulesTests`、`TargetSettingsTests`、`UiWiringTests`、Desktop | モードと対象方式、設定の保存、範囲・名前の判断。利用可能な実ディスプレイでの撮影はDesktop |
| クリック・ドラッグ・ホイール・キー分類、操作前後、撮影間隔 | `CaptureRulesTests`、`WheelRulesTests`、`ImageCompareTests`、`UiWiringTests`、Desktop | 全規則のRecord/Before/After受渡し、集約・相殺・画像同一判定、実フックと専用入力 |
| 操作キー変更、開始/取消/取り直し/終了の表示 | `HotkeyTests`、`UiTextsTests`、`UiWiringTests` | 有効値の保存、重複・予約キー拒否、メニューへの反映。OS登録の競合/失敗は差替えで検証 |
| オーバーレイ・対象枠・撮影通知 | `OverlayPlacementTests`、`UiTextsTests`、`UiWiringTests`、Desktop | 表示設定、直近10手、ボタン通知、取消/消去、保存中の無効化、逃避位置の計算。物理的な見た目の良さは自動判定対象外 |
| 記録一覧、単一/複数選択、未保存保護 | `CompositionEntryTests`、`ManagementTests`、`UiWiringTests` | 編集の有効/無効、一覧再読込、名前と本文の保持、モーダル・未保存時の管理停止拒否 |
| 名前・解説・手順の文章保存、自動文章へ戻す | `SessionEditorTests`、`BasicAutomationTests`、`UiWiringTests` | 実記録JSON/edits.json、保存前後の本文、再出力への反映、失敗時の警告 |
| 手順/記録の削除、確認の取消 | `SessionEditorTests`、`UiWiringTests` | 確認Yes/No、記録削除の成功/失敗。Coreは専用fixtureだけを削除し、本物のごみ箱を使用しない |
| 重複候補、採用元選択、複数元追加 | `MergeTests`、`CompositionEngineTests`、`CompositionEditingSessionTests`、`CompositionAutomationTests`、実合成フォーム | 元の記録を変更しない候補生成、追加前後の保持、無効入力の拒否 |
| 範囲差込・先頭末尾移動・並べ替え・除外/復帰・補足文章 | 同上 | GUIとCLIが共有する編集セッション、範囲の相対順、入力途中の文章、Undo/Redo |
| 途中保存/再開・閉じる三択・新規記録へ完成 | 同上 | dirtyと保存時点、保存失敗、閉じるSave/Discard/Cancel、画像/出典継承、原記録移動後の再開、連続番号 |
| テンプレート一覧・追加・保存・戻す・削除 | `TemplateStoreTests`、`OutputChoicesTests`、`BasicAutomationTests`、`UiWiringTests` | 名前検証、タグ誤り、未保存Save/Discard/Cancel、保存失敗、削除確認/失敗 |
| 未保存テンプレートのプレビュー | `TemplateStoreTests`、`UiWiringTests` | 埋込画像を含む生成HTML、原本不変、並列生成、成功/失敗時の専用一時領域cleanup |
| Markdown/HTML・形式選択・出力先・埋込・全テンプレート | `ExportTests`、`ExportCommandTests`、`CliParityTests`、`BasicAutomationTests`、`UiWiringTests` | 実ファイル・実EXEの終了コード、旧pwsh出力との一致、画像の並べ方、作らない形式を開かない |
| 丸・矢印・枠・ポインタ、旧記録互換 | `MarkTests`、`DragArrowTests`、`ExportTests` | 元画像を変更せず別画像へ合成、座標・半径、二重描画を防ぐ |
| HTMLの前後切替・拡大・印刷 | `HtmlViewTests`、`Test-ReportHtml.cjs` | CoreでHTML/CSS構造とリンク、別CLIでヘッドレスブラウザーの切替・右上ボタン・Escを検証。物理印刷機は検証しない |
| 設定移行・撮影/対象/出力/テンプレートフォルダーの保存復元 | `AppSettingsTests`、`TargetSettingsTests`、`BasicAutomationTests`、`UiWiringTests` | 古いJSON移行、壊れた設定、全撮影規則、設定フォルダー隔離 |
| ヘルプ・外部ファイル/フォルダーを開く | `UiWiringTests` | RichTextBox内容と実ボタンの起動要求、Shell失敗通知。Explorer/既定アプリ自体は起動しない |
| スタートメニュー登録/解除、管理者再起動 | `AppInteractionTests`、`LaunchTests`、`UiWiringTests` | 隔離中の拒否、対象EXE一致、要求/取消・失敗の分岐。実利用者のショートカット作成/削除、UAC承認を自動実行しない |
| 通常/無表示起動、既存プロセスへの表示・開始合図、本体管理停止 | `LaunchTests`、`ManagementTests`、実プロセス試験 | Mutex/名前付きイベント/管理pipeの隔離、待機時の停止、記録・保存・ダイアログ・未保存編集の保護 |
| JSON自動操作と専用workspace | `AutomationCommandTests`、`BasicAutomationTests`、`CompositionAutomationTests` | 入力/結果JSON、終了コード、領域外・通常設定/記録・リンク・不正JSON/参照を拒否 |

手順削除は既存のbest effort契約を維持する。画像のごみ箱移動が失敗しても手順JSONの削除を続け、次の画像も試す。移動に失敗した画像は残る。このケースの合格は「手順削除が成功した」ことであり、「画像を物理的に廃棄できた」ことではない。

全機能について上のいずれかの実行経路とケースを用意する。固定サイズ/多段DPI/全ハードウェア構成、外部アプリ固有UI、OSのUAC画面、実ごみ箱の復元、インストーラー配布までをこのテストの合格から推定しない。実際に利用できる環境、注入した境界、未実施のDesktopをケース表と実行summaryで区別する。

## JSONシナリオを実EXEへ渡す

```powershell
& .\artifacts\automation-runner\bin\OperationRecorderApp\release\OperationRecorder.exe --automation help | Write-Output
```

共通形式は次のとおり。`--workspace`は新しい空フォルダー、または所有マーカー`.operation-recorder-automation.json`がある専用領域を指定する。結果や編集対象のパスはその内側に限定される。通常の記録を試す場合は専用領域へコピーしたものを使う。

```powershell
& <OperationRecorder.exe> --automation <composition|session|template|settings> `
  --input <入力JSON> --workspace <専用領域> --result results/result.json | Write-Output
```

`--result`は専用領域の`results/`以下の`.json`に限定する。既存の記録や入力JSONを結果で上書きする指定は拒否する。既存結果も`success`と`exitCode`を持つ結果形式だけを再利用できる。

自動操作は通常GUI・Mutex・ホットキー登録より先に処理される。終了コードは0が成功、1が処理失敗、2が引数/JSONの誤り。結果JSONには`schemaVersion`、`success`、`area`、`workspace`、`result`、`exitCode`を返し、失敗時は`error.type/message`を返す。結果ファイルの保存に失敗した場合も非0で知らせる。

| 領域 | `action`または操作 | 主な入力 |
|---|---|---|
| `session` | `complete`、`inspect`、`metadata`、`edit`、`reset`、`delete-step`、`export` | `complete`は`sample`と新規の`session`。編集は`session`、元手順の`seq`、`caption/description/headline/body`。出力は`templates`、`out`、`md/html/embed`、`marks/radius` |
| `template` | `list`、`read`、`create`、`save`、`delete`、`check`、`preview` | `directory`（既定templates）、`name`（拡張子なし）、`text`、previewの`session` |
| `settings` | `defaults`、`load`、`save` | saveの`settings`オブジェクト。通常設定へは保存しない |
| `composition` | `operations`配列 | `sources`または`draft`の一方だけを指定する |

すべての入力に`"schemaVersion":1`を指定する。未知のキー、不正な型、必須項目の欠落は処理前に拒否する。設定の`save`も未知の設定項目や型違いを拒否する。以前の版の設定を読み込む`load`では移行処理を維持する。

`session export` の `pdf` と `docx` は既定で `false`。追加形式だけを出力する場合は `"md":false,"html":false` を指定する。PDFはインストール済みのEdge、DOCXはインストール済みまたはPATH上のPandocを使う。変換ソフトを明示する場合は、通常の `--export` の `--pandoc` を使う。

`session edit`は`headline`と`body`を両方指定する。自動文章への復元は`reset`を明示して行う。`metadata`は`caption`か`description`を少なくとも一方指定し、前後の空白はGUIと同じように除去して保存する。

`session transcript` は時刻付きの発話を隔離領域内の記録に取り込む。入力JSONの例は `{"schemaVersion":1,"action":"transcript","session":"records/sample","transcript":"inputs/narration.json","offsetMs":250}`。`transcript` のファイルは `[{"startMs":0,"endMs":850,"text":"入力を確定します"}]` のようなJSON配列。各区間の時刻は音声の先頭からのミリ秒で、`offsetMs` を加えて記録開始を基準にする。実EXEのCLI試験は手順の割り当て、既存操作と手修正文の保持、Markdown・HTML出力を検証する。

### サンプルから記録完了、編集、画像付きHTML出力まで

`session complete`は`sample`フォルダーの`events.jsonl`と参照PNGを取り込み、`records/名前`に完了済み記録を作る。`sample`と`session`は専用領域内の別フォルダー。出力先が既に存在する場合はエラーになり、同じ入力を新規名で処理できる。サンプルの`session.json`や`edits.json`は不要。

各イベントは正の32ビット整数の`seq`、操作種別`kind`、開始から7日以内の整数ミリ秒`tMs`を持つ。`seq`は記録内で一意とする。`before`と`after`は省略でき、指定する場合はサンプル内のPNGを相対パスの`file`で参照する。記録は最大10,000件、イベントファイルは32 MiB、PNGは合計256 MiBまで。入力の`seq`、`tMs`と操作内容を保持し、`time`と画像参照は新しい記録に合わせて設定する。完了した`session.json`には`end`、`eventCount`と`source: "sample"`が入る。

以下は合成PNGで再現するPowerShell 7の例。入力フック・ウィンドウ選択・実画面の撮影はDesktop試験で扱う。

```powershell
cd C:\develop\KotorinToys\tools\OperationRecorder
$ErrorActionPreference = 'Stop'
$run = Join-Path $PWD ('dev/history/v0.1.13/logs/sample-cli-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$build = Join-Path $run 'build'
dotnet build src/OperationRecorder/OperationRecorderApp.csproj -c Release --artifacts-path $build -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
$exe = Join-Path $build 'bin/OperationRecorderApp/release/OperationRecorder.exe'
$workspace = Join-Path $run 'workspace'

function Invoke-SampleAutomation($area, $request) {
    $request.schemaVersion = 1
    $inputPath = Join-Path $run ($request.action + '.json')
    $request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $inputPath -Encoding utf8
    $reply = & $exe --automation $area --input $inputPath --workspace $workspace --result ('results/' + $request.action + '.json') | Out-String
    if ($LASTEXITCODE -ne 0) { throw ('CLI操作に失敗しました。' + $request.action) }
    if (!($reply | ConvertFrom-Json).success) { throw ('CLIが成功結果を返しませんでした。' + $request.action) }
}

# 既存CLIで空の専用領域を初期化してから、サンプルを置く。
Invoke-SampleAutomation template @{ action = 'check'; text = '# 記録サンプル' }
$sample = Join-Path $workspace 'inputs/sample'
$img = New-Item -ItemType Directory -Path (Join-Path $sample 'img') -Force
[IO.File]::WriteAllBytes((Join-Path $img.FullName 'before.png'), [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAGUlEQVR4AQEOAPH/ADN3qjN3qgAzd6ozd6ojYgVR936/KgAAAABJRU5ErkJggg=='))
[IO.File]::WriteAllBytes((Join-Path $img.FullName 'after.png'), [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAGUlEQVR4AQEOAPH/AESqd0SqdwBEqndEqncmTgWVsZQbIAAAAABJRU5ErkJggg=='))
@{ seq = 1; kind = 'click'; tMs = 250; window = @{ title = '専用サンプル' }
   uia = @{ name = '登録'; controlType = 'Button' }
   before = @{ file = 'img/before.png' }; after = @{ file = 'img/after.png' } } |
    ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath (Join-Path $sample 'events.jsonl') -Encoding utf8

$session = 'records/sample'
Invoke-SampleAutomation session @{ action = 'complete'; sample = 'inputs/sample'; session = $session; caption = '専用サンプル' }
Invoke-SampleAutomation session @{ action = 'metadata'; session = $session; description = '更新した記録の説明' }
Invoke-SampleAutomation session @{ action = 'edit'; session = $session; seq = 1; headline = '登録を確定する'; body = '更新した手順の説明' }
$output = Join-Path $workspace 'exports'
& $exe --export (Join-Path $workspace $session) --template user --format html --embed --marks none --out $output | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'HTML出力に失敗しました。' }
$html = Get-ChildItem -LiteralPath $output -Filter '*.html' -File
$content = Get-Content -LiteralPath $html.FullName -Raw
foreach ($expected in '更新した記録の説明', '登録を確定する', '更新した手順の説明') {
    if (!$content.Contains($expected)) { throw ('HTMLに期待した文章がありません。' + $expected) }
}
if ([regex]::Matches($content, 'data:image/png;base64,').Count -ne 2) { throw '前後のPNGが揃っていません。' }
$html.FullName
```

たとえば既存の専用領域にコピーした記録を検査する入力は次のようになる。

```json
{"schemaVersion":1,"action":"inspect","session":"inputs/record-a"}
```

合成の例では、行・元記録・候補のindexは0から、元手順`seq`は記録の番号を指定する。`inspect`の結果から有効なindexとseqを確認する。

```json
{
  "schemaVersion": 1,
  "sources": ["inputs/record-a", "inputs/record-b"],
  "operations": [
    {"type":"select","row":0},
    {"type":"choose","candidate":0},
    {"type":"source","source":1,"steps":[1,2]},
    {"type":"insert","position":"after"},
    {"type":"move","rows":[1,2],"direction":"first"},
    {"type":"exclude","row":0},
    {"type":"note","headline":"補足説明","body":"不足部分を補う文章"},
    {"type":"undo"},
    {"type":"redo"},
    {"type":"save"},
    {"type":"inspect"},
    {"type":"complete"}
  ]
}
```

合成にはこのほか`add-source`、`include`、`text`、`commit`、`close`がある。`insert.position`は`before/after/end`、`move.direction`は`up/down/first/last`、`close.decision`は`save/discard/cancel`を使う。`index`による直接位置指定も可能だが、insertの`index`と`position`、moveの`index`と`direction`は排他であり、同時に指定すると処理前に拒否する。`source`と`steps`も組で指定する。

`text`は入力中文章を共有バッファへ置き、選択変更・保存等で確定する。結果の`operations`には各命令のdirty/履歴/行数を残し、`close`とUndo/Redoには`accepted`を返す。途中保存は`result.draft`を次の入力の`draft`へ渡して再開できる。

正確な必須項目・上限・拒否規則は[CompositionAutomation.cs](https://github.com/KotorinChunChun/OperationRecorder/blob/main/src/OperationRecorder/Automation/CompositionAutomation.cs)、[BasicAutomation.cs](https://github.com/KotorinChunChun/OperationRecorder/blob/main/src/OperationRecorder/Automation/BasicAutomation.cs)とその実行可能なテストを参照する。全操作を順に動かすfixtureは`CompositionAutomationTests`にある。

## ケースを追加するとき

### 生成HTMLをヘッドレスブラウザーで検証する

Node.js、Playwright、Edgeを用意する。既存の利用者ブラウザープロファイルやComputer Useを使わず、専用生成画像を埋め込んだHTMLを検証する。Playwrightが通常のモジュール検索先にない場合は、`OPREC_PLAYWRIGHT_MODULE`にそのパッケージフォルダーを指定する。

```powershell
cd C:\develop\KotorinToys\tools\OperationRecorder
$env:OPREC_HTML_TEST_ARTIFACTS = Join-Path (Get-Location) 'dev/history/v0.1.13/logs/html-local/fixtures'
dotnet test .\tests\OperationRecorderApp.Tests\OperationRecorderApp.Tests.csproj -c Release `
  --artifacts-path .\artifacts\html-tests --filter 'FullyQualifiedName~HtmlViewTests'
node .\dev\scripts\Test-ReportHtml.cjs `
  .\dev\history\v0.1.13\logs\html-local\fixtures .\dev\history\v0.1.13\logs\html-local\browser
Remove-Item Env:OPREC_HTML_TEST_ARTIFACTS
```

操作前/操作後の全4手順、両側モードだけの切替、全手順へ効く切替、右上の「閉じる（ESC）」、Esc、狭い画面、JavaScript無効時のクリック解除を試験する。終了0が成功、1が試験失敗。結果JSONと専用HTMLのスクリーンショットは指定した版別logsに保存する。これはCoreのケース表とは別のブラウザー実行であり、物理印刷機や外部アプリの試験を意味しない。

### 通常のケース追加

1. GUI固有の確認・ファイル選択・OS副作用は`AppInteraction`へ、記録OS境界は`RecordingRuntime`へ集約する。
2. GUIとCLIは共通の処理を呼ぶ。新しいフォーム操作は安定した`Control.Name`を付け、STAで実コントロールのイベントを検証する。
3. 入力・保存結果・原本不変・失敗/取消・後始末を検証する。対話デスクトップを使うテストには`Category=Desktop`を付ける。
4. [ケース対応表](../dev/history/v0.1.13/test-cases.json)を更新してからCore、必要に応じDesktopを実行する。未分類機能や未実施の層を合格として扱わない。

ケース表はビルド済みテストDLLから再生成する。全テストメソッドとTheoryの引数行を取得し、既知の機能へ対応付ける。新しい試験クラスを分類し忘れると生成を失敗させる。`automated`は実行入口があるという意味であり、今回実行済みの意味ではない。

```powershell
pwsh -NoProfile -File .\dev\scripts\Update-TestCaseManifest.ps1 `
  -TestAssembly .\artifacts\automation-runner\bin\OperationRecorderApp.Tests\release\OperationRecorderApp.Tests.dll
```
