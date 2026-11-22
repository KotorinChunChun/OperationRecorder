# 承認済みの固定領域を照合する

開発版0.1.11の外部証拠APIで、固定された見出しやボタンを診断用に保存する方式です。開始時に `groupPolicy.roiProfile` を明示すると、指定された承認済み領域をすべて同じWGCフレームと比較します。公開する領域は照合に成功した審査済み資材の画素で再構成し、それ以外を不透明な黒で埋めます。従来の `groupPolicy.disclosure` は全フレーム照合の契約を引き続き使用します。

## 資材の登録

1. 対象アプリの版、DPI、ウィンドウの実寸、想定する工程の画面を固定します。機密のない専用画面、または設計・審査済みの非秘密画像から、各領域の不透明なBGRA画素を用意します。実行時に撮れた業務画面から資材を自動登録する機能はありません。
2. 固定UIを識別する画素を `publish: false` の照合専用領域に登録し、公開の対象は `publish: true` にします。すべての領域が一致した場合にだけ公開します。領域は重ならない座標を指定します。公開領域には黒以外の画素が必要です。
3. 構造確認には、審査済みの非文字UI構造を `EvidenceUi` として用意し、`ApprovedRoiGate.StructureSha256(ui)` でハッシュを計算します。座標は対象のフレーム基準です。`EvidenceNode` の型、位置、寸法、有効状態を使います。パスワード、空の矩形、壊れた構造、切り詰めがあれば有効なハッシュを返しません。Windowsの文字や入力値を資材に含めません。
4. `OperationRecorder.exe` の隣に `approved-roi-catalog.json` と `approved-roi-assets` フォルダーを置きます。パッケージのファイル名は `<profileId>.<revision>.json` です。`Get-FileHash -Algorithm SHA256` でそのファイルのSHA256を計算し、カタログにID・版と共に登録します。資材変更時は審査して版を上げます。EXE・DLL・カタログ・資材を同じ管理権限下に置き、実行ユーザーには読み取り権限を付けます。書き込み可能な開発フォルダーは専用試験に使います。

カタログの形式です。`<sha256>` は承認したパッケージファイル全体のハッシュで置き換えます。

```json
{
  "schemaVersion": 1,
  "profiles": [{"profileId":"fixed-ui", "revision":1, "profileSha256":"<sha256>"}]
}
```

パッケージは `schemaVersion: 1`、`profileId`、`revision`、`appSha256`、`scenes` を持ちます。`appSha256` は対象EXEのSHA256です。各シーンに `sceneId`、`role`、`phases`、`dpi`、`windowWidth`、`windowHeight`、`uiSha256`、`regions` を指定します。工程名は `before`、`after`、`snapshot`、`failure` のいずれかです。

各領域には `regionId`、`region: {x,y,width,height}`、`bgra`、`publish` が必要です。`bgra` は上から下、左から右のBGRA32、アルファ255、行パディングなしのバイト列をBase64にした値です。IDとロールには小文字の英数字とハイフンを使い、先頭は英字、最大32文字です。重複領域、部分透過、寸法の不一致、ID・版・ハッシュの不一致は拒否します。

## PowerShellで検証・選択する

PowerShell 7.6以上で、同じビルドのEXE、DLLと同梱のpsm1を使います。`$target` の指定は既存のウィンドウ群APIと同じです。

```powershell
Import-Module ./scripts/OperationRecorderEvidence.psm1
$selection = @{
    profileId = 'fixed-ui'; revision = 1; profileSha256 = $approvedPackageSha256
}
Test-OperationEvidenceRoiProfile -Executable $exe -ProfileId $selection.profileId `
    -Revision $selection.revision -ProfileSha256 $selection.profileSha256 `
    -TargetExecutable $target.exePath

$session = New-OperationEvidenceSession -Executable $exe -Target $target `
    -SchemaVersion 2 -GroupPolicy @{ roiProfile = $selection } -OutputDirectory $newEvidenceDirectory
try {
    if ('approved-roi-scenes' -notin $session.Capabilities) { throw '承認済み領域方式が利用できません。' }
    Get-OperationEvidenceSnapshot -Session $session
} finally { $null = Close-OperationEvidenceSession -Session $session }
```

直接IPCでは `StartSession` の `groupPolicy.roiProfile` に同じ3項目を渡し、`capabilities` の `approved-roi-scenes` を確認します。`disclosure` との併用と `requirePublicDiagnostic:false` は拒否されます。未登録は `roi-profile-unavailable`、SHAなどの差し替えは `roi-profile-mismatch`、書式違反は `roi-profile-invalid`、対象EXEの不一致は `profile-app-mismatch` です。

## 判定と受入範囲

公開には、同じフレームの必須領域すべてが一つの承認済みシーンに一致し、取得前後のUI構造がそのシーンと一致することが必要です。対象窓の承認と世代、フレームの鮮度、保存とその検証も既存契約で確認します。必要な窓の欠落、新しい未承認窓、パスワードUI、照合領域と保護対象の重なり、曖昧な一致は公開と操作許可を拒否します。単発取得は観測用で、操作許可を返しません。

新方式のみ、WGCのフレーム時刻が現在の単調時計より32 ms以内で先行する場合は、期限内で短く待って実時計をもう一度読みます。元のフレーム時刻で鮮度を再検査し、時刻がまだ先行している場合や期限切れは拒否します。

成功時の `group.json` は各窓に `approvedSceneId` を記録します。出力の画素は審査済み資材から再構成したものです。公開範囲の外は常に黒です。brokerは保存画像を独立して復号し、承認済み資材との一致と範囲外の黒を再検証します。WGCで実際に照合した主体は隔離workerです。保管先はEXEと同じ信頼境界にあり、カタログのSHAは書き込み権限の代わりにはなりません。

操作の前後は `BeginStep` と `EndStep` を使います。`before-saved` と `operationAllowed:true` を受けた後に対象を再確認して一度だけ操作し、操作後の結果不明や保存未確認は保留にします。全黒の画像は診断品質を満たしません。画像が確認できても、黒化された範囲の社員数、日時、保存先や業務データの正しさは呼出し側で確認します。

表示・構造を同時に取得する機能ではありません。マスク内の描画だけが変わり、窓の構成・構造・固定領域の画素が同じ二画面は区別できないため、認証の有無や工程をこの観測範囲で確認できない画面は運用対象にしません。
