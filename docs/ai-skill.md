# AIスキルの導入

OperationRecorderをCodexやClaude Codeから使うためのスキルを `skills/operation-recorder` に同梱しています。スキルは本リポジトリの記録エンジン、テンプレート、利用説明を直接参照します。

## 別のPCへ導入する

Windows 11 x64、Git、PowerShell 7を用意します。PowerShell入口はPowerShell 7.6で確認しています。アプリをソースからビルドする場合は別途.NET 10 SDKが必要です。

次の例では `C:/develop/tool/OperationRecorder` に取得します。保存場所は変更できます。既に取得してある場合はcloneを省略します。

```powershell
git clone https://github.com/KotorinChunChun/OperationRecorder.git C:/develop/tool/OperationRecorder
pwsh -NoProfile -File C:/develop/tool/OperationRecorder/scripts/Install-OperationRecorderSkill.ps1
```

既定では現在のユーザーの `~/.codex/skills/operation-recorder` へ導入します。`CODEX_HOME` が設定されている場合はその配下の `skills/operation-recorder` が対象です。

Claude Codeにも導入する場合は、追加で次を実行します。

```powershell
pwsh -NoProfile -File C:/develop/tool/OperationRecorder/scripts/Install-OperationRecorderSkill.ps1 -SkillsDirectory "$HOME/.claude/skills"
```

スキルを利用する新しいチャットで「OperationRecorderで操作を記録して手順書を作って」と依頼します。撮影するウィンドウまたはアプリ、記録名、保存先を伝えてください。

## スキルカタログで同期しているPC

`~/.claude/skills` がカタログへのジャンクションになっている場合は、カタログの `skills` を指定できます。Codexへの反映は、そのカタログの手順に従うか、上記のCodex向け導入コマンドでも行えます。

```powershell
pwsh -NoProfile -File C:/develop/tool/OperationRecorder/scripts/Install-OperationRecorderSkill.ps1 -SkillsDirectory C:/develop/global-skill-catalog/skills
```

カタログの既存の定期同期が設定されている場合、その運用に従って変更が他PCへ伝搬します。この導入スクリプト自身はGitのcommit/pull/pushや他スキルの同期を実行しません。
他PCでもOperationRecorderのチェックアウトを取得して導入コマンドを実行してください。スキルの同期だけではリポジトリ本体やPC固有の設定は配られません。

## 参照先と更新

導入時に `%LOCALAPPDATA%/OperationRecorderSkill/repository.json` へリポジトリの絶対パスを保存します。この設定はCodexとClaude Codeで共用し、スキルカタログには入りません。他PCで同じパスに置く必要はありません。

環境変数 `OPERATION_RECORDER_ROOT` が設定されている場合は、その絶対パスを優先します。設定した参照先が不正な場合は停止します。

```powershell
# このpwshセッション内だけ参照先を変更する例
$env:OPERATION_RECORDER_ROOT = 'D:/repos/OperationRecorder'
& "$HOME/.codex/skills/operation-recorder/scripts/Resolve-OperationRecorderRepository.ps1"
```

リポジトリのエンジン・テンプレート・利用説明は、チェックアウトの更新をそのまま参照します。スキル本体の更新は導入コマンドを再実行します。移動した場合も、移動先の導入スクリプトを再実行します。

```powershell
git -C C:/develop/tool/OperationRecorder pull --ff-only
pwsh -NoProfile -File C:/develop/tool/OperationRecorder/scripts/Install-OperationRecorderSkill.ps1
```

ローカル変更や履歴の分岐でpullが失敗した場合は、変更を保全して解消してから進めます。導入スクリプトや記録コマンドが自動でpullすることはありません。

## 既存スキルの保全

上書きする既存ファイルと参照先設定は `%LOCALAPPDATA%/OperationRecorderSkill/backups/<実行識別子>/` に保存されます。導入結果にバックアップ先を表示します。元へ戻す場合は `skill/` の対象ファイルと、存在すれば `repository.json` をそれぞれ元の場所へ戻してください。

既存スキル内のその他のファイル、アプリの設定、操作記録、他のスキルは変更・削除しません。古い資材が残っていても、新しいスキルの記録・再出力はリポジトリへ委譲します。カタログの旧アプリが参照する資材を一括削除しないための扱いです。

事前確認には `-WhatIf` を使用できます。この場合はスキル・設定・バックアップを作成しません。

## 対応する実行方法

記録・Markdown再出力には `tools/legacy-recorder` のPowerShell入口を使います。スキルに同梱した同名の3スクリプトは、その入口へ引数と終了コードを渡すラッパーです。

HTML/PDF/DOCX出力、録画・音声などのアプリ機能と旧PowerShell入口の対応範囲は異なります。[利用者ガイド](USERS_GUIDE.md)を参照してください。スキル導入だけではEXEのビルド・アプリのインストール・起動・録画や録音は行いません。ビルドは[開発者ガイド](DEVELOPERS_GUIDE.md)を参照してください。
