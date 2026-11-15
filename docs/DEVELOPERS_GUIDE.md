# OperationRecorder 開発者ガイド

Windows 11 x64と.NET 10 SDKを使用する。OperationRecorderは親KotorinToysから独立したGitリポジトリであり、親では採用コミットを管理する。

## フォルダー構成

| 場所 | 正本・役割 |
|---|---|
| `src/OperationRecorder/` | アプリ本体。`UI`、`Recording`、`Engine`、`Replay`、`Automation`、`Export`、`Templates`、`Configuration`、`Integration`、`Infrastructure`に分離 |
| `tests/OperationRecorderApp.Tests/` | xUnitのサービス・フォーム・CLI試験 |
| `tests/OperationRecorder.RecordingFixture/` | 実入力・撮影試験の専用アプリ |
| `templates/` | 配布する手順書テンプレート |
| `scripts/` | 配布する再生・条件作成用PowerShell入口 |
| `tools/legacy-recorder/` | 旧PowerShell記録・報告書入口。`src/OperationRecorder/Engine`と`templates`を共有 |
| `Assets/`・`config/`・`tool.json` | 共通アイコン、配布設定例、親の管理定義 |
| `docs/` | 現行の利用方法・仕様・開発手順。利用説明の正本は`USERS_GUIDE.md` |
| `samples/` | 実際の操作記録から出力した、閲覧用のサンプル手順書 |
| `dev/scripts/` | ビルド、試験、ケース台帳更新など開発用の処理 |
| `dev/history/vX.Y.Z/` | 版別の要求・計画・報告。進行中も完了後も同じ場所 |
| `artifacts/` | Git対象外のbin/objと検証用出力。`Directory.Build.props`で集約 |
| `release/<実行識別子>/` | 単体ZIP生成時の出力。公開操作とは別で、前回生成物を上書きしない |

C#は各プロジェクト配下のSDK標準Compile規則で取り込む。記録エンジンを外部スキルから読む`SkillDir`は廃止し、`src/OperationRecorder/Engine`を唯一の正本とする。名前空間とEXE名は維持する。

旧スキルの記述は[来歴資料](development/legacy-skill.md)として残す。現在の操作指示や導入手順の正本にはしない。

## ビルドとデバッグ起動

```powershell
cd C:\develop\KotorinToys\tools\OperationRecorder
dotnet build .\OperationRecorder.slnx -c Release -m:1 -nr:false
dotnet build .\src\OperationRecorder\OperationRecorderApp.csproj -c Debug --artifacts-path .\artifacts\management -m:1 -nr:false
.\artifacts\management\bin\OperationRecorderApp\debug\OperationRecorder.exe
```

`OperationRecorder.slnx`には本体・テスト・専用fixtureを収録する。`--artifacts-path`は既定の`artifacts/`を上書きできる。管理ビルドの出力は`tool.json`の`executable`と一致し、親の開発ビルドも同じパスを使用する。

通常起動は画面を表示し、管理起動は`--startup`で無表示になる。GUI確認用の`--settings-dir <専用フォルダー> --passive`や管理要求は[KotorinToys連携](kotorintoys-integration.md)を参照する。

## 単体ZIPと正式配布

```powershell
pwsh -NoProfile -File .\dev\scripts\Build-OperationRecorder.ps1
# Core試験を省略して生成するときだけ指定
pwsh -NoProfile -File .\dev\scripts\Build-OperationRecorder.ps1 -SkipTests
```

単体ZIPの出力は`release/<実行識別子>/`。正式インストーラーは親の共通スクリプトで作る。[リリース手順](release.md)に従い、生成・公開・本PCへの導入をそれぞれ区別する。

配布物にはEXEのほか`templates/`、`scripts/`、`config/`、`Assets/app.ico`、`docs/`等を明示して取り込む。アプリ内マニュアルと既存リンクのため、`docs/USERS_GUIDE.md`から`MANUAL.md`と`operation-recorder/MANUAL.md`もビルド時に生成する。編集する正本は`docs/USERS_GUIDE.md`だけにする。

旧PowerShell入口はソースチェックアウトから使い、配布ZIPには同梱しない。例：`pwsh -File .\tools\legacy-recorder\New-OperationReport.ps1 -SessionDir <記録フォルダー>`。エンジンを複製せず、共通EngineをAdd-Typeする。

## 資料と記録

[開発資料の目録](https://github.com/KotorinChunChun/OperationRecorder/blob/main/dev/README.md)から対象版の要求・計画・テストケースへ辿る。生ログ・実行生成物は対象版の`dev/history/vX.Y.Z/logs/<実行識別子>/`へ保存し、Gitへ追加しない。利用者の記録・設定をビルド生成物と一緒に移動しない。

## 主なクラスの役割

| ファイル | 内容 |
|---|---|
| `Program.cs` | 起動(同時に 1 つだけ。`--settings` で設定の画面、`--startup` でトレイに常駐して起動、`--record` ですぐ記録、`--export` は画面を出さずに出力) |
| `LaunchOptions.cs` | 起動の引数と起動済みのときの動き(`LaunchOptions`)、起動中のアプリへの合図(`InstanceSignal`。名前付きイベント)、旧スタートアップ登録の検出 |
| `ExportCommand.cs` | `--export`(画面を出さずに、記録フォルダから md / HTML を出力する) |
| `OutputChoices.cs` | 「出力する報告書」のテンプレートの一覧と表示(画像の並べ方はテンプレートの `{{images.…}}` で決める) |
| `OverlayProximity.cs` | ポインターの距離と修飾キーに応じたオーバーレイの透過判定 |
| `TemplateStore.cs` / `TemplateEditorForm.cs` | テンプレートのエディタ(追加・削除・書き方の確認・保存・プレビューは TemplateStore、画面は受け渡しだけ) |
| `MergeForm*.cs` | 複数記録の合成画面。候補比較・挿入・移動・採否・文章編集・Undo/Redo・途中保存と再開 |
| `UiTexts.cs` | 画面・トレイ・通知の文字のうち、操作キーの設定で変わるもの |
| `MainForm.HotkeysTab.cs` | 「ショートカットキー」タブ(操作キーの変更。判定は記録エンジンの `HotkeySet.Validate`) |
| `MainForm*.cs` | 画面(記録する / 記録一覧・md 出力 / 使い方 / 設定)とタスクトレイ |
| `RecordingController.cs` | 記録 1 件の開始〜保存(ホットキー待ち・保存・md 出力・取り直し) |
| `RecordingRuntime.cs` / `AppInteraction.cs` | 記録OS境界と確認・ファイル選択・外部起動等の差替え。自動試験から通常環境を変更しない |
| `AutomationCommand.cs` / `BasicAutomation.cs` / `CompositionAutomation.cs` | 実EXEからJSONシナリオを実行し、共通サービスと編集セッションの結果・終了コードを返す |
| `CompositionEditingSession.cs` | GUIとCLI共通の合成命令、入力中文章、Undo/Redo、途中保存と閉じる判断 |
| `AppSettings.cs` | 設定(`%APPDATA%\OperationRecorder\settings.json`)と、前の版の設定からの移行(`FromJson`) |
| `ShortcutInstaller.cs` | スタートメニューへの登録・解除(このユーザーだけ) |
| `AppIcons.cs` / `Assets\app.ico` | アイコン |
| `WindowTitle.cs` | 独自画面のタイトルと実行製品版の共通表示 |
| `tool.json` | KotorinToys向けの製品識別・開発EXE・管理能力・文書とランタイム情報 |


## 自動テスト(画面を操作しない)

```powershell
cd C:\develop\KotorinToys\tools\OperationRecorder
pwsh -NoProfile -File .\dev\scripts\Test-OperationRecorder.ps1 -Suite Core
```

0.1.4から、全機能を単体/API・STA実フォーム・実EXE・専用別プロセスのOS fixtureへ分けてコマンドから試験する。
`-Suite Desktop`で実入力・撮影を、`-Suite All`で両方を実行できる。Computer Useは不要。
通常の常駐・設定・記録を隔離し、環境不足は`blocked`と非0終了で知らせる。
結果は対象版の`dev/history/vX.Y.Z/logs/`へTRXと`summary.json`で残す。
詳細な実行・再実行手順、確認/OS副作用の検証範囲、全機能のケース対応は[自動テスト](testing.md)を参照。

`OperationRecorderApp.Tests`(xUnit)。テスト用の記録フォルダ(画像はその場で描く)を一時フォルダに作り、
共通サービスと実フォームから次を確かめる。新しい機能は、判断の部分を画面のコードから分けてここでテストできるようにし、
画面(`MainForm*.cs`)は値を受け渡すだけにする。

| テスト | 確かめること |
|---|---|
| `ExportTests` | md / HTML の出力。テンプレートの `{{images.…}}` による画像の並べ方(左右・縦・片方だけ・同じ画面なら 1 枚)、埋め込み、形式の選択、名前・解説、文章の手直し(`edits.json`)、全テンプレート、以前のテンプレート名 |
| `ExportCommandTests` | `--export` の引数の読み取りと誤り、ビルドした exe を実際に起動して出力・終了コード |
| `CliParityTests` | 旧PowerShell入口の `New-OperationReport.ps1`(pwsh)とアプリで同じ md になるか(pwsh が無ければ省略) |
| `WheelRulesTests` / `ImageCompareTests` / `CaptureRulesTests` | ホイールのまとめ方と相殺、同じ画面かの判定、撮影ルールの既定(ホイールだけオフ) |
| `AppSettingsTests` | 設定の既定値、v1.1 の設定からの移行(ホイールをオフ・user-table / user-before の読み替え)、保存して読み直し |
| `OverlayProximityTests` / `OutputChoicesTests` | オーバーレイの透過と解除、テンプレートの並び順と表示、並べ方の選択肢 |
| `LaunchTests` | `--settings` / `--startup` / `--record` の読み取り、起動済みのときの動き(startup なら何もしない・settings なら設定の画面を出す・record なら記録を始める)、起動中のアプリへの合図、旧スタートアップ登録の読み取りと非変更(一時フォルダで) |
| `HtmlViewTests` | HTML の、JavaScript を使わない操作前 / 操作後の切り替え(ラジオボタンが本文より前・表の列と縦並びの見出しの印・1 枚にまとめた画像は隠さない)、クリックで拡大(画像が増えない)、印刷では両方を出す |
| `FormSmokeTests` | 記録の合成・テンプレートのエディタの画面を画面外に表示して、表示しただけで例外が出ないか(操作はしない) |
| `CompositionEngineTests` / `CompositionFormTests` / `CompositionEntryTests` | 複数記録の候補・差し込み・並べ替え・除外・文章編集・途中保存/再開・元記録移動・不正入力の拒否・独立した完成保存、実フォームの編集と停止保護 |
| `MarkTests` / `MergeTests` | 出力するときの印の合成(位置・種類・古い記録には重ねない・埋め込み・exe の引数)、2 つの記録の突き合わせ(座標や入力値が違っても同じ操作)・まとめ方の案・合成(番号の振り直し・画像と手直し) |
| `SessionEditorTests` / `DragArrowTests` / `TemplateStoreTests` | 手順の削除と番号の繰り上げ・画像と手直しの片付け、ドラッグの矢印の形、テンプレートの追加・削除・書き方の誤り・プレビュー |
| `HotkeyTests` / `UiTextsTests` | 操作キーの既定・判定(修飾キーまで一致)・使えない組み合わせ・選べるキー、変えたキーが表示に出るか |
| `TargetRulesTests` / `TargetSettingsTests` | 前面のウィンドウを対象にできるか、ウィンドウが映っているディスプレイ、全ディスプレイの範囲と撮影の間隔、対象の決め方・操作キー・撮影モードの設定の移行 |
