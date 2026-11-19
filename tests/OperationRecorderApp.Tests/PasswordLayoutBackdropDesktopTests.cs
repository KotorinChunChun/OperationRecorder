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
        "OPREC_FIXTURE_BACKDROP_PASSWORD_NAME", "OPREC_FIXTURE_BACKDROP_PASSWORD_VALUE"
    };
    static readonly Rectangle StandardField = new(164, 74, 220, 28), CustomField = new(164, 122, 220, 28);
    static readonly Rectangle StandardLabel = new(20, 74, 140, 28), CustomLabel = new(20, 122, 140, 28);

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
        long? BackgroundHwnd, long BackgroundOwnerHwnd, bool? BackgroundVisible, bool? BackgroundEnabled, long? ExtraHwnd, long? HiddenOwnerHwnd);

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
}
