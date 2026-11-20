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

### ログイン窓と無効な背後窓が同時に表示される場合

0.1.14以降は、読み取り専用コマンドでウィンドウ構成を診断できます。対象には、別途確認した正規のログインHWNDを指定します。

```powershell
$layoutArgs = @{
    Executable = $exe
    Target = $loginTarget
    PublicLabel = 'パスワード:'
    FieldClassPrefix = 'WindowsForms10.Window'
}
$layout = ./scripts/Get-OperationEvidencePasswordLayoutCandidate.ps1 @layoutArgs -DiagnoseWindowLayout
$layout | ConvertTo-Json -Depth 10
```

`diagnostic-only` の `diagnostics` には、判定理由、確認できた2窓の所有関係・可視／有効状態・クラス・DPI・物理枠・文字なしのUI構造ハッシュが入ります。所有関係は `login-owned-by-background`（背後窓がログイン窓を直接所有）、`background-owned-by-login`（ログイン窓が背後窓を直接所有）、`unowned`（どちらにも所有元がない）のいずれかです。窓タイトル、可変文字、画素は取得しません。

`review-required` で `diagnostics.layoutSha256` が得られた場合は、対象版・ログインHWND・背後窓・所有関係・無効状態を確認します。構成を確認した後に、そのハッシュを `$reviewedLayoutSha256` に指定します。

```powershell
$candidate = ./scripts/Get-OperationEvidencePasswordLayoutCandidate.ps1 @layoutArgs `
    -ReviewedWindowLayoutSha256 $reviewedLayoutSha256
```

同一PIDの確認済み構成が観測中も一致すれば `candidate-only` を返します。別の有効窓・第三の可視窓・別の所有元・別PID・認証用の背後画面・ウィンドウ構成の変化ではハッシュを発行せず、候補取得も拒否します。同じクラスでも、UI構造、DPI、2窓の相対位置が変われば確認済みハッシュは一致しません。窓構成ハッシュは候補取得専用です。マスク済み証拠の保存と操作許可は引き続き[パスワード欄の保護](password-only-local-evidence.md)の条件で判定します。

#### 構成を確認できない場合の診断項目

0.1.15以降、`-DiagnoseWindowLayout` は従来の `diagnostics.reason` に加えて、次の情報を返します。

```powershell
$layout.diagnostics |
    Select-Object reason, failureRole, loginStructure, backgroundStructure, change |
    ConvertTo-Json -Depth 6
```

| 項目 | 内容 |
|---|---|
| `failureRole` | 判定に失敗した窓を特定できた場合の `login` または `background` |
| `loginStructure` / `backgroundStructure` | 固定の `code`、確認できたルートの `win32RootEnabled` と `uiaRootEnabled`、走査の `visitedElements` と `deepestDepth`、上限の `maxElements` と `maxDepth` |
| `change` | 差があった `stage` と `kinds`。初回のグラフが不完全な場合も `first-snapshot` / `snapshot-truncated` を返す。この応答に審査用の構成ハッシュは付かない |

有効状態だけが食い違ったときは `uia-root-enabled-mismatch` で、両方の観測値を確認できます。背後窓がWin32で無効、UIAで有効を返す場合でも、他の2窓の条件、完全なUIA構造、認証候補の不在を繰り返し確認できれば、診断は `review-required` と構成ハッシュを返します。この構成で候補を得るには、状態の食い違いを含む構成を審査し、そのハッシュを指定します。

構造の `code` は、ルートの観測不能 `uia-root-unavailable`、HWND・型・表示状態の不一致 `uia-root-hwnd-mismatch`、`uia-root-control-type-mismatch`、`uia-root-offscreen`、矩形の不一致 `uia-bounds-invalid`、`uia-root-bounds-mismatch`、`uia-child-outside-window`、別PIDのノード `uia-process-mismatch`、上限 `uia-depth-limit`、`uia-element-limit`、UIA例外 `uia-exception`、構造ハッシュ不成立 `uia-structure-hash-unavailable`、画面外の認証要素 `uia-hidden-password`、可視のネイティブ子との照合不能 `uia-native-children-unproven`、構造成立 `ok` を区別します。ルートの深さは0で、要素に到達する前の深さは-1です。

`window-layout-changed` の `stage` は、ネイティブ再確認 `native-recheck`、2回目のグラフ `confirmation-snapshot`、UIAと構造の再観測 `confirmation-observation`、最終グラフ `final-snapshot` を区別します。`kinds` は `graph-revision`、`window-set`、`native-root-enabled`、`uia-root-enabled`、`uia-structure`、`uia-traversal` 等の変化した項目だけを返します。`graph-revision` だけが返る場合は、構造や矩形は同じでも観測中にウィンドウイベントがあったことを示します。UIAの例外本文、窓タイトル、可変文字、パスワードの名前・値、画素は応答に入りません。

試験は専用WinFormsで実施します。

```powershell
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite WindowTransition
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite PasswordLayoutBackdrop
```
