# 記録から手順案とスキルを作る

保存済みの記録から、目的、使用条件、前提、可変項目、作業手順、完了条件をまとめます。各手順から根拠の操作番号と記録開始からの経過時間を確認できます。

## 送信前に確認する

1. 「記録一覧・編集・出力」で保存済みの記録を1件選び、「手順案を分析…」を開きます。複数の記録を使う場合は、先に[合成](USERS_GUIDE.md#36-複数の記録から1本の手順書を完成させる013)して新しい記録へ保存します。
2. 送る操作を選びます。操作文と文字起こしはこの画面で修正・削除できます。画像の初期値は送信オフです。送る操作前・操作後の画像だけを選び、画像上で範囲をドラッグすると黒塗りが入ります。
3. 接続先URLとモデルIDを入力します。認証が必要な場合はAPIキーを入力します。画面に入力したキーは設定や手順案へ保存されません。
4. 「送信内容を確認…」で接続先、モデルID、操作文、文字起こし、黒塗り後の画像を確認します。「送信して分析」を押すと、表示された内容と操作番号、経過時間を送信します。音声、動画、元の画像ファイル、記録フォルダーのパスは送信データに含まれません。

接続先にはJSONオブジェクト形式をサポートするOpenAI互換のChat Completions APIを指定します。画像を送る場合は、PNGのデータURLを入力として受け取れるモデルを選びます。URLは `/v1` または `/chat/completions` で終わるエンドポイントを指定できます。HTTPSと、このPCのループバックHTTPに対応します。[Chat Completions API](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)

## 手順案を確認して保存する

- 「根拠を見る…」から、承認済みの操作文、文字起こし、画像を確認できます。目的や本文を直し、手順を追加・削除・並べ替えできます。追加する手順には、今回の送信対象に含めた操作の番号を1件以上指定します。
- 「案をJSONで保存…」で編集内容をJSONに保存できます。「保存済みの案を開く…」でそのJSONから編集を再開できます。JSONには承認済みの画像・文章も入ります。保存場所と共有範囲を確認してください。
- 「SKILL.mdを確認…」で名前を入力し、全文を確認してから保存します。生成したファイルは利用者が確認し、必要に応じてCodexのスキルフォルダーへ配置します。
- 「手順書を書き出す…」から、根拠番号と承認済み画像を含むMarkdownまたはHTMLを保存できます。手順案と成果物は独立したファイルとして保存され、元の記録は保持されます。

## PowerShell 7から使う

アプリの実行ファイルがあるフォルダーで実行します。各出力先には新しいファイル名を指定し、`review.json` と `draft.json` は送信・公開の前に開いて確認します。

```powershell
$record = 'C:\path\to\record'
.\OperationRecorder.exe --skill select --session $record --out selection.json
# selection.json で送る操作、操作文、文字起こし、画像と黒塗りを指定する
.\OperationRecorder.exe --skill prepare --session $record --selection selection.json --out review.json
# review.json を確認してから接続先とモデルを指定する
.\OperationRecorder.exe --skill analyze --review review.json --endpoint 'https://example.com/v1' --model 'model-id' --out draft.json
# draft.json の procedure を確認し、必要に応じて編集する
.\OperationRecorder.exe --skill export --draft draft.json --name register-record --out SKILL.md
.\OperationRecorder.exe --skill manual --draft draft.json --out manual.html
```

選択JSONでは `include` を無効にすると操作全体を除外します。`action` と `narration` を編集でき、`includeBefore`、`includeAfter` で操作前後の画像を個別に選べます。`beforeMasks` と `afterMasks` の `x`、`y`、`width`、`height` は元画像の左上を基準とするピクセル値です。認証が必要な場合は、環境変数 `OPREC_ANALYSIS_API_KEY` を設定したシェルから実行します。
