# OperationRecorderのリリース手順

正式配布はWindows 11 x64向けの自己完結インストーラーです。親KotorinToysの共通メンテナンスとInno Setup定義を使用し、OperationRecorderの独立Git管理を維持します。

## 生成と検証

1. 子の変更・版を確認し、`dotnet test .\OperationRecorderApp.Tests\OperationRecorderApp.Tests.csproj -c Release`を実行します。
2. 子のコミットと親のGitリンクを確定し、親の採用ソースだけを隔離してパッケージを生成します。

```powershell
cd C:\develop\KotorinToys
pwsh -File .\dev\scripts\stage-release-source.ps1 -Ref <採用親コミット>
pwsh -File <出力された隔離ソース>\dev\scripts\publish-packages.ps1 -Products OperationRecorder -RecordVersion v0.1.9
```

生成結果のパスを指定して、インストーラーを作成します。

```powershell
pwsh -File <出力された隔離ソース>\dev\scripts\build-installers.ps1 -Products OperationRecorder -PackageDirectory <生成されたパッケージディレクトリー> -RecordVersion v0.1.9
```

引数は親の最新スクリプトを正本とします。版別の生ログ・ハッシュ台帳・検証結果は、親の`dev/history/v0.1.9/logs/<実行識別子>/`へ出力します。将来の版では対象の版を指定します。

3. 発行物の`OperationRecorder.runtimeconfig.json`に`Microsoft.NETCore.App`と`Microsoft.WindowsDesktop.App`の10.0.13が含まれ、フレームワーク依存になっていないことを確認します。
4. `MANUAL.md`、`operation-recorder/MANUAL.md`、`templates/`、`Assets/app.ico`、`docs/`、`config/shortcuts.json`、Markdigの`THIRD-PARTY-NOTICES.md`、.NETの`runtime-licenses/`、共通`maintenance/`を同梱します。利用者設定・記録・秘密情報を含めません。
5. インストール後の製品識別・起動・表示・正常停止と、記録・保存・未保存編集時の停止拒否を確認します。

## GitHub公開とカタログ登録

公開先は[OperationRecorder](https://github.com/KotorinChunChun/OperationRecorder)です。タグ`v1.3.2`のReleaseに`OperationRecorder-1.3.2-setup.exe`、`OperationRecorder-SHA256SUMS.txt`、`OperationRecorder-packages.json`を登録し、匿名のHTTP再取得で容量・SHA-256を照合します。

親の公開`config/tools.json`へ公開状態とリポジトリURLを反映します。タグ・公開日・インストーラーURL・容量・SHA-256は本体が子のReleaseと製品別台帳から照合します。本体の「状態を更新」後、OperationRecorderを選択すると「インストール」が利用できます。カタログ更新と子の公開だけで、本体の実行バイナリーを自動的に更新することはありません。

配布の結果は子の`dev/history/v1.3.2/v1.3.2-imp.md`と、親の対象版報告へ残します。生ログや配布物はGitに登録しません。
