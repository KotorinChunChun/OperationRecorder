<#
.SYNOPSIS
Start-OperationRecording.ps1 の記録フォルダから、テンプレートに当てはめて操作報告書(Markdown)を生成する。

記録には、ユーザー向け・AI 向けのどちらに必要な情報もすべて入っている。何を載せるか(文章と画像だけか、
ウィンドウ名・コントロールID・相対座標まで載せるか)は、使うテンプレートだけで決まる。

md を作る処理そのものは src\ReportGenerator.cs / src\TemplateEngine.cs(C#)にあり、OperationRecorder アプリと
共通。このスクリプトは引数を整えて呼び出すだけ(初回は C# のコンパイルに数秒かかる)。

.PARAMETER Template
使うテンプレート。複数指定すると、その数だけ md を出力する。
名前だけ(例: psr)なら -TemplateDir の <名前>.md、パスならそのファイル。既定は user
  user        ユーザー向けの手順書（操作前後画像）
  psr         ステップ記録ツール形式（簡易UI操作履歴。日時・座標・プログラム・UI 要素付き）
  ai          AI向けの手順書（操作前後画像と詳細UI情報。ウィンドウ名・コントロールID・相対座標など）
  text        テキスト手順書（操作情報のみ。画像なし）
画像の並べ方(左右・縦・片方だけ・同じ画面なら 1 枚)は、テンプレートの中で {{images.side}} などを選んで決める(user.md の冒頭コメント参照)

.PARAMETER Marks
操作前の画像に合成する印(複数可)。pointer = マウスポインタ / circle = クリック位置の丸 / arrow = ドラッグの矢印 /
frame = 操作した部品の枠 / none = 描かない。既定は circle, arrow, frame。
記録の画像には印を描かずに保存してあり、出力するときに操作ログから描く(印を描いて保存していた v1.2 までの記録には描かない)

.PARAMETER MarkerRadius
クリック位置の丸の半径(px)。既定 14

.PARAMETER TemplateDir
テンプレートを置いたフォルダ。省略時はスキルの templates フォルダ

.PARAMETER OutFile
出力する md のパス。テンプレートを 1 つだけ指定したときに使える。
省略時は <ReportDir>\<記録フォルダ名>_<テンプレート名>.md

.EXAMPLE
pwsh -File New-OperationReport.ps1 -SessionDir .\session-20260917-101500
pwsh -File New-OperationReport.ps1 -SessionDir .\session-20260917-101500 -Template user, ai -ImageStyle Embed -ReportDir D:\手順書
pwsh -File New-OperationReport.ps1 -SessionDir .\session-20260917-101500 -Template D:\社内様式\手順書.md
#>
param(
    [Parameter(Mandatory)][string]$SessionDir,
    [string[]]$Template = @('user'),
    [string]$TemplateDir,
    [string]$OutFile,
    # md の出力先フォルダ。省略時は記録フォルダの中。-OutFile を指定したときは無視する
    [string]$ReportDir,
    # Link: img\ の PNG を相対パスで参照する / Embed: PNG を base64 で md に埋め込む(md 1 ファイルで完結する)
    [ValidateSet('Link', 'Embed')][string]$ImageStyle = 'Link',
    # pwsh -File では "circle,arrow" が 1 つの文字列で渡るので、ValidateSet ではなく MarkOptions.Parse で確かめる
    [string[]]$Marks = @('circle', 'arrow', 'frame'),
    [ValidateRange(1, 200)][int]$MarkerRadius = 14,
    # 記録の名前・解説。省略時は記録時に付けたもの(session.json の caption / description)を使う
    [string]$Caption,
    [string]$Description
)
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Import-OpRecorder.ps1')

# 相対パスは pwsh のカレントフォルダ基準で絶対パスにしてから C# に渡す(.NET の GetFullPath はプロセスのカレントを使うため)
function Get-FullPath([string]$p) { if ($p) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($p) } else { $null } }

$o = [OpRec.ReportGenerator+Options]::new()
$o.SessionDir = (Resolve-Path -LiteralPath $SessionDir).Path
$o.Templates = [string[]]@($Template | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } |
    ForEach-Object { if ($_ -match '[\\/]') { Get-FullPath $_ } else { $_ } })
$o.TemplateDir = if ($TemplateDir) { Get-FullPath $TemplateDir } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'templates' }
$o.OutFile = Get-FullPath $OutFile
$o.ReportDir = Get-FullPath $ReportDir
$o.EmbedImages = $ImageStyle -eq 'Embed'
$o.Marks = [OpRec.MarkOptions]::Parse((@($Marks | ForEach-Object { $_ -split ',' }) -join ','), $MarkerRadius)

if ($PSBoundParameters.ContainsKey('Caption')) { $o.Caption = $Caption }
if ($PSBoundParameters.ContainsKey('Description')) { $o.Description = $Description }

try {
    [OpRec.ReportGenerator]::Generate($o) | Write-Output
} catch {
    # C# の例外は MethodInvocationException に包まれるので、中のメッセージだけを伝える
    $inner = $_.Exception
    while ($inner.InnerException) { $inner = $inner.InnerException }
    throw $inner.Message
}
