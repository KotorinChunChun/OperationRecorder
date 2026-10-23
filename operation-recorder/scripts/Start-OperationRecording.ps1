<#
.SYNOPSIS
ユーザーの PC 操作(クリック・操作キー・テキスト入力)を検知し、操作の直前と直後のスクリーンショットと
操作対象の要素情報を記録して、操作報告書(md)を出力する。

パラメータを 1 つも指定せずに起動すると、対話形式で設定を聞く。

記録の操作キー(どのアプリを操作中でも効く)
  Ctrl+Shift      (Ctrl と Shift だけを押して離す)今の画面をキャプチャする
  Ctrl+Shift+F9   開始・再開
  Ctrl+Shift+F10  直前 1 手を取り消す
  Ctrl+Shift+F11  同じ設定で最初から取り直す(常駐時は今の記録を捨てて、F9 を待つ)
  Ctrl+Shift+F12  停止・終了(常駐時は今の記録を保存して、F9 を待つ。待機中に押すと常駐を終了)

.PARAMETER Elevate
管理者権限に昇格して実行する。既に管理者権限なら何もしない。昇格すると新しいウィンドウで動き、終わると
戻り値をこのプロセスに返す。「管理者として実行」したアプリの操作を記録するときに使う
(UAC の確認画面そのものは、管理者権限でも記録できない)

.PARAMETER Caption
記録の名前。記録フォルダ名を session-yyyyMMdd-HHmmss_<名前> にし、報告書の見出しを「<名前>操作手順書」にする
(フォルダ名に使えない文字は _ に置き換える)。常駐時は、すべての記録に同じ名前が付く

.PARAMETER Description
報告書の冒頭(「本書では<名前>の操作手順について説明する」の後)に載せる解説

.PARAMETER Mode
Window(指定ウィンドウ) / App(指定アプリの全ウィンドウ) / Active(操作したウィンドウ) /
Desktop(1 つのディスプレイ全体。-Monitor で番号、-Pick ならクリックしたウィンドウが映っているディスプレイ) /
AllDesktops(PC のすべてのディスプレイを合わせた全体。対象を決める必要はない。画像が大きいので撮影の間隔は自動で広げる)

.PARAMETER Foreground
開始のキー(Ctrl+Shift+F9)を押したときに前面にあるウィンドウを対象にする(App はそのアプリ、Desktop はそのウィンドウが
映っているディスプレイ)。キーを押した時点で決めるので -StartNow とは一緒に使えない(指定すると -StartNow を外す)。
前面に記録できるウィンドウが無ければ、最初にクリックしたものを対象にする

.PARAMETER Monitor
Desktop モードで撮るディスプレイの番号。-ListMonitors で一覧を表示できる

.PARAMETER MarkerRadius
クリック位置に描く丸の半径(px)。左クリック=赤、右クリック=青、中クリック=緑。0 なら描かない。既定 14

.PARAMETER StartNow
起動したらすぐ記録を始める。省略時は Ctrl+Shift+F9 を押すまで待つ

.PARAMETER Resident
常駐する。Ctrl+Shift+F12 で記録を保存して報告書を出したあと、Ctrl+Shift+F9 で次の記録を始められる。

.PARAMETER Template
md にするときに当てはめるテンプレート(複数可)。その数だけ md を出力する。既定は user。
名前(templates フォルダの <名前>.md)か md のパス。none を指定すると md を出さない。
記録は常にユーザー向け・AI 向けの両方に必要な情報をすべて保存しているので、後から
New-OperationReport.ps1 で別のテンプレートに当てはめ直すこともできる。
  user        ユーザー向けの手順書（操作前後画像）
  psr         ステップ記録ツール形式（簡易UI操作履歴。日時・座標・プログラム・UI 要素付き）
  ai          AI向けの手順書（操作前後画像と詳細UI情報。ウィンドウ名・コントロールID・相対座標など）
  text        テキスト手順書（操作情報のみ。画像なし）
画像の並べ方(左右・縦・片方だけ・同じ画面なら 1 枚)は、テンプレートの中で {{images.side}} などを選んで決める(user.md の冒頭コメント参照)

.PARAMETER ImageStyle
Link(img フォルダの PNG を相対パスで参照) / Embed(PNG を base64 で md に埋め込む)

.PARAMETER Return
Markdown(出力した md のフルパスを返す。テンプレートが複数ならその数だけ。既定) / Folder(記録フォルダのパスを返す)

.PARAMETER Open
記録を保存したあと、出力した md を関連付けられたプログラムで開く

.PARAMETER OutDir
記録フォルダ(画像・events.jsonl)を作る親フォルダのフルパス。省略時は ドキュメント\OperationRecords

.PARAMETER ReportDir
md を保存するフォルダのフルパス(ファイル名ではない)。md のファイル名は <記録フォルダ名>_<テンプレート名>.md。
省略時は記録フォルダの中

.EXAMPLE
pwsh -File Start-OperationRecording.ps1
pwsh -File Start-OperationRecording.ps1 -Mode App -WindowTitle "販売管理" -Template user,ai -ImageStyle Embed -Open
pwsh -File Start-OperationRecording.ps1 -Mode Window -Pick -Resident -OutDir D:\手順書
pwsh -File Start-OperationRecording.ps1 -Mode App -Foreground -Resident
pwsh -File Start-OperationRecording.ps1 -Mode AllDesktops -StartNow
pwsh -File Start-OperationRecording.ps1 -ListMonitors
pwsh -File Start-OperationRecording.ps1 -Mode Desktop -Monitor 2 -MarkerRadius 0 -StartNow
#>
param(
    # 記録の名前。記録フォルダ名(session-日時_名前)と、報告書の見出し「{名前}操作手順書」に使う
    [string]$Caption,
    # 報告書の冒頭に載せる解説
    [string]$Description,
    [ValidateSet('Window', 'App', 'Active', 'Desktop', 'AllDesktops')][string]$Mode,
    [string]$WindowTitle,
    [string]$ProcessName,
    # Desktop モードで撮るディスプレイの番号(-ListMonitors で確認できる)
    [int]$Monitor,
    [switch]$Pick,
    # 開始のキーを押したときに前面にあるウィンドウ(Desktop はそれが映っているディスプレイ)を対象にする
    [switch]$Foreground,
    # クリック位置に描く丸の半径(px)。左=赤 / 右=青 / 中=緑。0 なら描かない
    [ValidateRange(0, 200)][int]$MarkerRadius = 14,
    # ディスプレイの一覧を表示して終了する
    [switch]$ListMonitors,
    [switch]$StartNow,
    [switch]$Resident,
    [string[]]$Template = @('user'),
    [ValidateSet('Link', 'Embed')][string]$ImageStyle = 'Link',

    [ValidateSet('Markdown', 'Folder')][string]$Return = 'Markdown',
    [switch]$Open,
    [string]$OutDir,
    [string]$ReportDir,
    # テンプレートを置いたフォルダ。省略時はスキルの templates フォルダ
    [string]$TemplateDir,
    [int]$BeforeMs = 200,
    [int]$AfterMs = 500,
    [int]$IntervalMs = 100,
    [int]$MaxMinutes = 60,
    [switch]$NoInputValues,
    # 管理者権限に昇格して実行する(既に管理者なら何もしない)。UAC の確認が出る
    [switch]$Elevate,
    # 昇格して起動し直した子プロセスが、戻り値を親プロセスに渡すためのファイル(内部用)
    [string]$ResultFile,
    [switch]$Interactive
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 昇格した子プロセスのウィンドウはエラーで閉じると内容が読めないので、親に伝えてから止まる
trap {
    if ($ResultFile) {
        Add-Content -LiteralPath $ResultFile -Value ("#ERROR " + $_.Exception.Message) -Encoding utf8
        if ($Interactive) { Read-Host "エラーで終了しました。Enter でこのウィンドウを閉じます" | Out-Null }
    }
    break
}

$defaultOutDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'OperationRecords'
$boundAtStart = $PSBoundParameters

# ---------------- 対話入力 ----------------

. (Join-Path $PSScriptRoot 'PromptHelpers.ps1')

$needPrompt = $Interactive -or @($boundAtStart.Keys | Where-Object { $_ -notin 'Elevate', 'ResultFile' }).Count -eq 0
if ($needPrompt -and [Console]::IsInputRedirected) {
    throw "対話入力できない環境です。-Mode などのパラメータを指定して起動してください(Get-Help $PSCommandPath -Full)"
}

# ---------------- 管理者権限 ----------------

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 管理者権限で自分自身を起動し直し、終わるまで待って戻り値を受け取る。
# 昇格したプロセスは別のウィンドウになり標準出力を受け取れないので、-ResultFile 経由で戻り値を渡す
function Invoke-Elevated {
    $q = { param($s) '"' + ([string]$s -replace '"', '\"') + '"' }
    $argv = [System.Collections.Generic.List[string]]::new()
    $argv.Add('-NoProfile'); $argv.Add('-File'); $argv.Add((& $q $PSCommandPath))
    foreach ($k in $boundAtStart.Keys) {
        if ($k -in 'Elevate', 'ResultFile', 'Interactive') { continue }
        $v = $boundAtStart[$k]
        if ($v -is [System.Management.Automation.SwitchParameter]) { if ($v.IsPresent) { $argv.Add("-$k") }; continue }
        # 昇格したプロセスはカレントフォルダが System32 になるので、相対パスはここで絶対パスにしておく
        if ($k -in 'OutDir', 'ReportDir', 'TemplateDir') { $v = [System.IO.Path]::GetFullPath($v) }
        if ($k -eq 'Template') { $v = @($v | ForEach-Object { $_ -split ',' } | ForEach-Object { if ($_ -match '[\\/]') { [System.IO.Path]::GetFullPath($_) } else { $_ } }) -join ',' }
        $argv.Add("-$k"); $argv.Add((& $q $v))
    }
    if ($needPrompt) { $argv.Add('-Interactive') }
    $result = Join-Path ([System.IO.Path]::GetTempPath()) ("oprec-result-{0}.txt" -f [guid]::NewGuid())
    $argv.Add('-Elevate'); $argv.Add('-ResultFile'); $argv.Add((& $q $result))

    Write-Host "管理者権限に昇格して、新しいウィンドウで起動し直します(UAC の確認が出ます)…" -ForegroundColor Cyan
    try {
        $p = Start-Process pwsh -Verb RunAs -ArgumentList ($argv -join ' ') -Wait -PassThru
    } catch {
        throw "管理者権限への昇格が取り消されたか、失敗しました: $($_.Exception.Message)"
    }
    if (Test-Path -LiteralPath $result) {
        $lines = @(Get-Content -LiteralPath $result -Encoding utf8)
        Remove-Item -LiteralPath $result -Force
        $err = $lines | Where-Object { $_ -like '#ERROR *' } | Select-Object -First 1
        if ($err) { throw ("管理者権限で実行した記録がエラーで終了しました: " + $err.Substring(7)) }
        $lines | Write-Output
    } elseif ($p.ExitCode -ne 0) {
        throw "管理者権限で実行した記録が異常終了しました(終了コード $($p.ExitCode))"
    }
}

function Ensure-Engine {
    # 記録エンジンは必要になった時点で読み込む(初回のコンパイルに数秒かかる)
    if (-not ('OpRec.Recorder' -as [type])) {
        Write-Host "準備しています…"
        . (Join-Path $PSScriptRoot 'Import-OpRecorder.ps1')
    }
}

if ($needPrompt) {
    Show-Banner '操作記録ツール(Start-OperationRecording)' @'
 あなたが PC で行う操作(クリック・キー操作・文字の入力)を自動で記録し、操作前・操作後の
 スクリーンショット付きの操作手順書(md)を作るツールです。

 使い方
   1. これから出る質問に答えて、記録する対象(アプリ・ウィンドウ・ディスプレイ)などを決める
   2. 記録したいアプリに切り替えて Ctrl+Shift+F9 を押すと、記録が始まる
   3. ふだんどおりに操作する(クリックや Enter などのたびに、自動で画面が撮られる)
   4. Ctrl+Shift+F12 を押すと記録を保存し、操作手順書(md)が出力される

 記録中に使うキー(どのアプリを操作中でも効く)
   Ctrl+Shift       (この 2 つだけを押して離す)今の画面をキャプチャする
   Ctrl+Shift+F10   直前の 1 手を取り消す
   Ctrl+Shift+F11   最初から記録し直す
   Ctrl+Shift+F12   記録を保存して終了する

 質問への答え方
   Enter        画面に表示されている既定値(番号の横に * が付いた選択肢)で進む
   番号 / 名前  選択肢を選ぶ
   Esc          1 つ前の質問に戻る(選択肢の質問では 0 でも戻れる)
   Ctrl+C       途中でやめる

 注意: 記録する画面と入力した文字(パスワード欄を除く)が保存されます。
       記録に写ってほしくない画面は、あらかじめ閉じておいてください。
'@
    if (Test-Admin) {
        Write-Host ""
        Write-Host "■ 管理者権限: 既に管理者権限で実行しています" -ForegroundColor Cyan
    }

    # 質問を順番に並べ、Esc / 0 が返ったら「その時点で聞く対象になっている 1 つ前の質問」に戻る。
    # When は、その質問を聞くかどうか(前の答えで変わる)。Ask は答えを変数に入れ、戻るときは $back = $true にする。
    # どちらもスクリプトのスコープで変数を読み書きするので、呼び出しはドットソース(. $sb)で行う。
    # 戻ったときは前回の答えを既定値として表示する
    $steps = @(
        @{
            When = { -not (Test-Admin) -and -not $ResultFile }
            Ask = {
                $a = Read-Choice '管理者権限' @(
                    @{ Key = 'No'; Desc = 'このまま実行する(「管理者として実行」したアプリは、操作や要素情報を記録できないことがある)' }
                    @{ Key = 'Yes'; Desc = '管理者権限に昇格して実行し直す(UAC の確認が出る。設定の続きは新しいウィンドウで聞く)' }
                ) $(if ($Elevate) { 'Yes' } else { 'No' })
                if ($null -eq $a) { $back = $true } else { $Elevate = $a -eq 'Yes' }
            }
        }
        @{
            When = { $true }
            Ask = {
                $a = Read-Text '記録の名前(省略可)' ("記録フォルダ名の末尾(session-日時_名前)と、報告書の見出し「名前操作手順書」に使います`n例: 受注登録" +
                    $(if ($Caption) { "`n「-」を入力すると名前を消します" } else { '' })) $Caption
                if ($null -eq $a) { $back = $true } else { $Caption = if ($a -eq '-') { '' } else { $a } }
            }
        }
        @{
            When = { $true }
            Ask = {
                $a = Read-Text '報告書の冒頭に載せる解説(省略可)' ("「本書では〇〇の操作手順について説明する」の後に続く文章です`n例: 月末に受注データを販売管理システムへ登録する手順。" +
                    $(if ($Description) { "`n「-」を入力すると解説を消します" } else { '' })) $Description
                if ($null -eq $a) { $back = $true } else { $Description = if ($a -eq '-') { '' } else { $a } }
            }
        }
        @{
            When = { $true }
            Ask = {
                $a = Read-Choice '撮影モード' @(
                    @{ Key = 'Window'; Desc = '指定した 1 つのウィンドウだけを撮る。ダイアログ上の操作は記録しない' }
                    @{ Key = 'App'; Desc = '指定したアプリのウィンドウ全部(ダイアログ・メニューも含む)を撮る' }
                    @{ Key = 'Active'; Desc = '操作したウィンドウをその都度撮る。関係ない画面や入力値も残るので注意' }
                    @{ Key = 'Desktop'; Desc = '1 つのディスプレイを丸ごと撮る。映っている他のアプリも塗りつぶさずに写るので注意' }
                    @{ Key = 'AllDesktops'; Desc = 'すべてのディスプレイを合わせた全体を撮る。映っている他のアプリも写るので注意' }
                ) $(if ($Mode) { $Mode } else { 'App' })
                if ($null -eq $a) { $back = $true } else { $Mode = $a }
            }
        }
        @{
            When = { $Mode -notin 'Active', 'AllDesktops' }
            Ask = {
                if ($Mode -eq 'Desktop') {
                    Ensure-Engine
                    $monitors = @([OpRec.Monitors]::GetAll())
                    $items = @($monitors | ForEach-Object { @{ Key = [string]$_.Index; Desc = $_.Describe() } })
                    $items += @{ Key = 'Pick'; Desc = '記録開始後、最初にクリックしたウィンドウが映っているディスプレイにする' }
                    $items += @{ Key = 'Foreground'; Desc = '開始のキー(Ctrl+Shift+F9)を押したときに前面にあるウィンドウが映っているディスプレイにする' }
                    $primary = ($monitors | Where-Object IsPrimary | Select-Object -First 1)
                    $def = if ($Foreground) { 'Foreground' } elseif ($Pick) { 'Pick' } elseif ($Monitor) { [string]$Monitor } elseif ($primary) { [string]$primary.Index } else { '1' }
                    $a = Read-Choice '撮るディスプレイ(番号で選ぶ)' $items $def
                    if ($null -eq $a) { $back = $true }
                    else {
                        $WindowTitle = $null; $ProcessName = $null
                        $Foreground = [switch]($a -eq 'Foreground')
                        if ($a -eq 'Pick') { $Pick = $true; $Monitor = 0 }
                        elseif ($a -eq 'Foreground') { $Pick = $false; $Monitor = 0 }
                        else { $Pick = $false; $Monitor = [int]$a }
                    }
                } else {
                    while ($true) {
                        $def = if ($Foreground) { 'Foreground' } elseif ($WindowTitle) { 'Title' } elseif ($ProcessName) { 'Process' } else { 'Pick' }
                        $how = Read-Choice '対象の決め方' @(
                            @{ Key = 'Pick'; Desc = '記録開始後、最初にクリックしたウィンドウを対象にする' }
                            @{ Key = 'Foreground'; Desc = '開始のキー(Ctrl+Shift+F9)を押したときに前面にあるウィンドウを対象にする' }
                            @{ Key = 'Title'; Desc = 'ウィンドウタイトルの一部を入力して決める' }
                            @{ Key = 'Process'; Desc = 'プロセス名(例: EXCEL, notepad)を入力して決める。App なら同名プロセス全部' }
                        ) $def
                        if ($null -eq $how) { $back = $true; break }
                        $Foreground = [switch]($how -eq 'Foreground')
                        if ($how -eq 'Pick') { $Pick = $true; $WindowTitle = $null; $ProcessName = $null; $Monitor = 0; break }
                        if ($how -eq 'Foreground') { $Pick = $false; $WindowTitle = $null; $ProcessName = $null; $Monitor = 0; break }
                        if ($how -eq 'Title') {
                            $t = Read-Text 'ウィンドウタイトルの一部' '例: 販売管理 / メモ帳' $WindowTitle -Required
                            if ($null -eq $t) { continue }   # Esc なら「対象の決め方」に戻る
                            $WindowTitle = $t; $ProcessName = $null
                        } else {
                            $t = Read-Text 'プロセス名' '例: EXCEL / notepad(.exe は付けなくてよい)' $ProcessName -Required
                            if ($null -eq $t) { continue }
                            $ProcessName = $t; $WindowTitle = $null
                        }
                        $Pick = $false; $Monitor = 0
                        break
                    }
                }
            }
        }
        @{
            When = { $true }
            Ask = {
                while ($true) {
                    $r = Read-Text 'クリック位置の丸の半径(px)' '左クリック=赤丸 / 右クリック=青丸 / 中クリック=緑丸。0 なら丸を描かない(0〜200)' ([string]$MarkerRadius)
                    if ($null -eq $r) { $back = $true; break }
                    $n2 = 0
                    if ([int]::TryParse($r, [ref]$n2) -and $n2 -ge 0 -and $n2 -le 200) { $MarkerRadius = $n2; break }
                    Write-Host "  0〜200 の数値を入力してください" -ForegroundColor Yellow
                }
            }
        }
        @{
            # 前面のウィンドウを対象にするときは、開始のキーを押した時点で決めるので必ずキーを待つ
            When = { -not $Foreground -or $Mode -in 'Active', 'AllDesktops' }
            Ask = {
                $a = Read-Choice '記録を始めるタイミング' @(
                    @{ Key = 'F9'; Desc = 'Ctrl+Shift+F9 を押したら始める(対象のアプリに切り替えてから始められる)' }
                    @{ Key = 'Now'; Desc = '設定が終わったらすぐ始める' }
                ) $(if ($StartNow) { 'Now' } else { 'F9' })
                if ($null -eq $a) { $back = $true } else { $StartNow = $a -eq 'Now' }
            }
        }
        @{
            When = { $true }
            Ask = {
                $a = Read-Choice '常駐' @(
                    @{ Key = 'No'; Desc = '1 件記録したら終了する' }
                    @{ Key = 'Yes'; Desc = 'Ctrl+Shift+F12 で保存したあと、Ctrl+Shift+F9 で次の記録を始められる。待機中の Ctrl+Shift+F12 で常駐終了' }
                ) $(if ($Resident) { 'Yes' } else { 'No' })
                if ($null -eq $a) { $back = $true } else { $Resident = $a -eq 'Yes' }
            }
        }
        @{
            When = { $true }
            Ask = {
                # テンプレートは templates フォルダにある md を凡例に出して選ばせる(複数可)。説明は各ファイル冒頭コメントの 3 行目
                $tplDir = if ($TemplateDir) { $TemplateDir } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'templates' }
                $tplItems = @(Get-TemplateItems $tplDir)
                $tplItems += @{ Key = 'none'; Desc = 'md は出さない(画像と events.jsonl だけ。後から New-OperationReport.ps1 で作れる)' }
                $a = Read-MultiChoice '報告書(md)にするテンプレート' $tplItems @($Template)
                if ($null -eq $a) { $back = $true } else { $Template = @($a) }
            }
        }

        @{
            When = { $Template -notcontains 'none' }
            Ask = {
                $a = Read-Choice '画像の載せ方' @(
                    @{ Key = 'Link'; Desc = 'img フォルダの PNG を参照する(md が軽い。フォルダごと渡す必要がある)' }
                    @{ Key = 'Embed'; Desc = 'PNG を base64 で md に埋め込む(md 1 ファイルで完結。ファイルは大きくなる)' }
                ) $ImageStyle
                if ($null -eq $a) { $back = $true } else { $ImageStyle = $a }
            }
        }
        @{
            When = { $Template -notcontains 'none' }
            Ask = {
                $a = Read-Choice '保存後に md を開くか' @(
                    @{ Key = 'No'; Desc = '開かない' }
                    @{ Key = 'Yes'; Desc = '関連付けられたプログラム(.md を開くように設定されたアプリ)で開く' }
                ) $(if ($Open) { 'Yes' } else { 'No' })
                if ($null -eq $a) { $back = $true } else { $Open = $a -eq 'Yes' }
            }
        }
        @{
            When = { $true }
            Ask = {
                $a = Read-Choice '終了時に返す値' @(
                    @{ Key = 'Markdown'; Desc = '出力した md のフルパス' }
                    @{ Key = 'Folder'; Desc = '記録フォルダのフルパス' }
                ) $Return
                if ($null -eq $a) { $back = $true } else { $Return = $a }
            }
        }
        @{
            When = { $true }
            Ask = {
                $a = Read-Text '記録フォルダを作る場所(フォルダのフルパス)' "この中に session-日時 というフォルダを作り、画像・記録データを保存します`n例: D:\手順書" $(if ($OutDir) { $OutDir } else { $defaultOutDir })
                if ($null -eq $a) { $back = $true } else { $OutDir = $a }
            }
        }
        @{
            When = { $true }
            Ask = {
                $a = Read-Text 'md を保存するフォルダ(フォルダのフルパス。省略可)' ("ファイル名ではなくフォルダを指定します。md のファイル名は「session-日時_テンプレート名.md」になります`n例: D:\手順書\md`n" +
                    "空のまま Enter なら、記録フォルダ(session-日時)の中に保存します" + $(if ($ReportDir) { "`n「-」を入力すると指定を消して、記録フォルダの中に保存します" } else { '' })) $ReportDir
                if ($null -eq $a) { $back = $true } else { $ReportDir = if ($a -eq '-') { '' } else { $a } }
            }
        }
    )

    $i = 0
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
        # 昇格すると決めたら、残りの質問は昇格した新しいウィンドウで聞く
        if ($Elevate -and -not (Test-Admin)) { break }
        $i++
    }
}

if ($Elevate -and -not (Test-Admin)) {
    Invoke-Elevated
    return
}

Ensure-Engine

function Show-Monitors {
    foreach ($m in [OpRec.Monitors]::GetAll()) { Write-Host ("  {0}) {1}" -f $m.Index, $m.Describe()) }
}

if ($ListMonitors) {
    Write-Host "ディスプレイ一覧(-Monitor に番号を指定する):"
    Show-Monitors
    return
}

if (-not $Mode) { $Mode = if ($Monitor) { 'Desktop' } else { 'App' } }
if ($Mode -in 'Active', 'AllDesktops') { $Pick = $false; $Foreground = $false }
if ($Foreground) { $Pick = $false }
if ($Mode -in 'Window', 'App' -and -not $WindowTitle -and -not $ProcessName -and -not $Foreground) { $Pick = $true }
if ($Mode -eq 'Desktop' -and -not $Monitor -and -not $Foreground) { $Pick = $true }
if ($Foreground -and $StartNow) {
    Write-Host "-Foreground は開始のキーを押した時点の前面のウィンドウを対象にするので、-StartNow は使いません(Ctrl+Shift+F9 を待ちます)" -ForegroundColor Yellow
    $StartNow = $false
}
if ($Mode -eq 'Desktop' -and $Monitor -and -not [OpRec.Monitors]::Get($Monitor)) {
    Write-Host "ディスプレイ一覧:"; Show-Monitors
    throw "ディスプレイ $Monitor はありません"
}
if (-not $OutDir) { $OutDir = $defaultOutDir }
# pwsh -File で "user,ai" と 1 つの文字列で渡されたときにも分ける。none は md を出さない
$Template = @($Template | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Template -contains 'none') { $Template = @() }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# 実際に使う設定を、同じ記録をもう一度取れるコマンドラインにしておく(対話入力の後に表示し、AI 向け報告書にも載せる)
function Get-CommandLine {
    $q = { param($s) if ($s -match '[\s"]') { '"' + ($s -replace '"', '\"') + '"' } else { $s } }
    $c = "pwsh -File $(& $q $PSCommandPath)"
    if ($Caption) { $c += " -Caption $(& $q $Caption)" }
    if ($Description) { $c += " -Description $(& $q $Description)" }
    $c += " -Mode $Mode"
    if ($Elevate) { $c += ' -Elevate' }
    if ($Pick) { $c += ' -Pick' }
    elseif ($Foreground) { $c += ' -Foreground' }
    elseif ($Mode -in 'Active', 'AllDesktops') { }
    elseif ($Mode -eq 'Desktop') { $c += " -Monitor $Monitor" }
    else {
        if ($WindowTitle) { $c += " -WindowTitle $(& $q $WindowTitle)" }
        if ($ProcessName) { $c += " -ProcessName $(& $q $ProcessName)" }
    }
    $c += " -MarkerRadius $MarkerRadius"
    if ($StartNow) { $c += ' -StartNow' }
    if ($Resident) { $c += ' -Resident' }
    $c += " -Template $(if ($Template.Count) { $Template -join ',' } else { 'none' })"
    if ($Template.Count) {
        $c += " -ImageStyle $ImageStyle"
    }
    $c += " -Return $Return"
    if ($Open) { $c += ' -Open' }
    $c += " -OutDir $(& $q $OutDir)"
    if ($ReportDir) { $c += " -ReportDir $(& $q $ReportDir)" }
    if ($TemplateDir) { $c += " -TemplateDir $(& $q $TemplateDir)" }
    if ($BeforeMs -ne 200) { $c += " -BeforeMs $BeforeMs" }
    if ($AfterMs -ne 500) { $c += " -AfterMs $AfterMs" }
    if ($IntervalMs -ne 100) { $c += " -IntervalMs $IntervalMs" }
    if ($MaxMinutes -ne 60) { $c += " -MaxMinutes $MaxMinutes" }
    if ($NoInputValues) { $c += ' -NoInputValues' }
    return $c
}
$commandLine = Get-CommandLine
if ($needPrompt) {
    Write-Host ""
    Write-Host "同じ設定で起動するコマンド:" -ForegroundColor Cyan
    Write-Host "  $commandLine"
}

$hotkeyHelp = "Ctrl+Shift(だけを押して離す) = 今の画面をキャプチャ / Ctrl+Shift+F10 = 直前 1 手を取消 / Ctrl+Shift+F11 = 最初から取り直す / Ctrl+Shift+F12 = " +
    $(if ($Resident) { '保存して待機' } else { '保存して終了' })

# ---------------- 1 件分の記録 ----------------

# $Reuse: 取り直し(F11)のときに、前回決まった対象(クリックで選んだウィンドウなど)をそのまま使うための情報
function Invoke-OneRecording($Reuse) {
    $rec = [OpRec.Recorder]::new()
    $rec.Mode = [OpRec.CaptureMode]$Mode
    $rec.BeforeMs = $BeforeMs
    $rec.AfterMs = $AfterMs
    $rec.IntervalMs = $IntervalMs
    $rec.RetainMs = [Math]::Max(1500, $BeforeMs + 1000)
    $rec.RecordInputValues = -not $NoInputValues
    $rec.MarkerRadius = $MarkerRadius
    $rec.CommandLine = $commandLine
    $rec.Caption = $Caption
    $rec.Description = $Description

    $reused = $false
    if ($Reuse) {
        if ($Mode -eq 'Desktop' -and $Reuse.MonitorIndex) {
            $rec.TargetMonitor = [OpRec.Monitors]::Get($Reuse.MonitorIndex); $reused = [bool]$rec.TargetMonitor
        } elseif ($Reuse.Hwnd -and [OpRec.WinUtil]::IsAlive($Reuse.Hwnd)) {
            $rec.TargetHwnd = $Reuse.Hwnd; $rec.TargetProcessName = $Reuse.ProcessName; $reused = $true
        } elseif ($Mode -eq 'App' -and $Reuse.ProcessName) {
            $rec.TargetProcessName = $Reuse.ProcessName; $reused = $true
        }
    }

    if (-not $reused) {
        if ($Mode -in 'Active', 'AllDesktops') { }
        elseif ($Foreground) {
            # 開始のキーを押したときに前面にあるウィンドウ(Desktop はそれが映っているディスプレイ)
            if (-not $rec.UseForegroundTarget()) {
                $rec.PickTarget = $true
                Write-Host "前面に記録できるウィンドウが無かったので、最初にクリックしたものを対象にします" -ForegroundColor Yellow
            }
        }
        elseif ($Mode -eq 'Desktop') {
            if ($Pick) { $rec.PickTarget = $true }
            else {
                # ディスプレイ構成は常駐中に変わりうるので、記録を始めるたびに取り直す
                $rec.TargetMonitor = [OpRec.Monitors]::Get($Monitor)
                if (-not $rec.TargetMonitor) { throw "ディスプレイ $Monitor はありません" }
            }
        } else {
            $hwnd = [IntPtr]::Zero
            if ($WindowTitle -and -not $Pick) {
                $hwnd = [OpRec.WinUtil]::FindWindowByTitle($WindowTitle)
                if ($hwnd -eq [IntPtr]::Zero) { throw "タイトルに「$WindowTitle」を含むウィンドウが見つかりません" }
            } elseif ($ProcessName -and -not $Pick -and $Mode -eq 'Window') {
                $hwnd = [OpRec.WinUtil]::FindWindowByProcess($ProcessName)
                if ($hwnd -eq [IntPtr]::Zero) { throw "プロセス「$ProcessName」のウィンドウが見つかりません" }
            }
            if ($Mode -eq 'App' -and $ProcessName -and -not $Pick) { $rec.TargetProcessName = $ProcessName }
            if ($hwnd -ne [IntPtr]::Zero) { $rec.TargetHwnd = $hwnd }
            if ($Pick -or ($hwnd -eq [IntPtr]::Zero -and -not $rec.TargetProcessName)) { $rec.PickTarget = $true }
        }
    }

    $session = $rec.Start($OutDir)
    $stopFile = Join-Path $session 'STOP'
    Write-Host ""
    Write-Host "記録フォルダ: $session"
    if ($rec.PickTarget -and $Mode -eq 'Desktop') {
        Write-Host "記録したいディスプレイに映っているウィンドウを 1 回クリックしてください(このクリックは記録しません)" -ForegroundColor Yellow
    } elseif ($rec.PickTarget) {
        Write-Host "記録したいウィンドウを 1 回クリックしてください(このクリックは記録しません)" -ForegroundColor Yellow
    } elseif ($Mode -eq 'AllDesktops') {
        Write-Host "対象: すべてのディスプレイ(撮影の間隔 $($rec.IntervalMs) ms)"
        Write-Host "  すべてのディスプレイを撮ります。映っている他のアプリも写るので、関係ない画面は閉じておいてください。" -ForegroundColor Yellow
    } elseif ($rec.TargetMonitor) {
        Write-Host ("対象: ディスプレイ {0} {1}" -f $rec.TargetMonitor.Index, $rec.TargetMonitor.Describe())
        Write-Host "  ディスプレイ全体を撮ります。映っている他のアプリも写るので、関係ない画面は閉じておいてください。" -ForegroundColor Yellow
    } elseif ($rec.TargetHwnd -ne [IntPtr]::Zero) {
        Write-Host ("対象: " + [OpRec.WinUtil]::Describe($rec.TargetHwnd))
    } elseif ($rec.TargetProcessName) {
        Write-Host ("対象プロセス: " + $rec.TargetProcessName)
    }
    Write-Host "記録中です。$hotkeyHelp" -ForegroundColor Green

    $status = 'saved'
    $deadline = (Get-Date).AddMinutes($MaxMinutes)
    try {
        while ($true) {
            if ($rec.StopRequested.WaitOne(300)) { break }
            if ($rec.RestartRequested.WaitOne(0)) { $status = 'restart'; break }
            if (Test-Path $stopFile) { break }
            if ((Get-Date) -gt $deadline) { Write-Host "最大記録時間($MaxMinutes 分)に達したので保存します"; break }
        }
    } finally {
        $rec.Stop()
        if (Test-Path $stopFile) { Remove-Item $stopFile -Force }
    }

    $target = @{ Hwnd = $rec.TargetHwnd; ProcessName = $rec.TargetProcessName; MonitorIndex = $(if ($rec.TargetMonitor) { $rec.TargetMonitor.Index } else { 0 }) }
    if ($status -eq 'restart') {
        # 取り直すので、この記録は保存せずに捨てる
        Remove-Item $session -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "今の記録を取り消しました(保存していません)。" -ForegroundColor Yellow
        return @{ Status = 'restart'; Target = $target }
    }

    Write-Host "記録を保存しました: $($rec.SavedCount) 件"
    $mds = @()
    if ($Template.Count -and $rec.SavedCount -gt 0) {
        $args2 = @{ SessionDir = $session; Template = $Template; ImageStyle = $ImageStyle }
        # 丸の半径は、出力するときに描く丸に使う(0 なら丸を描かない)
        if ($MarkerRadius -gt 0) { $args2.MarkerRadius = $MarkerRadius } else { $args2.Marks = @('arrow', 'frame') }
        if ($ReportDir) { $args2.ReportDir = $ReportDir }
        if ($TemplateDir) { $args2.TemplateDir = $TemplateDir }
        foreach ($md in @(& (Join-Path $PSScriptRoot 'New-OperationReport.ps1') @args2)) {
            Write-Host "報告書: $md"
            $mds += $md
        }
    } elseif ($Template.Count) {
        Write-Host "記録した操作が 0 件なので、報告書は出力しません" -ForegroundColor Yellow
    }
    return @{ Status = 'saved'; Session = $session; Mds = $mds; Target = $target }
}

function Wait-StartKey {
    $listener = [OpRec.HotkeyListener]::new()
    $listener.Start()
    if ($listener.Error) { $listener.Dispose(); throw $listener.Error }
    $quitFile = Join-Path $OutDir 'QUIT'
    $startFile = Join-Path $OutDir 'START'
    try {
        while ($true) {
            if ($listener.StartPressed.WaitOne(300)) { return $true }
            if (Test-Path $startFile) { Remove-Item $startFile -Force; return $true }
            if ($listener.QuitPressed.WaitOne(0) -or (Test-Path $quitFile)) {
                if (Test-Path $quitFile) { Remove-Item $quitFile -Force }
                return $false
            }
        }
    } finally {
        $listener.Dispose()
    }
}

# ---------------- 実行 ----------------

$reuse = $null
$waitForStart = -not $StartNow
while ($true) {
    if ($waitForStart) {
        Write-Host ""
        Write-Host ("待機中です。Ctrl+Shift+F9 で記録を開始、Ctrl+Shift+F12 で" + $(if ($Resident) { '常駐を終了' } else { '終了' }) + "します。") -ForegroundColor Cyan
        if (-not (Wait-StartKey)) { Write-Host "終了しました。"; break }
    }
    $waitForStart = $true

    try {
        $res = Invoke-OneRecording $reuse
    } catch {
        Write-Host ("記録を開始できませんでした: " + $_.Exception.Message) -ForegroundColor Red
        if (-not $Resident) { throw }
        $reuse = $null
        continue
    }

    if ($res.Status -eq 'restart') {
        # 同じ設定・同じ対象で最初から。常駐していなければすぐ始め、常駐時は F9 を待つ
        $reuse = $res.Target
        if (-not $Resident) { $waitForStart = $false; Write-Host "同じ設定で最初から記録し直します。" -ForegroundColor Cyan }
        continue
    }

    $reuse = $null
    if ($Open) {
        if ($res.Mds.Count) { foreach ($md in $res.Mds) { Invoke-Item -LiteralPath $md } }
        else { Write-Host "md を出力していないので、開くものがありません" -ForegroundColor Yellow }
    }
    $values = if ($Return -eq 'Markdown' -and $res.Mds.Count) { @($res.Mds) } else {
        if ($Return -eq 'Markdown') { Write-Warning "md を出力していないので、記録フォルダのパスを返します" }
        @($res.Session)
    }
    $values | Write-Output
    if ($ResultFile) { Add-Content -LiteralPath $ResultFile -Value $values -Encoding utf8 }
    if (-not $Resident) { break }
}

if ($ResultFile -and $Interactive) { Read-Host "終了しました。Enter でこのウィンドウを閉じます" | Out-Null }
