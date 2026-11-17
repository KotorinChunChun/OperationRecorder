---
name: operation-recorder
description: >
  ユーザー自身のWindows操作を記録し、操作前後の画像とUI要素情報から手順書を作成・再出力する。
  操作記録、スクリーンショット付きマニュアル、PSRの代替を頼まれたときに使う。
  ローカルのOperationRecorderリポジトリのエンジンとテンプレートを直接使用する。
  AI自身によるアプリの自動操作・動作確認だけの依頼には使用しない。
---

# OperationRecorderによる操作記録

PowerShellはすべて `pwsh` で実行する。スキルは実処理を内蔵せず、導入済みのローカルリポジトリを使用する。

## 参照先を確認する

このスキル内の `scripts/Resolve-OperationRecorderRepository.ps1` を実行し、返された絶対パスを以後の `$repo` とする。
参照先は環境変数 `OPERATION_RECORDER_ROOT`、次に `%LOCALAPPDATA%/OperationRecorderSkill/repository.json` の `repositoryRoot` から解決する。明示された参照先が不正なら停止し、別の場所やスキル内の旧エンジンへ切り替えない。

未導入なら https://github.com/KotorinChunChun/OperationRecorder の `docs/ai-skill.md` を案内する。リポジトリ取得・スキル導入の依頼がある場合はその手順を実施する。記録の依頼だけで自動pullやアプリのインストールを行わない。

## 実行方法を選ぶ

まず `$repo/docs/USERS_GUIDE.md` の該当箇所を読む。ソース配置・ビルドが必要なら `$repo/docs/DEVELOPERS_GUIDE.md` を読む。

- 人が操作する様子の記録：撮影対象、名前、保存先を依頼から確認し、`tools/legacy-recorder/Start-OperationRecording.ps1` を使う。指定がなければ対象ウィンドウまたはアプリに絞る。ディスプレイ全体の撮影はその範囲が依頼された場合に使う。
- 保存済み記録のMarkdown出力：`tools/legacy-recorder/New-OperationReport.ps1` を使う。AIからは必要な引数を指定し、対話式の再発行ツールを使わない。
- HTML/PDF/DOCX、録画・音声など：現行ガイドを読み、対応するアプリ/CLIを使う。旧PowerShell入口でGUIの全機能を利用できるとは扱わない。EXEがなければ開発者ガイドでビルド方法を確認する。
- 利用者が対話式で設定・再発行したい場合：`Start-OperationRecording.ps1` または `Reissue-OperationReport.ps1` のコマンドを案内する。
- リプレイを明示された場合：`$repo/docs/replay.md`、条件案作成なら `docs/replay-learning.md` を読む。記録や手順書作成だけの依頼からリプレイを実行しない。

スキル内の同名ラッパーも上記3つのPowerShell入口へ委譲する。引数はリポジトリの `Get-Help <スクリプト> -Full` で確認する。スキルの更新時に残された古い `scripts/src`・テンプレート・マニュアルは実行・参照しない。

```powershell
# <スキルの絶対パス> は実際に読み込んだスキルの場所に置き換える
$repo = & '<スキルの絶対パス>/scripts/Resolve-OperationRecorderRepository.ps1'
pwsh -NoProfile -File "$repo/tools/legacy-recorder/Start-OperationRecording.ps1" -Mode App -ProcessName EXCEL -Caption '操作記録' -OutDir 'D:/OperationRecords'
pwsh -NoProfile -File "$repo/tools/legacy-recorder/New-OperationReport.ps1" -SessionDir 'D:/OperationRecords/session-対象' -Template user,ai -ImageStyle Embed
```

## 記録時の注意

利用者へ開始・停止キーと保存先を伝え、実際の操作は利用者が行う。ショートカットの既定値や変更方法は現行ガイド/スクリプトで確認する。
録画・マイクを勝手に有効にせず、記録中の入力値や対象内に表示された情報も保存されることを説明する。対象外のアプリを閉じたり操作したりしない。
保存後は生成された記録・画像・手順書の場所を示す。共有や公開は別途指示された場合に行う。
