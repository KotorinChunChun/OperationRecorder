<#
.SYNOPSIS
過去の記録(記録フォルダ)を一覧から選び、名前・解説を確認・編集して、テンプレートに当てはめた md を出力し直す。

起動すると対話形式で聞く。各質問は Enter で既定値、Esc(選択肢では 0 も可)で 1 つ前の質問に戻る。
  1. 記録フォルダを探す場所
  2. 再発行する記録(新しい順の一覧から番号で選ぶ)
  3. 見出しの名前(caption)と解説(description)の確認・編集
  4. 変更した名前・解説を記録(session.json)に保存するか
  5. テンプレート(複数可)・画像の載せ方・md の保存先・出力後に開くか

.PARAMETER OutDir
記録フォルダ(session-日時…)が並んでいるフォルダ。最初の質問の既定値になる。省略時は ドキュメント\OperationRecords

.PARAMETER TemplateDir
テンプレートを置いたフォルダ。省略時はスキルの templates フォルダ

.EXAMPLE
pwsh -File Reissue-OperationReport.ps1
pwsh -File Reissue-OperationReport.ps1 -OutDir D:\手順書
#>
param(
    [string]$OutDir,
    [string]$TemplateDir
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'PromptHelpers.ps1')

if ([Console]::IsInputRedirected) {
    throw "対話入力できない環境です。記録から md を作るだけなら New-OperationReport.ps1 -SessionDir <記録フォルダ> を使ってください"
}
if (-not $OutDir) { $OutDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'OperationRecords' }
if (-not $TemplateDir) { $TemplateDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'templates' }

function Get-Sessions([string]$root) {
    @(Get-ChildItem -LiteralPath $root -Directory -Filter 'session-*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'session.json') } |
        Sort-Object Name -Descending |
        ForEach-Object {
            $json = Get-Content -LiteralPath (Join-Path $_.FullName 'session.json') -Raw -Encoding utf8 | ConvertFrom-Json -DateKind String
            $eventsPath = Join-Path $_.FullName 'events.jsonl'
            $steps = if (Test-Path -LiteralPath $eventsPath) { @(Get-Content -LiteralPath $eventsPath -Encoding utf8 | Where-Object { $_.Trim() }).Count } else { 0 }
            $mds = @(Get-ChildItem -LiteralPath $_.FullName -Filter "$($_.Name)_*.md" -File -ErrorAction SilentlyContinue | ForEach-Object Name)
            [pscustomobject]@{ Dir = $_.FullName; Name = $_.Name; Json = $json; Steps = $steps; Mds = $mds }
        })
}

function Get-TargetText($json) {
    if ($json.targetMonitor) { "ディスプレイ $($json.targetMonitor.index)($($json.targetMonitor.bounds.w)x$($json.targetMonitor.bounds.h))" }
    elseif ($json.targetWindow) { $json.targetWindow.title }
    elseif ($json.targetProcess) { "$($json.targetProcess)(アプリ全体)" }
    else { '操作したウィンドウ' }
}

function Show-SessionDetail($s) {
    $j = $s.Json
    Write-Host ""
    Write-Host "  記録フォルダ: $($s.Dir)" -ForegroundColor DarkCyan
    Write-Host "  記録日時    : $($j.start -replace 'T', ' ') 〜 $($j.end -replace 'T', ' ')"
    Write-Host "  撮影モード  : $($j.mode) / 対象: $(Get-TargetText $j)"
    Write-Host "  手順数      : $($s.Steps)"
    Write-Host "  名前        : $(if ($j.caption) { $j.caption } else { '(なし)' })"
    Write-Host "  解説        : $(if ($j.description) { $j.description } else { '(なし)' })"
    Write-Host "  出力済みの md: $(if ($s.Mds.Count) { $s.Mds -join ', ' } else { '(なし)' })"
}

Show-Banner '操作記録 md 再発行ツール(Reissue-OperationReport)' @'
 操作記録ツール(Start-OperationRecording)で記録した操作(スクリーンショットと操作データ)から、
 操作手順書(md)を作り直すツールです。操作を記録し直す必要はありません。

 こんなときに使います
   ・別のテンプレートで出し直したい(画像を表で並べる版・操作前の画像だけの版・AI 向けなど)
   ・見出しの名前や冒頭の解説を直したい
   ・画像を md に埋め込んだ 1 ファイルの版を作りたい

 流れ
   1. 記録が保存されているフォルダを指定する(ふだんは Enter だけでよい)
   2. 記録の一覧から、再発行する記録を番号で選ぶ
   3. 見出しの名前と解説を確認し、必要なら直す
   4. テンプレート・画像の載せ方・保存先を選ぶと、md が出力される

 質問への答え方
   Enter        画面に表示されている既定値(番号の横に * が付いた選択肢)で進む
   番号 / 名前  選択肢を選ぶ。テンプレートはカンマ区切りで複数選べる(例: 1,3)
   Esc          1 つ前の質問に戻る(選択肢の質問では 0 でも戻れる)
   Ctrl+C       途中でやめる(md は出力しない)
'@

$recordsDir = $OutDir
$sessions = @()
$session = $null
$Caption = $null; $Description = $null; $SaveMeta = $true
$Template = @('user'); $ImageStyle = 'Link'; $ReportDir = ''; $Open = $false

# 質問の並べ方と戻り方は Start-OperationRecording.ps1 と同じ(When / Ask をドットソースで呼ぶ)
$steps = @(
    @{
        When = { $true }
        Ask = {
            while ($true) {
                $a = Read-Text '記録フォルダを探す場所(フォルダのフルパス)' '記録のときに -OutDir で指定したフォルダ(この中に session-日時… のフォルダが並んでいる)' $recordsDir -Required
                if ($null -eq $a) { $back = $true; break }
                if (-not (Test-Path -LiteralPath $a -PathType Container)) { Write-Host "  フォルダがありません: $a" -ForegroundColor Yellow; continue }
                $found = Get-Sessions $a
                if ($found.Count -eq 0) { Write-Host "  このフォルダには記録がありません: $a" -ForegroundColor Yellow; continue }
                $recordsDir = (Resolve-Path -LiteralPath $a).Path
                $sessions = $found
                break
            }
        }
    }
    @{
        When = { $true }
        Ask = {
            $items = @(for ($k = 0; $k -lt $sessions.Count; $k++) {
                $s = $sessions[$k]
                $cap = if ($s.Json.caption) { " 「$($s.Json.caption)」" } else { '' }
                @{ Key = [string]($k + 1); Desc = ("{0}{1} / 手順 {2} / md {3} 件" -f $s.Name, $cap, $s.Steps, $s.Mds.Count) }
            })
            $def = if ($session) { [string]([array]::IndexOf(@($sessions.Dir), $session.Dir) + 1) } else { '1' }
            $a = Read-Choice "再発行する記録(新しい順。$($sessions.Count) 件)" $items $def
            if ($null -eq $a) { $back = $true }
            else {
                $picked = $sessions[[int]$a - 1]
                if (-not $session -or $session.Dir -ne $picked.Dir) {
                    # 別の記録を選んだら、名前・解説の既定値をその記録の値に入れ替える
                    $session = $picked
                    $Caption = [string]$picked.Json.caption
                    $Description = [string]$picked.Json.description
                }
                Show-SessionDetail $session
            }
        }
    }
    @{
        When = { $true }
        Ask = {
            $a = Read-Text '見出しの名前(caption)' ("見出しは「名前操作手順書」、冒頭は「本書では名前の操作手順について説明する」になります`n" +
                "記録フォルダ名は変わりません" + $(if ($Caption) { "`n「-」を入力すると名前を消します" } else { '' })) $Caption
            if ($null -eq $a) { $back = $true } else { $Caption = if ($a -eq '-') { '' } else { $a } }
        }
    }
    @{
        When = { $true }
        Ask = {
            $a = Read-Text '冒頭に載せる解説(description)' ("「本書では〇〇の操作手順について説明する」の後に続く文章です" +
                $(if ($Description) { "`n「-」を入力すると解説を消します" } else { '' })) $Description
            if ($null -eq $a) { $back = $true } else { $Description = if ($a -eq '-') { '' } else { $a } }
        }
    }
    @{
        When = { $Caption -ne [string]$session.Json.caption -or $Description -ne [string]$session.Json.description }
        Ask = {
            $a = Read-Choice '変更した名前・解説を記録に保存するか' @(
                @{ Key = 'Yes'; Desc = '記録(session.json)に保存する。次に再発行するときも、この名前・解説が既定値になる' }
                @{ Key = 'No'; Desc = '今回出力する md にだけ使う(記録は変えない)' }
            ) $(if ($SaveMeta) { 'Yes' } else { 'No' })
            if ($null -eq $a) { $back = $true } else { $SaveMeta = $a -eq 'Yes' }
        }
    }
    @{
        When = { $true }
        Ask = {
            $a = Read-MultiChoice '報告書(md)にするテンプレート' @(Get-TemplateItems $TemplateDir) @($Template)
            if ($null -eq $a) { $back = $true } else { $Template = @($a) }
        }
    }

    @{
        When = { $true }
        Ask = {
            $a = Read-Choice '画像の載せ方' @(
                @{ Key = 'Link'; Desc = 'img フォルダの PNG を参照する(md が軽い。フォルダごと渡す必要がある)' }
                @{ Key = 'Embed'; Desc = 'PNG を base64 で md に埋め込む(md 1 ファイルで完結。ファイルは大きくなる)' }
            ) $ImageStyle
            if ($null -eq $a) { $back = $true } else { $ImageStyle = $a }
        }
    }
    @{
        When = { $true }
        Ask = {
            $names = @($Template | ForEach-Object { "$($session.Name)_$_.md" })
            $exists = @($names | Where-Object { $session.Mds -contains $_ })
            $hint = "ファイル名ではなくフォルダを指定します。md のファイル名は「$($names -join '」「')」になります`n" +
                "空のまま Enter なら、記録フォルダの中に保存します" +
                $(if ($exists.Count) { "(記録フォルダ内の $($exists -join ', ') は上書きされます)" } else { '' }) +
                $(if ($ReportDir) { "`n「-」を入力すると指定を消して、記録フォルダの中に保存します" } else { '' })
            $a = Read-Text 'md を保存するフォルダ(フォルダのフルパス。省略可)' $hint $ReportDir
            if ($null -eq $a) { $back = $true } else { $ReportDir = if ($a -eq '-') { '' } else { $a } }
        }
    }
    @{
        When = { $true }
        Ask = {
            $a = Read-Choice '出力後に md を開くか' @(
                @{ Key = 'No'; Desc = '開かない' }
                @{ Key = 'Yes'; Desc = '関連付けられたプログラム(.md を開くように設定されたアプリ)で開く' }
            ) $(if ($Open) { 'Yes' } else { 'No' })
            if ($null -eq $a) { $back = $true } else { $Open = $a -eq 'Yes' }
        }
    }
)

$first = 0
while ($true) {
    $i = $first
    while ($i -lt $steps.Count) {
        $step = $steps[$i]
        if (-not (. $step.When)) { $i++; continue }
        $back = $false
        . $step.Ask
        if ($back) {
            $j = $i - 1
            while ($j -ge 0 -and -not (. $steps[$j].When)) { $j-- }
            if ($j -lt 0) { Write-Host "  最初の質問です。これより前には戻れません" -ForegroundColor Yellow } else { $i = $j }
            continue
        }
        $i++
    }

    if ($SaveMeta -and ($Caption -ne [string]$session.Json.caption -or $Description -ne [string]$session.Json.description)) {
        $jsonPath = Join-Path $session.Dir 'session.json'
        $j = Get-Content -LiteralPath $jsonPath -Raw -Encoding utf8 | ConvertFrom-Json -DateKind String
        $j | Add-Member -NotePropertyName caption -NotePropertyValue $Caption -Force
        $j | Add-Member -NotePropertyName description -NotePropertyValue $Description -Force
        [System.IO.File]::WriteAllText($jsonPath, ($j | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
        $session.Json = $j
        Write-Host "名前・解説を記録に保存しました: $jsonPath"
    }

    $params = @{ SessionDir = $session.Dir; Template = $Template; ImageStyle = $ImageStyle; TemplateDir = $TemplateDir; Caption = $Caption; Description = $Description }
    if ($ReportDir) { $params.ReportDir = $ReportDir }
    $mds = @(& (Join-Path $PSScriptRoot 'New-OperationReport.ps1') @params)
    Write-Host ""
    foreach ($md in $mds) { Write-Host "報告書: $md" -ForegroundColor Green }
    if ($Open) { foreach ($md in $mds) { Invoke-Item -LiteralPath $md } }
    $mds | Write-Output

    # 出力した md を一覧の「出力済み」に反映しておく
    $session.Mds = @(Get-ChildItem -LiteralPath $session.Dir -Filter "$($session.Name)_*.md" -File -ErrorAction SilentlyContinue | ForEach-Object Name)

    $again = Read-Choice '続けて再発行するか' @(
        @{ Key = 'No'; Desc = '終了する' }
        @{ Key = 'Yes'; Desc = '別の記録(または同じ記録を別のテンプレートで)再発行する' }
    ) 'No'
    if ($again -ne 'Yes') { break }
    $first = 1   # 記録フォルダを探す場所はそのままで、記録の選択から
}
