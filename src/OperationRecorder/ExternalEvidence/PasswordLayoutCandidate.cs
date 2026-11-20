using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp.ExternalEvidence;

internal sealed record PasswordLayoutCandidateRequest(EvidenceTarget Target, string PublicLabel, string FieldClassPrefix,
    bool DiagnoseWindowLayout = false, string ReviewedWindowLayoutSha256 = null);

internal sealed record PasswordLayoutCandidateWork(PasswordLayoutCandidateRequest Request, DateTimeOffset DeadlineUtc);

internal sealed record PasswordLayoutCandidateField(string FieldClass, string LabelClass, int ControlTypeId,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle FieldBounds,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle LabelBounds,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle FieldRelativeBounds,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle LabelRelativeBounds);

internal sealed record PasswordLayoutCandidateReply(string Status, string Scope = null, string AppSha256 = null,
    string AppFileVersion = null, int? Dpi = null,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle CaptureBounds = default,
    string UiSha256 = null, string WindowClass = null, PasswordLayoutCandidateField Field = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PasswordLayoutDiagnostics Diagnostics = null);

/// <summary>Isolated inspection of a predeclared public label and its password-layout geometry.</summary>
public static class PasswordLayoutCandidate
{
    const string XronosLabel = "パスワード:";
    const string StandardFixtureLabel = "専用標準欄:";
    const string CustomFixtureLabel = "専用カスタム欄:";
    const string WinFormsEdit = "WindowsForms10.EDIT";
    const string WinFormsCustom = "WindowsForms10.Window";
    const int UiaEdit = 50004, UiaCustom = 50025, UiaPane = 50033;
    const string InvalidRequest = "invalid-request", TargetMismatch = "target-mismatch", LayoutUnproven = "layout-unproven",
        DeadlineExceeded = "deadline-exceeded", CandidateOnly = "candidate-only", DiagnosticOnly = "diagnostic-only";

    /// <summary>Starts a one-request process and closes its kill-on-close job when the request ends.</summary>
    public static async Task<string> ObserveJsonAsync(string executable, string requestJson, int timeoutMilliseconds)
    {
        PasswordLayoutCandidateRequest request;
        if (timeoutMilliseconds is < 200 or > 30000 || requestJson is not { Length: > 0 and <= 4096 })
            return Serialize(new(InvalidRequest));
        using var deadline = new CancellationTokenSource(timeoutMilliseconds);
        var until = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        try { request = JsonSerializer.Deserialize<PasswordLayoutCandidateRequest>(requestJson, ApprovedRoiCatalog.StrictJson); }
        catch (JsonException) { return Serialize(new(InvalidRequest)); }
        string invalid = Validate(request, out _);
        if (invalid != null) return Serialize(new(invalid));

        using var process = new Process();
        try
        {
            EvidenceFiles.RequireLocalUnlinkedPath(executable);
            var info = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("--evidence-layout-candidate");
            EvidenceProcessEnvironment.Scrub(info);
            process.StartInfo = info;
            if (!process.Start()) return Serialize(new(LayoutUnproven));
            using var job = new EvidenceProcessJob(process);
            var token = deadline.Token;
            var discardErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            await EvidenceWire.WriteAsync(process.StandardInput.BaseStream, new PasswordLayoutCandidateWork(request, until), token).WaitAsync(token);
            process.StandardInput.Close();
            var result = await EvidenceWire.ReadAsync<PasswordLayoutCandidateReply>(process.StandardOutput.BaseStream, token).WaitAsync(token);
            await process.WaitForExitAsync(token);
            await discardErrors.WaitAsync(token);
            if (token.IsCancellationRequested || DateTimeOffset.UtcNow >= until) return Serialize(new(DeadlineExceeded));
            return Serialize(process.ExitCode == 0 ? result : new(LayoutUnproven));
        }
        catch (OperationCanceledException) { return Serialize(new(DeadlineExceeded)); }
        catch { return Serialize(new(LayoutUnproven)); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }

    static string Serialize(PasswordLayoutCandidateReply reply) => reply.Status switch
    {
        CandidateOnly => JsonSerializer.Serialize(reply with { Diagnostics = null }, EvidenceProtocol.Json),
        DiagnosticOnly => JsonSerializer.Serialize(
            new { reply.Status, reply.Scope, reply.AppSha256, reply.AppFileVersion, reply.Diagnostics }, EvidenceProtocol.Json),
        _ => JsonSerializer.Serialize(new { status = reply.Status }, EvidenceProtocol.Json)
    };

    internal static async Task<int> RunAsync()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        PasswordLayoutCandidateWork work;
        using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var body = await EvidenceWire.ReadAsync<JsonElement>(input, readDeadline.Token).WaitAsync(readDeadline.Token);
            work = body.Deserialize<PasswordLayoutCandidateWork>(ApprovedRoiCatalog.StrictJson);
            if (work == null) throw new JsonException();
        }
        catch (OperationCanceledException) { return 2; }
        catch
        {
            await EvidenceWire.WriteAsync(output, new PasswordLayoutCandidateReply(InvalidRequest), CancellationToken.None);
            return 0;
        }
        var remaining = work.DeadlineUtc - DateTimeOffset.UtcNow;
        PasswordLayoutCandidateReply reply;
        if (remaining <= TimeSpan.Zero) reply = new(DeadlineExceeded);
        else if (remaining > TimeSpan.FromSeconds(30)) reply = new(InvalidRequest);
        else
        {
            using var deadline = new CancellationTokenSource(remaining);
            try
            {
                reply = Observe(work.Request, deadline.Token);
                if (DateTimeOffset.UtcNow >= work.DeadlineUtc) reply = new(DeadlineExceeded);
            }
            catch (OperationCanceledException) { reply = new(DeadlineExceeded); }
            catch (EvidenceUnavailableException exception) when (exception.Code == TargetMismatch) { reply = new(TargetMismatch); }
            catch { reply = new(LayoutUnproven); }
        }
        await EvidenceWire.WriteAsync(output, reply, CancellationToken.None);
        return 0;
    }

    static PasswordLayoutCandidateReply Observe(PasswordLayoutCandidateRequest request, CancellationToken token)
    {
        string invalid = Validate(request, out var scope);
        if (invalid != null) return new(invalid);
        var target = request.Target;
        using var registry = new WindowGraphRegistry();
        if (!registry.MatchesProcess(target)) return new(TargetMismatch);
        EvidenceFiles.RequireLocalUnlinkedPath(target.ExePath);
        // Retain the executable while checking identity and layout so its contents cannot be replaced mid-query.
        using var image = File.Open(target.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string version = FileVersionInfo.GetVersionInfo(target.ExePath).FileVersion;
        if (version is not { Length: > 0 and <= 40 } || !Version.TryParse(version, out var parsed) || parsed.Revision < 0)
            return new(TargetMismatch);
        string sha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        token.ThrowIfCancellationRequested();

        var graph = registry.Snapshot(target, 256);
        if (request.DiagnoseWindowLayout || request.ReviewedWindowLayoutSha256 != null)
            return ObserveBackdrop(request, registry, graph, scope, sha256, version, token);

        var window = graph.Windows.SingleOrDefault(candidate => candidate.Hwnd == target.WindowHandle);
        var observation = PasswordWindowObserver.ObserveLayout(request, window, graph, token);
        if (observation == null) return new(LayoutUnproven);
        var check = registry.Snapshot(target, 256);
        if (!PasswordGroupEvidenceWorker.SameGroup(graph, check) ||
            observation != PasswordWindowObserver.ObserveLayout(request, window, check, token) ||
            !PasswordGroupEvidenceWorker.SameGroup(graph, registry.Snapshot(target, 256))) return new(LayoutUnproven);
        token.ThrowIfCancellationRequested();

        return new(CandidateOnly, scope, sha256, version, window.Dpi, window.Bounds,
            observation.UiSha256, observation.WindowClass, observation.Field);
    }

    static PasswordLayoutCandidateReply ObserveBackdrop(PasswordLayoutCandidateRequest request, WindowGraphRegistry registry,
        WindowGraphSnapshot graph, string scope, string sha256, string version, CancellationToken token)
    {
        PasswordLayoutCandidateReply Diagnostic(PasswordLayoutDiagnostics diagnostics) => request.DiagnoseWindowLayout
            ? new(DiagnosticOnly, scope, sha256, version, Diagnostics: diagnostics)
            : new(LayoutUnproven);
        if (graph.Truncated)
            return Diagnostic(new("window-layout-unavailable", Change: new("first-snapshot", new[] { "snapshot-truncated" })));

        var (first, change) = PasswordLayoutBackdrop.ObserveStable(graph, () => registry.Snapshot(request.Target, 256),
            observed => PasswordWindowObserver.ObserveBackdrop(request, observed, sha256, version, token),
            PasswordLayoutBackdrop.CompareObservations);
        if (change != null) return Diagnostic(new("window-layout-changed", Change: change));
        token.ThrowIfCancellationRequested();

        if (first.Layout == null) return Diagnostic(first.Diagnostics);
        var login = first.Diagnostics.Login;
        return new(CandidateOnly, scope, sha256, version, login.Dpi, login.CaptureBounds,
            first.Layout.UiSha256, first.Layout.WindowClass, first.Layout.Field);
    }

    static string Validate(PasswordLayoutCandidateRequest request, out string scope)
    {
        scope = null;
        var target = request?.Target;
        if (target == null || target.Pid <= 0 || target.WindowsSessionId < 0 || target.WindowHandle <= 0 ||
            target.ProcessStartTimeUtc == default || string.IsNullOrWhiteSpace(target.ExePath) ||
            request.ReviewedWindowLayoutSha256 != null &&
                (request.DiagnoseWindowLayout || !EvidenceProtocol.IsHash(request.ReviewedWindowLayoutSha256)) ||
            request.FieldClassPrefix is not (WinFormsEdit or WinFormsCustom or "Edit")) return InvalidRequest;
        try
        {
            EvidenceFiles.RequireLocalUnlinkedPath(target.ExePath);
            string path = Path.GetFullPath(target.ExePath);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles) && string.Equals(path,
                Path.Combine(programFiles, "Xronos", "PerformanceCloud", "Bin", "Xronos.TimeLaborManagement.exe"),
                StringComparison.OrdinalIgnoreCase))
            {
                if (request.PublicLabel != XronosLabel) return InvalidRequest;
                scope = "xronos-local";
            }
            else if (string.Equals(Path.GetFileName(path), "OperationRecorder.EvidenceFixture.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (request.PublicLabel == StandardFixtureLabel && request.FieldClassPrefix is WinFormsEdit or "Edit" ||
                    request.PublicLabel == CustomFixtureLabel && request.FieldClassPrefix == WinFormsCustom)
                    scope = "synthetic-fixture";
                else return InvalidRequest;
            }
            else return TargetMismatch;
        }
        catch { return TargetMismatch; }
        return null;
    }

    internal static bool IsNativeClass(string value) => value is { Length: >= 4 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '#');

    internal static bool MatchesField(string selector, string actual, EvidenceNode node) => selector switch
    {
        WinFormsEdit => actual.StartsWith(WinFormsEdit + ".", StringComparison.OrdinalIgnoreCase) &&
            node.ControlTypeId == UiaEdit && node.IsPassword,
        "Edit" => string.Equals(actual, "Edit", StringComparison.OrdinalIgnoreCase) &&
            node.ControlTypeId == UiaEdit && node.IsPassword,
        WinFormsCustom => actual.StartsWith(WinFormsCustom + ".", StringComparison.OrdinalIgnoreCase) &&
            node.ControlTypeId is UiaEdit or UiaCustom or UiaPane,
        _ => false
    };
}
