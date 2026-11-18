using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OperationRecorderApp.ExternalEvidence;

public sealed record PublicRegionRule(string RuleId, string Role, int Dpi, int WindowWidth, int WindowHeight,
    Rectangle Region, string[] ApprovedPixelSha256);
public sealed record DisclosureProfile(int SchemaVersion, string ProfileId, int Revision, string AppSha256,
    PublicRegionRule[] Rules, string[] ApprovedFrameSha256 = null);
public sealed record DisclosureDecision(byte[] Png, string DisclosureStatus, bool RequirementsMet, string ReasonCode,
    string[] PublishedRuleIds, string VerifiedFrameSha256 = null);

/// <summary>
/// Copies only pixels matching pre-approved public assets from one immutable in-memory snapshot.
/// A window's entire frame must also match a pre-approved state. No UI strings, file writes,
/// reference-image learning, or automatic profile expansion occur in this layer.
/// </summary>
public static class DisclosureGate
{
    public static bool IsValidProfile(DisclosureProfile profile) { try { return profile != null && ValidProfile(profile); } catch { return false; } }
    const int MaximumDimension = 4096, MaximumPixels = 4194304, MaximumRules = 64;
    const int MaximumRuleHashes = 16, MaximumHashes = 256, MaximumFrameHashes = 64;
    const int MaximumDeniedRegions = 1024;
    const long MaximumRulePixels = 16777216;

    /// <summary>
    /// SHA256 of LE int32 width, LE int32 height, then row-major BGRA bytes without row padding.
    /// RGB channels are composited over black with (channel * alpha + 127) / 255; alpha is 255.
    /// Intended for previously approved reference assets, never for learning permissions from a capture.
    /// </summary>
    public static string PixelHash(Bitmap frame, Rectangle region)
    {
        byte[] pixels = null;
        try
        {
            if (!ValidFrame(frame) || !Inside(region, frame.Width, frame.Height))
                throw new EvidenceUnavailableException("invalid-frame");
            pixels = Normalize(frame, region);
            return HashPixels(pixels, region.Width, region.Height);
        }
        catch (EvidenceUnavailableException) { throw; }
        catch { throw new EvidenceUnavailableException("invalid-frame"); }
        finally { if (pixels != null) CryptographicOperations.ZeroMemory(pixels); }
    }

    public static DisclosureDecision Apply(Bitmap raw, string actualAppSha256, string role, int dpi,
        DisclosureProfile profile, IReadOnlyList<Rectangle> deniedRegions, bool contextCoherent)
    {
        int width, height;
        byte[] normalized = null;
        try
        {
            if (!ValidFrame(raw)) return Unavailable();
            width = raw.Width; height = raw.Height;
        }
        catch { return Unavailable(); }

        // This array starts opaque black, including pixels never covered by an approved rule.
        var protectedPixels = new byte[checked(width * height * 4)];
        for (int index = 3; index < protectedPixels.Length; index += 4) protectedPixels[index] = 255;
        DisclosureDecision Deny(string reason, string status = "blocked") =>
            new(Encode(protectedPixels, width, height), status, false, reason, Array.Empty<string>());

        try
        {
            if (profile == null) return Deny("profile-required", "opaque-only");
            if (!contextCoherent) return Deny("context-unproven");
            var snapshot = CopyProfile(profile);
            if (!ValidProfile(snapshot)) return Deny("invalid-profile");
            if (!HashEquals(actualAppSha256, snapshot.AppSha256)) return Deny("application-mismatch");
            if (deniedRegions == null) return Deny("protection-unproven");
            if (deniedRegions.Count > MaximumDeniedRegions) return Deny("invalid-denied-regions");
            var denied = deniedRegions.ToArray();
            if (denied.Any(rectangle => !Inside(rectangle, width, height))) return Deny("invalid-denied-regions");
            var rules = snapshot.Rules.Where(rule => string.Equals(rule.Role, role, StringComparison.Ordinal)).ToArray();
            if (rules.Length == 0) return Deny("role-not-approved");
            if (rules.Any(rule => rule.Dpi != dpi)) return Deny("dpi-mismatch");
            if (rules.Any(rule => rule.WindowWidth != width || rule.WindowHeight != height)) return Deny("size-mismatch");
            if (snapshot.ApprovedFrameSha256 == null || snapshot.ApprovedFrameSha256.Length == 0)
                return Deny("state-unproven");

            // Snapshot the raw bitmap exactly once. Whole-frame verification, ROI verification and
            // output composition all use these same bytes, never a second read of mutable raw pixels.
            normalized = Normalize(raw, new Rectangle(0, 0, width, height));
            var frameHash = HashPixels(normalized, width, height);
            if (!ContainsHash(snapshot.ApprovedFrameSha256, frameHash)) return Deny("state-mismatch");

            var published = new List<string>();
            string failure = null;
            bool hasNonBlackPixels = false;
            foreach (var rule in rules)
            {
                if (denied.Any(rectangle => rectangle.IntersectsWith(rule.Region)))
                { failure ??= "denied-overlap"; continue; }
                var pixels = Extract(normalized, width, rule.Region);
                try
                {
                    if (!ContainsHash(rule.ApprovedPixelSha256, HashPixels(pixels, rule.Region.Width, rule.Region.Height)))
                    { failure ??= "pixel-mismatch"; continue; }
                    for (int y = 0; y < rule.Region.Height; y++)
                        Buffer.BlockCopy(pixels, y * rule.Region.Width * 4, protectedPixels,
                            ((rule.Region.Y + y) * width + rule.Region.X) * 4, rule.Region.Width * 4);
                    for (int index = 0; index < pixels.Length; index += 4)
                        if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0) hasNonBlackPixels = true;
                    published.Add(rule.RuleId);
                }
                finally { CryptographicOperations.ZeroMemory(pixels); }
            }
            if (!hasNonBlackPixels) return Deny(failure ?? "no-diagnostic-pixels", "opaque-only");
            bool met = published.Count == rules.Length;
            return new(Encode(protectedPixels, width, height), met ? "public-regions" : "partial-public", met,
                met ? "approved" : failure ?? "requirements-unmet", published.ToArray(),frameHash);
        }
        catch
        {
            // No source pixel, profile content, exception message or provider text is returned.
            Array.Clear(protectedPixels);
            for (int index = 3; index < protectedPixels.Length; index += 4) protectedPixels[index] = 255;
            return Deny("disclosure-unavailable");
        }
        finally { if (normalized != null) CryptographicOperations.ZeroMemory(normalized); }
    }

    static DisclosureDecision Unavailable() => new(Array.Empty<byte>(), "blocked", false, "invalid-frame", Array.Empty<string>());
    static bool ValidFrame(Bitmap bitmap) => bitmap != null && ValidSize(bitmap.Width, bitmap.Height);
    static bool ValidSize(int width, int height) => width > 0 && height > 0 && width <= MaximumDimension &&
        height <= MaximumDimension && (long)width * height <= MaximumPixels;
    static bool Inside(Rectangle region, int width, int height) => region.X >= 0 && region.Y >= 0 && region.Width > 0 &&
        region.Height > 0 && (long)region.X + region.Width <= width && (long)region.Y + region.Height <= height;
    static bool ValidId(string value) => value != null && Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z");
    static bool ValidHash(string value) => value != null && Regex.IsMatch(value, "\\A[0-9A-Fa-f]{64}\\z");
    static bool HashEquals(string left, string right) => ValidHash(left) && ValidHash(right) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    static bool ContainsHash(string[] hashes, string hash) => hashes.Any(value => HashEquals(value, hash));
    static DisclosureProfile CopyProfile(DisclosureProfile profile)
    {
        if (profile.Rules == null || profile.Rules.Length > MaximumRules ||
            profile.ApprovedFrameSha256?.Length > MaximumFrameHashes) return profile with { Rules = null };
        var copied = new PublicRegionRule[profile.Rules.Length];
        for (int index = 0; index < copied.Length; index++)
        {
            var rule = profile.Rules[index];
            if (rule?.ApprovedPixelSha256 == null || rule.ApprovedPixelSha256.Length > MaximumRuleHashes)
                return profile with { Rules = null };
            copied[index] = rule with { ApprovedPixelSha256 = (string[])rule.ApprovedPixelSha256.Clone() };
        }
        return profile with { Rules = copied, ApprovedFrameSha256 = (string[])profile.ApprovedFrameSha256?.Clone() };
    }
    static bool ValidProfile(DisclosureProfile profile)
    {
        if (profile.SchemaVersion != 1 || !ValidId(profile.ProfileId) || profile.Revision < 1 || !ValidHash(profile.AppSha256) ||
            profile.Rules == null || profile.Rules.Length == 0 || profile.Rules.Length > MaximumRules ||
            profile.ApprovedFrameSha256?.Length > MaximumFrameHashes ||
            profile.ApprovedFrameSha256?.Any(value => !ValidHash(value)) == true) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int hashCount = profile.ApprovedFrameSha256?.Length ?? 0;
        long pixels = 0;
        foreach (var rule in profile.Rules)
        {
            if (rule == null || !ValidId(rule.RuleId) || !ValidId(rule.Role) || !ids.Add(rule.RuleId) ||
                rule.Dpi < 48 || rule.Dpi > 960 || !ValidSize(rule.WindowWidth, rule.WindowHeight) ||
                !Inside(rule.Region, rule.WindowWidth, rule.WindowHeight) || rule.ApprovedPixelSha256 == null ||
                rule.ApprovedPixelSha256.Length == 0 || rule.ApprovedPixelSha256.Length > MaximumRuleHashes ||
                rule.ApprovedPixelSha256.Any(value => !ValidHash(value))) return false;
            hashCount += rule.ApprovedPixelSha256.Length; pixels += (long)rule.Region.Width * rule.Region.Height;
            if (hashCount > MaximumHashes || pixels > MaximumRulePixels) return false;
        }
        return true;
    }

    static byte[] Normalize(Bitmap bitmap, Rectangle rectangle)
    {
        var result = new byte[checked(rectangle.Width * rectangle.Height * 4)];
        BitmapData data = null;
        try
        {
            data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[checked(rectangle.Width * 4)];
            for (int y = 0; y < rectangle.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < row.Length; x += 4)
                {
                    int output = y * row.Length + x, alpha = row[x + 3];
                    result[output] = (byte)((row[x] * alpha + 127) / 255);
                    result[output + 1] = (byte)((row[x + 1] * alpha + 127) / 255);
                    result[output + 2] = (byte)((row[x + 2] * alpha + 127) / 255);
                    result[output + 3] = 255;
                }
            }
        }
        finally { if (data != null) bitmap.UnlockBits(data); }
        return result;
    }
    static byte[] Extract(byte[] source, int sourceWidth, Rectangle rectangle)
    {
        var result = new byte[checked(rectangle.Width * rectangle.Height * 4)];
        for (int y = 0; y < rectangle.Height; y++)
            Buffer.BlockCopy(source, ((rectangle.Y + y) * sourceWidth + rectangle.X) * 4, result, y * rectangle.Width * 4, rectangle.Width * 4);
        return result;
    }
    static string HashPixels(byte[] pixels, int width, int height)
    {
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, width); BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], height);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(dimensions); hash.AppendData(pixels);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    static byte[] Encode(byte[] pixels, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * width * 4, IntPtr.Add(data.Scan0, y * data.Stride), width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
}
