# 対話入力の共通部品。Start-OperationRecording.ps1 と Reissue-OperationReport.ps1 がドットソースして使う。
#   Read-LineOrEsc / Read-Choice / Read-MultiChoice / Read-Text は、Esc(選択肢では 0 も)で $null を返す = 前の質問に戻る合図
#   Get-TemplateItems はテンプレートの一覧を凡例用に返す

# 1 行を読む。Esc を押したら $null を返す(前の質問に戻る合図)。
# Read-Host は Esc を「入力中の文字を消す」にしか使わず検知できないので、[Console]::ReadKey で 1 キーずつ読む。
# 日本語入力は確定した文字が KeyChar で届くので、そのまま使える
function Read-LineOrEsc([string]$Prompt) {
    Write-Host -NoNewline "${Prompt}: "
    $sb = [System.Text.StringBuilder]::new()
    while ($true) {
        $k = [Console]::ReadKey($true)
        switch ($k.Key) {
            'Escape' { Write-Host ' (前の質問に戻ります)' -ForegroundColor DarkGray; return $null }
            'Enter' { Write-Host ''; return $sb.ToString() }
            'Backspace' {
                if ($sb.Length -gt 0) {
                    $c = $sb[$sb.Length - 1]
                    $sb.Length--
                    # 全角文字はコンソール上で 2 桁ぶん消す
                    $w = if ([int]$c -ge 0x1100) { 2 } else { 1 }
                    Write-Host -NoNewline (("`b" * $w) + (' ' * $w) + ("`b" * $w))
                }
            }
            default {
                if ($k.KeyChar -ne [char]0 -and -not [char]::IsControl($k.KeyChar)) {
                    [void]$sb.Append($k.KeyChar)
                    Write-Host -NoNewline $k.KeyChar
                }
            }
        }
    }
}

# 選択肢を凡例付きで聞く。0 か Esc なら $null(前の質問に戻る)
function Read-Choice {
    param([string]$Title, [object[]]$Items, [string]$Default)
    Write-Host ""
    Write-Host "■ $Title" -ForegroundColor Cyan
    # 既定の選択肢は番号の直後に * を付ける(付かない行は空白 1 つで、行頭の位置を揃える)
    $w = [string]$Items.Count
    for ($i = 0; $i -lt $Items.Count; $i++) {
        $isDefault = $Items[$i].Key -eq $Default
        Write-Host ("  {0}){1} {2,-9} … {3}{4}" -f ([string]($i + 1)).PadLeft($w.Length), $(if ($isDefault) { '*' } else { ' ' }), $Items[$i].Key, $Items[$i].Desc, $(if ($isDefault) { ' (既定)' } else { '' }))
    }
    while ($true) {
        $ans = Read-LineOrEsc "  番号か名前を入力(Enter で $Default / 0 か Esc で戻る)"
        if ($null -eq $ans) { return $null }
        $ans = $ans.Trim()
        if ($ans -eq '') { return $Default }
        if ($ans -eq '0') { return $null }
        $n = 0
        if ([int]::TryParse($ans, [ref]$n) -and $n -ge 1 -and $n -le $Items.Count) { return $Items[$n - 1].Key }
        $hit = $Items | Where-Object { $_.Key -ieq $ans }
        if ($hit) { return $hit.Key }
        Write-Host "  選択肢の中から選んでください" -ForegroundColor Yellow
    }
}

# 複数選べる選択肢。0 か Esc なら $null(前の質問に戻る)
function Read-MultiChoice {
    param([string]$Title, [object[]]$Items, [string[]]$Default)
    Write-Host ""
    Write-Host "■ $Title(複数選ぶときはカンマ区切り。例: 1,3)" -ForegroundColor Cyan
    $w = [string]$Items.Count
    for ($i = 0; $i -lt $Items.Count; $i++) {
        $isDefault = $Default -contains $Items[$i].Key
        Write-Host ("  {0}){1} {2,-11} … {3}{4}" -f ([string]($i + 1)).PadLeft($w.Length), $(if ($isDefault) { '*' } else { ' ' }), $Items[$i].Key, $Items[$i].Desc, $(if ($isDefault) { ' (既定)' } else { '' }))
    }
    while ($true) {
        $ans = Read-LineOrEsc "  番号か名前を入力(Enter で $($Default -join ',') / 0 か Esc で戻る)"
        if ($null -eq $ans) { return $null }
        $ans = $ans.Trim()
        if ($ans -eq '') { return , $Default }
        if ($ans -eq '0') { return $null }
        $picked = [System.Collections.Generic.List[string]]::new()
        $ok = $true
        foreach ($a in ($ans -split '[,、\s]+' | Where-Object { $_ })) {
            $n = 0
            $hit = if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $Items.Count) { $Items[$n - 1] } else { $Items | Where-Object { $_.Key -ieq $a } | Select-Object -First 1 }
            if (-not $hit) { $ok = $false; break }
            if (-not $picked.Contains($hit.Key)) { $picked.Add($hit.Key) }
        }
        if ($ok -and $picked.Contains('none') -and $picked.Count -gt 1) { Write-Host "  none は他と一緒に選べません" -ForegroundColor Yellow; continue }
        if ($ok) { return , $picked.ToArray() }
        Write-Host "  選択肢の中から選んでください" -ForegroundColor Yellow
    }
}

# 文字列を聞く。Esc なら $null(前の質問に戻る)。0 は値として使う場合があるので、戻るのは Esc だけ
function Read-Text {
    param([string]$Title, [string]$Hint, [string]$Default, [switch]$Required)
    Write-Host ""
    Write-Host "■ $Title" -ForegroundColor Cyan
    if ($Hint) { foreach ($h in ($Hint -split "`n")) { Write-Host "  $h" } }
    while ($true) {
        $prompt = if ($Default) { "  入力(Enter で $Default / Esc で戻る)" } else { '  入力(Esc で戻る)' }
        $ans = Read-LineOrEsc $prompt
        if ($null -eq $ans) { return $null }
        $ans = $ans.Trim().Trim('"')
        if ($ans -eq '' -and $Default) { return $Default }
        if ($ans -ne '' -or -not $Required) { return $ans }
        Write-Host "  入力が必要です" -ForegroundColor Yellow
    }
}

# 起動時の説明(どういうツールで、どう使うか)を枠で囲んで表示する
function Show-Banner([string]$Title, [string]$Body) {
    $line = '=' * 72
    Write-Host ""
    Write-Host $line -ForegroundColor Cyan
    Write-Host " $Title" -ForegroundColor Cyan
    Write-Host $line -ForegroundColor Cyan
    foreach ($l in ($Body -replace "`r`n", "`n").Trim("`n").Split("`n")) { Write-Host $l }
    Write-Host $line -ForegroundColor Cyan
}

# テンプレートフォルダの md を、選択肢の凡例用に @{ Key = 名前; Desc = 説明 } の一覧にする。
# 説明は各テンプレート冒頭コメントの 3 行目から「操作報告書テンプレート」と末尾の「-Template 名前」を除いたもの
function Get-TemplateItems([string]$TemplateDir) {
    @(Get-ChildItem $TemplateDir -Filter *.md -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
        $desc = ((Get-Content $_.FullName -Encoding utf8 -TotalCount 3)[2] -replace '^\s*操作報告書テンプレート', '' -replace '\s+-Template\s+\S+\s*$', '').Trim()
        @{ Key = $_.BaseName; Desc = $desc }
    })
}
