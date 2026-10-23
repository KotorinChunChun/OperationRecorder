# OperationRecorderとKotorinToysの連携

OperationRecorderは、PCの操作と操作前後の画面を記録し、画像付きのMarkdown・HTML手順書を作るツールです。
使い方の詳細は[操作記録マニュアル](../operation-recorder/MANUAL.md)を参照してください。

## コレクションから使う

KotorinToysの一覧でOperationRecorderを選び、「起動」でタスクトレイへ常駐させ、「画面を表示」で記録対象や出力の設定を開きます。
引数なしでEXEを直接起動した場合も画面を開きます。起動済みなら既存の画面を表示します。
画面の×を押しても常駐は続きます。記録は「記録を開始」ボタン、トレイのメニュー、開始キーから行います。

「停止」は待機中のアプリを正常終了させます。記録中・保存中は停止を拒否するため、先に「保存して終了」で記録を保存し、待機状態になるまで待ってください。
「保存して終了」は記録の終了であり、アプリの常駐は続きます。

| 操作 | 既定のキー | 変更 |
|---|---|---|
| 記録の開始・再開 | Ctrl+Shift+F9 | 「ショートカットキー」タブ |
| 直前の1手を取り消す | Ctrl+Shift+F10 | 「ショートカットキー」タブ |
| 最初から取り直す | Ctrl+Shift+F11 | 「ショートカットキー」タブ |
| 記録を保存して終了する | Ctrl+Shift+F12 | 「ショートカットキー」タブ |
| 記録中の手動撮影 | Ctrl+Shiftだけを押して離す | 固定 |

キー一覧は[config/shortcuts.json](../config/shortcuts.json)にもあります。既定値を記載した一覧であり、利用者が変更した現在値はアプリの「ショートカットキー」タブで確認します。

## 自動起動と配布状態

新規の自動起動はOFFです。単体からスタートアップへの新しいショートカットを作成せず、KotorinToysの「本体設定・自動起動」で管理します。
現段階ではKotorinToys向けの正式インストーラーが未公開のため、自動起動の追加はできません。
旧版のスタートアップ登録が見つかった場合は案内を表示し、アプリはその登録を上書き・削除しません。

製品版は1.3.2、識別子は `operation-recorder` / `KotorinToys.OperationRecorder` です。
公開リポジトリURLとKotorinToys用インストーラーは未登録です。コレクション登録・開発版のビルドと、インストーラーの作成・外部公開は別工程です。

## 開発版のビルドと起動

Windows 11 x64と.NET 10 SDKが必要です。フレームワーク依存EXEの実行には.NET Desktop Runtime 10 x64を使用します。
記録エンジン、テンプレート、マニュアルは、このリポジトリの `operation-recorder/` を正本とします。

```powershell
cd C:\develop\KotorinToys\tools\OperationRecorder
dotnet build .\OperationRecorderApp.csproj -c Debug --artifacts-path .\artifacts\management
.\artifacts\management\bin\OperationRecorderApp\debug\OperationRecorder.exe
```

出力先 `artifacts/management/bin/OperationRecorderApp/debug/OperationRecorder.exe` を `tool.json` から参照します。
管理用起動は `--startup` で画面を出さず待機し、管理要求 `status`・`show`・`stop` へ応答します。
単体の `--settings`・`--record`・画面なし出力の `--export` も利用できます。

UI確認では `--settings-dir <専用フォルダー> --passive` で利用者設定と分離し、グローバルホットキーの取得と初回のショートカット質問を抑止します。
この起動方法は画面確認用です。実際の記録・キー操作の確認には通常起動を使用します。

## 保存先

通常の利用者設定は `%APPDATA%\OperationRecorder\settings.json`、記録の既定保存先はドキュメント内の `OperationRecords` です。
開発EXEの配置やKotorinToysへの追加によって既存データを移動しません。
記録対象に映る画面や入力を確認し、共有前に手順と画像を見直してください。
