using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OperationRecorderApp
{
    // 画面に重ねる「表示だけ」のウィンドウの共通部分。
    //   クリックを下のウィンドウへ素通しし(WS_EX_TRANSPARENT)、フォーカスを奪わず、記録の画像には写らない(WDA_EXCLUDEFROMCAPTURE)
    public abstract class ScreenDecoration : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        protected static readonly Color KeyColor = Color.FromArgb(255, 0, 255);   // 透明にする色

        // 診断用: 環境変数 OPREC_SHOW_IN_CAPTURE=1 のときは撮影から除外しない(画面表示の見た目を画像で確かめるため)
        public static readonly bool ShowInCaptureForDiagnosis = Environment.GetEnvironmentVariable("OPREC_SHOW_IN_CAPTURE") == "1";

        protected ScreenDecoration()
        {
            Text = WindowTitle.Create("記録表示");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 /*LAYERED*/ | 0x00000020 /*TRANSPARENT*/ | 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000008 /*TOPMOST*/;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!ShowInCaptureForDiagnosis) SetWindowDisplayAffinity(Handle, 0x11 /*WDA_EXCLUDEFROMCAPTURE*/);
        }
    }

    // 記録の対象になっているウィンドウを囲う枠(Windows のステップ記録ツールのように、どこを記録しているかを示す)。
    // 撮影した瞬間は枠を太く・明るくして知らせる
    public sealed class TargetFrame : ScreenDecoration
    {
        const int Thick = 4, Gap = 2;
        static readonly Color Normal = Color.FromArgb(230, 60, 60), FlashColor = Color.FromArgb(255, 230, 80);
        readonly Timer flashTimer = new Timer { Interval = 220 };
        bool flashing;

        public TargetFrame()
        {
            Text = WindowTitle.Create("記録対象の枠");
            flashTimer.Tick += (s, e) => { flashTimer.Stop(); flashing = false; Invalidate(); };
        }

        // r は対象の画面上の矩形(DWM の描画枠)。その外側に枠を描く。null なら隠す
        public void SetTarget(Rectangle? r)
        {
            if (r == null || r.Value.Width <= 0) { if (Visible) Hide(); return; }
            var b = Rectangle.Inflate(r.Value, Thick + Gap, Thick + Gap);
            if (Bounds != b) Bounds = b;
            if (!Visible) Show();
        }

        public void Flash()
        {
            flashing = true;
            Invalidate();
            flashTimer.Stop();
            flashTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int t = flashing ? Thick + 2 : Thick;
            using (var p = new Pen(flashing ? FlashColor : Normal, t) { Alignment = PenAlignment.Inset })
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) flashTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    // 撮影した瞬間に、クリックした位置に広がって消える丸を出す
    public sealed class ClickRipple : ScreenDecoration
    {
        const int Size0 = 72, Frames = 8;
        readonly Timer timer = new Timer { Interval = 40 };
        int frame;
        Color color = Color.FromArgb(255, 70, 70);

        public ClickRipple()
        {
            Text = WindowTitle.Create("撮影の合図");
            Size = new Size(Size0, Size0);
            timer.Tick += (s, e) =>
            {
                frame++;
                if (frame >= Frames) { timer.Stop(); Hide(); return; }
                Invalidate();
            };
        }

        public void Play(Point screen, string kind)
        {
            color = kind == "rightclick" || kind == "rightdrag" ? Color.FromArgb(60, 120, 255)
                  : kind == "middleclick" ? Color.FromArgb(40, 180, 80)
                  : kind == "wheel" ? Color.FromArgb(255, 160, 40)
                  : Color.FromArgb(255, 70, 70);
            Location = new Point(screen.X - Size0 / 2, screen.Y - Size0 / 2);
            frame = 0;
            if (!Visible) Show();
            Invalidate();
            timer.Stop();
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // TransparencyKey で抜くので、アンチエイリアスは使わない(縁に色が残るため)
            int r = 10 + (Size0 / 2 - 12) * frame / Frames;
            int c = Size0 / 2;
            using (var p = new Pen(color, 3)) e.Graphics.DrawEllipse(p, c - r, c - r, r * 2, r * 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
