{{!--
===============================================================================
 操作報告書テンプレート(ステップ記録ツール形式（簡易UI操作履歴）)  -Template psr
===============================================================================
 書き方と使える変数は user.md / ai.md の説明と同じ。名前に「|」が入ることがあるので、表ではなく箇条書きにしている。このテンプレートで使っている、ステップ記録ツール形式の変数:
   psrText                     「〇〇 (ボタン)」をクリック ― ウィンドウ「…」 のような 1 行
   timeText                    操作した日時(yyyy/MM/dd HH:mm:ss.fff)
   point.screen / point.window 画面上 / ウィンドウ内の座標 "[x, y]"(マウス操作のみ)
   window.program.name / .version / .company / .commandLine / .exe   操作したプログラム
   uiElements                  操作した要素から最上位のウィンドウまでの名前(無ければクラス名)をカンマ区切りで並べたもの
   uia.className               操作した要素のクラス名(UI Automation)
===============================================================================
--}}
# {{caption}}操作手順書(操作の記録)

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

{{#each steps}}
## 手順 {{number}}: {{psrText}}

{{#if body}}
{{body}}

{{#endif}}
- 日時: {{timeText}}
{{#if point}}
- 座標: 画面 {{point.screen}} / ウィンドウ内 {{point.window}}
{{#endif}}
{{#if window.program}}
- プログラム: {{window.program.name}}, {{window.program.version}}, {{window.program.company}}, {{window.program.commandLine}}, {{window.program.exe}}
{{#endif}}
{{#if uiElements}}
- UI 要素: {{uiElements}}
{{#endif}}
{{#if uia.className}}
- クラス名: {{uia.className}}
{{#endif}}

{{#if images.side}}
{{images.side}}

{{#endif}}
{{#endeach}}
