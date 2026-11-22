using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class PasswordLayoutCandidateTests
{
    static EvidenceTarget Target(string app = "OperationRecorder.EvidenceFixture.exe") =>
        new(13000, new DateTimeOffset(2026, 11, 19, 1, 0, 0, TimeSpan.Zero),
            Path.Combine(AppContext.BaseDirectory, app), 1, 393488);

    static string Request(EvidenceTarget target, string label, string field) =>
        JsonSerializer.Serialize(new { target, publicLabel = label, fieldClassPrefix = field }, EvidenceProtocol.Json);

    static async Task CheckStatus(string request, string expected, int timeout = 1000)
    {
        string unavailableExecutable = Path.Combine(AppContext.BaseDirectory, "NoSuchOperationRecorder.exe");
        string json = await PasswordLayoutCandidate.ObserveJsonAsync(unavailableExecutable, request, timeout);
        using var result = JsonDocument.Parse(json);
        var property = Assert.Single(result.RootElement.EnumerateObject());
        Assert.Equal("status", property.Name);
        Assert.Equal(expected, property.Value.GetString());
    }

    [Theory]
    [InlineData("OPREC_FAKE_VALUE", "WindowsForms10.EDIT")]
    [InlineData("パスワード:", "WindowsForms10.EDIT")]
    [InlineData("専用標準欄:", "WindowsForms10.Window")]
    [InlineData("専用カスタム欄:", "WindowsForms10.EDIT")]
    [InlineData("専用カスタム欄:", "WindowsForms10.")]
    public Task AllowedFixtureSelectorsAreFixed(string label, string field) =>
        CheckStatus(Request(Target(), label, field), "invalid-request");

    [Fact]
    public Task LabelAndClassDoNotPermitAnUnrelatedExecutable() =>
        CheckStatus(Request(Target("UnrelatedApplication.exe"), "専用カスタム欄:", "WindowsForms10.Window"), "target-mismatch");

    [Fact]
    public Task ApprovedExecutableFilenameAloneDoesNotSelectItsScope() =>
        CheckStatus(Request(Target("Xronos.TimeLaborManagement.exe"), "パスワード:", "WindowsForms10.Window"), "target-mismatch");

    [Fact]
    public Task InvalidIdentityIsRejectedBeforeStartingAWorker() =>
        CheckStatus(Request(Target() with { WindowHandle = 0 }, "専用標準欄:", "WindowsForms10.EDIT"), "invalid-request");

    [Fact]
    public Task DuplicateSelectorIsRejectedBeforeStartingAWorker() =>
        CheckStatus(Request(Target(), "専用標準欄:", "WindowsForms10.EDIT").Insert(1, "\"publicLabel\":\"OPREC_FAKE_VALUE\","),
            "invalid-request");

    [Fact]
    public Task UnknownDataIsRejectedWithoutEchoingItsContents() =>
        CheckStatus(Request(Target(), "専用標準欄:", "WindowsForms10.EDIT").Insert(1, "\"inputValue\":\"OPREC_FAKE_VALUE\","),
            "invalid-request");

    [Theory]
    [InlineData(199)]
    [InlineData(30001)]
    public Task OutOfRangeDeadlineIsRejected(int timeout) =>
        CheckStatus(Request(Target(), "専用標準欄:", "WindowsForms10.EDIT"), "invalid-request", timeout);

    [Fact]
    public Task ValidSelectorDoesNotReportSuccessWithoutTheWorker() =>
        CheckStatus(Request(Target(), "専用カスタム欄:", "WindowsForms10.Window"), "layout-unproven");

    [Theory]
    [InlineData("\"diagnoseWindowLayout\":true,", "layout-unproven")]
    [InlineData("\"reviewedWindowLayoutSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",", "layout-unproven")]
    [InlineData("\"reviewedWindowLayoutSha256\":\"bad\",", "invalid-request")]
    [InlineData("\"diagnoseWindowLayout\":true,\"reviewedWindowLayoutSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",", "invalid-request")]
    public Task LayoutOptionsAreValidatedBeforeStartingAWorker(string properties, string expected) =>
        CheckStatus(Request(Target(), "専用標準欄:", "WindowsForms10.EDIT").Insert(1, properties), expected);
}
