---
name: operation-recorder
description: >
  ユーザー自身が Windows 上で行う PC 操作(クリック・ダブルクリック・右クリック・ドラッグ・
  Enter や Ctrl+S などの操作キー)を検知して、操作の直前(既定 0.2 秒前)と直後(既定 0.5 秒後)の
  スクリーンショットと、操作した要素の情報(ウィンドウ名・クラス名・AutomationId・コントロールID・
  ウィンドウ相対座標・入力した値)を記録し、Markdown の操作報告書(ユーザー向けマニュアル /
  AI 向けマニュアル)を生成するツール。
  「操作を記録して手順書を作りたい」「画面操作のスクショを自動で撮って」「操作マニュアルを作って」
  「この作業手順を AI が再現できる形で残したい」「ステップ記録ツール(psr)の代わりが欲しい」のように、
  **人が操作する様子を記録してドキュメントにする話**が出たら、自前でスクリプトを書く前に必ずこのスキルを使うこと。
  Claude 自身がアプリを自動操作して検証する用途は windows-ui-automation スキルの担当。
---

# 操作記録と操作報告書の生成

**スクリプトは必ず `pwsh` で実行する。`powershell.exe` は使わない。**

```
scripts/
  Start-OperationRecording.ps1   記録する(Ctrl+Shift+F9 開始 / F10 1手取消 / F11 取り直し / F12 保存・終了)
  New-OperationReport.ps1        記録フォルダをテンプレートに当てはめて md を生成する(-Template 複数可)
  Reissue-OperationReport.ps1    過去の記録を一覧から選び、名前・解説を確認・編集して md を再発行する(対話式)
  PromptHelpers.ps1              対話入力の共通部品(Esc で戻る入力・凡例付きの選択肢・テンプレート一覧)
  Import-OpRecorder.ps1          src/*.cs をコンパイルして読み込む(直接呼ぶ必要はない)
  src/*.cs                       記録エンジン本体(フック・撮影・要素情報・保存)と、md 作成(ReportGenerator.cs / TemplateEngine.cs)
MANUAL.md                        利用者向けマニュアル(アプリの「使い方」タブにもそのまま表示される)
templates/
  user.md                        ユーザー向けの手順書（操作前後画像）。変数の説明は冒頭コメント。一覧に出る名前は各テンプレートの 3 行目
  psr.md                         ステップ記録ツール形式（簡易UI操作履歴。日時・座標・プログラム・UI 要素）
  ai.md                          AI向けの手順書（操作前後画像と詳細UI情報）
  text.md                        テキスト手順書（操作情報のみ。画像なし。文章・入力内容・画面名だけ）
```

## デスクトップアプリ(OperationRecorder.exe)

同じ記録エンジンを内蔵した WinForms アプリがある。ソースはこのスキルと同じリポジトリ(global-skill-catalog)の
`apps/OperationRecorderApp`(このスキルから見て `../../apps/OperationRecorderApp`)。skills の外なので毎時の自動同期の
対象ではなく、変更したら手動で commit / push する。配布用 zip は GitHub の Release(`operation-recorder-v<版>`)に置く。

- `src\*.cs`・`templates\*.md`・`MANUAL.md` は**コピーせず、このスキルのファイルを csproj から直接取り込む**。
  エンジン・テンプレート・マニュアルを直したら、アプリは `pwsh -File build.ps1` で作り直すだけでよい。
- md の作成は C#(`ReportGenerator.cs` / `TemplateEngine.cs`)に一本化してある。`New-OperationReport.ps1` はこれを
  呼ぶだけの薄いラッパー。以前の pwsh 実装と、記録 7 件 × 全テンプレート + 埋め込み + 名前・解説の上書きで
  出力がバイト単位で一致することを確認済み。md の作り方を変えるときは C# 側だけを直す。
- 配布: `build.ps1` が .NET 同梱の単体 exe(約 68MB)を作り、`templates\`・`MANUAL.md` と一緒に
  `dist\OperationRecorder-<版>-win-x64.zip` にまとめる。配布先に pwsh や .NET は要らない。
- アプリの動き: タブは「記録対象」「撮影の設定」「出力の設定」「ショートカットキー」「記録一覧・編集・出力」「使い方」「設定」。× で閉じてもタスクトレイに残り、
  待機中は Ctrl+Shift+F9 だけを `RegisterHotKey` で受ける(F12 は奪わない)。記録中の F10〜F12 は CLI と同じ。
  F11 は今の記録を捨てて待機に戻る(次に F9 を押すと同じ対象で記録する)。開始待ちの状態は無い(「記録を開始」はすぐ始め、待機中は F9 をいつでも受け付ける。オーバーレイは記録中だけ)。初回起動時にスタートメニュー(このユーザーだけ)への登録を聞く。
  起動の引数は `--settings`(設定の画面を出す。引数なしも同じ)/ `--startup`(画面を出さずトレイに常駐。スタートアップからの起動用。
  設定タブの「Windows にサインインしたら…起動する」はスタートアップに `--startup` 付きのショートカットを置く)/ `--record`
  (前回の設定のまますぐ記録。スタートメニューの「すぐに記録」)。同時に 1 つだけ起動できる。起動済みのときに起動されたら、
  `--startup` なら何もせず、`--record` なら起動中のアプリで記録を始め、それ以外は起動中のアプリの設定の画面を前に出す
  (名前付きイベント `Local\OperationRecorderApp.Show` / `.Record` で合図する)。`--hide` は廃止。
- 記録エンジンは自分のプロセスのウィンドウでの操作を記録しない(アプリの画面・トレイのメニュー・pwsh のコンソール)。
  自分のプロセスのウィンドウは、マスク(斜線)を作るときの「手前にある他のウィンドウ」にも数えない。
- 記録中の画面表示(v1.1): 対象を囲う赤い枠(`TargetFrame`)、撮影の合図(枠が光る・クリック位置の丸 `ClickRipple`)、
  撮影ログのオーバーレイ(`RecordingOverlay`。直近 10 手のサムネイル・操作ボタン。`FleeEnabled` ならポインタのあるウィンドウから逃げる)。
  どれも `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` で記録の画像に写らない。枠と丸はクリックを素通しする。
  **診断用に環境変数 `OPREC_SHOW_IN_CAPTURE=1` で撮影除外を外せる**(除外したままだと PrintWindow でも真っ黒になり、
  見た目を確認できないため)。
- 記録の操作キーは `Hotkeys.cs` の `HotkeySet`(開始・取消・取り直し・終了。既定 Ctrl+Shift+F9〜F12)。`Recorder.Hotkeys` と
  `HotkeyListener.Start(HotkeySet, withQuit)` に渡す。修飾キーまで完全に一致したときだけ操作キーとして扱い、記録しない。
  アプリは「ショートカットキー」タブで変えられ(`HotkeySet.Validate` で重複・修飾キー無し・よく使うキーを弾く)、CLI は既定のまま。
- 撮影モード `AllDesktops` は、記録開始時のすべてのディスプレイを合わせた範囲を撮る(隙間は塗りつぶす)。対象を決める必要は無い。
  広い範囲のモード(Desktop / AllDesktops)は `CaptureBudget.IntervalFor` で撮影の間隔を広げ、リングのメモリを約 256MB に抑える。
- 「開始のキーを押したときに前面にあるもの」を対象にするとき(アプリの「記録する対象」、CLI の `-Foreground`)は、
  `Recorder.Start` の前に `Recorder.UseForegroundTarget()` を呼ぶ(Window / App はそのウィンドウ、Desktop は `TargetRules.MonitorFor`
  でいちばん広く映っているディスプレイ)。自分自身・デスクトップ・タスクバーなら false を返すので、そのときは Pick にする。
- エンジンのアプリ向け API: `Rules`(撮影ルール。`CaptureRules` の定数がキー)、`StepSaved`(1 行の JSON)、
  `StepRemoved`(seq)、`StepCaptured`(撮影した瞬間。種類と位置)、`CurrentTargetRect()`、`RequestUndo()`。
- 撮影ルール: 左・右・中クリック、ホイール、キー(グループ: A〜Z / 1〜0 / F1〜F24 / 十字キー / 特殊キー / Ctrl・Alt・Win との組み合わせ。`CaptureRules.KeyGroupOf`)、テキスト入力、Ctrl+Shift、Win+Shift+S ごとに
  記録する / 操作前を撮る / 操作後を撮る。既定はマウスホイールとキーのグループ全部が「記録しない」で、ほかは全部オン(CLI はこの既定で動く)。
  A〜Z・1〜0 をオンにしても入力欄の中では文字入力として扱う。アプリは前の版の設定でも、キーのグループをすべて「記録しない」に直す(`SettingsVersion` 4)。
- ホイールは同じルートウィンドウで 1 秒以内に続けて回した分を 1 手順にまとめ、向きを含めて足し引きする(`WheelNet`)。
  差し引き 0 になった手順は保存しない(画像のピンも外す)。
- 手順の文章の手直しは記録フォルダの `edits.json`(`{"steps": {"<seq>": {"headline", "body"}}}`)。
  `ReportGenerator` が md を作るときに上書きする。events.jsonl は書き換えない。CLI の md にも反映される。
- 印(マウスポインタ・クリック位置の丸・ドラッグの矢印・部品の枠)は v1.3 から**記録の画像に描かず**、出力するときに
  `MarkPainter` で合成する(`ReportGenerator.Options.Marks`、CLI `-Marks circle,arrow,frame,pointer|none` `-MarkerRadius`、exe `--marks` `--radius`)。
  位置は events.jsonl の `screen` / `dragEnd` / `markRect`(部品の画面上の矩形)と画像の `area` から求める(`MarkSpec.FromEvent`)。
  session.json に `"marks": "output"` がある記録だけに描く(それより前の記録は画像に描いてあるので重ねない)。Link のときは
  `<出力先>\<記録フォルダ名>_marked\` に描いた画像を書いて参照する。元の img\ は書き換えない。
- 2 つの記録の合成は `SessionMerger`(`Signature` = 種類・ボタン・ウィンドウのクラス名・部品の種類 / ID / クラス名(ID が無ければ名前)・キー。
  座標・入力値は使わない)。`Plan` は LCS で突き合わせ、最初の一致より前の A を残し、一致の範囲は B、最後の一致の後は B に続きが
  あれば B、無ければ A の残りを入れる。`Merge` は番号を振り直し、画像と edits.json を写して「(合成)」の記録フォルダを作る。
  印を描いて保存していた古い記録から来た手順には `marksBaked: true` を付けて印を重ねない。
- 手順の削除は `SessionEditor.DeleteStep`(events.jsonl の行・その手順だけの画像・edits.json の手直しを消し、eventCount を直す。
  番号は並び順で振るので後ろが繰り上がる)。ドラッグの矢印の形は `DragArrow`(丸の縁から離した位置へ、白い縁取り付き)。
- アプリのテンプレートのエディタ(`TemplateEditorForm`。中身は `TemplateStore`: 追加・削除・書き方の確認・保存していない中身でのプレビュー)。
- HTML はアプリだけが出す(Markdig で md → HTML。`HtmlExporter` が JavaScript なしの操作前 / 操作後の切り替え(先頭のラジオボタン + `:checked ~`)と、クリックで拡大(`:target`)を足す。1 枚にまとめた画像は alt を「操作前・操作後」にして隠さない)。CLI(pwsh)は md のみ。アプリの exe は
  `OperationRecorder.exe --export <記録フォルダ> [--template …] [--format html,md] [--embed]`
  で**画面を出さずに** md / HTML を出せる(詳しくは `apps/OperationRecorderApp/README.md`)。
- **確認は画面の操作ではなく自動テストで行う**: `dotnet test apps/OperationRecorderApp.Tests -c Release`。
  出力(並べ方・1 枚化・埋め込み・手直し・全テンプレート)、`--export`、pwsh 版との一致、ホイールのまとめ方、
  同じ画面の判定、設定の移行、オーバーレイの逃げ先を、画面を出さずに確かめる。新しい機能は判断の部分を
  画面に頼らないクラス(エンジンは `StepRules.cs` の `WheelRules` / `ImageCompare`、`Hotkeys.cs`、`TargetRules.cs` の
  `TargetRules` / `CaptureBudget`、アプリは `OutputChoices` / `OverlayPlacement` / `UiTexts` / `AppSettings.FromJson` /
  `ExportCommand`)に分けてテストを足し、画面のコードは受け渡しだけにする。
  UI Automation での画面確認は、見た目(配置・切れ)の最終確認だけにする。
- 設定は `%APPDATA%\OperationRecorder\settings.json`。**Claude がアプリを動かして確認するときは、このフォルダを
  退避してテスト用の設定を置き、終わったら必ず戻す**(利用者の設定・初回起動の質問を消さないため)。
- **利用者のアプリが起動中だと、同時起動の制限で Claude のテスト用はすぐ終了する。** ウィンドウをタイトルで探すと
  利用者のアプリを操作してしまう(実際に一度起きかけた)ので、必ず `Process.MainWindowHandle` で自分が起動した
  プロセスのウィンドウを使い、起動直後に終了していないかを確かめる。

## 使い方

```powershell
$s = "$env:USERPROFILE\.claude\skills\operation-recorder\scripts"

# パラメータなしで起動すると、選択肢の凡例を出しながら対話形式で設定を聞く(-Interactive で強制も可)
pwsh -File "$s\Start-OperationRecording.ps1"

# パラメータ指定の例
pwsh -File "$s\Start-OperationRecording.ps1" -Mode App -WindowTitle "販売管理"
pwsh -File "$s\Start-OperationRecording.ps1" -Mode Window -Pick -Resident -Open
pwsh -File "$s\Start-OperationRecording.ps1" -Mode App -ProcessName EXCEL -Template user,ai -ImageStyle Embed -OutDir D:\手順書 -StartNow

# 記録済みフォルダから md を作り直す(同じ記録から何度でも作れる)
pwsh -File "$s\New-OperationReport.ps1" -SessionDir <記録フォルダ> -Template ai -ImageStyle Embed -ReportDir <md を保存するフォルダ>

# 過去の記録を一覧から選んで再発行する(対話式。-OutDir は記録フォルダが並んでいる場所の既定値)
pwsh -File "$s\Reissue-OperationReport.ps1"
```

### 再発行ツール(Reissue-OperationReport.ps1)

対話式。Esc(選択肢では 0 も可)で 1 つ前の質問に戻る。

1. 記録フォルダを探す場所(既定は `-OutDir`、無ければ `ドキュメント\OperationRecords`)。`session-*` で `session.json` があるフォルダを記録とみなす
2. 再発行する記録を新しい順の一覧から番号で選ぶ(名前・手順数・出力済み md の数を表示)。選ぶと記録日時・対象・名前・解説・出力済み md を表示
3. 見出しの名前(caption)・解説(description)を確認・編集(Enter でそのまま、`-` で消す)
4. 変えたときだけ、記録(`session.json`)に保存するかを聞く。保存しなければ今回の md にだけ使う。記録フォルダ名は変えない
5. テンプレート(複数可)・画像の載せ方・md の保存先(記録フォルダ内の同名 md は上書きになる旨を表示)・出力後に開くか
6. 出力後「続けて再発行するか」。続けると、記録フォルダの場所はそのままで 2 から聞き直す

出力した md のフルパスを戻り値として出す。標準入力が無い環境ではエラーにするので、Claude から md を作るときは
`New-OperationReport.ps1` を使う。

対話入力の最初に、ツールの説明(何をするツールか・使い方・記録中のキー・答え方)を枠で表示する
(`PromptHelpers.ps1` の `Show-Banner`。再発行ツールも同じ)。機能を変えたら、この説明文も直すこと。
対話入力では、Esc(選択肢の質問では `0` も可)で 1 つ前の質問に戻る。戻った質問では前回の答えが既定値になる。
Esc を検知するため、入力は `Read-Host` ではなく `[Console]::ReadKey` で 1 キーずつ読んでいる(`Read-LineOrEsc`)。
文字入力の質問(半径・フォルダなど)では `0` が値になりうるので、戻るのは Esc だけ。

### 記録の操作キー(どのアプリを操作中でも効く。これらのキー自体は記録しない)

| キー | 動作 |
|---|---|
| Ctrl+Shift(この 2 つだけ) | 今の画面をキャプチャして `kind: capture` の手順にする。操作前・操作後は同じ画像だが、後で編集できるよう別ファイル(`NNNN_before.png` / `NNNN_after.png`)に保存。見出しは「手順 N.」だけ |
| Ctrl+Shift+F9 | 開始・再開。起動直後は、これを押すまで記録しない(`-StartNow` なら起動と同時に開始) |
| Ctrl+Shift+F10 | 直前 1 手を取り消す(確定前の入力・保存待ちの操作・保存済みの操作のうち、いちばん新しいもの)。続けて押すとさらに戻る |
| Ctrl+Shift+F11 | 同じ設定・同じ対象で最初から取り直す。今の記録は保存せずに記録フォルダごと消す。常駐していなければすぐ記録を始め直し、常駐時は F9 を待つ |
| Ctrl+Shift+F12 | 停止・終了。記録を保存して md を出す。常駐していなければ終了、常駐時は F9 を待つ。**待機中**に押すと終了 |

- 待機中はキーボードフックを外し、`RegisterHotKey` で Ctrl+Shift+F9 / F12 だけを受け取る(打鍵を見ない)。
  他のアプリが同じキーを登録済みだと起動時にエラーになる。記録中はキーボードフックの中で F9〜F12 を判定する。
- 取り消し・キャプチャのときは `MessageBeep` で音を鳴らす(コンソールが見えなくても分かるように)。
- Ctrl+Shift のキャプチャは、キーボードフックで「Ctrl と Shift が両方押された → 他のキーもクリックも無いまま
  片方が離された」を検出する。画像は両方が押された時点のもの。Ctrl+Shift+F9 や Ctrl+Shift+S、Ctrl+Shift+クリックでは撮らない。
  Windows の設定で Ctrl+Shift が入力言語の切り替えに割り当てられていると、キャプチャと同時に言語も切り替わる。
- F11 の取り直しでは、`-Pick` で選んだウィンドウ・ディスプレイをそのまま使う(ウィンドウが閉じていれば選び直し)。

| パラメータ | 値 | 既定 |
|---|---|---|
| `-Caption` | 記録の名前。記録フォルダを `session-yyyyMMdd-HHmmss_名前` にし、報告書の見出しを「名前操作手順書」にする(フォルダに使えない文字は `_`、60 文字まで)。常駐時は全記録に同じ名前 | なし |
| `-Description` | 報告書の冒頭「本書では名前の操作手順について説明する」の後に載せる解説 | なし |
| `-Elevate` | 管理者権限に昇格して実行する(既に管理者なら何もしない)。昇格すると新しいウィンドウで動き、戻り値は `-ResultFile`(内部用)経由で元のプロセスに返す。対話入力では最初の質問 | 昇格しない |
| `-Mode` | `Window` / `App` / `Active` / `Desktop` / `AllDesktops`(下の表) | `App`(`-Monitor` があれば `Desktop`) |
| `-WindowTitle` / `-ProcessName` / `-Pick` | 対象の決め方。どれも無ければ `-Pick` | |
| `-Foreground` | Ctrl+Shift+F9 を押したときに前面にあるウィンドウを対象にする(App はそのアプリ、Desktop はそれがいちばん広く映っているディスプレイ)。`-StartNow` とは併用しない(指定すると外す)。前面が自分・デスクトップ・タスクバーなら `-Pick` になる | |
| `-Monitor` | `Desktop` で撮るディスプレイの番号。無ければ `-Pick` | |
| `-ListMonitors` | ディスプレイ一覧を表示して終了 | |
| `-MarkerRadius` | クリック位置の丸の半径(px、0〜200)。左=赤 / 右=青 / 中=緑。`0` なら描かない | `14` |
| `-StartNow` | 起動と同時に記録を始める(無ければ Ctrl+Shift+F9 を待つ) | F9 を待つ |
| `-Resident` | 常駐する(上の操作キーの表を参照) | 常駐しない |
| `-Template` | 当てはめるテンプレート(複数可。`user` / `psr` / `ai` / `text` / 独自の md のパス / `none`)。下の「報告書」参照 | `user` |
| `-ImageStyle` | `Link`(img\ の PNG を相対パス参照) / `Embed`(base64 で md に埋め込み、1 ファイルで完結) | `Link` |

| `-Return` | `Markdown`(md のフルパス。テンプレートの数だけ行が出る。md が無ければ記録フォルダ) / `Folder`(記録フォルダのフルパス) | `Markdown` |
| `-Open` | 保存後、md を関連付けられたプログラムで開く(`Invoke-Item`) | 開かない |
| `-OutDir` | 記録フォルダ(`session-日時`)を作る親フォルダのフルパス | `ドキュメント\OperationRecords` |
| `-ReportDir` | md を保存する**フォルダ**のフルパス(ファイル名ではない。md の名前は `session-日時_テンプレート名.md`) | 記録フォルダの中 |
| `-TemplateDir` | テンプレートを探すフォルダ | スキルの `templates` |

その他: `-BeforeMs 200` / `-AfterMs 500` / `-IntervalMs 100` / `-MaxMinutes 60`(超えたら保存) / `-NoInputValues`(入力値を記録しない)。

記録フォルダは `<OutDir>\session-yyyyMMdd-HHmmss[_名前]\` で、`events.jsonl`(1操作1行)・`session.json`(起動コマンドを含む)・
`recorder.log`・`img\NNNN_before.png / NNNN_after.png`・`session-…_<テンプレート名>.md` が入る。常駐時は、戻り値が保存 1 件ごとに出力される。

- `Embed` の data URI は VS Code のプレビューなどでは表示されるが、GitHub など表示しないビューアもある。
- `-ReportDir` を記録フォルダと別のドライブにすると、`Link` の画像パスは絶対パスになる。
- AI 向けテンプレートには、実際に使った設定を並べた起動コマンド(`session.json` の `command`)を載せている。
  対話入力で設定したときも、終わりに同じコマンドを表示する。

### Claude が記録を起動するとき

- 記録するのはユーザーの操作。Claude はクリックしない。起動したら「Ctrl+Shift+F9 で開始、終わったら Ctrl+Shift+F12」と伝える。
- シェル実行ツールの `run_in_background` で起動する。この場合、ユーザーはコンソールを見られないので `-Pick` は使わず、`-WindowTitle` / `-ProcessName` / `-Monitor` で対象を指定する。
- 標準入力が無いので、パラメータなしの起動は対話入力できずエラーになる。必ず `-Mode` などを渡す。
- Claude から操作するときのファイル: 待機中に `<OutDir>\START` を作ると開始、`<OutDir>\QUIT` を作ると終了。
  記録中に記録フォルダへ `STOP` を作ると保存して停止。
- コンパイルに数秒かかる。標準出力に「待機中です」か「記録中です」が出るまでは準備中。

## 撮影モード

| モード | 撮影範囲 | 検知する操作 |
|---|---|---|
| `Window` | 指定した1つのウィンドウ | そのウィンドウ上の操作だけ。同じアプリのダイアログは対象外 |
| `App` | 指定プロセスの見えているウィンドウ全部を囲む矩形 | そのプロセスのウィンドウ上の操作(ダイアログ・メニューも含む) |
| `Active` | 前面のウィンドウ+カーソル下のウィンドウ | すべてのアプリの操作 |
| `Desktop` | 指定したディスプレイ全体(**塗りつぶしなし**) | そのディスプレイ上のクリックと、中心がそのディスプレイにある前面ウィンドウでのキー操作 |
| `AllDesktops` | すべてのディスプレイを合わせた全体(**塗りつぶしなし**。ディスプレイの無い隙間だけ塗る) | すべての操作(自分のプロセスを除く) |

`App` の対象プロセスは、`-ProcessName` を指定したときだけ同名プロセス全部に広げる。`-WindowTitle` / `-Pick` で
決めたときは、そのウィンドウのプロセス 1 つだけ(pwsh.exe や msedge.exe のように無関係な同名プロセスが多いため)。
ダイアログが本体から離れた位置に出ると、間の領域はすべて斜線になり画像が大きくなる。

`Desktop` のディスプレイは `-Monitor <番号>` で指定する。番号は左上から(X → Y の順)に 1 から振ったもので、
`-ListMonitors` で一覧を出せる(対話入力でも一覧を凡例に出す)。`-Pick` では最初にクリックしたウィンドウが
映っているディスプレイ(`MonitorFromWindow`)になる。常駐中にディスプレイ構成が変わってもよいよう、記録を
始めるたびに取り直す。

## 絶対に守ること: 対象外の画面を画像に残さない

撮影範囲の中でも、**対象ウィンドウが実際に見えている部分以外は、保存時に斜線で塗りつぶす**。
撮影のたびに全トップレベルウィンドウを Z オーダー順に走査して、対象ウィンドウより手前にある
別ウィンドウの矩形を可視領域から引いている(`RecorderHookCapture.cs` の `CaptureFrame`)。
App モードの矩形内に見えるデスクトップや他アプリも同様に塗りつぶす。

- 記録中は Claude がウィンドウを最前面に持ってくることはできない(ユーザーが操作中)ので、
  windows-ui-automation のように最前面化で写り込みを防ぐ方法は使えない。この塗りつぶしが唯一の防御。
- 範囲はすべて `DWMWA_EXTENDED_FRAME_BOUNDS` から取る。`GetWindowRect` は透明なリサイズ枠を含み、
  背後のウィンドウが写り込む(windows-ui-automation スキルで実際に起きた事故)。
- 判定を緩める変更(例: 透明なオーバーレイを手前の窓として扱わない)はしないこと。塗りすぎは画像が
  見にくくなるだけだが、塗り漏れは他アプリの業務データの流出になる。
- `Active` モードは、ユーザーが触ったすべてのアプリの画面と入力値を記録する。業務データが写る前提で
  使い、できるだけ `Window` / `App` を勧める。
- **`Desktop` / `AllDesktops` モードは例外で、塗りつぶしをしない**(ユーザーが明示的に求めた機能)。ディスプレイに
  映っているものはすべて写る。起動時に警告を出している。
- **Claude が動作確認のために記録を起動するときも、ユーザーの画面を記録してしまう。** 入力を送らない
  「起動して止めるだけ」のテストのつもりで Desktop モードを 10 秒動かしたところ、そのディスプレイで
  ユーザーが操作していた業務用 Excel が 5 件記録された(2026-09-17)。画像は開かずに記録フォルダごと
  削除した。記録を起動する前には、必ずユーザーに「これから記録を動かす」と伝え、対象の画面を操作しない
  よう頼むこと。テスト用のウィンドウだけを対象にできる `Window` / `App` モードで確認できるなら、そちらを使う。

## 何を撮り、何を撮らないか

- **マウス**: 左・右・中クリック、ダブルクリック(Windows のダブルクリック時間内の2回目を1操作にまとめる)、
  ドラッグ(押した位置から一定以上動いて離した)。「前」はボタンを押した瞬間の BeforeMs 前、
  「後」はボタンを**離してから** AfterMs 後。
- **記録しないドラッグ**: ウィンドウを別のディスプレイへ(から)移動するドラッグ。押した位置と離した位置のディスプレイ
  (`MonitorFromPoint`)が違い、押したウィンドウの位置か大きさが変わっていれば「ウィンドウの移動」とみなして捨てる
  (向きはどちらでも)。Desktop モードで、対象外のディスプレイで押したドラッグは、そもそも記録範囲外。
  同じディスプレイの中でウィンドウを動かすドラッグは記録する。
- **撮るキー**: Enter / Tab / Esc / F1〜F24 / Apps、Ctrl・Alt・Win との組み合わせ。
  テキストボックスの外では Space / Backspace / Delete / Insert / 矢印 / Home / End / PageUp / PageDown も撮る。
- **撮らないキー**: 文字キー(どこで押しても撮らない)、Shift だけとの組み合わせ、日本語入力関連のキー
  (変換・無変換・半角/全角・かな)、CapsLock・NumLock、テキストボックス内の編集キー、押しっぱなしのリピート。
- **日本語入力の確定**: IME がオンの状態で文字を打った後の Enter / Esc / F6〜F10 は変換操作とみなして撮らない。
  IME の状態は `WM_IME_CONTROL / IMC_GETOPENSTATUS` で取るので、これに答えないアプリでは確定の Enter も撮ってしまう。
- **テキストボックスへの入力**: `SetWinEventHook(EVENT_OBJECT_FOCUS)` でフォーカスの移動を受け、書き込める
  Edit / Document 要素にフォーカスが入った時点の画面を「操作前」として固定する。フォーカスが外れた時点で値を
  UI Automation で読み取り、打鍵があったか値が変わっていれば、その時点の画面を「操作後」にして
  `kind: input`(「『氏名』欄に『山田』と入力します」)の手順を 1 つ作る。
  - フォーカスを移すためのクリック・Enter・Tab は別の手順としても記録される(二重になるのは仕様。ユーザー了承済み)。
  - 手順の並びは時刻順(入力の手順の時刻は最後に打鍵した時刻)。報告書は `tMs` で並べ替える。
  - 入力欄が閉じてから値を読めないときに備え、打鍵が 0.15 秒止まるたびに値を読んでおく。
  - 記録開始時に既にフォーカスがあった入力欄は、最初の打鍵の時点を「操作前」にする。
  - パスワード欄(`IsPassword` か Win32 の `ES_PASSWORD`)は値を読まず「パスワードを入力します」とだけ書く。
- Ctrl+Shift+F9〜F12 は記録の操作キーなので記録しない。

## 報告書

記録(`events.jsonl`)には、ユーザー向け・AI 向けのどちらに必要な情報もすべて保存している。**何を載せるかは
md にするときに当てはめるテンプレートだけで決まる**ので、「ユーザー向け / AI 向け」を選ぶパラメータは無い
(以前の `-Report` / `-Mode` / `-UserTemplate` / `-AiTemplate` は `-Template` に統合した)。

| テンプレート | 内容 |
|---|---|
| `user` | 冒頭は見出し「名前操作手順書」と「本書では名前の操作手順について説明する」+解説だけ(記録日時・対象・手順数は載せない)。各手順は見出し(操作の文)・入力内容・画面名・操作前/操作後の画像を縦に並べる |
| `text` | `user` から画像を除いたもの。各手順は見出し(画面キャプチャの手順は「(画面キャプチャ)」)・入力内容・「画面: …」 |
| `psr` | Windows のステップ記録ツール形式。手順ごとに 1 行の操作文(`psrText`)と、日時・座標・プログラム・UI 要素・クラス名を箇条書き(名前に縦棒が入ることがあるので表にしない) |
| `ai` | `user` の内容に加えて手順ごとの YAML(window のタイトル/クラス/プロセス、`point.window` / `point.client` / `point.screen`、`target_uia` の controlType / name / automationId / className / frameworkId / boundsInWindow / ancestors、`target_win32` の className / controlId、`input`、画像の撮影時刻のずれ)。冒頭に見出し・解説に加えて、記録日時・対象・手順数、起動コマンド、座標系と要素特定の優先順位の説明 |

- **画像の並べ方はテンプレートで決める**(アプリにも CLI にも並べ方の設定は無い。v1.3 で `-ImageLayout` / `-MergeSameImages` を廃止)。
  手順ごとの変数 `images.side`(操作前 | 操作後 の 2 列の表)/ `images.vertical`(縦)/ `images.before` / `images.after`(片方だけ)/
  `images.sideMerged` / `images.verticalMerged`(同じ画面なら 1 枚にまとめる)から選んで置く。user・ai・psr は `images.side`。
  `imagesBlock` は `images.side` と同じ(以前のテンプレートとの互換用)。並べ方を変えたいときはテンプレートを複製して書き換える。
  `imagesSame` は操作前と操作後の画像が同じかどうか(保存時に `events.jsonl` に書く。古い記録はファイルを比べる)。
  以前の `user-table` / `user-before` は廃止した(名前で指定されたら `user` として扱う)。
- `-Template` は複数指定でき(`-Template user,ai`)、その数だけ `<記録フォルダ名>_<テンプレート名>.md` を出す。
  名前なら templates 内の `<名前>.md`、パスならそのファイル。`none` は md を出さない(記録スクリプトのみ)。
- 記録後でも `New-OperationReport.ps1 -SessionDir … -Template …` で別のテンプレートに当てはめ直せる。
  名前・解説は `session.json` の `caption` / `description` を使い、`-Caption` / `-Description` で上書きできる
  (以前の `-Title` は廃止。テンプレート変数 `title` は「名前操作手順書」として残してある)。
- 対話入力では templates 内の md を凡例に出し、カンマ区切りで複数選ばせる(説明は冒頭コメントの 3 行目から
  「操作報告書テンプレート」と末尾の `-Template 名前` を除いたもの)。テンプレートを追加するときは 3 行目の書き方をそろえる。
- 体裁を変えたいときはスクリプトではなくテンプレートを直す。社内様式などはコピーを別フォルダに置いて `-TemplateDir` で指定する。
- 操作前の画像に、クリック位置の丸(左=赤 / 右=青 / 中=緑、半径 `-MarkerRadius`、ドラッグは同じ色の矢印も)と、
  操作した要素のオレンジ枠を描く。丸は `-MarkerRadius 0` で消せる(オレンジ枠は残る)。文章は UI Automation の Name と ControlType から機械的に作るので、
  配る前に人が読み直す前提。

### テンプレートの書き方

- 書式: `{{変数}}` / `{{q 変数}}`(YAML 用の引用付き。null は `null`)/ `{{#if 変数}}…{{#else}}…{{#endif}}` /
  `{{#unless 変数}}…{{#endunless}}` / `{{#each 一覧}}…{{#endeach}}` / `{{!-- コメント --}}`。
  **制御タグはすべて `#` で始める**(ユーザーの指定。Handlebars の `{{else}}` / `{{/if}}` は使えず、書くと
  古い書き方だというエラーになる)。制御タグやコメントだけの行は行ごと消える。行の途中に書いたタグは消えない。
- 変数名は独自のもの(UI Automation のプロパティ名 ControlType / AutomationId / ClassName / FrameworkId /
  IsEnabled を camelCase にしたものが多い)。
- 使える変数の一覧は、各テンプレート冒頭のコメントに書いてある。変数を増やすときは
  `New-OperationReport.ps1` の「テンプレートに渡すデータ」と、テンプレートのコメントの両方を直す。
- `{{#each}}` の中で見つからない名前は外側(手順 → 文書全体)を探す。値が null でも内側にキーがあればそこで確定する。
- タグの対応が取れていないと、どのタグかを示すエラーで止まる。

## ハマりやすい点(実際に踏んだもの)

- **`Add-Type` の参照**: .NET 9 以降の System.Drawing は `System.Private.Windows.Core` /
  `System.Private.Windows.GdiPlus` を参照に足さないと CS0012 になる。`WindowsBase` を名前で渡すと
  `System.Windows.Point` の無い参照アセンブリに解決されるので、実体のパスを渡す(`Import-OpRecorder.ps1` 対応済み)。
- **フックのコールバックで重い処理をしない**: `LowLevelHooksTimeout`(既定 300ms 前後)を超えると、Windows が
  黙ってフックを外す。コールバックはキューに積むだけにして、UI Automation は別スレッドで呼ぶ。
- **「0.2 秒前」は後から撮れない**: 撮影スレッドが IntervalMs ごとに撮り、直近 RetainMs 分をリングに持っている。
  使うフレームは参照カウントで固定し、保存が終わるまで破棄しない。
- **キー操作の対象要素**: `AutomationElement.FocusedElement` は、Tab などでは読みに行った時点で既に次の要素へ
  移っている。フックの時点で `GetGUIThreadInfo` から取ったフォーカス HWND を優先する。
- **WinForms の `LabeledBy`** は無関係なラベルを返すことがある。報告書では要素自身の Name を優先する。
- **テストでダブルクリックを送るとき**: windows-ui-automation の `Invoke-DoubleClick` は2回のクリックの間が
  約 0.58 秒あり、Windows 既定のダブルクリック時間(0.5 秒)を超える。アプリ側でも2回のクリックになるので、
  記録が2件になるのは正しい動作。
- **会社のセキュリティソフト**: キーボードの低レベルフックはキーロガーと同じ仕組みなので、警告されたり
  `SetWindowsHookEx` が失敗したりすることがある。失敗した場合は起動時にエラーを出して止まる。

## 既知の制限

- Window モードでは、メニューやドロップダウンがウィンドウの外にはみ出した部分は写らない(App モードなら写る)。
- 撮影と「どの窓が手前か」の判定の間に窓が動くと、数 ms ぶん塗りつぶしの位置がずれることがある。
- 管理者権限で動いているアプリの要素情報やそのアプリ上の操作は、記録側も管理者で起動しないと取れないことがある(`-Elevate`)。
- **UAC の確認画面は、管理者権限でも記録できない。** 標準設定では UAC はセキュアデスクトップに表示され、通常の
  デスクトップのプロセスからは撮影もフックもできない(画面ロック中と同じく「撮影できません」になる)。
  UAC をセキュアデスクトップに出さない設定にすれば写る可能性はあるが、セキュリティを下げる変更なので勧めない。
  Claude からその設定を変えないこと。
