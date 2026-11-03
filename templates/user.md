{{!--
===============================================================================
 操作報告書テンプレート(ユーザー向けの手順書（操作前後画像）)  -Template user
===============================================================================
 書き方
   {{変数}}                    値を差し込む(null は空文字)
   {{q 変数}}                  JSON 形式の引用付き文字列にする(null は null)。YAML に書くとき用
   {{#if 変数}} … {{#else}} … {{#endif}}      値があるとき(null・空文字・false・空の一覧以外)
   {{#unless 変数}} … {{#else}} … {{#endunless}}  値が無いとき
   {{#each 一覧}} … {{#endeach}}             一覧の要素ごとに繰り返す。中では要素の変数を直接書ける
   {{!-- … --}}                コメント(出力しない。中に {{ }} を書いてよい)
   制御タグはすべて # で始める。{{#…}} や {{!-- --}} だけの行は、行ごと消える(空行が残らない)

 文書全体の変数
   caption        記録の名前(記録時の -Caption。New-OperationReport.ps1 の -Caption で上書きできる。無ければ空)
   description    報告書の冒頭に載せる解説(記録時の -Description。-Description で上書きできる。無ければ空)
   title          「{caption}操作手順書」(以前のテンプレートとの互換用)
   recordStart    記録開始日時(yyyy-MM-dd HH:mm:ss)
   recordEnd      記録終了日時
   target         記録対象(ウィンドウタイトル / 「プロセス名(アプリ全体)」/「操作したウィンドウ」)
   captureMode    撮影モード(Window / App / Active / Desktop)
   command        この記録を起動したコマンド(同じ設定で記録し直すとき用。古い記録では null)
   stepCount      手順数
   beforeMs       「操作前」を撮るタイミング(ms 前)
   afterMs        「操作後」を撮るタイミング(ms 後)
   steps          手順の一覧(下記)

 steps の各要素の変数
   number         手順番号(1 から)
   headline       見出しの文(末尾の「。」なし)。手動キャプチャ(Ctrl+Shift)の手順では空
   actionSentence 操作の文(例: 「登録」ボタンをクリックします。)。入力の手順では空
   inputText      入力の文(例: 「氏名」欄に「山田」と入力します。)。入力の手順だけ。複数行の値はコードブロック付き
   body           本文。複数行の入力の手順は inputText、それ以外は空(見出しと同じ文は本文に繰り返さない)
   windowTitle    操作したウィンドウのタイトル
   hasImages      操作前・操作後のどちらかの画像があれば true
   ※ 入力の手順(テキストボックスへの入力)は、フォーカスが入った時点の画面が操作前、外れた時点の画面が操作後
   images.side            画像の部分の md。操作前 | 操作後 を左右 2 列の表に並べる(user・ai・psr はこれ)
   images.vertical        操作前の下に操作後を縦に並べる
   images.before          操作前の画像だけ(見出しなし)
   images.after           操作後の画像だけ
   images.sideMerged      左右に並べる。ただし操作前と操作後が同じ画面なら 1 枚にまとめる
   images.verticalMerged  縦に並べる。ただし同じ画面なら 1 枚にまとめる
   imagesBlock            images.side と同じ(以前のテンプレートとの互換用)
   imagesSame             操作前と操作後が同じ画面なら true
   画像の並べ方を変えたいときは、テンプレートを複製して images.… を書き換える
   before.image   操作前の画像の md 記法(-ImageStyle に応じて相対パスか base64)。並べ方を自分で作るとき用
   after.image    操作後の画像の md 記法
   ※ before / after は画像が無いとき null。AI 向けテンプレートで使える変数はすべてここでも使える
===============================================================================
--}}
# {{caption}}操作手順書

{{#if caption}}
本書では{{caption}}の操作手順について説明する
{{#else}}
本書では操作手順について説明する
{{#endif}}

{{#if description}}
{{description}}

{{#endif}}
{{#each steps}}
## 手順 {{number}}.{{#if headline}} {{headline}}{{#endif}}

{{#if body}}
{{body}}

{{#endif}}
{{#if windowTitle}}
**画面:** {{windowTitle}}

{{#endif}}
{{#if images.side}}
{{images.side}}

{{#endif}}
{{#endeach}}
