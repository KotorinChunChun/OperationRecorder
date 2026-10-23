# 記録エンジン(src/*.cs)をコンパイルして読み込む。Start-OperationRecording.ps1 から呼ばれる。
if ('OpRec.Recorder' -as [type]) { return }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Drawing

$src = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName
Add-Type -Path $src -ReferencedAssemblies @(
    'UIAutomationClient', 'UIAutomationTypes',
    # 名前で 'WindowsBase' と書くと System.Windows.Point を含まない参照用のアセンブリに解決されるので、実体のパスを渡す
    [System.Windows.Automation.AutomationElement].GetMethod('FromPoint').GetParameters()[0].ParameterType.Assembly.Location,
    'System.Drawing.Common', 'System.Drawing.Primitives',
    # .NET 9 以降の System.Drawing は内部インターフェース(IPointer 等)をここに持つ。無いと CS0012 になる
    'System.Private.Windows.Core', 'System.Private.Windows.GdiPlus',
    'System.Text.Json', 'System.Text.Encodings.Web',
    'System.Collections', 'System.Collections.Concurrent', 'System.Linq',
    'System.Diagnostics.Process', 'System.Diagnostics.FileVersionInfo', 'System.ComponentModel.Primitives',
    'System.Console', 'System.Threading', 'System.Threading.Thread',
    'System.Runtime.InteropServices', 'System.Memory', 'System.Text.Encoding.Extensions',
    'System.Text.RegularExpressions', 'System.Runtime'
)
