# 複数記録からCodex CLIで再生条件を作る

0.1.6以降の機能。繰り返すたびに変わるクラス名、タイトル、UIAの名前/IDを複数記録で比較し、Codex CLIに再生対象の条件案を作らせる。公開済み0.1.5には含まれない。

操作順・入力値・画像条件は最初の記録を基準にする。他の記録は対象条件の根拠として使う。手順の追加/削除/合成は既存の合成機能を使う。

## 比較する記録

同じ業務を2回以上記録する。アプリを起動し直す、対象ファイル名や連番を変えるなど、対応したい変動を含む記録を用意する。有限個の例から将来の全変動への最適性を保証するものではない。

`sources.json`へ先頭を基準として記録を指定する。相対パスはこのJSONの場所から解決する。同じ記録の重複指定は不可。上限は20記録、各2000手順、比較データ8MB。

```json
{
  "schemaVersion": 1,
  "sources": ["C:/OperationRecords/first", "C:/OperationRecords/second", "C:/OperationRecords/third"]
}
```

## 資料を確認してから生成する

PowerShell 7とログイン済みのCodex CLIを使用する。配布フォルダーから次を実行する。通常のOperationRecorderが起動中でも資料作成と条件生成はできる。

```powershell
# まず資料だけを作る。Codexは起動しない
pwsh -NoProfile -File .\scripts\New-OperationReplayWithCodex.ps1 `
  -InputFile 'C:\Replay\sources.json' -OutputDirectory 'C:\Replay\comparison' -PrepareOnly

# comparison\analysisの内容を確認した後に生成する
pwsh -NoProfile -File .\scripts\New-OperationReplayWithCodex.ps1 -Bundle 'C:\Replay\comparison'
```

`-PrepareOnly`なしで`-InputFile`と`-OutputDirectory`を指定すると資料作成から生成まで続けて行う。`-CodexExecutable`でCLIのパス、`-Model`でモデル、`-Executable`でOperationRecorder.exeを指定できる。再生成時は新しい`-DraftOutput`を指定する。

`analysis/observations.json`にはクラス名・タイトル・UIA名/ID/ControlType・操作種別・対応番号・EXEのファイル名だけを出す。入力値、生キー、commandLine、画像、原本の絶対パスは含めない。ただしタイトルや要素名自体に個人情報が含まれる場合があるため、送信前に確認する。ローカル用の`manifest.json`は原本の場所とハッシュを持ち、プロンプトには加えない。

Codex CLIにはread-only/ephemeral/ignore-user-configと出力schemaを指定し、最終JSONだけを条件案として使う。プロンプトでは記録内の文字列を指示として扱わず、外部参照・シェル・UI・テスト等のツールを使わないよう指示する。read-onlyはOS全体の読取り範囲を遮断する機能ではない。通常のCodexユーザーconfigを読み込まないため、独自のプロバイダー設定等が必要な環境は、その設定での利用方法を別途確認する。利用するCLIの引数は`codex exec --help`で確認できる。

## 案を確認して受け入れる

| ファイル | 内容 |
|---|---|
| analysis/prompt.md | 対応付け、固定/変動部分、条件設計を求める日本語プロンプト |
| analysis/observations.json / response.schema.json | 比較データと出力契約 |
| proposal-*.json | 対応するrecordingId/sourceSeq、条件、理由、変動部分、リスク、質問 |
| draft.json | 操作と入力値を保持した確認待ちconfig |
| draft.json.review.json | 根拠、別要素への一致警告、未解決項目 |

同じ配列位置だけで対応付けず、操作種別・EXE・UIA種類・前後関係を比較する。2記録以上の根拠、対応順序、重複利用、実際の観測例への条件一致を検査する。未解決項目のある案は確認済みにできず、確認前の案は再生できない。

不足がある場合は該当する条件/対象/入力等を修正し、解決した項目だけを`learningReview.unresolved`から取り除く。根拠不足なら追加記録からbundleを作り直す。基準の入力値・画像条件は保持するため、変動する入力値は環境変数へ、動的な画像条件は目的に応じて手動で調整する。

```powershell
# 条件と根拠を確認した後、新規configへ受け入れる
.\OperationRecorder.exe --replay learn-accept --config 'C:\Replay\comparison\draft.json' --out 'C:\Replay\job.json' | Write-Output
.\OperationRecorder.exe --replay check --config 'C:\Replay\job.json' | Write-Output

# 実際に操作するときだけ通常の再生入口を明示する
pwsh -NoProfile -File .\scripts\Invoke-OperationReplay.ps1 -Config 'C:\Replay\job.json' -Mode run
```

受入後にconfigを編集すると再確認が必要。比較元のどれかが変わった場合も実行を拒否する。原本や既存の案を上書きしない。生成用ps1は受入・再生を自動実行しない。

## 条件式

ウィンドウのconditionにはtitle/className、要素のconditionにはautomationId/name/className/controlTypeを指定する。

```json
{
  "all": [
    { "field": "className", "match": "regex", "value": "Editor_[a-f0-9]{6}", "ignoreCase": false },
    { "field": "title", "match": "regex", "value": "請求書 [0-9]{3} - Editor", "ignoreCase": false }
  ],
  "any": [],
  "none": []
}
```

allはAND、anyは空でなければOR、noneはどれかに一致すると除外する。全体はこれらのANDで、正の条件が必要、合計32条件まで。比較はexact/contains/prefix/regex。regexは全体一致・最大512文字・100ms制限・NonBacktrackingで、lookaround/後方参照は非対応。空文字へ一致する表現や単純な`.+`は拒否する。C#/PowerShell等の任意コードを評価しない。

手順ごとに独立したbindingを作り、工程の途中でタイトルが変わる場合にも対応する。EXEの絶対パスは基準から保持し、UIAは同じControlTypeへ拘束する。既存属性とconditionはANDで評価する。条件が揃うまで監視し、候補が複数なら停止する。画像/DPI/非常停止は[再生の説明](replay.md)を参照する。

## 個別CLI

ps1を使わず、資料作成とJSON案の適用を分けることもできる。

```powershell
.\OperationRecorder.exe --replay learn-prepare --input 'C:\Replay\sources.json' --out 'C:\Replay\comparison' | Write-Output
.\OperationRecorder.exe --replay learn-apply --bundle 'C:\Replay\comparison' --proposal 'C:\Replay\proposal.json' --out 'C:\Replay\draft.json' | Write-Output
```
