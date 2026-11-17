using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace OperationRecorderApp.ExternalEvidence;

public static class EvidenceFiles
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    public static void RequireLocalUnlinkedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.IndexOf(':', 2) >= 0 || new DriveInfo(Path.GetPathRoot(path)).DriveType != DriveType.Fixed)
            throw new EvidenceUnavailableException("invalid-output");
        for (string p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new EvidenceUnavailableException("invalid-output");
    }
    public static void AtomicJson<T>(string path, T value) => AtomicBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, EvidenceProtocol.Json));
    public static void AtomicBytes(string path, byte[] bytes)
    {
        RequireLocalUnlinkedPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, true);
    }
    // The new strict API never sends unredacted pixels or Name/Value/Text to a serializer/encoder.
    // Construct a fresh opaque image; no raw image is written to a temporary file.
    public static byte[] OpaquePng(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 4096 || size.Height > 4096 || (long)size.Width * size.Height > 4194304)
            throw new EvidenceUnavailableException("capture-unavailable");
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Black);
        using var output = new MemoryStream(); bitmap.Save(output, ImageFormat.Png); return output.ToArray();
    }
    public static string Save(CaptureWork work, byte[] protectedPng, EvidenceUi safeUi, DateTimeOffset capturedAt, byte[] protectedVideo = null, long? timelineOffsetMs = null)
    {
        if (DateTimeOffset.UtcNow >= work.DeadlineUtc) throw new EvidenceUnavailableException("deadline-exceeded");
        RequireLocalUnlinkedPath(work.Directory);
        Directory.CreateDirectory(work.Directory);
        var assembly = typeof(EvidenceFiles).Assembly;
        var uiBytes = JsonSerializer.SerializeToUtf8Bytes(safeUi, EvidenceProtocol.Json);
        AtomicBytes(Path.Combine(work.Directory, "frame.png"), protectedPng);
        AtomicBytes(Path.Combine(work.Directory, "ui.json"), uiBytes);
        if (protectedVideo != null) AtomicBytes(Path.Combine(work.Directory, "frame.mp4"), protectedVideo);
        var commit = new EvidenceCommit(1, work.SessionId, work.StepId, work.StepToken, work.Phase, capturedAt, DateTimeOffset.UtcNow,
            timelineOffsetMs ?? (long)(capturedAt - work.SessionStartedUtc).TotalMilliseconds,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
            HashFile(string.IsNullOrEmpty(assembly.Location) ? Environment.ProcessPath : assembly.Location), Hash(protectedPng), Hash(uiBytes), protectedVideo == null ? null : Hash(protectedVideo));
        string path = Path.Combine(work.Directory, "commit.json"); AtomicJson(path, commit); return path;
    }
    public static EvidenceCommit Verify(CaptureWork work)
    {
        RequireLocalUnlinkedPath(work.Directory);
        var commit = JsonSerializer.Deserialize<EvidenceCommit>(File.ReadAllBytes(Path.Combine(work.Directory, "commit.json")), EvidenceProtocol.Json);
        if (commit == null || commit.SchemaVersion != 1 || commit.SessionId != work.SessionId || commit.StepId != work.StepId ||
            commit.StepToken != work.StepToken || commit.Phase != work.Phase ||
            commit.ImageSha256 != HashFile(Path.Combine(work.Directory, "frame.png")) ||
            commit.UiSha256 != HashFile(Path.Combine(work.Directory, "ui.json")) ||
            (work.Policy.RecordVideo && (commit.VideoSha256 == null || commit.VideoSha256 != HashFile(Path.Combine(work.Directory, "frame.mp4")))))
            throw new EvidenceUnavailableException("save-unconfirmed");
        return commit;
    }
}
