param(
    [Parameter(Mandatory = $true)][string]$TestAssembly,
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'OperationRecorderApp.csproj') -Raw
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $OutputFile) { $OutputFile = Join-Path $repositoryRoot "dev/history/v$version/test-cases.json" }
$assemblyPath = (Resolve-Path -LiteralPath $TestAssembly).Path
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFile)
$historyRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "dev/history/v$version"))
if (-not $outputPath.StartsWith($historyRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ケース表は対象版のdev/history/vX.Y.Z配下へ保存してください。'
}

# 機能の正本。新しい試験クラスを分類しなければ生成を失敗させ、漏れを残さない。
$features = @(
    [ordered]@{ id = 'F-SETTINGS'; title = '設定の既定・移行・保存復元'; boundary = '専用設定JSON、通常設定は変更しない' },
    [ordered]@{ id = 'F-TARGET'; title = '撮影モードと対象ウィンドウ・プロセス・ディスプレイ'; boundary = 'Coreは判断と設定、実OS撮影はDesktopの別ケース' },
    [ordered]@{ id = 'F-INPUT'; title = '入力分類・ホイール集約・撮影規則・画像同一判定'; boundary = '合成イベントと専用生成画像、実入力はDesktopの別ケース' },
    [ordered]@{ id = 'F-HOTKEY'; title = '操作キーの変更・検証・状態表示'; boundary = 'キー値と画面配線、実OS登録の失敗は差替えで検証' },
    [ordered]@{ id = 'F-OVERLAY'; title = 'オーバーレイの直近手順・ボタン・逃避計算'; boundary = '実コントロールと位置計算、外観の良さや全DPIは判定しない' },
    [ordered]@{ id = 'F-RECORDING'; title = '記録の開始・取消・取り直し・保存と失敗処理'; boundary = '実controllerに記録・ホットキー・dispatch・保存境界を注入' },
    [ordered]@{ id = 'F-SESSION-LIST'; title = '記録一覧と複数選択・未保存の保持'; boundary = '実フォーム、画像と記録は専用fixture' },
    [ordered]@{ id = 'F-SESSION-EDIT'; title = '記録名・解説・手順文章と自動文章への復元'; boundary = '実session.json/edits.jsonとフォーム・共通サービス' },
    [ordered]@{ id = 'F-SESSION-DELETE'; title = '手順と記録の削除・確認・失敗'; boundary = 'OSごみ箱は差替え。画像移動失敗でも手順削除を続ける既存best effortを維持' },
    [ordered]@{ id = 'F-COMPOSITION-ENGINE'; title = '重複候補と画像・文章・出典を保持した合成'; boundary = '生成画像を含む複数原記録のスナップショット、原本ハッシュ不変' },
    [ordered]@{ id = 'F-COMPOSITION-SESSION'; title = '共通編集命令・入力中文章・履歴・保存/再開/完成'; boundary = 'GUIとCLIが共有する実編集セッション、保存失敗・閉じる三択' },
    [ordered]@{ id = 'F-COMPOSITION-CLI'; title = '合成JSONシナリオの編集・保存・完成'; boundary = '共通編集セッション、専用workspace、不正命令/領域外参照の拒否' },
    [ordered]@{ id = 'F-COMPOSITION-FORM'; title = '合成フォームの候補・差込・移動・採否・文章・保存'; boundary = 'STA実フォーム、確認とOS副作用はAppInteraction差替え' },
    [ordered]@{ id = 'F-TEMPLATE'; title = 'テンプレートの一覧・追加・保存・戻す・削除・未保存確認'; boundary = '専用テンプレート、実フォーム、確認/ごみ箱を差替え' },
    [ordered]@{ id = 'F-PREVIEW'; title = '未保存テンプレートのプレビューと並列生成'; boundary = '実Markdown/HTMLと埋込画像、Guid一時領域の後始末、ブラウザー描画待ちを使わない' },
    [ordered]@{ id = 'F-EXPORT'; title = 'Markdown/HTMLの出力形式・画像・本文と旧CLI互換'; boundary = '実出力ファイル、生成画像、pwsh比較、外部の既定アプリは起動しない' },
    [ordered]@{ id = 'F-MARKS'; title = '丸・矢印・部品枠・ポインタの画像合成'; boundary = '生成画像の色・座標と原画像不変、古い記録への二重描画を防ぐ' },
    [ordered]@{ id = 'F-HTML'; title = 'HTMLの操作前後切替・拡大・印刷'; boundary = 'CoreでHTML/CSS構造、別のTest-ReportHtml.cjsでヘッドレスブラウザーの表示/ボタン/Escを検証。物理印刷機は対象外' },
    [ordered]@{ id = 'F-LAUNCH'; title = '通常/無表示起動・既存インスタンスへの合図'; boundary = '起動引数・専用Mutex/イベント/管理pipe、通常常駐を変更しない' },
    [ordered]@{ id = 'F-MANAGEMENT'; title = '本体からの表示・停止・記録中/未保存の停止拒否'; boundary = '管理停止方針と実フォーム・専用プロセス、既存アプリを停止しない' },
    [ordered]@{ id = 'F-INTERACTION'; title = 'ヘルプ・外部起動・ショートカット・管理者再起動'; boundary = 'AppInteractionの要求/取消/失敗または隔離拒否、Explorer/UAC/本番ショートカットは実行しない' },
    [ordered]@{ id = 'F-AUTOMATION'; title = '自動操作CLIの結果・終了コード・専用領域'; boundary = '実EXEまたは共通サービス、通常設定/記録・領域外・リンク・不正入力を拒否' },
    [ordered]@{ id = 'F-DESKTOP'; title = '別PID fixtureを使った実OS入力・撮影・保存'; boundary = 'Windowsの実Recorder。専用fixtureだけに入力し、環境不足はblocked。実ハードウェア構成の範囲で検証' }
)
$classFeature = @{
    AppSettingsTests = 'F-SETTINGS'; TargetSettingsTests = 'F-SETTINGS'; TargetRulesTests = 'F-TARGET'
    CaptureRulesTests = 'F-INPUT'; WheelRulesTests = 'F-INPUT'; ImageCompareTests = 'F-INPUT'
    HotkeyTests = 'F-HOTKEY'; UiTextsTests = 'F-HOTKEY'; OverlayPlacementTests = 'F-OVERLAY'
    RecordingLifecycleTests = 'F-RECORDING'; CompositionEntryTests = 'F-SESSION-LIST'; SessionEditorTests = 'F-SESSION-DELETE'
    MergeTests = 'F-COMPOSITION-ENGINE'; CompositionEngineTests = 'F-COMPOSITION-ENGINE'
    CompositionEditingSessionTests = 'F-COMPOSITION-SESSION'; CompositionAutomationTests = 'F-COMPOSITION-CLI'
    CompositionFormTests = 'F-COMPOSITION-FORM'; CompositionSessionUiTests = 'F-COMPOSITION-FORM'
    TemplateStoreTests = 'F-TEMPLATE'; OutputChoicesTests = 'F-TEMPLATE'
    ExportTests = 'F-EXPORT'; ExportCommandTests = 'F-EXPORT'; CliParityTests = 'F-EXPORT'
    MarkTests = 'F-MARKS'; DragArrowTests = 'F-MARKS'; HtmlViewTests = 'F-HTML'
    LaunchTests = 'F-LAUNCH'; AutomationInstanceTests = 'F-LAUNCH'; ManagementTests = 'F-MANAGEMENT'
    AppInteractionTests = 'F-INTERACTION'; AutomationCommandTests = 'F-AUTOMATION'
}

function Get-FeatureId([string]$ClassName, [string]$MethodName) {
    if ($ClassName -eq 'UiWiringTests') {
        switch -Regex ($MethodName) {
            '^撮影規則' { return 'F-SETTINGS' }; '^各撮影モード' { return 'F-TARGET' }
            '^出力しない形式|^保存後に指定' { return 'F-EXPORT' }; '^操作キー' { return 'F-HOTKEY' }
            '^記録名解説|^保存出力' { return 'F-SESSION-EDIT' }; '^手順削除|^記録削除' { return 'F-SESSION-DELETE' }
            '^ヘルプ' { return 'F-INTERACTION' }; '^プレビュー|^テンプレートの未保存プレビュー' { return 'F-PREVIEW' }
            '^テンプレートフォルダ' { return 'F-SETTINGS' }; '^テンプレート|^選択直前に消えたテンプレート' { return 'F-TEMPLATE' }
            '^オーバーレイ' { return 'F-OVERLAY' }
        }
    }
    if ($ClassName -eq 'BasicAutomationTests') {
        if ($MethodName -match '^記録') { return 'F-SESSION-EDIT' }
        if ($MethodName -match '^テンプレート') { return 'F-TEMPLATE' }
        if ($MethodName -match '^設定') { return 'F-SETTINGS' }
        return 'F-AUTOMATION'
    }
    if ($ClassName -eq 'FormSmokeTests') { return $(if ($MethodName -match '合成') { 'F-COMPOSITION-FORM' } else { 'F-TEMPLATE' }) }
    if ($ClassName -match 'Desktop') { return 'F-DESKTOP' }
    if ($classFeature.ContainsKey($ClassName)) { return $classFeature[$ClassName] }
    throw "機能未分類のテストです：$ClassName.$MethodName"
}

function Get-Layer([string]$ClassName, [string]$MethodName) {
    if ($ClassName -match 'Desktop') { return 'desktop' }
    if ($ClassName -match '^UiWiringTests$|^CompositionFormTests$|^CompositionSessionUiTests$|^CompositionEntryTests$|^FormSmokeTests$') { return 'sta' }
    if ($ClassName -eq 'ManagementTests' -and $MethodName -match '未保存') { return 'sta' }
    if ($ClassName -eq 'AutomationInstanceTests' -and $MethodName -match 'プロセス|IPC|pipe|EXE') { return 'cli' }
    if ($ClassName -eq 'AutomationCommandTests') { return $(if ($MethodName -match '^試験領域') { 'api' } else { 'cli' }) }
    if ($ClassName -eq 'BasicAutomationTests' -and $MethodName -match '再解析ポイントを読み込む前') { return 'cli' }
    if ($ClassName -eq 'CliParityTests' -or $MethodName -match 'exe|EXE|通常起動を呼ばない|引数エラーを返す|終了コード|同一指定を処理前') { return 'cli' }
    if ($ClassName -match '^Composition.*Tests$|^BasicAutomationTests$|^TemplateStoreTests$|^SessionEditorTests$|^ExportTests$|^MarkTests$|^HtmlViewTests$') { return 'api' }
    return 'unit'
}

$listing = @(& dotnet vstest $assemblyPath --ListTests 2>&1 | ForEach-Object { $_.ToString() })
if ($LASTEXITCODE -ne 0) { throw "テスト一覧の取得に失敗しました。`n$($listing -join [Environment]::NewLine)" }
$discovered = @($listing | ForEach-Object { if ($_ -match '^\s{4}(\S.+)$') { $Matches[1] } })
if ($discovered.Count -eq 0) { throw 'テストを1件も取得できませんでした。' }
$groups = $discovered | Group-Object { ($_ -split '\(', 2)[0] }
$traitIds = @{}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'OperationRecorderApp.Tests') -Filter '*Tests.cs') {
    $source = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($source, '\[(?:Fact|Theory),\s*Trait\("CaseId",\s*"([^"]+)"\)\][\s\S]*?public\s+(?:async\s+)?(?:void|Task)\s+([^\s(]+)\(')) {
        $traitIds[$match.Groups[2].Value] = $match.Groups[1].Value
    }
}
$cases = foreach ($group in $groups | Sort-Object Name) {
    $fullName = $group.Name
    if ($fullName -notmatch '^OperationRecorderApp\.Tests\.([^.]+)\.(.+)$') { throw "不明なテスト名です：$fullName" }
    $className = $Matches[1]; $methodName = $Matches[2]
    $featureId = Get-FeatureId $className $methodName
    $feature = $features | Where-Object id -eq $featureId
    $layer = Get-Layer $className $methodName
    $id = if ($traitIds.ContainsKey($methodName)) { $traitIds[$methodName] } else {
        'TEST-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fullName))).Substring(0, 12)
    }
    [pscustomobject][ordered]@{
        id = $id; fullName = $fullName; title = $methodName; featureIds = @($featureId); layer = $layer
        suite = $(if ($layer -eq 'desktop') { 'Desktop' } else { 'Core' })
        environment = $(if ($layer -eq 'desktop') { 'Windows対話desktop、前面一致、通常OperationRecorderなし、専用別PIDfixture' } else { 'Windows、.NET 10 SDK/Desktop Runtime、pwsh。通常設定・記録は隔離' })
        boundary = $feature.boundary; displayNames = @($group.Group | Sort-Object)
    }
}
$used = @($cases | ForEach-Object featureIds | Sort-Object -Unique)
if (@($cases | Group-Object id | Where-Object Count -gt 1).Count -gt 0) { throw '重複したケースIDがあります。' }
foreach ($feature in $features) {
    $feature.caseCount = @($cases | Where-Object { $_.featureIds -contains $feature.id }).Count
    $feature.status = $(if ($used -contains $feature.id) { 'automated' } else { 'missing' })
}
$document = [ordered]@{
    schemaVersion = 1; version = $version
    description = '全テストメソッドとTheoryの引数行を機能へ対応付ける。status=automatedは実行入口があることで、今回実行済みの意味ではない。結果は各実行のsummary.json/TRXを見る。'
    generatedBy = 'dev/scripts/Update-TestCaseManifest.ps1'
    command = 'pwsh -NoProfile -File ./dev/scripts/Update-TestCaseManifest.ps1 -TestAssembly <ビルド済みOperationRecorderApp.Tests.dll>'
    methodCount = @($cases).Count; expandedCaseCount = $discovered.Count
    additionalSuites = @(
        [ordered]@{ id = 'html-browser'; entrypoint = 'dev/scripts/Test-ReportHtml.cjs'; requirements = 'Node.js/Playwright/Edge'; result = '指定した版別logsのsummary.json'; boundary = '専用生成HTMLをヘッドレスで検証。Core/ Desktopの引数行数には含めない' }
    )
    layers = @(
        [ordered]@{ id = 'unit'; description = '判断・状態遷移と注入したOS境界' },
        [ordered]@{ id = 'api'; description = 'GUIと同じサービス・編集セッションと実ファイル' },
        [ordered]@{ id = 'sta'; description = 'STA実フォーム・コントロールイベント、確認とOS副作用は差替え' },
        [ordered]@{ id = 'cli'; description = '実EXE/pwsh/隔離プロセス、結果JSONと終了コード' },
        [ordered]@{ id = 'desktop'; description = '専用別PIDfixtureで実OS入力と撮影、環境不足はblocked' }
    )
    unverifiedBoundaries = @(
        'この台帳は試験の対応表。Desktopの実行可否と実行結果はrunnerのsummaryに記録する。',
        'OSのUAC承認、実利用者のショートカット/ごみ箱、外部アプリの起動は要求/拒否/取消/失敗の差替え検証。',
        '見た目の良さ、全DPI/全ハードウェア、外部ブラウザー/印刷機の実描画、インストーラー配布はこの台帳の合格範囲に含めない。',
        '手順の画像移動失敗は画像を残したままJSON削除を続ける既存契約。画像の物理廃棄成功を示さない。'
    )
    features = $features; cases = @($cases)
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
[IO.File]::WriteAllText($outputPath, ($document | ConvertTo-Json -Depth 12) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "ケース表を更新しました：$outputPath（$(@($cases).Count)メソッド/$($discovered.Count)引数行）"
$missing = @($features | Where-Object status -eq 'missing')
if ($missing.Count -gt 0) { throw "実行入口のない機能：$($missing.id -join ', ')" }
