using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    // 記録中に画面の上に出す小さなウィンドウ。記録の対象・状態・操作ボタン・使えるキー・直近 10 手のサムネイルを表示する。
    // - ドラッグで好きな場所へ動かせる
    // - マウスポインタがあるウィンドウに重なると、重ならない画面の隅へ逃げる(操作の邪魔をしない)
    // - SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)で、記録の画像には写らない
    // - クリックしてもフォーカスを奪わない(WS_EX_NOACTIVATE)。このウィンドウでの操作は記録されない
    public sealed class RecordingOverlay : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        public event Action UndoClicked, RestartClicked, StopClicked, OpenMainClicked;
        public event Action<Point> MovedByUser;

        sealed class Item { public int Seq; public string Text; public Image Thumb; }

        const int MaxItems = 10, ThumbW = 120, ThumbH = 68, RowH = 76, W = 360;
        // 上から: 記録の対象(タイトル) / 状態 / 操作ボタン / 使えるキーの一覧 / 手順の一覧
        const int TargetY = 6, StatusY = 26, ButtonY = 52, KeysY = 84, KeysH = 52, HeaderH = KeysY + KeysH + 4;
        readonly List<Item> items = new List<Item>();
        readonly Label lblStatus = new Label();
        readonly Label lblTarget = new Label();
        readonly Label lblKeys = new Label();
        readonly Panel list = new DoubleBufferedPanel();
        readonly Button btnUndo, btnRestart, btnStop, btnOpen;
        readonly System.Windows.Forms.Timer fleeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        bool dragging;
        Point dragOffset;
        DateTime lastUserMove = DateTime.MinValue;
        public bool ExcludedFromCapture { get; private set; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool FleeEnabled { get; set; } = true;   // マウスポインタのあるウィンドウから逃げるか

        sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

        public RecordingOverlay()
        {
            Text = WindowTitle.Create("撮影ログ");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 36, 44);
            ForeColor = Color.White;
            Font = new Font("Yu Gothic UI", 9f);
            Size = new Size(W, HeaderH + RowH * 3);
            Padding = new Padding(1);

            lblTarget.SetBounds(10, TargetY, W - 50, 18);
            lblTarget.Font = new Font("Yu Gothic UI", 9f, FontStyle.Bold);
            lblTarget.ForeColor = Color.FromArgb(200, 210, 230);
            lblTarget.AutoEllipsis = true;
            Controls.Add(lblTarget);

            lblStatus.SetBounds(10, StatusY, W - 20, 22);
            lblStatus.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            lblStatus.ForeColor = Color.White;
            Controls.Add(lblStatus);

            btnUndo = MakeButton("1 手取消", 10, (s, e) => UndoClicked?.Invoke());
            btnRestart = MakeButton("取り直し", 95, (s, e) => RestartClicked?.Invoke());
            btnStop = MakeButton("保存して終了", 180, (s, e) => StopClicked?.Invoke());
            btnStop.Width = 100;
            btnOpen = MakeButton("…", W - 40, (s, e) => OpenMainClicked?.Invoke());
            btnOpen.Width = 30;
            new ToolTip().SetToolTip(btnOpen, "操作記録ツールの画面を開く");

            lblKeys.SetBounds(10, KeysY, W - 20, KeysH);
            lblKeys.Font = new Font("Yu Gothic UI", 8.5f);
            lblKeys.ForeColor = Color.FromArgb(185, 192, 205);
            Controls.Add(lblKeys);

            list.SetBounds(6, HeaderH, W - 12, Height - HeaderH);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            list.BackColor = Color.FromArgb(44, 49, 59);
            list.Paint += PaintList;
            Controls.Add(list);

            // ボタン以外の場所をつかんでドラッグできる
            foreach (Control c in new Control[] { this, lblStatus, lblTarget, lblKeys, list })
            {
                c.MouseDown += OnDragStart;
                c.MouseMove += OnDragMove;
                c.MouseUp += OnDragEnd;
            }
            fleeTimer.Tick += (s, e) => FleeFromPointerWindow();
        }

        Button MakeButton(string text, int x, EventHandler click)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(62, 70, 84),
                Bounds = new Rectangle(x, ButtonY, 80, 26), TabStop = false,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 100, 120);
            b.Click += click;
            Controls.Add(b);
            return b;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/ | 0x00000008 /*WS_EX_TOPMOST*/;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 記録の画像に写らないようにする(Windows 10 2004 以降)。使えない環境では写るので、状態の表示で知らせる
            ExcludedFromCapture = ScreenDecoration.ShowInCaptureForDiagnosis || SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Color.FromArgb(90, 100, 120))) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        // 画面の右下(または前回の位置)に出す
        public void ShowAt(int x, int y)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var pos = x == int.MinValue ? new Point(wa.Right - Width - 16, wa.Bottom - Height - 16) : new Point(x, y);
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(pos, Size))))
                pos = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            Location = pos;
            if (!Visible) Show();
            fleeTimer.Start();
        }

        public void HideOverlay()
        {
            fleeTimer.Stop();
            Hide();
        }

        // タイトル: 記録の対象(記録を始めたときに決まったウィンドウ・アプリ・ディスプレイの名前)
        public void SetTarget(string text)
        {
            string t = string.IsNullOrEmpty(text) ? "" : "対象: " + text;
            if (lblTarget.Text != t) lblTarget.Text = t;
        }

        // 使えるキーの一覧(操作キーの設定で変わる)
        public void SetShortcuts(string text)
        {
            if (lblKeys.Text != text) lblKeys.Text = text;
        }

        public void SetStatus(string text, Color dot, bool recording)
        {
            lblStatus.Text = "● " + text + (ExcludedFromCapture ? "" : "(この表示は画像に写ります)");
            lblStatus.ForeColor = dot;
            btnUndo.Enabled = btnRestart.Enabled = btnStop.Enabled = recording;
        }

        public void ClearItems()
        {
            foreach (var it in items) it.Thumb?.Dispose();
            items.Clear();
            list.Invalidate();
        }

        // 保存された手順(events.jsonl の 1 行)を一覧の先頭に足す
        public void AddStep(string line, string sessionDir, int number)
        {
            try
            {
                var e = JsonNode.Parse(line) as JsonObject;
                if (e == null) return;
                int seq = (int)e["seq"];
                string kind = (string)e["kind"];
                string head = ReportGenerator.HeadlineFromJson(line);
                if (string.IsNullOrEmpty(head)) head = kind == "capture" ? "画面キャプチャ" + (e["trigger"] != null ? "(" + (string)e["trigger"] + ")" : "") : kind;
                string file = (string)(e["before"]?["file"] ?? e["after"]?["file"]);
                Image thumb = null;
                if (file != null)
                {
                    string path = Path.Combine(sessionDir, file);
                    if (File.Exists(path))
                        using (var src = Image.FromStream(new MemoryStream(File.ReadAllBytes(path))))
                            thumb = MakeThumb(src);
                }
                items.Insert(0, new Item { Seq = seq, Text = number + ". " + head, Thumb = thumb });
                while (items.Count > MaxItems) { items[items.Count - 1].Thumb?.Dispose(); items.RemoveAt(items.Count - 1); }
                list.Invalidate();
            }
            catch { }
        }

        public void RemoveStep(int seq)
        {
            var it = items.FirstOrDefault(i => i.Seq == seq);
            if (it == null) return;
            it.Thumb?.Dispose();
            items.Remove(it);
            list.Invalidate();
        }

        static Image MakeThumb(Image src)
        {
            double k = Math.Min((double)ThumbW / src.Width, (double)ThumbH / src.Height);
            int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return bmp;
        }

        void PaintList(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "まだ撮影していません。\n操作すると、ここに直近 " + MaxItems + " 手が出ます。", Font,
                    new Rectangle(8, 8, list.Width - 16, 60), Color.Silver, TextFormatFlags.WordBreak);
                return;
            }
            int y = 4;
            foreach (var it in items)
            {
                if (y > list.Height) break;
                var thumbRect = new Rectangle(6, y + (RowH - ThumbH) / 2 - 2, ThumbW, ThumbH);
                using (var bg = new SolidBrush(Color.FromArgb(26, 29, 36))) g.FillRectangle(bg, thumbRect);
                if (it.Thumb != null)
                    g.DrawImage(it.Thumb, thumbRect.X + (ThumbW - it.Thumb.Width) / 2, thumbRect.Y + (ThumbH - it.Thumb.Height) / 2, it.Thumb.Width, it.Thumb.Height);
                TextRenderer.DrawText(g, it.Text, Font, new Rectangle(ThumbW + 14, y + 4, list.Width - ThumbW - 22, RowH - 8), Color.White,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                using (var p = new Pen(Color.FromArgb(60, 66, 78))) g.DrawLine(p, 4, y + RowH - 2, list.Width - 4, y + RowH - 2);
                y += RowH;
            }
        }

        // 一覧の行数に合わせて高さを変える(最大 MaxItems 行)
        public void FitHeight(int rows)
        {
            int h = HeaderH + RowH * Math.Max(3, Math.Min(MaxItems, rows));
            var wa = Screen.FromControl(this).WorkingArea;
            h = Math.Min(h, wa.Height - 20);
            if (Height != h) { Height = h; list.Height = h - HeaderH; }
        }

        // ---------- ドラッグ ----------

        void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
        }

        void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Location = new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y);
        }

        void OnDragEnd(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            lastUserMove = DateTime.Now;
            MovedByUser?.Invoke(Location);
        }

        // ---------- ポインタのあるウィンドウから逃げる ----------

        void FleeFromPointerWindow()
        {
            if (!Visible || dragging) return;
            N.POINT cp;
            if (!N.GetCursorPos(out cp)) return;
            var cursor = new Point(cp.X, cp.Y);
            if (!OverlayPlacement.ShouldCheck(FleeEnabled, (DateTime.Now - lastUserMove).TotalSeconds, Bounds, cursor)) return;
            IntPtr root = WinUtil.Root(N.WindowFromPoint(cp));
            if (root == IntPtr.Zero || WinUtil.PidOf(root) == (uint)Environment.ProcessId) return;
            if (TargetRules.IsShellWindow(WinUtil.ClassOf(root))) return;   // デスクトップ・タスクバーからは逃げない
            var to = OverlayPlacement.Choose(Bounds, WinUtil.GetFrame(root), Screen.AllScreens.Select(sc => sc.WorkingArea));
            if (to.HasValue) Location = to.Value;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fleeTimer.Dispose(); ClearItems(); }
            base.Dispose(disposing);
        }
    }
}
