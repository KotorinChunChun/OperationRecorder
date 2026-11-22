using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OperationRecorderApp.ExternalEvidence;

public sealed record ApprovedRoiDecision(byte[] Png, string SceneId, string ReasonCode);

/// <summary>Matches a single frame against pre-approved pixels and emits only approved assets.</summary>
public static class ApprovedRoiGate
{
    const int MaximumDimension = 4096, MaximumPixels = 4194304, MaximumNodes = 256;
    const int EditControl = 50004, CustomControl = 50025, DeniedMargin = 2;

    /// <summary>
    /// A fingerprint of the sorted multiset of geometry, control types and enabled flags.
    /// Null denotes UI structure that cannot establish an approved state.
    /// </summary>
    public static string StructureSha256(EvidenceUi ui)
    {
        if (ui == null || ui.Truncated || ui.TextRead || ui.Elements == null ||
            ui.Elements.Count is < 1 or > MaximumNodes ||
            ui.Elements.Any(node => node == null || node.IsPassword || node.ControlTypeId is < 50000 or > 50100 ||
                node.X < 0 || node.Y < 0 || node.Width <= 0 || node.Height <= 0))
            return null;

        var nodes = ui.Elements.OrderBy(node => node.ControlTypeId)
            .ThenBy(node => node.X).ThenBy(node => node.Y)
            .ThenBy(node => node.Width).ThenBy(node => node.Height)
            .ThenBy(node => node.Enabled);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("oprec-ui-structure-v1\0"u8);
        Span<byte> field = stackalloc byte[21];
        foreach (var node in nodes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(field, node.ControlTypeId);
            BinaryPrimitives.WriteInt32LittleEndian(field[4..], node.X);
            BinaryPrimitives.WriteInt32LittleEndian(field[8..], node.Y);
            BinaryPrimitives.WriteInt32LittleEndian(field[12..], node.Width);
            BinaryPrimitives.WriteInt32LittleEndian(field[16..], node.Height);
            field[20] = node.Enabled ? (byte)1 : (byte)0;
            hash.AppendData(field);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static ApprovedRoiDecision Apply(Bitmap raw, ApprovedRoiProfile profile, string role, string phase, int dpi,
        EvidenceUi uiBefore, EvidenceUi uiAfter, bool contextCoherent)
    {
        int width, height;
        try
        {
            if (raw == null || !ValidSize(width = raw.Width, height = raw.Height))
                return new(Array.Empty<byte>(), null, "invalid-frame");
        }
        catch { return new(Array.Empty<byte>(), null, "invalid-frame"); }
        ApprovedRoiDecision Block(string reason) => new(EvidenceFiles.OpaquePng(new(width, height)), null, reason);
        byte[] frame = null;
        try
        {
            if (!contextCoherent) return Block("context-unproven");
            if (HasPassword(uiBefore) || HasPassword(uiAfter)) return Block("roi-authentication-present");
            string beforeHash = StructureSha256(uiBefore), afterHash = StructureSha256(uiAfter);
            if (beforeHash == null || !string.Equals(beforeHash, afterHash, StringComparison.Ordinal) ||
                !FitsFrame(uiBefore, width, height) || !FitsFrame(uiAfter, width, height) ||
                !ApprovedRoiCatalog.IsValidProfile(profile))
                return Block("roi-scene-unproven");

            var candidates = profile.Scenes.Where(scene =>
                MatchesContext(scene, role, phase, dpi, width, height, beforeHash)).ToArray();
            if (candidates.Length == 0) return Block("roi-scene-unproven");
            var permitted = candidates.Where(scene => !OverlapsDeniedNodes(scene, uiBefore) &&
                !OverlapsDeniedNodes(scene, uiAfter)).ToArray();
            if (permitted.Length == 0) return Block("roi-denied-overlap");

            frame = ReadPixels(raw);
            var matches = permitted.Where(scene => scene.Regions.All(region =>
                MatchesRegion(frame, width, region))).Take(2).ToArray();
            if (matches.Length != 1) return Block("roi-scene-unproven");
            var approved = matches[0];
            return new(Encode(ComposeApprovedPixels(approved), width, height), approved.SceneId, "roi-approved");
        }
        catch
        {
            return Block("disclosure-unavailable");
        }
        finally
        {
            if (frame != null) CryptographicOperations.ZeroMemory(frame);
        }
    }

    /// <summary>Independently checks every saved pixel against the selected scene's protected raster.</summary>
    public static bool VerifyProtected(Bitmap image, ApprovedRoiProfile profile, string sceneId, string role,
        string phase, int dpi, EvidenceUi ui)
    {
        if (image == null || sceneId == null || !ValidSize(image.Width, image.Height) ||
            !ApprovedRoiCatalog.IsValidProfile(profile)) return false;
        string structure = StructureSha256(ui);
        if (structure == null || !FitsFrame(ui, image.Width, image.Height)) return false;

        var scene = profile.Scenes.SingleOrDefault(candidate => candidate.SceneId == sceneId);
        if (scene == null || !MatchesContext(scene, role, phase, dpi, image.Width, image.Height, structure) ||
            OverlapsDeniedNodes(scene, ui)) return false;

        byte[] pixels = ReadPixels(image);
        try { return pixels.AsSpan().SequenceEqual(ComposeApprovedPixels(scene)); }
        finally { CryptographicOperations.ZeroMemory(pixels); }
    }

    static bool ValidSize(int width, int height) => width is > 0 and <= MaximumDimension &&
        height is > 0 and <= MaximumDimension && (long)width * height <= MaximumPixels;

    static bool HasPassword(EvidenceUi ui) => ui?.Elements?.Any(node => node?.IsPassword == true) == true;

    static bool FitsFrame(EvidenceUi ui, int width, int height) => ui.Elements.All(node =>
        (long)node.X + node.Width <= width && (long)node.Y + node.Height <= height);

    static bool MatchesContext(ApprovedRoiScene scene, string role, string phase, int dpi, int width,
        int height, string structure) => scene.Role == role && scene.Phases.Contains(phase, StringComparer.Ordinal) &&
        scene.Dpi == dpi && scene.WindowWidth == width && scene.WindowHeight == height &&
        string.Equals(scene.UiSha256, structure, StringComparison.OrdinalIgnoreCase);

    static bool OverlapsDeniedNodes(ApprovedRoiScene scene, EvidenceUi ui)
    {
        var denied = ui.Elements.Where(node => node.ControlTypeId is EditControl or CustomControl).Select(node =>
            Rectangle.Inflate(new Rectangle(node.X, node.Y, node.Width, node.Height), DeniedMargin, DeniedMargin));
        return denied.Any(blocked => scene.Regions.Any(region => region.Region.IntersectsWith(blocked)));
    }

    static bool MatchesRegion(byte[] frame, int width, ApprovedRoiRegion region)
    {
        var bounds = region.Region;
        int rowBytes = bounds.Width * 4;
        for (int y = 0; y < bounds.Height; y++)
        {
            int offset = ((bounds.Y + y) * width + bounds.X) * 4;
            if (!frame.AsSpan(offset, rowBytes).SequenceEqual(region.Bgra.AsSpan(y * rowBytes, rowBytes)))
                return false;
        }
        return true;
    }

    static byte[] ComposeApprovedPixels(ApprovedRoiScene scene)
    {
        var output = new byte[checked(scene.WindowWidth * scene.WindowHeight * 4)];
        for (int index = 3; index < output.Length; index += 4) output[index] = 255;
        foreach (var region in scene.Regions.Where(region => region.Publish))
        {
            var bounds = region.Region;
            int rowBytes = bounds.Width * 4;
            for (int y = 0; y < bounds.Height; y++)
            {
                int targetOffset = ((bounds.Y + y) * scene.WindowWidth + bounds.X) * 4;
                region.Bgra.AsSpan(y * rowBytes, rowBytes).CopyTo(output.AsSpan(targetOffset, rowBytes));
            }
        }
        return output;
    }

    static byte[] ReadPixels(Bitmap bitmap)
    {
        int rowBytes = checked(bitmap.Width * 4);
        var pixels = new byte[checked(rowBytes * bitmap.Height)];
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = null;
        try
        {
            data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < bounds.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            return pixels;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(pixels);
            throw;
        }
        finally
        {
            if (data != null) bitmap.UnlockBits(data);
        }
    }

    static byte[] Encode(byte[] pixels, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        int rowBytes = width * 4;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
        }
        finally { bitmap.UnlockBits(data); }
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }
}
