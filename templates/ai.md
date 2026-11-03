{{!--
===============================================================================
 操作報告書テンプレート(AI向けの手順書（操作前後画像と詳細UI情報）)  -Template ai
===============================================================================
 書き方・文書全体の変数・steps の基本の変数は user.md の説明と同じ。

 steps の各要素で使える AI 向けの変数(値が無いものは null。{{#if}} で囲んで使う)
   seq                記録上の通し番号
   kind               click / doubleclick / rightclick / middleclick / drag / rightdrag / wheel / key / input / capture(Ctrl+Shift の手動キャプチャ)
   button             left / right / middle(マウス操作のみ)
   key                押したキー(例: Enter, Ctrl+S)(キー操作のみ)
   time               操作した日時(yyyy-MM-ddTHH:mm:ss.fff)
   window.title / window.className / window.process(例: EXCEL.exe) / window.hwnd
   window.frame       ウィンドウの画面上の矩形 "[x, y, w, h]"
   window.program     プログラムの情報。.name / .product / .version / .company / .exe / .path / .commandLine
   uiElements         操作した要素から最上位のウィンドウまでの名前(無ければクラス名)をカンマ区切りで並べたもの
   psrText / timeText Windows のステップ記録ツール形式の 1 行 / 日時(yyyy/MM/dd HH:mm:ss.fff)
   point              マウス操作のみ
     point.window     DWM 描画枠の左上を原点とする座標 "[x, y]"
     point.client     クライアント領域の左上を原点とする座標 "[x, y]"
     point.screen     仮想スクリーン座標 "[x, y]"
   dragEnd            ドラッグのみ。dragEnd.window / dragEnd.screen
   uia                UI Automation で取った操作対象の要素
     uia.controlType / uia.name / uia.automationId / uia.className / uia.frameworkId
     uia.isEnabled    "true" / "false"
     uia.boundsInWindow  要素の矩形(ウィンドウ相対)"[x, y, w, h]"
     uia.ancestors    親要素の一覧(外側 → 内側)。各要素に controlType / name / automationId
   win32              Win32 のコントロール情報
     win32.className / win32.controlId / win32.text(入力欄では null)/ win32.hwnd
   input              kind: input の手順で、入力欄から読み取った値
     input.controlType / input.name / input.label / input.automationId
     input.isPassword "true" / "false"   input.value(パスワード欄は null)
   before / after     画像
     before.file(記録フォルダからの相対パス) / before.image(md 記法)
     before.offsetMs(基準時刻からのずれ ms)
     before.offsetBasis(基準: eventStart=操作した瞬間 / mouseUp=ボタンを離した瞬間 /
                        focusIn=入力欄にフォーカスが入った瞬間 / focusOut=入力欄からフォーカスが外れた瞬間)
     before.area(画面上の撮影範囲 "[x, y, w, h]") / before.masked("true" / "false")
===============================================================================
--}}
# {{caption}}操作手順書(AI 向け)

{{#if caption}}
本書では{{caption}}の操作手順について説明する
{{#else}}
本書では操作手順について説明する
{{#endif}}

{{#if description}}
{{description}}

{{#endif}}
- 記録日時: {{recordStart}} 〜 {{recordEnd}}
- 対象: {{target}}
- 手順数: {{stepCount}}

{{#if command}}
## 記録の起動コマンド

同じ設定で記録をやり直すときは、次のコマンドを実行する。

```powershell
{{command}}
```

{{#endif}}
## このドキュメントの読み方(AI 向け)

- 各手順の YAML ブロックは、操作を再現するためのデータ。画像は操作前(マウスは押した瞬間、キーは押した瞬間の約 {{beforeMs}}ms 前)と操作後(マウスはボタンを離してから、キーは押してから約 {{afterMs}}ms 後)。
- `kind: input` の手順は、入力欄にフォーカスが入った時点の画面が操作前、フォーカスが外れた時点の画面が操作後。フォーカスを移すためのクリックや Enter / Tab は、別の手順としても記録されている。
- 座標はすべて物理ピクセル(per-monitor DPI aware)。`point.window` は DWM の描画枠(影を含まない実際の見た目)の左上、`point.client` はクライアント領域の左上、`point.screen` は仮想スクリーン座標が原点。
- 要素を特定するときの優先順位: ① `target_uia.automationId` ② ウィンドウ内での `controlType` + `name`(`ancestors` で絞り込む) ③ `target_win32.controlId`(Win32 / WinForms のみ) ④ `point.window` の相対座標。`hwnd` は記録時だけ有効な値なので再現には使わない。
- `input.value` は、フォーカスが外れた時点で入力欄から読み取った値(パスワード欄は値を記録していない)。
- 画像の斜線部分は、対象外のウィンドウが重なっていたため塗りつぶした領域。丸はクリック位置(左ボタン=赤 / 右=青 / 中=緑)、オレンジ枠は操作対象の要素。

{{#each steps}}
## 手順 {{number}}.{{#if headline}} {{headline}}{{#endif}}

{{#if body}}
{{body}}

{{#endif}}
{{#if windowTitle}}
画面: {{windowTitle}}

{{#endif}}
{{#if images.side}}
{{images.side}}

{{#endif}}
```yaml
seq: {{seq}}
kind: {{kind}}
{{#if button}}
button: {{button}}
{{#endif}}
{{#if key}}
key: {{q key}}
{{#endif}}
time: {{q time}}
{{#if window}}
window:
  title: {{q window.title}}
  className: {{q window.className}}
  process: {{q window.process}}
  frameOnScreen: {{window.frame}}
  hwnd: {{q window.hwnd}}
{{#if window.program}}
  program: { name: {{q window.program.name}}, version: {{q window.program.version}}, company: {{q window.program.company}}, exe: {{q window.program.exe}}, commandLine: {{q window.program.commandLine}} }
{{#endif}}
{{#endif}}
{{#if point}}
point:
  window: {{point.window}}
  client: {{point.client}}
  screen: {{point.screen}}
{{#endif}}
{{#if dragEnd}}
dragEnd:
  window: {{dragEnd.window}}
  screen: {{dragEnd.screen}}
{{#endif}}
{{#if uia}}
target_uia:
  controlType: {{uia.controlType}}
  name: {{q uia.name}}
  automationId: {{q uia.automationId}}
  className: {{q uia.className}}
  frameworkId: {{q uia.frameworkId}}
  isEnabled: {{uia.isEnabled}}
  boundsInWindow: {{uia.boundsInWindow}}
{{#if uia.ancestors}}
  ancestors:  # 外側 → 内側
{{#each uia.ancestors}}
    - { controlType: {{controlType}}, name: {{q name}}, automationId: {{q automationId}}, className: {{q className}} }
{{#endeach}}
{{#endif}}
{{#endif}}
{{#if uiElements}}
uiElements: {{q uiElements}}  # 操作した要素 → 最上位のウィンドウ(名前、無ければクラス名)
{{#endif}}
{{#if win32}}
target_win32:
  className: {{q win32.className}}
  controlId: {{q win32.controlId}}
{{#if win32.text}}
  text: {{q win32.text}}
{{#endif}}
  hwnd: {{q win32.hwnd}}
{{#endif}}
{{#if input}}
input:
  controlType: {{input.controlType}}
  name: {{q input.name}}
  label: {{q input.label}}
  automationId: {{q input.automationId}}
  isPassword: {{input.isPassword}}
  value: {{q input.value}}
{{#endif}}
{{#if before}}
before: { file: {{q before.file}}, offsetMs: {{before.offsetMs}}, offsetBasis: {{before.offsetBasis}}, areaOnScreen: {{before.area}}, masked: {{before.masked}} }
{{#endif}}
{{#if after}}
after: { file: {{q after.file}}, offsetMs: {{after.offsetMs}}, offsetBasis: {{after.offsetBasis}}, areaOnScreen: {{after.area}}, masked: {{after.masked}} }
{{#endif}}
```

{{#endeach}}
