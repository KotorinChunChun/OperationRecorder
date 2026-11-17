using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class ExternalEvidencePowerShellTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PS7のガード付き操作は保存完了時だけ一度実行する(bool failBefore)
    {
        string root=Path.Combine(Path.GetTempPath(),"oprec-evidence-ps-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string script=Path.Combine(root,"probe.ps1");
        string source="""
param($module,$executable,$output,$failBefore)
$ErrorActionPreference='Stop'
Import-Module $module -Force
$session=New-OperationEvidenceSession -Executable $executable -OutputDirectory $output -Target @{
    pid=$(if($failBefore -eq 'true'){99}else{1}); processStartTimeUtc=[DateTimeOffset]::UtcNow.ToString('o'); exePath='C:/fixture.exe'; windowsSessionId=1; windowHandle=2
}
$script:actions=0
try {
    try {
        $response=Invoke-OperationEvidenceStep -Session $session -StepId ([guid]::NewGuid()) -OperationName invoke -TimeoutMilliseconds 800 -Action {$script:actions++}
        if($failBefore -eq 'true' -or $response.status -ne 'after-saved'){throw 'Unexpected response'}
    } catch { if($failBefore -ne 'true'){throw} }
    $expected=if($failBefore -eq 'true'){0}else{1}
    if($script:actions -ne $expected){throw 'Action count mismatch'}
    'passed'
} finally { $null=Close-OperationEvidenceSession -Session $session }
""";
        File.WriteAllText(script,source,new UTF8Encoding(false));
        try
        {
            var info=new ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-NoProfile","-File",script,"-module",Path.Combine(AppContext.BaseDirectory,"scripts","OperationRecorderEvidence.psm1"),"-executable",ExternalEvidenceProcessTests.FixtureExe,"-output",Path.Combine(root,"session"),"-failBefore",failBefore?"true":"false"})info.ArgumentList.Add(arg);
            using var process=Process.Start(info); var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(process.ExitCode==0,await error); Assert.Contains("passed",await output);
        }
        finally { Directory.Delete(root,true); }
    }
}
