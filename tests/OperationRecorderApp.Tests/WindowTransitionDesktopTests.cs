using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

[Collection("管理画面の隔離設定")]
public sealed class WindowTransitionDesktopTests
{
    const string PasswordId = "fixture-transition-password";
    const string RoiId = "fixture-transition-destination";
    const string RoiWithNoticeId = "fixture-destination-notice";
    const string OwnedRoiId = "fixture-transition-notice";
    const string SaveBlockMarker = "fixture-save-blocked.txt";
    const string CandidateScript = "scripts/Get-OperationEvidencePasswordLayoutCandidate.ps1";
    static readonly string[] Binaries = { "OperationRecorder.exe", "OperationRecorder.dll", "scripts/OperationRecorderEvidence.psm1", CandidateScript };
    static readonly Rectangle StandardField = new(164, 74, 220, 28);
    static readonly Rectangle CustomField = new(164, 122, 220, 28);
    static readonly Rectangle Header = new(18, 22, 34, 15);
    static readonly Rectangle Action = new(26, 88, 34, 16);
    static readonly Rectangle Anchor = new(272, 18, 8, 8);

    [Fact, Trait("Category", "Desktop")]
    public async Task 実WGCで別ウィンドウに切り替わり承認時だけ同じ工程を確定する()
    {
        if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1" || !WgcCaptureBackend.IsInputDesktopAvailable())
            throw new InvalidOperationException("DESKTOP_BLOCKED: 専用fixture指定と入力デスクトップへのアクセスが必要です。");

        string artifacts = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
        string root = Path.Combine(artifacts ?? Path.GetTempPath(), "oprec-transition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = PrepareProgram(Path.Combine(root, "program"));
            var cases = new[] { "known", "known-owned", "unrecognized", "unrecognized-owned", "ambiguous", "save-blocked", "deadline-expired", "known-destroyed" };
            var results = new object[cases.Length];
            for (int index = 0; index < cases.Length; index++)
                results[index] = await ExerciseAsync(root, executable, cases[index]);
            var verifiedBinaries = Binaries.Select(file =>
            {
                string path = Path.Combine(Path.GetDirectoryName(executable), file);
                return new { path, sha256 = EvidenceFiles.HashFile(path) };
            }).Append(new { path = ExternalEvidenceProcessTests.FixtureExe,
                sha256 = EvidenceFiles.HashFile(ExternalEvidenceProcessTests.FixtureExe) });
            WriteJson(Path.Combine(root, "result.json"), new { result = "passed", cases = results, verifiedBinaries });
        }
        finally
        {
            if (artifacts == null) Directory.Delete(root, recursive: true);
        }
    }

    static async Task<object> ExerciseAsync(string artifacts, string executable, string mode)
    {
        string fixtureId = Guid.NewGuid().ToString("N");
        using var fixture = StartProcess(ExternalEvidenceProcessTests.FixtureExe, "--window-transition", fixtureId);
        string fixturePipe = "oprec-password-fixture-" + fixtureId;
        try
        {
            string started = await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var descriptor = JsonDocument.Parse(started);
            Assert.Equal(fixture.Id, descriptor.RootElement.GetProperty("pid").GetInt32());
            var target = new EvidenceTarget(fixture.Id, fixture.StartTime.ToUniversalTime(), fixture.MainModule.FileName,
                fixture.SessionId, descriptor.RootElement.GetProperty("hwnd").GetInt64());
            using var registry = new WindowGraphRegistry();
            Assert.True(registry.MatchesProcess(target));
            var (dpi, approvals) = await ReviewStableFixtureAsync(registry, target);
            string program = Path.GetDirectoryName(executable);
            var (password, transitionAfterRoiProfile, expectedUiSha256) = WriteFixtureProfiles(program, target, dpi);
            string run = Path.Combine(artifacts, mode);
            Directory.CreateDirectory(run);
            var response = await RunPowerShellAsync(run, program, executable, new { target, fixturePipe, mode, approvals, password, transitionAfterRoiProfile });

            Assert.Contains("approved-window-transition", response.Capabilities);
            Assert.Equal("before-saved", response.Before.Status);
            Assert.True(response.Before.OperationAllowed, "パスワード保護済みの操作前証拠が必要です。");
            Assert.Equal(1, response.State.ActionCount);
            Assert.Equal(response.State.ActionCount, response.FinalState.ActionCount);
            Assert.Equal(response.State.OriginVisible, response.FinalState.OriginVisible);
            Assert.Equal(response.State.Destinations, response.FinalState.Destinations);
            Assert.False(response.State.OriginVisible);
            Assert.Equal(mode == "known-destroyed", response.State.OriginDestroyed);
            Assert.Equal(response.State.OriginDestroyed, response.FinalState.OriginDestroyed);
            Assert.Equal(!response.State.OriginDestroyed, IsWindow(new IntPtr(target.WindowHandle)));
            Assert.Equal(mode == "ambiguous" ? 2 : 1, response.State.Destinations.Length);
            Assert.DoesNotContain(target.WindowHandle, response.State.Destinations);
            Assert.Equal(mode.EndsWith("-owned", StringComparison.Ordinal), response.State.Notice.HasValue);
            Assert.Equal(response.State.Notice, response.FinalState.Notice);
            Assert.False(response.After.OperationAllowed);
            if (mode != "deadline-expired")
            {
                Assert.Equal(response.Before.StepId, response.After.StepId);
                Assert.Equal(response.Before.StepToken, response.After.StepToken);
            }
            Assert.NotEqual(Guid.Empty, response.Before.StepToken);
            Assert.Equal("inspected", response.Inspection.Status);
            var inspected = Assert.Single(response.Inspection.StepStates);
            Assert.Equal((response.Before.StepId, response.Before.StepToken), (inspected.StepId, inspected.StepToken));
            Assert.True(inspected.BeforeSaved);
            var before = VerifySavedGroup(response.Before, target, "before", dpi);
            object after = null;
            if (mode is "known" or "known-owned" or "known-destroyed")
            {
                Assert.Equal("after-saved", response.After.Status);
                Assert.Equal("success", response.After.Outcome);
                Assert.Empty(response.After.PendingSteps);
                Assert.Equal("completed", inspected.Status);
                Assert.True(inspected.AfterSaved);
                after = VerifySavedGroup(response.After, target with { WindowHandle = Assert.Single(response.State.Destinations) },
                    "after", dpi, response.State.Notice);
                Assert.Equal("duplicate-step", response.RetryEnd.Status);
            }
            else
            {
                string expectedStatus = mode switch
                {
                    "save-blocked" => "output-exists",
                    "deadline-expired" => "deadline-exceeded",
                    _ => "transition-unconfirmed"
                };
                Assert.Equal(expectedStatus, response.After.Status);
                Assert.Equal(response.Before.StepId, Assert.Single(response.After.PendingSteps));
                Assert.Empty(response.After.EvidencePaths);
                Assert.Equal("indeterminate", inspected.Status);
                Assert.False(inspected.AfterSaved);
                string step = Path.GetDirectoryName(Path.GetDirectoryName(before.CommitPath));
                var afterDirectories = Directory.EnumerateDirectories(step, "after*");
                if (mode == "save-blocked")
                {
                    string marker = Assert.Single(Directory.EnumerateFileSystemEntries(Assert.Single(afterDirectories)));
                    Assert.Equal(SaveBlockMarker, Path.GetFileName(marker));
                    Assert.Equal("専用の保存妨害の印", File.ReadAllText(marker));
                }
                else Assert.Empty(afterDirectories);
                Assert.Equal("step-indeterminate", response.RetryEnd.Status);
                Assert.False(response.Next.OperationAllowed);
                Assert.Equal("pending-step", response.Next.Status);
            }
            if (mode == "known")
            {
                var candidates = response.Candidates.EnumerateArray().ToArray();
                Assert.Equal(4, candidates.Length);
                VerifyCandidate(candidates[0], target, dpi, expectedUiSha256, StandardField, 50004);
                VerifyCandidate(candidates[1], target, dpi, expectedUiSha256, CustomField, 50033);
                VerifyRejectedCandidate(candidates[2], "invalid-request");
                VerifyRejectedCandidate(candidates[3], "target-mismatch");
            }
            Assert.False(response.RetryEnd.OperationAllowed);
            return new { mode, pid = target.Pid, originHwnd = target.WindowHandle, dpi, before, after,
                response.After.Status, response.After.Quality, response.After.Outcome, response.After.PendingSteps,
                retryEnd = response.RetryEnd.Status, next = response.Next?.Status, inspected, response.State, response.Candidates };
        }
        finally { await StopProcessAsync(fixture); }
    }

    static string PrepareProgram(string destination)
    {
        Directory.CreateDirectory(destination);
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        for (var ancestor = output; ancestor != null; ancestor = ancestor.Parent)
        {
            string source = Path.Combine(ancestor.FullName, "OperationRecorderApp", output.Name);
            if (!File.Exists(Path.Combine(source, "OperationRecorder.exe"))) continue;
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var file in Binaries.Where(file => file.StartsWith("scripts/", StringComparison.Ordinal)))
            {
                string path = Path.Combine(destination, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(Path.Combine(source, file), path);
            }
            return Path.Combine(destination, "OperationRecorder.exe");
        }
        throw new FileNotFoundException("OperationRecorderの開発ビルドがありません。");
    }

    static (PasswordProfileSelection Password, RoiProfileSelection Destination, string UiSha256)
        WriteFixtureProfiles(string program, EvidenceTarget target, int dpi)
    {
        // Fixed topology, dimensions and reference pixels are reviewed in the dedicated form's source.
        var ui = new EvidenceUi(new[]
        {
            new EvidenceNode(50032, 0, 0, 440, 252, false, true),
            new EvidenceNode(50020, 20, 74, 140, 28, false, true),
            new EvidenceNode(50004, 164, 74, 220, 28, true, true),
            new EvidenceNode(50020, 20, 122, 140, 28, false, true),
            new EvidenceNode(50033, 164, 122, 220, 28, false, true)
        }, false);
        string uiHash = PasswordEvidenceGate.StructureSha256(ui);
        string appHash = EvidenceFiles.HashFile(target.ExePath);
        var scene = new PasswordEvidenceScene(PasswordId, new[] { "before" }, dpi, 440, 252,
            "WindowsForms10.Window.8.", uiHash, new[]
            {
                new PasswordFieldRule("standard", "WindowsForms10.Static.", "専用標準欄:", "WindowsForms10.Edit.", 50004, StandardField, Rectangle.Inflate(StandardField, 4, 4)),
                new PasswordFieldRule("custom", "WindowsForms10.Static.", "専用カスタム欄:", "WindowsForms10.Window.8.", 50033, CustomField, Rectangle.Inflate(CustomField, 4, 4))
            });
        var passwordProfile = new PasswordEvidenceProfile(1, PasswordId, 1, "synthetic-fixture", appHash,
            FileVersionInfo.GetVersionInfo(target.ExePath).FileVersion, new[] { scene });
        var password = new PasswordProfileSelection(PasswordId, 1, WriteAsset("password-evidence", PasswordId, passwordProfile));
        WriteJson(Path.Combine(program, "password-evidence-catalog.json"), new { schemaVersion = 1, profiles = new[] { password } });

        var rootNode = new EvidenceNode(50032, 0, 0, 360, 230, false, true);
        var destinationUi = new EvidenceUi(new[] { rootNode }, false);
        string destinationUiHash = ApprovedRoiGate.StructureSha256(destinationUi);
        // UIA includes the directly owned Form in the root's subtree, clipped to the root's frame.
        var clippedNotice = Rectangle.Intersect(new(0, 0, 360, 230), new(88, 105, 360, 230));
        var destinationWithNotice = new EvidenceUi(new[] { rootNode,
            new EvidenceNode(50032, clippedNotice.X, clippedNotice.Y, clippedNotice.Width, clippedNotice.Height, false, true) }, false);
        string destinationWithNoticeHash = ApprovedRoiGate.StructureSha256(destinationWithNotice);
        ApprovedRoiScene Scene(string id, string role, string hash, Color header, Color action, Color anchor) =>
            new(id, role, new[] { "after" }, dpi, 360, 230, hash, new[]
            {
                Region("header", Header, header, true), Region("action", Action, action, true), Region("anchor", Anchor, anchor, false)
            });
        var scenes = new[] { Scene(RoiId, "root", destinationUiHash, Color.Blue, Color.Lime, Color.Red),
            Scene(RoiWithNoticeId, "root", destinationWithNoticeHash, Color.Blue, Color.Lime, Color.Red),
            Scene(OwnedRoiId, "owned", destinationUiHash, Color.Cyan, Color.Yellow, Color.Orange) };
        var roi = new RoiProfileSelection(RoiId, 1, WriteAsset("approved-roi", RoiId, new ApprovedRoiProfile(1, RoiId, 1, appHash, scenes)));
        WriteJson(Path.Combine(program, "approved-roi-catalog.json"), new { schemaVersion = 1, profiles = new[] { roi } });
        Assert.Equal(PasswordId, PasswordEvidenceCatalog.Load(password, program).ProfileId);
        Assert.Equal(RoiId, ApprovedRoiCatalog.Load(roi, program).ProfileId);
        return (password, roi, uiHash);

        string WriteAsset(string catalog, string id, object profile)
        {
            string assets = Path.Combine(program, catalog + "-assets");
            Directory.CreateDirectory(assets);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, EvidenceProtocol.Json);
            File.WriteAllBytes(Path.Combine(assets, id + ".1.json"), bytes);
            return EvidenceFiles.Hash(bytes);
        }
    }

    static ApprovedRoiRegion Region(string id, Rectangle bounds, Color color, bool publish)
    {
        var bgra = new byte[bounds.Width * bounds.Height * 4];
        for (int index = 0; index < bgra.Length; index += 4)
        {
            bgra[index] = color.B;
            bgra[index + 1] = color.G;
            bgra[index + 2] = color.R;
            bgra[index + 3] = 255;
        }
        return new(id, bounds, bgra, publish);
    }

    static async Task<PowerShellResult> RunPowerShellAsync(string run, string program, string executable, object config)
    {
        string script = Path.Combine(run, "run.ps1");
        string configPath = Path.Combine(run, "fixture.json");
        WriteJson(configPath, config);
        File.WriteAllText(script, """
            param($Program, $Executable, $Config, $OutputDirectory)
            $ErrorActionPreference = 'Stop'
            $case = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json -AsHashtable -DateKind String
            function Send-FixtureCommand([string]$Command) {
                $control = [IO.Pipes.NamedPipeClientStream]::new('.', $case.fixturePipe, [IO.Pipes.PipeDirection]::InOut)
                try {
                    $control.Connect(3000)
                    $writer = [IO.StreamWriter]::new($control)
                    $writer.AutoFlush = $true
                    $reader = [IO.StreamReader]::new($control)
                    $writer.WriteLine($Command)
                    $reader.ReadLine() | ConvertFrom-Json
                } finally { $control.Dispose() }
            }
            $candidates = @()
            if ($case.mode -eq 'known') {
                $candidate = Join-Path $Program 'scripts/Get-OperationEvidencePasswordLayoutCandidate.ps1'
                $files = @(Get-ChildItem -LiteralPath $Program -Recurse -File | Select-Object -ExpandProperty FullName)
                $candidates += & $candidate -Executable $Executable -Target $case.target -PublicLabel '専用標準欄:' -FieldClassPrefix 'WindowsForms10.EDIT'
                $candidates += & $candidate -Executable $Executable -Target $case.target -PublicLabel '専用カスタム欄:' -FieldClassPrefix 'WindowsForms10.Window'
                $candidates += & $candidate -Executable $Executable -Target $case.target -PublicLabel '未登録欄:' -FieldClassPrefix 'WindowsForms10.EDIT'
                $wrongSession = @{} + $case.target
                $wrongSession.windowsSessionId = $case.target.windowsSessionId + 1
                $candidates += & $candidate -Executable $Executable -Target $wrongSession -PublicLabel '専用標準欄:' -FieldClassPrefix 'WindowsForms10.EDIT'
                $remainingFiles = @(Get-ChildItem -LiteralPath $Program -Recurse -File | Select-Object -ExpandProperty FullName)
                if ((Compare-Object $files $remainingFiles) -or (Test-Path -LiteralPath $OutputDirectory)) { throw '設定候補の取得で出力ファイルが変更されました。' }
            }
            Import-Module (Join-Path $Program 'scripts/OperationRecorderEvidence.psm1') -Force
            $policy = @{passwordProfile=$case.password;transitionAfterRoiProfile=$case.transitionAfterRoiProfile;approvals=$case.approvals;maxFrameAgeMs=9000}
            $session = New-OperationEvidenceSession -Executable $Executable -SchemaVersion 2 -Target $case.target -GroupPolicy $policy -OutputDirectory $OutputDirectory
            $next = $null
            try {
                $before = Send-OperationEvidenceRequest -Session $session -Request @{
                    method='BeginStep'; stepId=[guid]::NewGuid().ToString(); operationName='invoke'; expectWindowTransition=$true
                } -TimeoutMilliseconds 12000
                if ($before.status -ne 'before-saved' -or $before.operationAllowed -ne $true) {
                    throw "専用操作を開始できません。$($before | ConvertTo-Json -Depth 12 -Compress)"
                }
                $operation = if ($case.mode -in 'save-blocked', 'deadline-expired') { 'known' } else { $case.mode }
                $state = Send-FixtureCommand ('transition-' + $operation)
                if ($case.mode -eq 'save-blocked') {
                    $step = Split-Path -Parent (Split-Path -Parent $before.evidencePaths[0])
                    $reservation = Join-Path $step 'after'
                    [void](New-Item -ItemType Directory -Path $reservation)
                    Set-Content -LiteralPath (Join-Path $reservation 'fixture-save-blocked.txt') -Value '専用の保存妨害の印' -NoNewline
                }
                $end = @{method='EndStep';stepToken=$before.stepToken;outcome='success'}
                $firstEnd = $end.Clone()
                if ($case.mode -eq 'deadline-expired') { $firstEnd.deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(-3).ToString('o') }
                $after = Send-OperationEvidenceRequest -Session $session -Request $firstEnd -TimeoutMilliseconds 12000
                $retryEnd = Send-OperationEvidenceRequest -Session $session -Request $end
                $inspection = Send-OperationEvidenceRequest -Session $session -Request @{
                    method='InspectSession'; stepId=$before.stepId; outputDirectory=$OutputDirectory
                }
                if ($case.mode -notin 'known', 'known-owned', 'known-destroyed') {
                    $next = Send-OperationEvidenceRequest -Session $session -Request @{
                        method='BeginStep'; stepId=[guid]::NewGuid().ToString(); operationName='invoke'; expectWindowTransition=$true
                    }
                }
                $finalState = Send-FixtureCommand 'transition-status'
            } finally { $null = Close-OperationEvidenceSession -Session $session }
            @{callerPid=$PID;capabilities=@($session.Capabilities);before=$before;after=$after;retryEnd=$retryEnd;inspection=$inspection;next=$next;state=$state;finalState=$finalState;candidates=$candidates} | ConvertTo-Json -Depth 12 -Compress
            """);
        using var process = StartProcess("pwsh", "-NoProfile", "-File", script, program, executable, configPath, Path.Combine(run, "output"));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            string output = await stdout;
            string errors = await stderr;
            File.WriteAllText(Path.Combine(run, "responses.json"), output);
            if (errors.Length > 0) File.WriteAllText(Path.Combine(run, "stderr.log"), errors);
            Assert.True(process.ExitCode == 0, errors);
            var response = JsonSerializer.Deserialize<PowerShellResult>(output, EvidenceProtocol.Json);
            Assert.Equal(process.Id, response.CallerPid);
            Assert.NotEqual(Environment.ProcessId, response.CallerPid);
            return response;
        }
        finally { await StopProcessAsync(process); }
    }

    static void VerifyRejectedCandidate(JsonElement candidate, string status)
    {
        var property = Assert.Single(candidate.EnumerateObject());
        Assert.Equal("status", property.Name);
        Assert.Equal(status, property.Value.GetString());
    }

    static CaptureResult VerifySavedGroup(EvidenceReply reply, EvidenceTarget target, string phase, int dpi, long? noticeHwnd = null)
    {
        string commitPath = Assert.Single(reply.EvidencePaths);
        string directory = Path.GetDirectoryName(commitPath);
        var commit = ReadJson<EvidenceCommit>(commitPath);
        string groupPath = Path.Combine(directory, "group.json");
        Assert.Equal(commit.GroupSha256, EvidenceFiles.HashFile(groupPath));
        var group = ReadJson<GroupEvidenceManifest>(groupPath);
        Assert.Equal((reply.SessionId, reply.StepId, reply.StepToken, phase), (commit.SessionId, commit.StepId, commit.StepToken, commit.Phase));
        Assert.Equal((commit.SessionId, commit.StepId, commit.StepToken, phase), (group.SessionId, group.StepId, group.StepToken, group.Phase));
        Assert.True(reply.Quality.RequirementsMet);
        Assert.True(group.Quality.RequirementsMet);
        var row = Assert.Single(group.Windows, window => window.Role == "root");
        Assert.Equal(target.WindowHandle, row.Window.Hwnd);
        bool before = phase == "before";
        if (before)
        {
            Assert.Equal(PasswordId, row.PasswordMask.SceneId);
            Assert.Equal(2, row.PasswordMask.Fields.Length);
            Assert.Equal("password-masked", row.DisclosureStatus);
        }
        else
        {
            Assert.Equal(noticeHwnd.HasValue ? RoiWithNoticeId : RoiId, row.ApprovedSceneId);
            Assert.Equal("public-regions", row.DisclosureStatus);
            Assert.Equal(target, ReadJson<GroupCapturePlan>(Path.Combine(directory, "plan.json")).TransitionAfterTarget);
        }
        string rootImage = VerifyWindowFiles(row);
        string phaseImage = Path.Combine(directory, "frame.png");
        Assert.Equal(commit.ImageSha256, EvidenceFiles.HashFile(phaseImage));
        Assert.Equal(row.ImageSha256, commit.ImageSha256);
        Assert.Equal(commit.UiSha256, EvidenceFiles.HashFile(Path.Combine(directory, "ui.json")));
        VerifyPixels(rootImage, before, Color.Blue, Color.Lime);
        var images = new[] { phaseImage, rootImage };
        WindowCapture notice = null;
        if (noticeHwnd.HasValue)
        {
            var owned = Assert.Single(group.Windows, window => window.Role == "owned");
            Assert.Equal((noticeHwnd.Value, target.WindowHandle), (owned.Window.Hwnd, owned.Window.OwnerHwnd));
            Assert.Equal("owned", owned.Window.Relation);
            Assert.Equal(OwnedRoiId, owned.ApprovedSceneId);
            Assert.Equal("public-regions", owned.DisclosureStatus);
            string ownedImage = VerifyWindowFiles(owned);
            VerifyPixels(ownedImage, false, Color.Cyan, Color.Yellow);
            notice = new(owned.Window.Hwnd, ownedImage, owned.ImageSha256);
            images = images.Append(ownedImage).ToArray();
        }
        Assert.Equal(images.Select(Path.GetFullPath).Order(),
            Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories).Order());
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(path);
            VerifyNoFixtureValues(Encoding.UTF8.GetString(bytes));
            VerifyNoFixtureValues(Encoding.Unicode.GetString(bytes));
        }
        return new(commitPath, rootImage, row.ImageSha256, reply.StepId, reply.StepToken, row.DisclosureStatus, row.ReasonCode, target.WindowHandle, notice);

        string VerifyWindowFiles(WindowEvidence window)
        {
            Assert.Equal((target.Pid, dpi), (window.Window.Pid, window.Window.Dpi));
            Assert.Equal("wgc-window", window.CaptureMethod);
            Assert.Equal("fresh", window.CaptureStatus);
            Assert.True(window.FrameTime100ns > 0);
            Assert.True(window.RequirementsMet);
            string imagePath = Path.GetFullPath(Path.Combine(directory, window.ImageFile));
            Assert.Equal(window.ImageSha256, EvidenceFiles.HashFile(imagePath));
            string uiPath = Path.Combine(directory, window.UiFile);
            Assert.Equal(window.UiSha256, EvidenceFiles.HashFile(uiPath));
            Assert.False(ReadJson<EvidenceUi>(uiPath).TextRead);
            return imagePath;
        }
    }

    static void VerifyPixels(string path, bool before, Color header, Color action)
    {
        using var file = File.OpenRead(path);
        using var image = new Bitmap(file);
        if (before)
        {
            Assert.Equal(new Size(440, 252), image.Size);
            Assert.Equal(header.ToArgb(), image.GetPixel(24, 21).ToArgb());
            Assert.Equal(Color.White.ToArgb(), image.GetPixel(411, 164).ToArgb());
        }
        else Assert.Equal(new Size(360, 230), image.Size);
        Rectangle standard = Rectangle.Inflate(StandardField, 4, 4);
        Rectangle custom = Rectangle.Inflate(CustomField, 4, 4);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int pixel = image.GetPixel(x, y).ToArgb();
                if (!before || standard.Contains(x, y) || custom.Contains(x, y))
                {
                    var expected = before ? Color.Black : Header.Contains(x, y) ? header : Action.Contains(x, y) ? action : Color.Black;
                    Assert.Equal(expected.ToArgb(), pixel);
                }
                Assert.NotEqual(Color.Magenta.ToArgb(), pixel);
            }
    }

    static void VerifyCandidate(JsonElement candidate, EvidenceTarget target, int dpi, string uiHash, Rectangle bounds, int controlType)
    {
        VerifyNoFixtureValues(candidate.GetRawText());
        Assert.Equal(new[] { "appFileVersion", "appSha256", "captureBounds", "dpi", "field", "scope", "status", "uiSha256", "windowClass" },
            candidate.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("candidate-only", candidate.GetProperty("status").GetString());
        Assert.Equal("synthetic-fixture", candidate.GetProperty("scope").GetString());
        Assert.Equal(EvidenceFiles.HashFile(target.ExePath), candidate.GetProperty("appSha256").GetString());
        Assert.Equal(FileVersionInfo.GetVersionInfo(target.ExePath).FileVersion, candidate.GetProperty("appFileVersion").GetString());
        Assert.Equal(dpi, candidate.GetProperty("dpi").GetInt32());
        Assert.Equal(uiHash, candidate.GetProperty("uiSha256").GetString());
        var capture = candidate.GetProperty("captureBounds");
        Assert.Equal(440, capture.GetProperty("width").GetInt32());
        Assert.Equal(252, capture.GetProperty("height").GetInt32());
        var field = candidate.GetProperty("field");
        Assert.Equal(new[] { "controlTypeId", "fieldBounds", "fieldClass", "fieldRelativeBounds", "labelBounds", "labelClass", "labelRelativeBounds" },
            field.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(controlType, field.GetProperty("controlTypeId").GetInt32());
        Assert.Equal(bounds, RectangleOf(field.GetProperty("fieldRelativeBounds")));
        Assert.Equal(new Rectangle(20, bounds.Y, 140, 28), RectangleOf(field.GetProperty("labelRelativeBounds")));

        static Rectangle RectangleOf(JsonElement box) => new(box.GetProperty("x").GetInt32(), box.GetProperty("y").GetInt32(),
            box.GetProperty("width").GetInt32(), box.GetProperty("height").GetInt32());
    }

    static void VerifyNoFixtureValues(string text)
    {
        Assert.DoesNotContain("OPREC_STANDARD_FAKE_PASSWORD", text);
        Assert.DoesNotContain("OPREC_CUSTOM_FAKE_PASSWORD", text);
        Assert.DoesNotContain("2031-02-03", text);
    }

    static async Task<(int Dpi, WindowApproval[] Approvals)> ReviewStableFixtureAsync(WindowGraphRegistry registry, EvidenceTarget target)
    {
        var watch = Stopwatch.StartNew();
        long? revision = null;
        long stableSince = 0;
        while (watch.ElapsedMilliseconds < 5000)
        {
            var graph = registry.Snapshot(target, 32);
            var root = Assert.Single(graph.Windows, window => window.Hwnd == target.WindowHandle);
            if (!graph.Truncated && root.Visible && graph.Revision == revision && watch.ElapsedMilliseconds - stableSince >= 250)
            {
                Assert.Equal(new Size(440, 252), root.Bounds.Size);
                Assert.True(root.Dpi > 0);
                Assert.Equal(4, graph.Windows.Count(window => window.IsChild));
                return (root.Dpi, graph.Windows.Where(window => !window.IsChild).Select(window => ReviewWindow(window, target)).ToArray());
            }
            if (graph.Truncated || graph.Revision != revision) stableSince = watch.ElapsedMilliseconds;
            revision = graph.Truncated ? null : graph.Revision;
            await Task.Delay(40);
        }
        throw new TimeoutException("専用fixtureのウィンドウ構造が安定しませんでした。");
    }

    static WindowApproval ReviewWindow(WindowObservation window, EvidenceTarget target)
    {
        Assert.Equal(target.Pid, window.Pid);
        if (window.Hwnd == target.WindowHandle) return new(window.Hwnd, "root");
        Assert.False(window.Visible);
        var className = new StringBuilder(256);
        Assert.True(GetClassName(new IntPtr(window.Hwnd), className, className.Capacity) > 0);
        string value = className.ToString();
        Assert.True(value is "IME" or "GDI+ Hook Window Class" ||
            value.StartsWith(".NET-BroadcastEventWindow.", StringComparison.Ordinal), "未知の専用fixture補助クラス: " + value);
        return new(window.Hwnd, "fixture-support", Required: false);
    }

    static Process StartProcess(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("専用検証プロセスの起動に失敗しました。");
    }

    static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), EvidenceProtocol.Json);
    static void WriteJson(string path, object value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, EvidenceProtocol.Json));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder name, int count);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr window);

    sealed record FixtureState(int ActionCount, bool OriginVisible, bool OriginDestroyed, long[] Destinations, long? Notice);
    sealed record PowerShellResult(int CallerPid, string[] Capabilities, EvidenceReply Before, EvidenceReply After,
        EvidenceReply RetryEnd, EvidenceReply Inspection, EvidenceReply Next, FixtureState State, FixtureState FinalState, JsonElement Candidates);
    sealed record CaptureResult(string CommitPath, string ImagePath, string ImageSha256, Guid StepId, Guid StepToken,
        string DisclosureStatus, string ReasonCode, long Hwnd, WindowCapture Notice);
    sealed record WindowCapture(long Hwnd, string ImagePath, string ImageSha256);
}
