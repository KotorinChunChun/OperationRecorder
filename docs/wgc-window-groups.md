# WGCとウィンドウ群の外部証拠API

開発版0.1.9のschemaVersion 2。撮影、公開判定、保存完了、診断品質を分離します。**専用fixtureでは非アクティブ・背後更新・画面外のWGC取得と、親画像からの子領域の座標・画素切出しを確認しました。最小化・Hide・子HWND直接取得では取得不能を観測し、子fallback全体や他の状態・実業務アプリの受入は未完了です。** 実際の対象アプリで使用済みとは扱わないでください。

## 保証する範囲

- HWNDを指定したWindows.Graphics.Captureだけを使います。前面化、復元、画面全体、PrintWindow、OS制限の迂回へ切り替えません。
- プロセスPID/開始UTC時刻/EXE/ユーザー/Windowsセッションとroot HWNDを固定し、親子と所有を別に観測します。構造・世代の一致だけを描画の原子的な同一性証明には使いません。
- 既定は全体マスク。公開は、実際に取得した**同じフレーム全体**とROIの両方が事前承認SHA256へ一致し、版・ロール・DPI・サイズ・保護条件も一致した場合だけです。
- 公開対象は既知の非秘密の参照画像に一致する状態に限ります。未知のエラー文や社員情報を自動公開する機能ではありません。動的な一般画面ではstate-unproven/state-mismatchとして保護を維持します。
- Name/Value/Textは読みません。文字診断と連続録画は後続です。schema2では動画指定を拒否します。schema1の1フレーム動画区間を連続録画とは扱いません。

## 起動とPowerShell 7.6

ビルドは.NET 10 SDK、TFMはnet10.0-windows10.0.19041.0。製品の対象環境は引き続きWindows 11 x64です。Windows SDK投影を使用します。

```powershell
dotnet build ./src/OperationRecorder/OperationRecorderApp.csproj -c Release --artifacts-path ./artifacts/evidence
Import-Module ./scripts/OperationRecorderEvidence.psm1
$exe = (Resolve-Path ./artifacts/evidence/bin/OperationRecorderApp/release/OperationRecorder.exe).Path
# $targetは従来契約のpid/processStartTimeUtc/exePath/windowsSessionId/windowHandle。
$session = New-OperationEvidenceSession -Executable $exe -Target $target `
  -OutputDirectory 'D:/Evidence/未使用の実行フォルダー' -SchemaVersion 2
try {
    $observed = Send-OperationEvidenceRequest -Session $session -Request @{method='ListWindows'}
    $observed.windowGraph
    # 診断画像を要求する既定では、公開プロファイルが無いままの操作は拒否される。
    # 許可操作の実装は利用側の責任。失敗後に自動で同じ工程を再実行しない。
} finally { Close-OperationEvidenceSession -Session $session }
```

公開プロファイルは開始時の `-GroupPolicy @{ disclosure=$profile }` に指定します。ファイルは手動で内容を審査したものだけを読みます。許可画像を現行のキャプチャから自動学習するコマンドはありません。

## プロファイル

0.1.11以降は、固定された領域を公開する[承認済み領域方式](approved-roi-scenes.md)も明示選択できます。以下は従来の全フレーム照合の仕様です。

0.1.12以降の[パスワード欄だけを保護する方式](password-only-local-evidence.md)は、登録したログイン画面に限定した別の選択です。

0.1.13では、ログイン窓から登録した別のrootへ移る操作に[ウィンドウ切替専用の工程](approved-window-transition.md)を明示できます。

`DisclosureProfile` はschemaVersion=1、profileId、revision、appSha256、rules、approvedFrameSha256を持ちます。appSha256は対象EXEファイルのSHA256で、全依存モジュールの真正性を保証する署名ではありません。

各ruleはruleId、role、dpi、windowWidth/windowHeight、region（x/y/width/height）、approvedPixelSha256です。全frameの許可リストも必須です。承認資材は事前に安全と判断した画像から作成してください。秘密が入った実画面を収集して登録する運用にはしません。

canonical hashは、LE int32幅・高さと、黒背景へ合成した不透明BGRA画素（strideなし）のSHA256。`DisclosureGate.PixelHash(Bitmap, Rectangle)`を承認資材作成時に利用できます。実フレーム照合後に元画像を読み直さず、照合に使った同じ正規化バイトだけを新しい黒canvasへ合成します。認証や未知Custom/Edit領域と重なるruleは拒否し、保護範囲が計算できなければ全体保護です。

UIA照合とWGC取得は同時ではありません。公開の安全根拠は構造の前後一致ではなく、そのフレーム自体の事前承認画素との完全一致です。微小な描画・DPI・テーマ・版の差でも拒否される保守的な方式です。ハッシュを自動更新して通すことはしません。

## ウィンドウの参加

GroupPolicyはapprovals、disclosure、requirePublicDiagnostic（既定true）、maxWindows（既定16/最大32）、maxFrameAgeMs（既定3000）、maxGroupSkewMs（既定2000）、maxTotalPixels（既定8,388,608/最大16,777,216）を指定できます。1フレームは最大4,194,304画素です。プロファイル等を含む開始方針は16KiB以内。方針はセッション中に変更できません。

approvalsは `{hwnd,role,required,windowId,generation,allowParentCrop}`。開始時に未指定ならrootを承認対象にし、元から含まれている同PIDの子は非公開のcontained構造として結びます。新しい窓は未承認です。

`ListWindows`で観測したwindowId/generationを確認し、明示的に `ApproveWindows` のwindowApprovalsへ追加できます。これは内容の公開許可を広げません。公開できるロール/画素は開始時のプロファイルに限定されます。既存承認のrole/required/世代を変更する場合は新しいセッションが必要です。別プロセス、同名アプリ、親プロセスという理由だけで自動参加させません。foreignは構造情報のみで、未調査の表示フラグを「非表示と確定」と解釈しません。

親子はWS_CHILD/GA_PARENT、所有はGW_OWNERとルート所有チェーン、UIAは別の論理ツリーです。無所有の別トップレベルは自動承認しません。HWNDの世代は観測に基づくため、イベント欠落中に完全同型で再利用された場合の絶対検出は保証しません。公開には別途、全frameの承認照合を要求します。

子の直接WGC取得が成立しない場合に限り、既に許可した親のWGCフレームから検証した領域を切り出せます。親フレーム外にある部分は捏造せず不足とします。親画像に含まれる子/認証領域も親側で保護します。専用fixtureでは直接取得不能を観測し、親DWMサイズとWGC ContentSizeの一致・包含・既知色画素の切出しを確認しました。実workerの子fallbackと公開承認を通す全体試験は未受入です。

## 品質と保存

schema2応答はqualityを持ちます。captureStatus、disclosureStatus、diagnosticStatus、requirementsMet、reasons、missingWindowsを別々に確認します。全黒の保存だけではdiagnosticStatus=sufficientにしません。geometryだけでよいと明示したセッションでも、その公開画像が診断可能になったとは表示しません。

BeginStepは `before-saved`、operationAllowed=true、必要品質の充足が揃ってから操作可能です。保存できても不足があれば `before-inadequate` / `after-inadequate` となり、次工程を止めます。unknown結果もpendingです。取得中の集合変化、世代変更、未知窓、時刻差、必須窓不足は品質へ反映します。

成果物は工程/phaseごとにplan.json、group.json、windowId別の保護PNG/UI JSON、SHA256付きcommit.jsonです。windowId/generation、親/owner、役割、HWND、DPI/サイズ、WGCフレームQPC時刻（100ns）、取得方式、公開状態、品質を保存します。前後で窓が増減しても同じstepIdで追跡します。単一時点の原子的なウィンドウ群とは称しません。

保存後のbroker検証でも集合・世代が変わっていれば画像を再保護し、group/commitを再確定して不足を返します。応答確定時にも必須フレームの鮮度を再確認し、保存・検証待ちで期限を超えた画像では操作を許可しません。

InspectSessionはファイルの存在だけでなくハッシュと群の対応を検証します。過去の成果物には現在時刻からの鮮度制約を適用しません。最大100工程を返し、100件を超える場合はhasMoreSteps=true、特定工程はstepIdで確認します。従来schema1へ拡張フィールドを無断で返さず、新契約の失敗を旧方式へ自動降格しません。

<a id="capture-snapshot"></a>

## 通常記録と並行する単発取得（0.1.10以降）

別ツールは通常記録とは別に、同じ `OperationRecorder.exe` の非表示brokerへ接続します。通常記録の開始・停止や書き込み先とは独立しています。単発取得の出力先には新しい外部証拠用フォルダーを指定します。通常記録の保存済みデータは、利用者ガイドの記録フォルダーから読み取れます。

```powershell
# $exe、$targetは上の起動例と同じ。出力先には新規のフォルダーを指定する。
$session = New-OperationEvidenceSession -Executable $exe -Target $target `
  -OutputDirectory 'D:/Evidence/単発取得用の新規フォルダー' -SchemaVersion 2
try {
    # HWNDの関係と幾何だけが必要な場合は ListWindows を使用する。
    $snapshot = Get-OperationEvidenceSnapshot -Session $session -TimeoutMilliseconds 5000
    $snapshot | Select-Object status, quality, collectionEndedUtc, evidencePaths
} finally { $null = Close-OperationEvidenceSession -Session $session }
```

直接IPCの場合は、開始済みschema 2セッションに `method:'CaptureSnapshot'` と共通項目 `schemaVersion/requestId/sessionId/deadlineUtc` を送ります。`StartSession` の `capabilities` に `capture-snapshot` があることを確認できます。対象と公開方針は開始時のものを使います。`BeginStep` と `EndStep` は操作前後の取得に引き続き使用します。

| 応答 | 意味 |
|---|---|
| `snapshot-saved` | 取得結果を保存し、セッションの要求品質を満たした |
| `snapshot-inadequate` | 保護済みの結果は保存できたが、取得・公開の要求品質が不足した。`quality` で理由と欠落を確認する |
| `snapshot-busy` | 同一ユーザー・Windowsセッションに別の単発取得がある。空きを待たず返す。後で取得する場合は新しい `requestId` を使う |
| `deadline-exceeded` / `response-unconfirmed` | 期限内に取得または応答を確認できなかった |

保存先は外部証拠フォルダー配下の `snapshots/<requestIdの32桁N形式>/` です。`evidencePaths` は検証済み `commit.json` へのパス、`collectionEndedUtc` は窓群の収集終了時刻です。各画像の取得時刻は `group.json` の `frameTime100ns` を参照します。単発取得の `operationAllowed` は常にfalseで、工程IDや操作トークンを発行しません。`InspectSession` の工程一覧には含めません。

公開条件は前後撮影と共通です。既定では画素を黒く保護し、UI構造に文字や値を含めません。WGCで画像を取得できなかった窓は結果に欠落として現れます。窓の関係だけを調べる場合は `ListWindows` を使えます。空き待ちで通常記録を止めることを避けるため、単発取得は同時1件に制限します。

## 検証コマンドと未受入

```powershell
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite Core
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite WindowGraph
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite Wgc
pwsh -NoProfile -File ./dev/scripts/Test-OperationRecorder.ps1 -Suite Snapshot
```

初期実行はOpenInputDesktop参照拒否でWGC_BLOCKEDとなり、runnerでblocked/終了コード3へ分類しました。その後、pwshの子プロセス経由で実行した専用試験では取得が成立しました。OS解除やdesktop切替は行っておらず、実行条件の差の原因は未特定です。blockedを成功/skipへ置き換えません。

2026-11-18の専用fixtureの観測結果は次のとおりです。このアプリと実行条件での結果であり、一般の全アプリへ適用しません。

| 状態 | 観測 |
|---|---|
| 非アクティブ | captured。赤い参照画素を確認 |
| 別の専用窓で覆った後の内容更新 | captured。更新後の青い画素を確認 |
| 画面外 | captured |
| 最小化 | deadline-exceeded |
| SW_HIDE | capture-unavailable |
| 子HWNDの直接取得 | capture-unavailable |
| 親フレームからの子crop | DPI96で座標・包含・既知色画素を確認。workerの子fallback全体は未受入 |
| 実WGCと公開profileを組み合わせた肯定E2E | 未受入。公開判定の肯定例は合成画像・偽backendで検証 |
| 親外popup、GPU障害 | 未受入 |
| cloaked、ロック、切断、別仮想desktop、複数DPI | 未試行 |

ロック/切断/別仮想desktopは専用VM/セッションで別途確認します。普段の利用者環境を解除・切替して試験しません。古いtimestamp/世代のフレームを受理せず、同じ画像ハッシュという理由だけで停止とも判断しません。

対象アプリの操作、状態比較、異常時の通常終了→期限→強制終了→再列挙は利用側の責任です。Recorderの取得/保存を無期限に待って終了を妨げません。対象アプリの年月/勤怠データ/終了タスク、通知、他アプリ、通常設定は今回変更しません。
