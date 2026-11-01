# OperationRecorderのリリース手順

正式配布はWindows 11 x64向けの自己完結インストーラーです。親KotorinToysの共通メンテナンスとInno Setup定義を使用し、OperationRecorderの独立Git管理を維持します。

## 生成と検証

1. 子の変更・版を確認し、`pwsh -NoProfile -File .\dev\scripts\Test-OperationRecorder.ps1 -Suite Core`を実行します。実入力・撮影の確認は`-Suite Desktop`を明示し、環境不足の`blocked`を合格と区別します。詳細は[自動テスト](testing.md)を参照してください。
2. 子のコミットと親のGitリンクを確定し、親の採用ソースだけを隔離してパッケージを生成します。

```powershell
cd C:\develop\KotorinToys
pwsh -File .\dev\scripts\stage-release-source.ps1 -Ref <採用親コミット>
pwsh -File <出力された隔離ソース>\dev\scripts\publish-packages.ps1 -Products OperationRecorder -RecordVersion <対象の親記録版>
```

生成結果のパスを指定して、インストーラーを作成します。

```powershell
pwsh -File <出力された隔離ソース>\dev\scripts\build-installers.ps1 -Products OperationRecorder -PackageDirectory <生成されたパッケージディレクトリー> -RecordVersion <対象の親記録版>
```

引数は親の最新スクリプトを正本とします。版別の生ログ・ハッシュ台帳・検証結果は、親の`dev/history/vX.Y.Z/logs/<実行識別子>/`へ出力します。対象外の子リポジトリが未配置なら、親とOperationRecorderの採用コミットだけを同じGit archive方式で抽出し、release-source.jsonへ採用元を記録します。未配置の別製品を復元したり、作業差分をコピーしたりしません。

3. 発行物の`OperationRecorder.runtimeconfig.json`に`Microsoft.NETCore.App`と`Microsoft.WindowsDesktop.App`の10.0.14が含まれ、フレームワーク依存になっていないことを確認します。公開時の公式最新修正版と配布安全ゲートで照合します。
4. `MANUAL.md`、`operation-recorder/MANUAL.md`、`templates/`、`Assets/app.ico`、`docs/`、`config/shortcuts.json`、`config/replay-example.json`、`config/replay-learning-prompt.md`、`config/replay-learning-response.schema.json`、`scripts/Invoke-OperationReplay.ps1`、`scripts/New-OperationReplayWithCodex.ps1`、Markdigの`THIRD-PARTY-NOTICES.md`、.NETの`runtime-licenses/`、共通`maintenance/`を同梱します。利用者設定・記録・秘密情報を含めません。
5. インストール後の製品識別・起動・表示・正常停止と、記録・保存・未保存編集時の停止拒否を確認します。

## GitHub公開とカタログ登録

公開先は[OperationRecorder](https://github.com/KotorinChunChun/OperationRecorder)です。製品版をX.Y.Zとすると、タグ`vX.Y.Z`のReleaseに`OperationRecorder-X.Y.Z-setup.exe`、`OperationRecorder-SHA256SUMS.txt`、`OperationRecorder-packages.json`を登録し、匿名のHTTP再取得で容量・SHA-256を照合します。既存タグや資産は差し替えません。

親の公開`config/tools.json`へ版・公開状態とリポジトリURLを反映します。「公開情報一覧の更新」Actionsを実行し、release-indexブランチに対象タグ・資産が掲載されたことを確認します。タグ・公開日・インストーラーURL・容量・SHA-256は本体が一覧と製品別台帳から照合します。本体の「状態を更新」後、OperationRecorderを選択すると「インストール」または「アップデート」が利用できます。公開情報のキャッシュは最大5分残る場合があります。カタログ更新と子の公開だけで、本体の実行バイナリーを自動的に更新することはありません。

配布の結果は子の`dev/history/vX.Y.Z/vX.Y.Z-imp.md`と、親の対象版報告へ残します。生ログや配布物はGitに登録しません。
