using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace OperationRecorderApp
{
    // タスクトレイのアイコン。状態が一目で分かるよう、丸の色を変える(待機 = 灰 / 記録中 = 赤)
    public static class AppIcons
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static readonly Icon Idle = Make(Color.FromArgb(120, 120, 120));
        public static readonly Icon Recording = Make(Color.FromArgb(220, 30, 30));

        static Icon Make(Color dot)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var body = new SolidBrush(Color.FromArgb(40, 70, 120)))
                        g.FillRectangle(body, 3, 5, 26, 20);                          // 画面
                    using (var screen = new SolidBrush(Color.FromArgb(225, 235, 250)))
                        g.FillRectangle(screen, 5, 7, 22, 16);
                    using (var stand = new SolidBrush(Color.FromArgb(40, 70, 120)))
                        g.FillRectangle(stand, 12, 25, 8, 4);
                    using (var b = new SolidBrush(dot))
                        g.FillEllipse(b, 16, 14, 15, 15);                            // 状態の丸
                    using (var p = new Pen(Color.White, 2))
                        g.DrawEllipse(p, 16, 14, 15, 15);
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
        }
    }
}
