using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

[Collection("管理画面の隔離設定")]
public sealed class PasswordLayoutBackdropDesktopTests
{
    const string CandidateScript = "scripts/Get-OperationEvidencePasswordLayoutCandidate.ps1";
    const string Rejected = "layout-unproven";
    const string Diagnostic = "diagnostic";
    static readonly string[] Binaries = { "OperationRecorder.exe", "OperationRecorder.dll", "scripts/OperationRecorderEvidence.psm1", CandidateScript };
    static readonly string[] PrivateFixtureText =
    {
        "standardPassword", "customPassword", "OPREC_STANDARD_FAKE_PASSWORD", "OPREC_CUSTOM_FAKE_PASSWORD",
        "OPREC_FIXTURE_BACKDROP_TITLE", "OPREC_FIXTURE_BACKDROP_LABEL_NAME", "OPREC_FIXTURE_BACKDROP_DYNAMIC_TEXT",
        "OPREC_FIXTURE_BACKDROP_PASSWORD_NAME", "OPREC_FIXTURE_BACKDROP_PASSWORD_VALUE",
        "OPREC_FIXTURE_INVALID_CONTROL_ID", "OPREC_FIXTURE_INVALID_ACCESSIBLE_NAME",
        "OPREC_FIXTURE_INVALID_DYNAMIC_TEXT", "OPREC_FIXTURE_INVALID_DUMMY_VALUE"
    };
    static readonly Rectangle StandardField = new(164, 74, 220, 28), CustomField = new(164, 122, 220, 28);
    static readonly Rectangle StandardLabel = new(20, 74, 140, 28), CustomLabel = new(20, 122, 140, 28);

    [Fact, Trait("Category", "Desktop")]
    public async Task 専用WinFormsの無効化方法ごとにWin32とUIAの有効状態を確認する()
    {
        if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1" || !Environment.UserInteractive)
            throw new InvalidOperationException("DESKTOP_BLOCKED: 専用fixture指定と対話デスクトップが必要です。");

        await using var fixture = await Fixture.StartAsync();
        var observations = new List<object>();
        foreach (var ownership in new[] { "layout-owned", "layout-owning", "layout-unowned" })
        {
            await fixture.ChangeAsync(ownership);
            foreach (var command in new[] { "layout-disable-managed", "layout-disable-native" })
            {
                var state = await fixture.ChangeAsync(command);
                Assert.True(state.BackgroundVisible);
                Assert.False(state.BackgroundEnabled);
                var background = AutomationElement.FromHandle(new IntPtr(state.BackgroundHwnd.Value));
                Assert.Equal(fixture.Target.Pid, background.Current.ProcessId);
                bool uiaEnabled = background.Current.IsEnabled;
                bool nativeOnly = command == "layout-disable-native";
                Assert.Equal(nativeOnly, state.BackgroundManagedEnabled);
                Assert.Equal(nativeOnly, uiaEnabled);
                observations.Add(new { ownership, command, win32Enabled = state.BackgroundEnabled,
                    uiaEnabled, managedEnabled = state.BackgroundManagedEnabled });
            }
        }

        var artifacts = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
        if (artifacts != null)
        {
            Directory.CreateDirectory(artifacts);
            WriteJson(Path.Combine(artifacts, "winforms-enabled-probe.json"), observations);
        }
    }

    [Fact, Trait("Category", "Desktop")]
    public async Task 非オフスクリーンの不正矩形は失敗箇所を返して候補を拒否する()
    {
        if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1" || !Environment.UserInteractive)
            throw new InvalidOperationException("DESKTOP_BLOCKED: 専用fixture指定と対話デスクトップが必要です。");

        string artifacts = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
        string root = Path.Combine(artifacts ?? Path.GetTempPath(), "oprec-layout-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var probes = new List<object>();
        var responses = new List<object>();
        try
        {
            string executable = PrepareProgram(Path.Combine(root, "program"));
            string runner = Path.Combine(root, "read-candidate.ps1");
            string context = Path.Combine(root, "target.json");
            WritePowerShellRunner(runner);
            await using var fixture = await Fixture.StartAsync();
            WriteJson(context, fixture.Target);
            await fixture.ChangeAsync("layout-unowned");
            var reviewed = VerifyDiagnostic(await ReadAsync("baseline", Diagnostic), "review-required");
            string reviewedHash = reviewed.GetProperty("layoutSha256").GetString();
            Assert.True(EvidenceProtocol.IsHash(reviewedHash));

            foreach (string command in new[]
            {
                "layout-backdrop-zero-panel",
                "layout-backdrop-zero-password",
                "layout-backdrop-invalid-provider"
            })
            {
                await fixture.ChangeAsync("layout-unowned");
                var state = await fixture.ChangeAsync(command);
                long handle = Assert.IsType<long>(state.BackgroundInvalidBoundsHwnd);
                bool virtualChild = command == "layout-backdrop-invalid-provider";
                var observation = ProbeInvalidBounds(state.BackgroundHwnd.Value, handle, fixture.Target.Pid, virtualChild);
                probes.Add(new { command, handle, observation });
                if (virtualChild)
                {
                    Assert.NotNull(observation);
                    Assert.False(observation.IsOffscreen);
                    Assert.False(observation.IsPassword);
                    Assert.Equal(ControlType.Custom.Id, observation.ControlTypeId);
                    Assert.Contains(observation.BoundsCode, new[] { "empty", "non-positive-size" });
                    Assert.Null(observation.NativeWindowHandle);
                }
                if (observation == null || observation.IsOffscreen || observation.BoundsCode == "ok")
                {
                    VerifyStatusOnly(await ReadAsync(command + "-previously-reviewed", "candidate", reviewedHash), Rejected);
                    continue;
                }

                var diagnostic = VerifyDiagnostic(await ReadAsync(command, Diagnostic), "background-structure-unproven");
                var structure = VerifyBackgroundFailure(diagnostic, "uia-bounds-invalid", uiaEnabled: false);
                Assert.Equal(observation.Position, structure.GetProperty("visitedElements").GetInt32());
                var failure = structure.GetProperty("failureElement");
                Assert.Equal(observation.RawViewPath.Length, failure.GetProperty("depth").GetInt32());
                Assert.Equal(observation.RawViewPath, failure.GetProperty("rawViewPath").EnumerateArray().Select(index => index.GetInt32()));
                Assert.Equal(observation.ControlTypeId, failure.GetProperty("controlTypeId").GetInt32());
                Assert.Equal(observation.IsPassword, failure.GetProperty("isPassword").GetBoolean());
                Assert.False(failure.GetProperty("isOffscreen").GetBoolean());
                Assert.Equal(observation.BoundsCode, failure.GetProperty("boundsCode").GetString());
                if (observation.NativeWindowHandle is long nativeHandle)
                {
                    Assert.Equal(nativeHandle, failure.GetProperty("nativeWindowHandle").GetInt64());
                    Assert.Equal(observation.NativeBoundsCode, failure.GetProperty("nativeBoundsCode").GetString());
                }
                else
                {
                    Assert.False(failure.TryGetProperty("nativeWindowHandle", out _));
                    Assert.False(failure.TryGetProperty("nativeBoundsCode", out _));
                }
                VerifyStatusOnly(await ReadAsync(command + "-previously-reviewed", "candidate", reviewedHash), Rejected);
            }
            Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Contains("catalog", StringComparison.OrdinalIgnoreCase));

            async Task<JsonElement> ReadAsync(string name, string mode, string hash = "")
            {
                var response = await ReadCandidateAsync(runner, executable, context, mode, hash, "standard");
                responses.Add(new { name, response });
                return response;
            }
        }
        finally
        {
            if (artifacts == null) Directory.Delete(root, recursive: true);
            else WriteJson(Path.Combine(root, "invalid-bounds-observations.json"), new { probes, responses });
        }
    }

    static InvalidBoundsObservation ProbeInvalidBounds(long background, long handle, int pid, bool virtualChild)
    {
        var walker = TreeWalker.RawViewWalker;
        int visited = 0;
        return Find(AutomationElement.FromHandle(new IntPtr(background)), Array.Empty<int>(), false);

        InvalidBoundsObservation Find(AutomationElement element, int[] path, bool hostFound)
        {
            Assert.InRange(++visited, 1, 256);
            Assert.InRange(path.Length, 0, 16);
            var current = element.Current;
            Assert.Equal(pid, current.ProcessId);
            long nativeHandle = unchecked((uint)current.NativeWindowHandle);
            bool expected = virtualChild ? hostFound && current.ControlType.Id == ControlType.Custom.Id : nativeHandle == handle;
            if (expected)
            {
                var bounds = current.BoundingRectangle;
                string boundsCode = bounds.IsEmpty ? "empty" : bounds.Width <= 0 || bounds.Height <= 0 ? "non-positive-size" :
                    new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }.Any(edge => Math.Truncate(edge) != edge) ? "non-integer" : "ok";
                string nativeBoundsCode = null;
                if (nativeHandle != 0)
                {
                    Assert.Equal(handle, nativeHandle);
                    Assert.True(GetWindowRect(new IntPtr(nativeHandle), out var nativeBounds));
                    nativeBoundsCode = nativeBounds.Right <= nativeBounds.Left || nativeBounds.Bottom <= nativeBounds.Top
                        ? "non-positive-size" : "ok";
                }
                return new(path, visited, current.ControlType.Id, current.IsOffscreen, current.IsPassword,
                    boundsCode, nativeHandle == 0 ? null : nativeHandle, nativeBoundsCode);
            }
            int index = 0;
            for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child), index++)
                if (Find(child, [.. path, index], hostFound || nativeHandle == handle) is { } found) return found;
            return null;
        }
    }

    sealed record InvalidBoundsObservation(int[] RawViewPath, int Position, int ControlTypeId, bool IsOffscreen,
        bool IsPassword, string BoundsCode, long? NativeWindowHandle, string NativeBoundsCode);

    [Fact, Trait("Category", "Desktop")]
    public async Task 可視で無効な背後窓はレビュー済みの構成だけで候補を取得する()
    {
        if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1" || !Environment.UserInteractive)
            throw new InvalidOperationException("DESKTOP_BLOCKED: 専用fixture指定と対話デスクトップが必要です。");

        string artifacts = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
        string root = Path.Combine(artifacts ?? Path.GetTempPath(), "oprec-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = PrepareProgram(Path.Combine(root, "program"));
            string program = Path.GetDirectoryName(executable);
            string runner = Path.Combine(root, "read-candidate.ps1");
            string context = Path.Combine(root, "target.json");
            WritePowerShellRunner(runner);
            string[] originalFiles = FileHashes(program);
            var results = new List<object>();
            await using var fixture = await Fixture.StartAsync();
            int dpi = (int)GetDpiForWindow(new IntPtr(fixture.Target.WindowHandle));
            Assert.InRange(dpi, 96, 768);
            WriteJson(context, fixture.Target);

            var single = await fixture.ChangeAsync("layout-status");
            Assert.True(single.LoginEnabled);
            Assert.Null(single.BackgroundHwnd);
            VerifyCandidate(await ReadAsync("single-window"), fixture.Target, single, dpi, standard: true);

            var ownerships = new[]
            {
                (Command: "layout-owned", Mode: "login-owned-by-background"),
                (Command: "layout-owning", Mode: "background-owned-by-login"),
                (Command: "layout-unowned", Mode: "unowned"),
            };
            var digests = new HashSet<string>(StringComparer.Ordinal);
            string reviewedHash = null;
            string managedOwnedHash = null;
            foreach (var (command, mode) in ownerships)
            {
                var state = await fixture.ChangeAsync(command);
                Assert.True(state.BackgroundVisible);
                Assert.False(state.BackgroundEnabled);
                Assert.True(state.LoginEnabled);
                Assert.NotEqual(state.LoginHwnd, state.BackgroundHwnd);
                Assert.Equal(mode == "login-owned-by-background" ? state.BackgroundHwnd : 0, state.LoginOwnerHwnd);
                Assert.Equal(mode == "background-owned-by-login" ? state.LoginHwnd : 0, state.BackgroundOwnerHwnd);

                VerifyStatusOnly(await ReadAsync(mode + "-no-review"), Rejected);
                var diagnosis = await ReadAsync(mode + "-diagnostic", Diagnostic);
                reviewedHash = VerifyReviewedLayout(diagnosis, fixture.Target, state, mode, dpi);
                Assert.True(digests.Add(reviewedHash), "所有関係を変えると構成ハッシュも変わる必要があります。");
                if (mode == "login-owned-by-background") managedOwnedHash = reviewedHash;
                VerifyCandidate(await ReadAsync(mode + "-standard", hash: reviewedHash), fixture.Target, state, dpi, standard: true);
                VerifyCandidate(await ReadAsync(mode + "-custom", hash: reviewedHash, field: "custom"), fixture.Target, state, dpi, standard: false);
            }

            VerifyStatusOnly(await ReadAsync("wrong-reviewed-hash", hash: new string('a', 64)), Rejected);
            VerifyStatusOnly(await ReadAsync("invalid-reviewed-hash", hash: "not-a-sha256"), "invalid-request");
            await fixture.ChangeAsync("layout-change-structure");
            var changed = await ReadAsync("changed-background-diagnostic", Diagnostic);
            var changedDiagnostics = VerifyDiagnostic(changed, "review-required");
            Assert.NotEqual(reviewedHash, changedDiagnostics.GetProperty("layoutSha256").GetString());
            VerifyStatusOnly(await ReadAsync("previously-reviewed-structure", hash: reviewedHash), Rejected);

            await VerifyRejectedLayout("layout-enable-backdrop", "enabled-background", "background-window-enabled");
            await VerifyRejectedLayout("layout-extra-backdrop", "ambiguous-background", "ambiguous-window-layout", reset: true);
            await VerifyRejectedLayout("layout-hidden-owner", "unknown-owner", "unsupported-ownership", reset: true);
            await VerifyRejectedLayout("layout-backdrop-password", "background-with-password", "background-authentication-ui", reset: true);

            await fixture.ChangeAsync("layout-unowned");
            await fixture.ChangeAsync("layout-backdrop-deep");
            var deep = VerifyDiagnostic(await ReadAsync("background-depth-diagnostic", Diagnostic), "background-structure-unproven");
            var deepStructure = VerifyBackgroundFailure(deep, "uia-depth-limit", uiaEnabled: false);
            Assert.Equal(17, deepStructure.GetProperty("deepestDepth").GetInt32());
            Assert.InRange(deepStructure.GetProperty("visitedElements").GetInt32(), 17, 256);
            VerifyStatusOnly(await ReadAsync("unproven-background-depth", hash: reviewedHash), Rejected);

            await fixture.ChangeAsync("layout-owned");
            var nativeDisabled = await fixture.ChangeAsync("layout-disable-native");
            Assert.False(nativeDisabled.BackgroundEnabled);
            Assert.True(nativeDisabled.BackgroundManagedEnabled);
            VerifyStatusOnly(await ReadAsync("native-disabled-no-review"), Rejected);
            var nativeDiagnosis = await ReadAsync("native-disabled-diagnostic", Diagnostic);
            string nativeHash = VerifyReviewedLayout(nativeDiagnosis, fixture.Target, nativeDisabled, "login-owned-by-background", dpi);
            var diagnostics = nativeDiagnosis.GetProperty("diagnostics");
            Assert.False(diagnostics.TryGetProperty("failureRole", out _));
            VerifyBackgroundStructure(diagnostics, "uia-root-enabled-mismatch", uiaEnabled: true);
            Assert.DoesNotContain(nativeHash, digests);
            VerifyStatusOnly(await ReadAsync("managed-hash-with-native-disable", hash: managedOwnedHash), Rejected);
            VerifyCandidate(await ReadAsync("native-disabled-standard", hash: nativeHash), fixture.Target, nativeDisabled, dpi, standard: true);
            VerifyCandidate(await ReadAsync("native-disabled-custom", hash: nativeHash, field: "custom"), fixture.Target, nativeDisabled, dpi, standard: false);

            var withEdit = await fixture.ChangeAsync("layout-backdrop-disabled-edit");
            var edit = AutomationElement.FromHandle(new IntPtr(withEdit.BackgroundEditHwnd.Value));
            Assert.Equal(fixture.Target.Pid, edit.Current.ProcessId);
            Assert.Equal(50004, edit.Current.ControlType.Id);
            Assert.False(edit.Current.IsPassword);
            Assert.False(edit.Current.IsEnabled);
            var blockedEdit = VerifyDiagnostic(await ReadAsync("native-disabled-edit-diagnostic", Diagnostic), "background-authentication-ui");
            VerifyBackgroundFailure(blockedEdit, "uia-root-enabled-mismatch", uiaEnabled: true);
            VerifyStatusOnly(await ReadAsync("native-disabled-edit-blocked", hash: nativeHash), Rejected);

            await fixture.ChangeAsync("layout-owned");
            await fixture.ChangeAsync("layout-disable-native");
            await fixture.ChangeAsync("layout-backdrop-deep");
            var nativeDeep = VerifyDiagnostic(await ReadAsync("native-disabled-depth-diagnostic", Diagnostic), "background-structure-unproven");
            Assert.Equal(17, VerifyBackgroundFailure(nativeDeep, "uia-depth-limit", uiaEnabled: true).GetProperty("deepestDepth").GetInt32());
            VerifyStatusOnly(await ReadAsync("native-disabled-depth-blocked", hash: nativeHash), Rejected);

            await using (var foreign = await Fixture.StartAsync())
            {
                Assert.NotEqual(fixture.Target.Pid, foreign.Target.Pid);
                WriteJson(context, fixture.Target with { WindowHandle = foreign.Target.WindowHandle });
                VerifyStatusOnly(await ReadAsync("foreign-pid-target", hash: reviewedHash), "target-mismatch");
            }

            Assert.Equal(originalFiles, FileHashes(program));
            Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Contains("catalog", StringComparison.OrdinalIgnoreCase));
            var verifiedBinaries = Binaries.Select(file =>
            {
                string path = Path.Combine(program, file);
                return new { path, sha256 = EvidenceFiles.HashFile(path) };
            }).Append(new { path = ExternalEvidenceProcessTests.FixtureExe,
                sha256 = EvidenceFiles.HashFile(ExternalEvidenceProcessTests.FixtureExe) });
            WriteJson(Path.Combine(root, "result.json"), new
            {
                result = "passed", cases = results, productFilesUnchanged = true,
                captureCreated = false, catalogCreated = false, verifiedBinaries
            });

            async Task<JsonElement> ReadAsync(string name, string mode = "candidate", string hash = "", string field = "standard")
            {
                var response = await ReadCandidateAsync(runner, executable, context, mode, hash, field);
                results.Add(new { name, response });
                return response;
            }

            async Task VerifyRejectedLayout(string command, string name, string reason, bool reset = false)
            {
                if (reset) await fixture.ChangeAsync("layout-unowned");
                var state = await fixture.ChangeAsync(command);
                Assert.True(state.BackgroundVisible);
                switch (command)
                {
                    case "layout-enable-backdrop": Assert.True(state.BackgroundEnabled); break;
                    case "layout-extra-backdrop": Assert.NotNull(state.ExtraHwnd); break;
                    case "layout-hidden-owner": Assert.Equal(state.HiddenOwnerHwnd, state.BackgroundOwnerHwnd); break;
                }
                var diagnostic = VerifyDiagnostic(await ReadAsync(name + "-diagnostic", Diagnostic), reason);
                Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("layoutSha256").ValueKind);
                VerifyStatusOnly(await ReadAsync(name, hash: reviewedHash), Rejected);
            }
        }
        finally
        {
            if (artifacts == null) Directory.Delete(root, recursive: true);
        }
    }

    static string VerifyReviewedLayout(JsonElement reply, EvidenceTarget target, LayoutState state, string ownership, int dpi)
    {
        var diagnostic = VerifyDiagnostic(reply, "review-required");
        Assert.Equal("synthetic-fixture", reply.GetProperty("scope").GetString());
        Assert.Equal(EvidenceFiles.HashFile(target.ExePath), reply.GetProperty("appSha256").GetString());
        Assert.Equal(FileVersionInfo.GetVersionInfo(target.ExePath).FileVersion, reply.GetProperty("appFileVersion").GetString());
        Assert.Equal(ownership, diagnostic.GetProperty("ownership").GetString());
        var login = diagnostic.GetProperty("login");
        var background = diagnostic.GetProperty("background");
        Assert.Equal(state.LoginHwnd, login.GetProperty("hwnd").GetInt64());
        Assert.Equal(state.LoginOwnerHwnd, login.GetProperty("ownerHwnd").GetInt64());
        Assert.Equal(state.BackgroundHwnd, background.GetProperty("hwnd").GetInt64());
        Assert.Equal(state.BackgroundOwnerHwnd, background.GetProperty("ownerHwnd").GetInt64());
        Assert.True(login.GetProperty("visible").GetBoolean());
        Assert.True(background.GetProperty("visible").GetBoolean());
        Assert.True(login.GetProperty("enabled").GetBoolean());
        Assert.False(background.GetProperty("enabled").GetBoolean());
        Assert.Equal(dpi, login.GetProperty("dpi").GetInt32());
        Assert.Equal(dpi, background.GetProperty("dpi").GetInt32());
        Assert.Equal(new Rectangle(state.LoginX, state.LoginY, 440, 252), ReadRectangle(login.GetProperty("captureBounds")));
        Assert.Equal(new Rectangle(state.LoginX - 16, state.LoginY - 16, 500, 312), ReadRectangle(background.GetProperty("captureBounds")));
        Assert.Equal(login.GetProperty("windowClass").GetString(), background.GetProperty("windowClass").GetString());
        Assert.Equal(ExpectedLoginStructure(), login.GetProperty("uiSha256").GetString());
        Assert.True(EvidenceProtocol.IsHash(background.GetProperty("uiSha256").GetString()));
        string hash = diagnostic.GetProperty("layoutSha256").GetString();
        Assert.True(EvidenceProtocol.IsHash(hash));
        return hash;
    }

    static JsonElement VerifyDiagnostic(JsonElement reply, string reason)
    {
        Assert.Equal("diagnostic-only", reply.GetProperty("status").GetString());
        Assert.False(reply.TryGetProperty("field", out _));
        var diagnostic = reply.GetProperty("diagnostics");
        Assert.Equal(reason, diagnostic.GetProperty("reason").GetString());
        return diagnostic;
    }

    static JsonElement VerifyBackgroundFailure(JsonElement diagnostic, string code, bool uiaEnabled)
    {
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("layoutSha256").ValueKind);
        Assert.Equal("background", diagnostic.GetProperty("failureRole").GetString());
        return VerifyBackgroundStructure(diagnostic, code, uiaEnabled);
    }

    static JsonElement VerifyBackgroundStructure(JsonElement diagnostic, string code, bool uiaEnabled)
    {
        var structure = diagnostic.GetProperty("backgroundStructure");
        Assert.Equal(code, structure.GetProperty("code").GetString());
        Assert.False(structure.GetProperty("win32RootEnabled").GetBoolean());
        Assert.Equal(uiaEnabled, structure.GetProperty("uiaRootEnabled").GetBoolean());
        Assert.Equal(16, structure.GetProperty("maxDepth").GetInt32());
        Assert.Equal(256, structure.GetProperty("maxElements").GetInt32());
        return structure;
    }

    static void VerifyCandidate(JsonElement reply, EvidenceTarget target, LayoutState state, int dpi, bool standard)
    {
        Assert.Equal("candidate-only", reply.GetProperty("status").GetString());
        Assert.Equal("synthetic-fixture", reply.GetProperty("scope").GetString());
        Assert.Equal(EvidenceFiles.HashFile(target.ExePath), reply.GetProperty("appSha256").GetString());
        Assert.Equal(dpi, reply.GetProperty("dpi").GetInt32());
        Assert.Equal(new Rectangle(state.LoginX, state.LoginY, 440, 252), ReadRectangle(reply.GetProperty("captureBounds")));
        Assert.Equal(ExpectedLoginStructure(), reply.GetProperty("uiSha256").GetString());
        Assert.False(reply.TryGetProperty("diagnostics", out _));
        var field = reply.GetProperty("field");
        Assert.Equal(standard ? 50004 : 50033, field.GetProperty("controlTypeId").GetInt32());
        Assert.StartsWith(standard ? "WindowsForms10.EDIT." : "WindowsForms10.Window.", field.GetProperty("fieldClass").GetString(), StringComparison.OrdinalIgnoreCase);
        var fieldRelative = standard ? StandardField : CustomField;
        var labelRelative = standard ? StandardLabel : CustomLabel;
        Assert.Equal(fieldRelative, ReadRectangle(field.GetProperty("fieldRelativeBounds")));
        Assert.Equal(labelRelative, ReadRectangle(field.GetProperty("labelRelativeBounds")));
        fieldRelative.Offset(state.LoginX, state.LoginY);
        labelRelative.Offset(state.LoginX, state.LoginY);
        Assert.Equal(fieldRelative, ReadRectangle(field.GetProperty("fieldBounds")));
        Assert.Equal(labelRelative, ReadRectangle(field.GetProperty("labelBounds")));
    }

    static string ExpectedLoginStructure() => PasswordEvidenceGate.StructureSha256(new EvidenceUi(new[]
    {
        new EvidenceNode(50032, 0, 0, 440, 252, false, true),
        new EvidenceNode(50020, 20, 74, 140, 28, false, true),
        new EvidenceNode(50004, 164, 74, 220, 28, true, true),
        new EvidenceNode(50020, 20, 122, 140, 28, false, true),
        new EvidenceNode(50033, 164, 122, 220, 28, false, true)
    }, false));

    static Rectangle ReadRectangle(JsonElement json) => new(json.GetProperty("x").GetInt32(), json.GetProperty("y").GetInt32(),
        json.GetProperty("width").GetInt32(), json.GetProperty("height").GetInt32());

    static void VerifyStatusOnly(JsonElement reply, string expected)
    {
        var property = Assert.Single(reply.EnumerateObject());
        Assert.Equal("status", property.Name);
        Assert.Equal(expected, property.Value.GetString());
    }

    static void WritePowerShellRunner(string path) => File.WriteAllText(path, """
        param($Program, $Context, $Mode, $ReviewHash, $Field)
        $ErrorActionPreference = 'Stop'
        $parameters = @{
            Executable = $Program
            Target = Get-Content -LiteralPath $Context -Raw | ConvertFrom-Json -AsHashtable -DateKind String
        }
        if ($Field -eq 'custom') {
            $parameters.PublicLabel = '専用カスタム欄:'
            $parameters.FieldClassPrefix = 'WindowsForms10.Window'
        } else {
            $parameters.PublicLabel = '専用標準欄:'
            $parameters.FieldClassPrefix = 'WindowsForms10.EDIT'
        }
        if ($Mode -eq 'diagnostic') { $parameters.DiagnoseWindowLayout = $true }
        if ($ReviewHash) { $parameters.ReviewedWindowLayoutSha256 = $ReviewHash }
        $candidate = Join-Path (Split-Path -Parent $Program) 'scripts/Get-OperationEvidencePasswordLayoutCandidate.ps1'
        & $candidate @parameters | ConvertTo-Json -Depth 12 -Compress
        """);

    static async Task<JsonElement> ReadCandidateAsync(string runner, string executable, string context, string mode, string hash, string field)
    {
        using var shell = StartProcess("pwsh", "-NoProfile", "-File", runner, executable, context, mode, hash, field);
        var output = shell.StandardOutput.ReadToEndAsync();
        var errors = shell.StandardError.ReadToEndAsync();
        try
        {
            await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            string stdout = await output;
            string stderr = await errors;
            foreach (string item in PrivateFixtureText)
            {
                Assert.DoesNotContain(item, stdout, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(item, stderr, StringComparison.OrdinalIgnoreCase);
            }
            Assert.True(shell.ExitCode == 0, stderr);
            Assert.Empty(stderr);
            return JsonSerializer.Deserialize<JsonElement>(stdout);
        }
        finally { await StopProcessAsync(shell); }
    }

    static string PrepareProgram(string destination)
    {
        Directory.CreateDirectory(destination);
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        for (var ancestor = output; ancestor != null; ancestor = ancestor.Parent)
        {
            string source = Path.Combine(ancestor.FullName, "OperationRecorderApp", output.Name);
            if (!File.Exists(Path.Combine(source, "OperationRecorder.exe"))) continue;
            foreach (string file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(destination, "scripts"));
            foreach (string file in Binaries.Where(file => file.StartsWith("scripts/", StringComparison.Ordinal)))
                File.Copy(Path.Combine(source, file), Path.Combine(destination, file));
            return Path.Combine(destination, "OperationRecorder.exe");
        }
        throw new FileNotFoundException("OperationRecorderの開発ビルドがありません。");
    }

    static string[] FileHashes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + " " + EvidenceFiles.HashFile(path)).ToArray();

    static void WriteJson(string path, object value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, EvidenceProtocol.Json));

    static Process StartProcess(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("専用試験プロセスを起動できません。");
    }

    static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    sealed class Fixture(Process process, string pipeName, EvidenceTarget target) : IAsyncDisposable
    {
        public EvidenceTarget Target { get; } = target;

        public static async Task<Fixture> StartAsync()
        {
            string id = Guid.NewGuid().ToString("N");
            var process = StartProcess(ExternalEvidenceProcessTests.FixtureExe, "--password-layout-backdrop", id);
            try
            {
                string started = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var descriptor = JsonDocument.Parse(started);
                Assert.Equal(process.Id, descriptor.RootElement.GetProperty("pid").GetInt32());
                long windowHandle = descriptor.RootElement.GetProperty("hwnd").GetInt64();
                var target = new EvidenceTarget(process.Id, process.StartTime.ToUniversalTime(), process.MainModule.FileName,
                    process.SessionId, windowHandle);
                using var registry = new WindowGraphRegistry();
                Assert.True(registry.MatchesProcess(target));
                return new Fixture(process, "oprec-password-fixture-" + id, target);
            }
            catch
            {
                await StopProcessAsync(process);
                process.Dispose();
                throw;
            }
        }

        public async Task<LayoutState> ChangeAsync(string command)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(command.AsMemory(), deadline.Token);
            var state = JsonSerializer.Deserialize<LayoutState>(await reader.ReadLineAsync(deadline.Token), EvidenceProtocol.Json);
            Assert.Equal(Target.WindowHandle, state.LoginHwnd);
            return state;
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopProcessAsync(process); }
            finally { process.Dispose(); }
        }
    }

    sealed record LayoutState(long LoginHwnd, long LoginOwnerHwnd, bool LoginEnabled, int LoginX, int LoginY,
        long? BackgroundHwnd, long BackgroundOwnerHwnd, bool? BackgroundVisible, bool? BackgroundEnabled,
        bool? BackgroundManagedEnabled, long? BackgroundEditHwnd, long? ExtraHwnd, long? HiddenOwnerHwnd,
        long? BackgroundInvalidBoundsHwnd);

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
}
