using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace OpRec
{
    // 記録の判断のうち、フックや画面に頼らない部分。自動テスト(apps\OperationRecorderApp.Tests)から直接呼べるように分けてある
    public static class WheelRules
    {
        public const int MergeWindowMs = 1000;   // 前に回してからこの時間内なら、同じ手順にまとめる

        // WM_MOUSEWHEEL の delta(120 = 1 目盛り)を、向き付きの目盛りの数にする。上 = 正 / 下 = 負。小さな delta も 1 目盛りに数える
        public static int Notches(int delta)
        {
            if (delta == 0) return 0;
            return Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 120);
        }

        // 前のホイール手順にまとめるか。向きは問わない(逆に回した分は差し引く)
        public static bool CanMerge(bool prevSent, IntPtr prevRoot, long prevLastTs, IntPtr root, long ts) =>
            !prevSent && prevRoot == root && ts - prevLastTs <= MergeWindowMs;

        // 合計が 0(回して元に戻した)なら、手順にしない
        public static bool IsCancelled(int net) => net == 0;

        public static Dictionary<string, object> Info(int net) =>
            new Dictionary<string, object> { { "direction", net > 0 ? "up" : "down" }, { "notches", Math.Abs(net) } };
    }

    public static class ImageCompare
    {
        // 2 枚の画像のピクセルがすべて同じか(操作前と操作後を 1 枚にまとめるかの判断に使う)
        public static bool Same(Bitmap a, Bitmap b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Size != b.Size) return false;
            var rect = new Rectangle(Point.Empty, a.Size);
            BitmapData da = null, db = null;
            try
            {
                da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int rowBytes = rect.Width * 3;
                var ra = new byte[rowBytes];
                var rb = new byte[rowBytes];
                for (int y = 0; y < rect.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(da.Scan0 + y * da.Stride, ra, 0, rowBytes);
                    System.Runtime.InteropServices.Marshal.Copy(db.Scan0 + y * db.Stride, rb, 0, rowBytes);
                    if (!ra.AsSpan().SequenceEqual(rb)) return false;
                }
                return true;
            }
            catch { return false; }
            finally
            {
                if (da != null) a.UnlockBits(da);
                if (db != null) b.UnlockBits(db);
            }
        }
    }

    // ドラッグを、操作前の画像に矢印で描くときの形
    public static class DragArrow
    {
        // 押した位置 c(丸の中心・半径 r)から離した位置 e への矢印。丸の縁から始め、先端は e。
        // 丸の中で離した(ほとんど動いていない)ときは false
        public static bool TryGet(Point c, Point e, int r, out PointF from, out PointF to, out float headSize)
        {
            from = to = PointF.Empty; headSize = 0;
            double dx = e.X - c.X, dy = e.Y - c.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= Math.Max(r, 4) + 4) return false;
            double ux = dx / len, uy = dy / len;
            from = new PointF((float)(c.X + ux * r), (float)(c.Y + uy * r));
            to = new PointF(e.X, e.Y);
            headSize = (float)Math.Max(12, Math.Min(26, len / 6));
            return true;
        }

        // 矢印の先の三角形(先端 to、底辺は線に直角)
        public static PointF[] Head(PointF from, PointF to, float size)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return new[] { to, to, to };
            double ux = dx / len, uy = dy / len;
            double bx = to.X - ux * size, by = to.Y - uy * size;   // 底辺の中点
            double half = size * 0.55;
            return new[]
            {
                to,
                new PointF((float)(bx - uy * half), (float)(by + ux * half)),
                new PointF((float)(bx + uy * half), (float)(by - ux * half)),
            };
        }
    }
}
