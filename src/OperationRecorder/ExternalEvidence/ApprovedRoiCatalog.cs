using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OperationRecorderApp.ExternalEvidence;

public sealed record RoiProfileSelection(string ProfileId, int Revision, string ProfileSha256);

public sealed record ApprovedRoiProfile(int SchemaVersion, string ProfileId, int Revision, string AppSha256,
    ApprovedRoiScene[] Scenes);

public sealed record ApprovedRoiScene(string SceneId, string Role, string[] Phases, int Dpi, int WindowWidth,
    int WindowHeight, string UiSha256, ApprovedRoiRegion[] Regions);

public sealed record ApprovedRoiRegion(string RegionId,
    [property: JsonConverter(typeof(ApprovedRoiRectangleConverter))] Rectangle Region, byte[] Bgra, bool Publish);

// Keep the manifest shape independent of the other public properties on Rectangle.
internal sealed class ApprovedRoiRectangleConverter : JsonConverter<Rectangle>
{
    sealed record Coordinates(int X, int Y, int Width, int Height);

    public override Rectangle Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var coordinates = JsonSerializer.Deserialize<Coordinates>(ref reader, options) ?? throw new JsonException();
        return new Rectangle(coordinates.X, coordinates.Y, coordinates.Width, coordinates.Height);
    }

    public override void Write(Utf8JsonWriter writer, Rectangle value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Coordinates(value.X, value.Y, value.Width, value.Height), options);
}

public static class ApprovedRoiCatalog
{
    const int CatalogBytesLimit = 256 * 1024;
    const int ProfileBytesLimit = 8 * 1024 * 1024;
    const int PixelBytesLimit = 4 * 1024 * 1024;
    const int CatalogEntriesLimit = 256;
    const int ScenesLimit = 64;
    const int RegionsLimit = 64;
    const int FrameDimensionLimit = 4096;
    const int FramePixelsLimit = 4_194_304;

    static readonly JsonSerializerOptions StrictJson = new(EvidenceProtocol.Json)
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    sealed record Catalog(int SchemaVersion, RoiProfileSelection[] Profiles);

    public static ApprovedRoiProfile Load(RoiProfileSelection selection) => Load(selection, AppContext.BaseDirectory);

    // The explicit directory is for an offline reviewer or a test, never a request field.
    public static ApprovedRoiProfile Load(RoiProfileSelection selection, string trustedBaseDirectory)
    {
        if (!ValidSelection(selection)) throw new EvidenceUnavailableException("roi-profile-invalid");
        try
        {
            EvidenceFiles.RequireLocalUnlinkedPath(trustedBaseDirectory);
            byte[] catalogBytes = ReadBounded(Path.Combine(trustedBaseDirectory, "approved-roi-catalog.json"), CatalogBytesLimit);
            var catalog = JsonSerializer.Deserialize<Catalog>(catalogBytes, StrictJson);
            if (!ValidCatalog(catalog)) throw new EvidenceUnavailableException("roi-profile-invalid");

            var entry = catalog.Profiles.FirstOrDefault(profile =>
                profile.ProfileId == selection.ProfileId && profile.Revision == selection.Revision);
            if (entry == null) throw new EvidenceUnavailableException("roi-profile-unavailable");
            if (!SameHash(entry.ProfileSha256, selection.ProfileSha256))
                throw new EvidenceUnavailableException("roi-profile-mismatch");

            string fileName = FormattableString.Invariant($"{entry.ProfileId}.{entry.Revision}.json");
            byte[] profileBytes = ReadBounded(Path.Combine(trustedBaseDirectory, "approved-roi-assets", fileName), ProfileBytesLimit);
            if (!SameHash(EvidenceFiles.Hash(profileBytes), entry.ProfileSha256))
                throw new EvidenceUnavailableException("roi-profile-mismatch");

            var profile = JsonSerializer.Deserialize<ApprovedRoiProfile>(profileBytes, StrictJson);
            if (!IsValidProfile(profile)) throw new EvidenceUnavailableException("roi-profile-invalid");
            if (profile.ProfileId != entry.ProfileId || profile.Revision != entry.Revision)
                throw new EvidenceUnavailableException("roi-profile-mismatch");
            return profile;
        }
        catch (EvidenceUnavailableException ex) when (ex.Code == "invalid-output")
        {
            throw new EvidenceUnavailableException("roi-profile-invalid");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new EvidenceUnavailableException("roi-profile-invalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new EvidenceUnavailableException("roi-profile-unavailable");
        }
    }

    static byte[] ReadBounded(string path, int limit)
    {
        EvidenceFiles.RequireLocalUnlinkedPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length <= 0 || length > limit) throw new EvidenceUnavailableException("roi-profile-invalid");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new EvidenceUnavailableException("roi-profile-invalid");
        EvidenceFiles.RequireLocalUnlinkedPath(path);
        return bytes;
    }

    static bool ValidCatalog(Catalog catalog)
    {
        if (catalog?.SchemaVersion != 1 || catalog.Profiles == null || catalog.Profiles.Length > CatalogEntriesLimit) return false;
        var keys = new HashSet<(string Id, int Revision)>();
        return catalog.Profiles.All(profile => ValidSelection(profile) && keys.Add((profile.ProfileId, profile.Revision)));
    }

    static bool ValidSelection(RoiProfileSelection selection) => selection != null && ValidId(selection.ProfileId) &&
        selection.Revision > 0 && EvidenceProtocol.IsHash(selection.ProfileSha256);

    static bool ValidId(string value) => value != null && Regex.IsMatch(value, "\\A[a-z][a-z0-9-]{0,31}\\z");

    static bool SameHash(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static bool IsValidProfile(ApprovedRoiProfile profile)
    {
        if (profile?.SchemaVersion != 1 || !ValidId(profile.ProfileId) || profile.Revision <= 0 ||
            !EvidenceProtocol.IsHash(profile.AppSha256) || profile.Scenes == null || profile.Scenes.Length is < 1 or > ScenesLimit)
            return false;

        var sceneIds = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var scene in profile.Scenes)
        {
            if (!ValidScene(scene) || !sceneIds.Add(scene.SceneId)) return false;
            var regionIds = new HashSet<string>(StringComparer.Ordinal);
            var checkedRegions = new List<Rectangle>();
            bool hasAnchor = false;
            bool hasPublicPixels = false;
            foreach (var region in scene.Regions)
            {
                if (!ValidRegion(region, scene) || !regionIds.Add(region.RegionId) ||
                    checkedRegions.Any(existing => existing.IntersectsWith(region.Region)))
                    return false;
                checkedRegions.Add(region.Region);
                totalBytes += region.Bgra.Length;
                if (totalBytes > PixelBytesLimit) return false;

                bool nonBlack = false;
                for (int index = 0; index < region.Bgra.Length; index += 4)
                {
                    if (region.Bgra[index + 3] != 255) return false;
                    nonBlack |= (region.Bgra[index] | region.Bgra[index + 1] | region.Bgra[index + 2]) != 0;
                }
                hasAnchor |= !region.Publish;
                hasPublicPixels |= region.Publish && nonBlack;
            }
            if (!hasAnchor || !hasPublicPixels) return false;
        }
        return true;
    }

    static bool ValidScene(ApprovedRoiScene scene) => scene != null && ValidId(scene.SceneId) && ValidId(scene.Role) &&
        scene.Dpi > 0 && scene.WindowWidth is > 0 and <= FrameDimensionLimit && scene.WindowHeight is > 0 and <= FrameDimensionLimit &&
        (long)scene.WindowWidth * scene.WindowHeight <= FramePixelsLimit && EvidenceProtocol.IsHash(scene.UiSha256) &&
        scene.Phases is { Length: >= 1 and <= 4 } && scene.Phases.Distinct(StringComparer.Ordinal).Count() == scene.Phases.Length &&
        scene.Phases.All(phase => phase is "before" or "after" or "snapshot" or "failure") &&
        scene.Regions is { Length: >= 2 and <= RegionsLimit };

    static bool ValidRegion(ApprovedRoiRegion region, ApprovedRoiScene scene)
    {
        if (region == null || !ValidId(region.RegionId) || region.Bgra == null) return false;
        var bounds = region.Region;
        return bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 &&
            (long)bounds.X + bounds.Width <= scene.WindowWidth && (long)bounds.Y + bounds.Height <= scene.WindowHeight &&
            (long)bounds.Width * bounds.Height * 4 == region.Bgra.Length;
    }
}
