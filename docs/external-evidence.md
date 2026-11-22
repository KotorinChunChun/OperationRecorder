# 外部自動操作の証拠API

WGCとウィンドウ群の開発版拡張は[schema2の契約](wgc-window-groups.md)を参照してください。以下のschema1とは明示的に選択し、診断品質や実機受入を混同しません。

開発版0.1.7で追加した、操作前後の証拠を明示的に保存するAPIです。利用者の常駐アプリ、設定、入力フックとは独立して動きます。APIは対象アプリへ入力したり、終了させたりしません。実業務アプリとの連携受入は未実施です。

## 保護モードと保証範囲

初版は機密保護を優先する厳格モードです。**画像全体を黒くマスクし、Name/Value/Textを一切読みません。** 保存するUI情報は、対象内のコントロール型・矩形・有効状態・password判定と、取得範囲の切詰め情報です。認証判定はほかの情報より先に行い、passwordのサブツリーへ降りません。未知の認証UIも文字と画素を残しません。

画像から画面の文言や操作の見た目を診断する用途には、この初版の情報だけでは不足します。公開領域を指定して実画素を残す機能、文字情報を含む診断、連続録画の秘密領域マスクは未実装です。従来の人向け記録・リプレイにこの機密保護を追加したという意味でもありません。

`before-saved` と `operationAllowed:true` は、PNG/UI JSON/任意MP4とハッシュ付きcommit、および工程状態を保存してから返します。`FileStream.Flush(true)` と同一ディレクトリ内のatomic renameを使います。プロセスクラッシュ後の保存確認を目的とし、ストレージ故障や停電に対する完全な耐久性は保証しません。beforeとafterは別々に確定し、after失敗時もbeforeを削除しません。

「証拠が保存された」と「操作が成功した」は別です。操作結果は呼出し側が判定して `outcome` に渡します。`unknown` は画像が保存されてもpendingに残り、次のBeginStepを受け付けません。Recorder自身は期待状態の業務上の正しさを判定しません。

## プロセスと期限

- broker：非表示の専用EXE。同一ユーザー限定のNamedPipeを使い、接続元PIDと開始時刻も確認します。UIAは呼びません。
- worker：要求ごとの別プロセス。対象HWNDのPrintWindow、UIA、画像/動画保存を行います。デスクトップは撮影しません。
- worker/encoderはKill-on-close Job Objectに所属します。期限超過・broker終了・クラッシュ時に対象アプリではなく記録用プロセスを終了します。
- 呼出し元の死亡/開始時刻変更は200ms間隔で監視します。停止しないbrokerはさらに500ms後に自身を終了します。
- 要求期限は最大30秒。クライアントも独立した期限を持ち、遅れた応答を次の応答として読まず、接続を破棄します。応答が不明なら操作を許可しません。
- 出力先は未使用の固定ローカルディレクトリ。UNC、再解析ポイント、既存記録への上書きを拒否し、現在のユーザーだけの継承ACLを設定します。

PID・プロセス開始UTC時刻・EXE絶対パス・Windowsセッション・同一ユーザー・トップレベルHWNDを各工程で再検証します。HWNDの破棄も監視します。新しいダイアログなど別HWNDを記録したい場合は、その対象を明示した別セッションを開始します。対象を自動で別ウィンドウへ切り替えません。

## ランタイムとビルド

Windows 11 x64、.NET 10 Desktop Runtimeを使います。ソースからのビルドには.NET 10 SDK、同梱PSクライアントにはPowerShell 7.6以降（.NET 10）が必要です。PSクライアントはDLL付きのframework-dependentビルドで使います。単体EXEの内部アセンブリをPSへ読み込む方式には対応していません。直接NamedPipeを実装するクライアントは、このDLL読み込みに依存しません。

```powershell
dotnet build ./src/OperationRecorder/OperationRecorderApp.csproj -c Release --artifacts-path ./artifacts/evidence -m:1 -nr:false
```

EXEは `artifacts/evidence/bin/OperationRecorderApp/release/OperationRecorder.exe` です。`OperationRecorder.dll`、deps/runtimeconfig、依存DLLを同じ出力から使ってください。GUIや通常の設定は起動しません。

## APIの共通形式

直接IPCを実装する場合は `OperationRecorder.exe --evidence-host <pipe名> <呼出し元PID> <開始UTC ticks>` を起動します。pipe名は `oprec-evidence-` にUUIDの32桁N形式を連結し、開始ticksは.NETのUTC DateTime.Ticks（西暦1年起点、100ns単位）です。どちらも資格情報ではありません。10秒以内に同じ呼出し元プロセスから接続してください。

メッセージは「4バイトlittle-endian符号付き整数のUTF-8バイト長」＋「BOMなしUTF-8 JSON」です。要求/応答とも最大65536バイト、JSON深さ16。1接続で逐次要求を送り、要求ごとに同じrequestIdの応答を待ちます。アイドル60秒で終了します。worker入口はbroker内部用で、クライアントから直接呼びません。

すべての要求に `schemaVersion:1`、UUIDの `requestId/sessionId`、UTCの `deadlineUtc`、`method` が必要です。応答にも同じrequestId/sessionIdを返します。未知のキー/型、過大なメッセージ、自由な文字メタデータは受け付けません。

`stepId` は呼出し側が工程ごとに決めるUUID、`stepToken` はBeginStepで返されるUUIDです。資格情報、入力値、画面の文字、例外文を要求へ渡さないでください。`operationName` は `invoke/set-value/native-message/select/observe/custom` のいずれか。期待/実際のメタデータは `controlTypeId`（50000〜50100）、`enabled`、`fingerprintSha256`（64桁16進）だけです。

| method | 主な入力 | 主な応答 |
|---|---|---|
| StartSession | target、outputDirectory、policy | started、capabilities |
| BeginStep | stepId、operationName、expectedStateMetadata | before-saved、stepToken、evidencePaths、savedAt、operationAllowed:true |
| EndStep | stepToken、outcome（success/failure/unknown）、actualStateMetadata | after-saved、outcome、evidencePaths、savedAt |
| RecordFailure | stepId、errorCode | failure-savedまたは期限/取得失敗、evidencePaths |
| Flush | 共通項目 | flushed、pendingSteps、checkpoint/通常記録への参照 |
| StopSession | 共通項目 | stopped、pendingSteps、保存済み成果物への参照 |
| InspectSession | 共通項目、outputDirectory、任意stepId | inspected、stepStates、pendingSteps、hasMoreSteps。操作許可は常にfalse |

targetは `{pid,processStartTimeUtc,exePath,windowsSessionId,windowHandle}`。開始時刻はUTCの完全な精度で渡します。policyは `{recordVideo:false,ffmpegPath:null,maxElements:128,maxDepth:8}` が既定です。要素数は最大256、深さは最大16、画像は最大4096×4096かつ419万画素です。範囲を超えたUIは `truncated:true` とし、完全取得済みとは扱いません。

RecordFailureのerrorCodeは `unexpected-state/timeout/capture-unavailable/save-unavailable/caller-exit/operation-failed` です。生の例外文は保存・返信しません。

動画を指定した場合は、libx264付きFFmpegの絶対パスも指定します。各before/after/failureの**保護済み1フレームをMP4区間として保存**します。連続録画ではありません。commitの `timelineOffsetMs` がセッション開始からの単調時計時刻です。動画指定なしではencoderを起動せず、MP4を生成しません。encoderの停止/失敗でも期限を超えて呼出し側を待たせません。

## PowerShell 7の例

```powershell
Import-Module ./scripts/OperationRecorderEvidence.psm1
$exe = (Resolve-Path ./artifacts/evidence/bin/OperationRecorderApp/release/OperationRecorder.exe).Path

# このPID/HWNDは呼出し側が許可対象から特定する。名前だけで対象を選ばない。
$targetProcess = Get-Process -Id $targetPid
$target = @{
    pid = $targetProcess.Id
    processStartTimeUtc = $targetProcess.StartTime.ToUniversalTime().ToString('o')
    exePath = $targetProcess.MainModule.FileName
    windowsSessionId = $targetProcess.SessionId
    windowHandle = $targetWindowHandle.ToInt64()
}
$session = New-OperationEvidenceSession -Executable $exe -Target $target -OutputDirectory 'D:/Evidence/run-一意な名前'
try {
    # Actionはbeforeの保存完了時だけ1回呼ばれる。実際の操作と許可範囲は呼出し側で実装する。
    $result = Invoke-OperationEvidenceStep -Session $session -StepId ([guid]::NewGuid()) -OperationName invoke -Action {
        # 許可されたUIA Invoke、ネイティブメッセージ等を1操作だけ実行する。
    }
    $result.status
} finally {
    Close-OperationEvidenceSession -Session $session -TimeoutMilliseconds 1000
}
```

直接使う場合は `Send-OperationEvidenceRequest -Session $session -Request @{method='BeginStep';stepId=$stepId;operationName='invoke'}` のように呼びます。`status='before-saved'` とbooleanの `operationAllowed=true` の両方を確認してから操作します。成功応答の取りこぼしでも、同じ工程を再操作してはいけません。

## 障害応答と復旧

`capture-unavailable`、`save-unavailable`、`video-unavailable`、`deadline-exceeded`、`worker-unavailable`、`target-mismatch` は成功ではありません。クライアント期限、切断、応答ID不一致は `response-unconfirmed` として返します。すべて `operationAllowed:false` です。

同じrequestIdは `duplicate-request` または `request-conflict`、同じstepId/完了tokenは `duplicate-step` になります。これらは元の操作許可を再送せず、記録も操作も再実行しません。afterの途中失敗は `step-indeterminate`、未完了がある場合は `pending-step` で進行を止めます。

セッションは最大10000工程、通常要求IDの保持は20000件です。上限後の新規工程はsession-limitで拒否しますが、開始済み工程のEndStep/RecordFailure、Flush/Stop/読取検査は受け付けます。上限後も工程tokenの状態で重複した保存・操作許可を防ぎます。

クラッシュ後は出力ディレクトリを新セッションへ上書きせず、InspectSessionを使います。新しいbrokerへ共通項目と `method:'InspectSession'`、元のsessionId/outputDirectoryを送り、既知の工程はstepIdで絞れます。最大100工程ずつ返し、100件超はhasMoreSteps=trueです。大量の既知工程はstepIdを指定して検査します。commitがあってもbeforeだけなら結果はindeterminateです。既存工程を再開・再実行するAPIはありません。

## 成果物

```text
external-session.json           外部APIスキーマ1、固定対象と保護方針
checkpoint.json                 Flush/Stop時の工程状態と未完了参照
steps/<stepId>/step.json        token、操作種別、前後の確定/未確定、結果
steps/<stepId>/<phase>/
  started.json                 要求と工程の受理。これだけなら保存未確定
  frame.png / ui.json           保護画像と文字を含まないUI構造
  frame.mp4                    指定時だけ、保護画像の1フレーム区間
  commit.json                  成果物SHA256、UTC時刻/offset、実装版/実装SHA256
session.json / events.jsonl
edits.json                     完了工程を既存の報告書へ出す互換投影
```

commitはbefore/after/failureそれぞれ独立です。実装SHA256はframework-dependentではOperationRecorder.dll、single-fileでは実行EXEの値を記録します。一時ファイルにも保護済みデータだけを書きます。工程の未確定状態と証拠の有無はInspectSessionで別々に確認できます。既存報告書の出力対象は前後証拠が確定した工程です。

## 呼出し側が実装する終了監視

想定外状態や保存失敗を検出した場合は、新しい業務操作を禁止し、RecordFailureを短い期限で試みます。その成否にかかわらず、呼出し側が「対象識別の再検証→通常終了要求→期限→強制終了→同じ対象の再列挙」を実施してください。証拠のFlush/動画完了を無期限に待って対象アプリの終了を遅らせないでください。各期限、強制終了の許可、再列挙対象は呼出し側の業務方針です。

対象アプリ側には、全操作のBegin/End、保存成功前の操作禁止、想定/実際状態の比較、想定外時の期限付き失敗証拠と終了監視、コード差分確認後に最初から再実地確認する処理が必要です。年月変更や業務データ変更の許可をAPI導入から広げてはいけません。このリポジトリには対象アプリ専用のKill、年月操作、終了タスクや通知を実装していません。

## 検証の境界

Coreでは偽UI/障害worker/実IPC/PS7クライアントと既存記録互換を検証します。専用の別PID fixtureでは実UIA Invoke・対象識別・PNG/実MP4の全画素マスクを検証します。実際の対象アプリ、本物の資格情報、業務データ、ほかのUIプロバイダーや全DPI構成は未受入です。黒マスクのAPI単体成功は、業務連携や見た目の診断を完了したことを意味しません。
