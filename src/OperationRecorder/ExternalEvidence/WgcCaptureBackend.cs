using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OperationRecorderApp.ExternalEvidence;

/// <summary>A raw in-memory WGC result. The caller must protect pixels before encoding and dispose this object.</summary>
public sealed class WgcCapturedFrame : IDisposable
{
    public Bitmap Bitmap { get; }
    public long FrameTime100ns { get; }
    public Size ContentSize { get; }
    public DateTimeOffset ReceivedAt { get; }
    internal WgcCapturedFrame(Bitmap bitmap, long frameTime100ns, DateTimeOffset receivedAt)
    { Bitmap = bitmap; ContentSize = bitmap.Size; FrameTime100ns = frameTime100ns; ReceivedAt = receivedAt; }
    public void Dispose()
    {
        try { using var graphics = Graphics.FromImage(Bitmap); graphics.Clear(Color.Black); } catch { }
        Bitmap.Dispose();
    }
}

/// <summary>HWND-only WGC. Never restores, activates, captures a monitor, saves pixels, or falls back.</summary>
public sealed class WgcCaptureBackend
{
    public async Task<WgcCapturedFrame> CaptureAsync(long hwnd, DateTimeOffset deadlineUtc, CancellationToken token)
    {
        if (hwnd == 0 || !IsWindow(new IntPtr(hwnd))) throw new EvidenceUnavailableException("target-mismatch");
        if (!IsInputDesktopAvailable()) throw new EvidenceUnavailableException("session-unavailable");
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new EvidenceUnavailableException("deadline-exceeded");
        if (remaining > TimeSpan.FromSeconds(30)) throw new EvidenceUnavailableException("invalid-request");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(remaining);
        try { return await Task.Run(() => Capture(new IntPtr(hwnd), deadline.Token), deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; throw new EvidenceUnavailableException("deadline-exceeded"); }
        catch (EvidenceUnavailableException) { throw; }
        catch { throw new EvidenceUnavailableException("capture-unavailable"); }
    }

    static WgcCapturedFrame Capture(IntPtr hwnd, CancellationToken token)
    {
        int apartment = RoInitialize(1); bool initialized = apartment >= 0;
        if (!initialized) throw new EvidenceUnavailableException("capture-unavailable");
        try
        {
            token.ThrowIfCancellationRequested();
            if (!GraphicsCaptureSession.IsSupported()) throw new EvidenceUnavailableException("capture-unsupported");
            var item = CreateItem(hwnd);
            var size = item.Size;
            ValidateSize(size.Width, size.Height);
            using var device = CreateDevice();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
            using var session = pool.CreateCaptureSession(item);
            var available = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelled = token.Register(() => available.TrySetCanceled(token));
            Direct3D11CaptureFrame selected = null;
            long barrier100ns = Clock100ns();
            void Arrived(Direct3D11CaptureFramePool sender, object args)
            {
                Direct3D11CaptureFrame frame = null;
                try
                {
                    frame = sender.TryGetNextFrame();
                    if (frame == null) return;
                    // Never accept a frame predating this capture request.
                    if (frame.SystemRelativeTime.Ticks < barrier100ns || !available.TrySetResult(frame)) frame.Dispose();
                }
                catch { frame?.Dispose(); available.TrySetException(new EvidenceUnavailableException("capture-unavailable")); }
            }
            void Closed(GraphicsCaptureItem sender, object args) => available.TrySetException(new EvidenceUnavailableException("target-mismatch"));
            pool.FrameArrived += Arrived; item.Closed += Closed;
            try
            {
                session.StartCapture();
                selected = available.Task.WaitAsync(token).GetAwaiter().GetResult();
                using var frame = selected;
                token.ThrowIfCancellationRequested();
                var content = frame.ContentSize;
                ValidateSize(content.Width, content.Height);
                // A resized or clipped frame is not silently interpreted with old coordinates.
                if (content.Width != size.Width || content.Height != size.Height) throw new EvidenceUnavailableException("capture-size-changed");
                if (!IsWindow(hwnd)) throw new EvidenceUnavailableException("target-mismatch");
                using var software = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().WaitAsync(token).GetAwaiter().GetResult();
                if (software.PixelWidth < content.Width || software.PixelHeight < content.Height || software.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    throw new EvidenceUnavailableException("capture-unavailable");
                int bytes = checked(software.PixelWidth * software.PixelHeight * 4);
                var buffer = new Windows.Storage.Streams.Buffer((uint)bytes);
                software.CopyToBuffer(buffer);
                byte[] pixels = new byte[bytes];
                try
                {
                    using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
                    var bitmap = new Bitmap(content.Width, content.Height, PixelFormat.Format32bppPArgb);
                    try
                    {
                        var locked = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                        try
                        {
                            for (int row = 0; row < content.Height; row++)
                                Marshal.Copy(pixels, row * software.PixelWidth * 4, IntPtr.Add(locked.Scan0, row * locked.Stride), content.Width * 4);
                        }
                        finally { bitmap.UnlockBits(locked); }
                        token.ThrowIfCancellationRequested();
                        return new WgcCapturedFrame(bitmap, frame.SystemRelativeTime.Ticks, DateTimeOffset.UtcNow);
                    }
                    catch { bitmap.Dispose(); throw; }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pixels);
                    try
                    {
                        using var writer = new DataWriter(); writer.WriteBytes(pixels);
                        software.CopyFromBuffer(writer.DetachBuffer());
                    }
                    catch { } // Disposal still releases the WinRT buffer; erasure failure must not leak the owned Bitmap.
                }
            }
            finally
            {
                pool.FrameArrived -= Arrived; item.Closed -= Closed;
                if (available.Task.IsCompletedSuccessfully && !ReferenceEquals(selected, available.Task.Result)) available.Task.Result.Dispose();
            }
        }
        finally { RoUninitialize(); }
    }

    public static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > 4194304)
            throw new EvidenceUnavailableException("capture-unavailable");
    }
    public static long Clock100ns() => (long)(Stopwatch.GetTimestamp() * (10000000.0 / Stopwatch.Frequency));
    public static bool IsInputDesktopAvailable()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1 /* DESKTOP_READOBJECTS */);
        if (desktop == IntPtr.Zero) return false;
        CloseDesktop(desktop); return true;
    }

    static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        IntPtr name = IntPtr.Zero, factory = IntPtr.Zero, pointer = IntPtr.Zero;
        try
        {
            const string typeName = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Check(WindowsCreateString(typeName, typeName.Length, out name));
            Guid interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Check(RoGetActivationFactory(name, ref interop, out factory));
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * IntPtr.Size));
            Guid item = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Check(create(factory, hwnd, ref item, out pointer));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer);
        }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); if (factory != IntPtr.Zero) Marshal.Release(factory); if (name != IntPtr.Zero) WindowsDeleteString(name); }
    }
    static IDirect3DDevice CreateDevice()
    {
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, inspectable = IntPtr.Zero, multithread = IntPtr.Zero;
        try
        {
            Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out _, out context));
            Guid threading = new("9B7E4E00-342C-4106-A19F-4F2704F689F0"); Check(Marshal.QueryInterface(context, in threading, out multithread));
            var protect = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtected>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(multithread), 5 * IntPtr.Size));
            protect(multithread, 1);
            Guid id = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C"); Check(Marshal.QueryInterface(device, in id, out dxgi));
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally { foreach (var pointer in new[] { inspectable, dxgi, multithread, context, device }) if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    }
    static void Check(int result) { if (result < 0) throw new EvidenceUnavailableException(result == unchecked((int)0x80070005) ? "capture-denied" : "capture-unavailable"); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateForWindow(IntPtr self, IntPtr hwnd, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetMultithreadProtected(IntPtr self, int enabled);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("combase.dll")] static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr result);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint count, uint sdk, out IntPtr device, out uint featureLevel, out IntPtr context);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr result);
}
