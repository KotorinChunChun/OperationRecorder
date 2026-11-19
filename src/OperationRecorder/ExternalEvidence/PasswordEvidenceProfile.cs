using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OperationRecorderApp.ExternalEvidence;

public sealed record PasswordProfileSelection(string ProfileId, int Revision, string ProfileSha256);

public sealed record PasswordEvidenceProfile(int SchemaVersion, string ProfileId, int Revision, string AppScope,
    string AppSha256, string AppFileVersion, PasswordEvidenceScene[] Scenes);

public sealed record PasswordEvidenceScene(string SceneId, string[] Phases, int Dpi, int WindowWidth,
    int WindowHeight, string WindowClassPrefix, string UiSha256, PasswordFieldRule[] Fields);

public sealed record PasswordFieldRule(string FieldId, string LabelClassPrefix, string PublicLabel,
    string FieldClassPrefix, int FieldControlTypeId,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle FieldBounds,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle Mask);

public static class PasswordEvidenceCatalog
{
    const int FileLimit = 256 * 1024;
    const int CatalogLimit = 256;
    const int SceneLimit = 64;
    const int FieldLimit = 16;
    const int Margin = 4;
    const string Invalid = "password-profile-invalid";
    const string Unavailable = "password-profile-unavailable";
    const string Mismatch = "password-profile-mismatch";

    sealed record Catalog(int SchemaVersion, PasswordProfileSelection[] Profiles);

    public static PasswordEvidenceProfile Load(PasswordProfileSelection selection) => Load(selection, AppContext.BaseDirectory);

    // Explicit base directory is for isolated tests and offline enrollment checks, never the wire request.
    public static PasswordEvidenceProfile Load(PasswordProfileSelection selection, string trustedBaseDirectory)
    {
        if (!ValidSelection(selection)) throw new EvidenceUnavailableException(Invalid);
        try
        {
            EvidenceFiles.RequireLocalUnlinkedPath(trustedBaseDirectory);
            var catalogPath = Path.Combine(trustedBaseDirectory, "password-evidence-catalog.json");
            var catalog = JsonSerializer.Deserialize<Catalog>(Read(catalogPath), ApprovedRoiCatalog.StrictJson);
            if (!ValidCatalog(catalog)) throw new EvidenceUnavailableException(Invalid);
            var entry = catalog.Profiles.FirstOrDefault(candidate =>
                candidate.ProfileId == selection.ProfileId && candidate.Revision == selection.Revision);
            if (entry == null) throw new EvidenceUnavailableException(Unavailable);
            if (!SameHash(entry.ProfileSha256, selection.ProfileSha256)) throw new EvidenceUnavailableException(Mismatch);

            string file = FormattableString.Invariant($"{entry.ProfileId}.{entry.Revision}.json");
            var profileBytes = Read(Path.Combine(trustedBaseDirectory, "password-evidence-assets", file));
            if (!SameHash(EvidenceFiles.Hash(profileBytes), entry.ProfileSha256)) throw new EvidenceUnavailableException(Mismatch);
            var profile = JsonSerializer.Deserialize<PasswordEvidenceProfile>(profileBytes, ApprovedRoiCatalog.StrictJson);
            if (!IsValidProfile(profile)) throw new EvidenceUnavailableException(Invalid);
            if (profile.ProfileId != entry.ProfileId || profile.Revision != entry.Revision)
                throw new EvidenceUnavailableException(Mismatch);
            return profile;
        }
        catch (EvidenceUnavailableException error) when (error.Code == "invalid-output")
        {
            throw new EvidenceUnavailableException(Invalid);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            throw new EvidenceUnavailableException(Invalid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new EvidenceUnavailableException(Unavailable);
        }
    }

    /// <summary>Checks the trusted profile against a locally resolved executable and its measured SHA256.</summary>
    public static bool MatchesApplication(PasswordEvidenceProfile profile, string executablePath, string actualAppSha256)
    {
        if (!IsValidProfile(profile) || !SameHash(profile.AppSha256, actualAppSha256)) return false;
        try
        {
            EvidenceFiles.RequireLocalUnlinkedPath(executablePath);
            string path = Path.GetFullPath(executablePath);
            bool approvedPath = profile.AppScope switch
            {
                "xronos-local" => string.Equals(path, XronosExecutable(), StringComparison.OrdinalIgnoreCase),
                "synthetic-fixture" => string.Equals(Path.GetFileName(path), "OperationRecorder.EvidenceFixture.exe", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            return approvedPath && string.Equals(profile.AppFileVersion, FileVersionInfo.GetVersionInfo(path).FileVersion, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static string XronosExecutable()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return string.IsNullOrEmpty(programFiles) ? null : Path.Combine(programFiles, "Xronos", "PerformanceCloud", "Bin", "Xronos.TimeLaborManagement.exe");
    }

    static byte[] Read(string path) => ApprovedRoiCatalog.ReadBounded(path, FileLimit, Invalid);

    static bool ValidSelection(PasswordProfileSelection selection) => selection != null &&
        ApprovedRoiCatalog.ValidId(selection.ProfileId) && selection.Revision > 0 && EvidenceProtocol.IsHash(selection.ProfileSha256);

    static bool ValidCatalog(Catalog catalog)
    {
        if (catalog?.SchemaVersion != 1 || catalog.Profiles == null || catalog.Profiles.Length > CatalogLimit) return false;
        var keys = new HashSet<(string, int)>();
        return catalog.Profiles.All(profile => ValidSelection(profile) && keys.Add((profile.ProfileId, profile.Revision)));
    }

    internal static bool IsValidProfile(PasswordEvidenceProfile profile)
    {
        if (profile?.SchemaVersion != 1 || !ApprovedRoiCatalog.ValidId(profile.ProfileId) || profile.Revision <= 0 ||
            profile.AppScope is not ("xronos-local" or "synthetic-fixture") || !EvidenceProtocol.IsHash(profile.AppSha256) ||
            !ValidPublicString(profile.AppFileVersion, 40) || !Version.TryParse(profile.AppFileVersion, out var version) || version.Revision < 0 ||
            profile.Scenes is not { Length: >= 1 and <= SceneLimit }) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return profile.Scenes.All(scene => IsValidScene(scene) && ids.Add(scene.SceneId));
    }

    internal static bool IsValidScene(PasswordEvidenceScene scene)
    {
        if (scene == null || !ApprovedRoiCatalog.ValidId(scene.SceneId) || scene.Dpi is < 1 or > 960 ||
            scene.WindowWidth is < 1 or > 4096 || scene.WindowHeight is < 1 or > 4096 ||
            (long)scene.WindowWidth * scene.WindowHeight > 4_194_304 || !ValidClassPrefix(scene.WindowClassPrefix) ||
            !EvidenceProtocol.IsHash(scene.UiSha256) || scene.Phases is not { Length: >= 1 and <= 4 } ||
            scene.Phases.Distinct(StringComparer.Ordinal).Count() != scene.Phases.Length ||
            scene.Phases.Any(phase => phase is not ("before" or "after" or "snapshot" or "failure")) ||
            scene.Fields is not { Length: >= 1 and <= FieldLimit }) return false;

        var frame = new Rectangle(0, 0, scene.WindowWidth, scene.WindowHeight);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var masks = new List<Rectangle>();
        foreach (var field in scene.Fields)
        {
            if (field == null || !ApprovedRoiCatalog.ValidId(field.FieldId) || !ids.Add(field.FieldId) ||
                !ValidClassPrefix(field.LabelClassPrefix) || !ValidPublicString(field.PublicLabel, 80) || !labels.Add(field.PublicLabel) ||
                !ValidClassPrefix(field.FieldClassPrefix) || field.FieldControlTypeId is < 50000 or > 50100 ||
                !Within(field.FieldBounds, frame) || !Within(field.Mask, frame) ||
                !field.Mask.Contains(Rectangle.Inflate(field.FieldBounds, Margin, Margin)) ||
                masks.Any(mask => mask.IntersectsWith(field.Mask))) return false;
            masks.Add(field.Mask);
        }
        return true;
    }

    internal static bool Within(Rectangle area, Rectangle frame) => area.X >= frame.X && area.Y >= frame.Y &&
        area.Width > 0 && area.Height > 0 && (long)area.X + area.Width <= frame.Right && (long)area.Y + area.Height <= frame.Bottom;

    static bool ValidClassPrefix(string value) => value is { Length: >= 4 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    static bool ValidPublicString(string value, int maximumLength) => value != null && value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    static bool SameHash(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
