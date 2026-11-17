using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class ExternalEvidenceCompatibilityTests
{
    [Fact]
    public async Task 外部証拠の完了工程を既存テンプレートで出力して原本を保持する()
    {
        using var c=new ExternalEvidenceSessionTests.Context(); await c.Start();
        var begin=await c.Session.HandleAsync(c.Request("BeginStep",Guid.NewGuid()));
        var end=await c.Session.HandleAsync(c.Request("EndStep",token:begin.StepToken)); Assert.Equal("after-saved",end.Status);
        string session=Path.Combine(c.Root,"output");
        var original=Directory.GetFiles(session,"*",SearchOption.AllDirectories).Where(f=>!f.EndsWith("external.lock")).ToDictionary(f=>f,ExternalEvidence.EvidenceFiles.HashFile);
        var options=ExportCommand.Parse(new[]{"--export",session,"--template","user,ai,text","--format","md,html","--embed","--out",Path.Combine(c.Root,"reports")},out var error);
        Assert.Null(error); var files=ExportCommand.Run(options); Assert.Equal(6,files.Count);
        Assert.All(files,file=>Assert.Contains("外部操作",File.ReadAllText(file)));
        foreach(var file in original) Assert.Equal(file.Value,ExternalEvidence.EvidenceFiles.HashFile(file.Key));
    }
}
