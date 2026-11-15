using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;

namespace OperationRecorderApp
{
    sealed class ScreenVideo
    {
        const int Width = 1280, Height = 720, IntervalMs = 200;
        readonly BlockingCollection<(long Slot, Bitmap Frame)> frames = new BlockingCollection<(long, Bitmap)>(4);
        readonly Process encoder;
        readonly Task writing;
        readonly Task errors;
        readonly string target, temporary;
        long stopMs;

        public ScreenVideo(string ffmpeg, string directory)
        {
            target = Path.Combine(directory, "screen.mp4");
            temporary = Path.Combine(directory, "screen-partial.mp4");
            var launch = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] {
                "-hide_banner", "-loglevel", "error", "-n", "-f", "image2pipe", "-framerate", "5",
                "-vcodec", "mjpeg", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "ultrafast",
                "-crf", "23", "-pix_fmt", "yuv420p", "-movflags", "+faststart", temporary })
                launch.ArgumentList.Add(argument);
            encoder = Process.Start(launch) ?? throw new IOException("FFmpegを起動できません。");
            errors = encoder.StandardError.BaseStream.CopyToAsync(Stream.Null);
            writing = Task.Run(Encode);
        }

        public void AddFrame(long elapsedMs, Bitmap source)
        {
            if (frames.IsAddingCompleted || frames.Count >= frames.BoundedCapacity || writing.IsCompleted) return;
            var frame = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(frame))
                {
                    graphics.Clear(Color.Black);
                    double scale = Math.Min((double)Width / source.Width, (double)Height / source.Height);
                    int width = Math.Max(1, (int)(source.Width * scale));
                    int height = Math.Max(1, (int)(source.Height * scale));
                    graphics.DrawImage(source, (Width - width) / 2, (Height - height) / 2, width, height);
                }
                if (frames.TryAdd((elapsedMs / IntervalMs, frame))) frame = null;
            }
            finally { frame?.Dispose(); }
        }

        void Encode()
        {
            using var blank = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            using var buffer = new MemoryStream();
            blank.Save(buffer, ImageFormat.Jpeg);
            byte[] black = buffer.ToArray(), previous = black;
            long index = 0, lastFrame = -10;
            using var input = encoder.StandardInput.BaseStream;
            foreach (var item in frames.GetConsumingEnumerable())
            {
                using var frame = item.Frame;
                if (item.Slot < index) continue;
                while (index < item.Slot) input.Write(index++ - lastFrame <= 2 ? previous : black);
                buffer.SetLength(0);
                frame.Save(buffer, ImageFormat.Jpeg);
                previous = buffer.ToArray();
                input.Write(previous);
                lastFrame = index++;
            }
            long end = Math.Max(1, (stopMs + IntervalMs - 1) / IntervalMs);
            while (index < end) input.Write(index++ - lastFrame <= 2 ? previous : black);
        }

        public void Stop(long elapsedMs)
        {
            stopMs = elapsedMs;
            frames.CompleteAdding();
            try
            {
                if (!writing.Wait(TimeSpan.FromSeconds(30)) || !encoder.WaitForExit(30000))
                    throw new TimeoutException("録画ファイルの保存がタイムアウトしました。");
                if (encoder.ExitCode != 0 || !File.Exists(temporary))
                    throw new IOException("FFmpegがMP4の作成に失敗しました。libx264対応版と空き容量を確認してください。");
                File.Move(temporary, target);
            }
            finally
            {
                if (!encoder.HasExited) { encoder.Kill(entireProcessTree: true); encoder.WaitForExit(3000); }
                try { writing.GetAwaiter().GetResult(); errors.GetAwaiter().GetResult(); } catch { }
                while (frames.TryTake(out var item)) item.Frame.Dispose();
                frames.Dispose();
                encoder.Dispose();
            }
        }
    }
}
