# ログイン後の別ウィンドウへの工程引継ぎ

OperationRecorder 0.1.13の外部証拠API（schema 2）で、開始時のログイン窓が閉じるか非表示になり、同じプロセスに登録済みの後続画面が開く場合に使います。操作前は登録済みのパスワード欄を黒くして保存し、操作後は承認した固定領域だけをWGCフレームから保存します。登録資材の形式は[パスワード欄の保護](password-only-local-evidence.md)と[承認済み固定領域](approved-roi-scenes.md)を参照してください。

## 呼び出し

ログイン窓の `before` を持つパスワードプロファイルと、後続窓の `root / after` を持つ固定領域プロファイルをそれぞれ登録します。両資材を対象のEXEに合わせます。PowerShell 7.6で、同じビルドのEXE・DLL・スクリプトを組み合わせます。

```powershell
Import-Module ./scripts/OperationRecorderEvidence.psm1
$policy = @{
    passwordProfile = $approvedLoginSelection
    transitionAfterRoiProfile = $approvedDestinationSelection
}
$session = New-OperationEvidenceSession -Executable $exe -SchemaVersion 2 `
    -Target $loginTarget -GroupPolicy $policy -OutputDirectory $unusedLocalEvidenceDirectory
try {
    # 事前許可の後に一度だけ操作し、呼び出し側で画面切替を待って戻る処理。
    $result = Invoke-OperationEvidenceStep -Session $session -StepId ([guid]::NewGuid()) `
        -OperationName invoke -ExpectWindowTransition -Action $invokeOnceAndWaitForTransition
    $result.capture
} finally {
    $null = Close-OperationEvidenceSession -Session $session
}
```

直接IPCを使う場合、`StartSession` の `groupPolicy` に上記の選択オブジェクト（各 `profileId/revision/profileSha256`）を指定します。`capabilities` に `approved-window-transition` があることを確認し、切替が起きる `BeginStep` にだけ `expectWindowTransition:true` を送ります。`before-saved` かつ `operationAllowed:true` を受けてから一度だけ操作します。切替を待った後、同じ `stepToken` と `outcome:"success"` を `EndStep` に送ります。

`evidencePaths` が示す `commit.json` と同じフォルダーの `group.json` に、ウィンドウごとの保存画像（`windows[].imageFile`）とSHA256が記録されます。複数窓を使う場合は `role` が `root` と `owned` の各行を参照します。

Recorderが元のPID・開始時刻・Windowsセッション・EXEを照合し、同プロセスで新たに可視になった候補を確認します。後続のHWNDは要求から受け取りません。後続が一意で承認済みのWGC固定領域と保存結果を確認できた場合に `after-saved` を返します。

後続のrootが直接所有する同じプロセスの通知窓が1枚だけある場合は、通知窓専用の `owned / after` を同じ固定領域プロファイルに登録します。通知窓の表示でrootのUI構造が変わる場合は、通知ありの `root / after` も登録します。通知窓自身の画素と構造も照合するため、`after-saved` まで通知を表示したままにします。子コントロールは親の画像に含まれます。別プロセスや画面違い、候補・通知窓の競合、期限切れ、保存失敗を受けた場合は工程を未確定として扱い、操作や `EndStep` を再送せず呼び出し側の異常終了手順へ進みます。後続窓に対して作業を続ける場合は、成功後にその窓を確認して新しいセッションを作ります。

## パスワード欄の設定候補

調査対象は正規パスのクロノスのログイン窓か専用試験用のフォームです。`$loginTarget` は既存の `EvidenceTarget`（`pid/processStartTimeUtc/exePath/windowsSessionId/windowHandle`）と同じ形式です。公開されている静的ラベルと、型が分かっている欄を調べるため、ラベルとWinFormsクラスの接頭辞を指定します。実クロノスで指定できる公開ラベルは `パスワード:` です。

```powershell
$candidate = ./scripts/Get-OperationEvidencePasswordLayoutCandidate.ps1 `
    -Executable $exe -Target $loginTarget -PublicLabel 'パスワード:' `
    -FieldClassPrefix 'WindowsForms10.Window' -TimeoutMilliseconds 5000
$candidate | ConvertTo-Json -Depth 10
```

`candidate-only` は対象EXEのSHA256とファイル版、DPI、キャプチャ枠、文字を含まないUI構造のSHA256、公開ラベルに対応する欄のクラス・型・物理ピクセル座標を取得できた状態です。WinFormsの標準パスワード欄には `WindowsForms10.EDIT` を指定できます。専用の読み取りプロセスが構造を確認し、画像取得、欄の値取得、入力、プロファイル書き込みは行いません。候補からマスクの余白と描画範囲を確認し、登録資材は別に審査してください。

試験は専用WinFormsで実施します。

```powershell
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite WindowTransition
```
