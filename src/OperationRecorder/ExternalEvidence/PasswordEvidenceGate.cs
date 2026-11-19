using System;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OperationRecorderApp.ExternalEvidence;

/// <summary>Copies only pixels outside a proven password envelope and verifies the saved raster.</summary>
public static class PasswordEvidenceGate
{
    const string Unavailable = "password-mask-unavailable";
    const int MaximumNodes = 256;
    const int MaximumSide = 4096;

    public static string StructureSha256(EvidenceUi ui)
    {
        if (ui == null || ui.Truncated || ui.TextRead || ui.Elements == null || ui.Elements.Count is < 1 or > MaximumNodes ||
            ui.Elements.Any(node => node == null || node.ControlTypeId is < 50000 or > 50100 ||
                !PasswordEvidenceCatalog.Within(Bounds(node), new Rectangle(0, 0, MaximumSide, MaximumSide)))) return null;

        var nodes = ui.Elements.OrderBy(node => node.ControlTypeId)
            .ThenBy(node => node.X).ThenBy(node => node.Y)
            .ThenBy(node => node.Width).ThenBy(node => node.Height)
            .ThenBy(node => node.IsPassword).ThenBy(node => node.Enabled);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("oprec-password-ui-structure-v1\0"u8);
        Span<byte> field = stackalloc byte[22];
        foreach (var node in nodes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(field, node.ControlTypeId);
            BinaryPrimitives.WriteInt32LittleEndian(field[4..], node.X);
            BinaryPrimitives.WriteInt32LittleEndian(field[8..], node.Y);
            BinaryPrimitives.WriteInt32LittleEndian(field[12..], node.Width);
            BinaryPrimitives.WriteInt32LittleEndian(field[16..], node.Height);
            field[20] = node.IsPassword ? (byte)1 : (byte)0;
            field[21] = node.Enabled ? (byte)1 : (byte)0;
            hash.AppendData(field);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool MatchesUi(PasswordEvidenceScene scene, EvidenceUi ui)
    {
        if (!PasswordEvidenceCatalog.IsValidScene(scene) ||
            !string.Equals(scene.UiSha256, StructureSha256(ui), StringComparison.OrdinalIgnoreCase)) return false;

        var frame = new Rectangle(0, 0, scene.WindowWidth, scene.WindowHeight);
        if (ui.Elements.Any(node => !PasswordEvidenceCatalog.Within(Bounds(node), frame))) return false;
        foreach (var field in scene.Fields)
        {
            var candidates = ui.Elements.Where(node => MatchesField(node, field)).Take(2).ToArray();
            if (candidates.Length != 1 || !candidates[0].Enabled) return false;
        }
        return ui.Elements.All(node => !node.IsPassword || scene.Fields.Any(field => MatchesField(node, field)));
    }

    public static byte[] Mask(Bitmap raw, PasswordEvidenceScene scene)
    {
        byte[] safe = null;
        try
        {
            if (!MatchesDimensions(raw, scene)) throw new EvidenceUnavailableException(Unavailable);
            var frame = new Rectangle(0, 0, scene.WindowWidth, scene.WindowHeight);
            var masks = OrderedMasks(scene);
            int rowBytes = frame.Width * 4;
            safe = new byte[rowBytes * frame.Height];
            for (int i = 3; i < safe.Length; i += 4) safe[i] = 255;
            var data = raw.LockBits(frame, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < frame.Height; y++)
                {
                    IntPtr source = IntPtr.Add(data.Scan0, y * data.Stride);
                    int target = y * rowBytes;
                    int x = 0;
                    foreach (var mask in masks)
                    {
                        if (y < mask.Top || y >= mask.Bottom) continue;
                        CopyVisible(source, safe, target, x, mask.Left);
                        x = mask.Right;
                    }
                    CopyVisible(source, safe, target, x, frame.Width);
                }
            }
            finally { raw.UnlockBits(data); }
            return Encode(safe, frame);
        }
        catch
        {
            // The worker falls back to a separately generated opaque frame. Do not surface provider or image data.
            throw new EvidenceUnavailableException(Unavailable);
        }
        finally
        {
            if (safe != null) CryptographicOperations.ZeroMemory(safe);
        }
    }

    public static bool VerifyProtected(Bitmap image, PasswordEvidenceScene scene, EvidenceUi ui)
    {
        byte[] row = null;
        try
        {
            if (!MatchesDimensions(image, scene) || !MatchesUi(scene, ui)) return false;
            var frame = new Rectangle(0, 0, scene.WindowWidth, scene.WindowHeight);
            var masks = OrderedMasks(scene);
            row = new byte[frame.Width * 4];
            bool hasPublicPixels = false;
            var data = image.LockBits(frame, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    int x = 0;
                    foreach (var mask in masks)
                    {
                        if (y < mask.Top || y >= mask.Bottom) continue;
                        if (!VerifySpan(row, x, mask.Left, false, ref hasPublicPixels) ||
                            !VerifySpan(row, mask.Left, mask.Right, true, ref hasPublicPixels)) return false;
                        x = mask.Right;
                    }
                    if (!VerifySpan(row, x, frame.Width, false, ref hasPublicPixels)) return false;
                }
            }
            finally { image.UnlockBits(data); }
            return hasPublicPixels;
        }
        catch { return false; }
        finally
        {
            if (row != null) CryptographicOperations.ZeroMemory(row);
        }
    }

    static Rectangle Bounds(EvidenceNode node) => new(node.X, node.Y, node.Width, node.Height);

    static bool MatchesField(EvidenceNode node, PasswordFieldRule field) =>
        node.ControlTypeId == field.FieldControlTypeId && Bounds(node) == field.FieldBounds;

    static bool MatchesDimensions(Bitmap image, PasswordEvidenceScene scene) => image != null &&
        PasswordEvidenceCatalog.IsValidScene(scene) && image.Width == scene.WindowWidth && image.Height == scene.WindowHeight;

    static Rectangle[] OrderedMasks(PasswordEvidenceScene scene) => scene.Fields.Select(field => field.Mask).OrderBy(mask => mask.Left).ToArray();

    static void CopyVisible(IntPtr source, byte[] target, int rowOffset, int first, int end)
    {
        if (first >= end) return;
        int offset = first * 4;
        int start = rowOffset + offset;
        int count = (end - first) * 4;
        Marshal.Copy(IntPtr.Add(source, offset), target, start, count);
        // Store only an opaque composite. RGB hidden under transparency is not copied into the PNG.
        for (int index = start; index < start + count; index += 4)
        {
            int alpha = target[index + 3];
            if (alpha == 255) continue;
            target[index] = (byte)((target[index] * alpha + 127) / 255);
            target[index + 1] = (byte)((target[index + 1] * alpha + 127) / 255);
            target[index + 2] = (byte)((target[index + 2] * alpha + 127) / 255);
            target[index + 3] = 255;
        }
    }

    static bool VerifySpan(byte[] row, int first, int end, bool masked, ref bool hasPublicPixels)
    {
        for (int index = first * 4; index < end * 4; index += 4)
        {
            if (row[index + 3] != 255) return false;
            bool colored = (row[index] | row[index + 1] | row[index + 2]) != 0;
            if (masked && colored) return false;
            if (colored) hasPublicPixels = true;
        }
        return true;
    }

    static byte[] Encode(byte[] pixels, Rectangle frame)
    {
        using var image = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        int rowBytes = frame.Width * 4;
        var data = image.LockBits(frame, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < frame.Height; y++)
                Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
        }
        finally { image.UnlockBits(data); }
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
