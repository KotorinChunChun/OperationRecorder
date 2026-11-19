# パスワード欄だけを保護するローカル証拠

開発版0.1.12の外部証拠APIで、登録したログイン画面のパスワード欄を黒く塗り、ほかの画素をローカルに保存する方式です。schema 2の `groupPolicy.passwordProfile` で明示的に選びます。従来の全黒、全フレーム照合、固定領域照合も利用できます。`disclosure` や `roiProfile` との併用、および `requirePublicDiagnostic:false` は拒否します。

## 登録する資材

実行ファイルと同じ管理下に `password-evidence-catalog.json` と `password-evidence-assets/<profileId>.<revision>.json` を置きます。カタログは `{"schemaVersion":1,"profiles":[{"profileId":"login-screen","revision":1,"profileSha256":"<資材JSONのSHA256>"}]}` の形式です。SHA256は `Get-FileHash -LiteralPath <資材JSON> -Algorithm SHA256` で計算し、改訂時は版を上げます。EXE・DLL・カタログ・資材の書き込みは管理者側で制御します。取得APIの要求から任意の資材パスやマスク矩形は渡せません。保存先には共有や同期の対象にならないローカルの新規フォルダーを指定します。

資材JSONは次の項目です。座標と構造ハッシュは、登録対象の版と画面を審査して用意します。

| 項目 | 内容 |
|---|---|
| `schemaVersion`, `profileId`, `revision` | `schemaVersion` は `1`。IDと版は登録内容と一致が必要 |
| `appScope`, `appSha256`, `appFileVersion` | 実運用は `xronos-local`。正規のクロノスEXEパス、ファイル全体のSHA256、4要素のファイル版で固定。`synthetic-fixture` は専用試験用 |
| `scenes[]` | `sceneId`, `phases`, `dpi`, `windowWidth`, `windowHeight`, `windowClassPrefix`, `uiSha256`, `fields`。`phases` は `before`、`after`、`snapshot`、`failure` の組み合わせ |
| `fields[]` | `fieldId`, `labelClassPrefix`, `publicLabel`, `fieldClassPrefix`, `fieldControlTypeId`, `fieldBounds`, `mask`。公開ラベルは隣接する静的ラベルの表示名 |

`fieldBounds` と `mask` は、キャプチャ枠左上からの物理ピクセル `{x,y,width,height}` です。登録 `mask` は `fieldBounds` の各辺に4 px以上の余白を取り、影・枠・はみ出す描画もカバーできるよう審査します。マスク同士は重ねず、すべてをフレーム内に収めます。`PasswordEvidenceGate.StructureSha256(EvidenceUi)` は、審査済みの文字なしUI構造の型、物理矩形、パスワード属性、Enabledから正規ハッシュを作ります。UI要素の位置や数を確認し、未確認の実画面を取り込んでルールとして登録する運用はしません。

## PowerShellで事前検査と取得

PowerShell 7.6以上で、同じビルドのEXE、DLL、psm1を使います。`$target` のPID、開始UTC時刻、EXEパス、WindowsセッションID、root HWNDは[ウィンドウ群の契約](wgc-window-groups.md)と同じです。

```powershell
Import-Module ./scripts/OperationRecorderEvidence.psm1
$selection = @{
    profileId = 'login-screen'; revision = 1; profileSha256 = $approvedPackageSha256
}
Test-OperationEvidencePasswordProfile -Executable $exe -ProfileId $selection.profileId `
    -Revision $selection.revision -ProfileSha256 $selection.profileSha256 `
    -TargetExecutable $target.exePath

$session = New-OperationEvidenceSession -Executable $exe -Target $target `
    -SchemaVersion 2 -GroupPolicy @{ passwordProfile = $selection } -OutputDirectory $localEvidenceDirectory
try {
    if ('password-only-local-evidence' -notin $session.Capabilities) { throw 'この方式を利用できません。' }
    Get-OperationEvidenceSnapshot -Session $session
} finally { $null = Close-OperationEvidenceSession -Session $session }
```

直接IPCでは `StartSession` の `groupPolicy.passwordProfile` に `profileId/revision/profileSha256` を送ります。`capabilities` に `password-only-local-evidence` が含まれることを確認します。未登録は `password-profile-unavailable`、登録資材の差し替えは `password-profile-mismatch`、不正規則は `password-profile-invalid`、対象EXE・ファイル版不一致は `profile-app-mismatch` です。

## 確認と失敗時の挙動

パスワード欄を含む、登録済みの可視rootウィンドウ1枚が対象です。ほかの可視トップレベル、未承認窓、登録外のパスワード欄、切り詰められたUI構造は拒否します。正規の入力欄は、登録した公開STATICラベル、その隣にあるNative子ウィンドウのクラスと親子関係、UIAのコントロール型・境界、Win32のピクセル境界がそろったときだけ認めます。`IsPassword=false` でも登録規則に一致するカスタム欄は同じ手順で黒化します。値、入力文字、欄の名前、字幕は出力しません。認証欄は事前審査した描画余白までマスクし、PNGは透過のない画像として作り直します。

絶対位置を移動した場合は、次の取得でDPIと相対位置を再確認します。撮影中に位置や世代が変わった場合、未登録のDPI・寸法、マスクの範囲を確認できない場合は品質不足を返します。既知のログイン窓から別画面へ遷移し、欄がなくなった場合もこのモードでは停止します。画像はマスク済みの暫定出力として保存し、brokerがPNG・SHA・証拠構造・登録マスクの全画素と現在のWin32境界を確認した結果を確定します。

成功時は `quality.disclosureStatus` と窓行が `password-masked` になります。`group.json` のroot行の `passwordMask` は `sceneId` と、登録ID・欄HWND・ラベルHWNDの `fields` だけを持ちます。確定した証拠の `BeginStep` が `before-saved` かつ `operationAllowed:true` の場合だけ次の操作へ進みます。確認不能では全黒または画像なしとなり、`before-inadequate` 等で止まります。期限、鮮度、保存確認、結果不明時の再試行禁止は既存契約どおりです。

UIAとWGCは別の観測です。登録対象には、パスワードが欄とその余白内に収まると審査で確認できる画面だけを使います。同じ構造のまま独自描画で欄外に秘密を表示する画面、独自ポップアップや別の認証画面を特定できない構成は対象にしません。実業務用の登録資材は専用フォーム試験とは別に受け入れてください。

```powershell
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite PasswordEvidence
```
