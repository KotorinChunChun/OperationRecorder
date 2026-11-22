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
    // - ポインタが近づくと透明になり、下のウィンドウに入力を通す
    // - SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)で、記録の画像には写らない
    // - クリックしてもフォーカスを奪わない(WS_EX_NOACTIVATE)。このウィンドウでの操作は記録されない
    public sealed class RecordingOverlay : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        public event Action UndoClicked, RestartClicked, StopClicked, OpenMainClicked;
        public event Action<Rectangle> BoundsChangedByUser;
        public event Action HiddenByUser;

        sealed class Item { public int Seq; public string Text; public Image Thumb; }

        const int MaxItems = 10, ThumbW = 120, ThumbH = 68, RowH = 76, W = 490;
        // 上から: 記録の対象(タイトル) / 状態 / 操作ボタン / 使えるキーの一覧 / 手順の一覧
        const int TargetY = 6, StatusY = 26, ButtonY = 51, KeysY = 96, KeysH = 39, HeaderH = KeysY + KeysH + 27;
        readonly List<Item> items = new List<Item>();
        readonly Label lblStatus = new Label();
        readonly Label lblTarget = new Label();
        readonly Label lblKeys = new Label();
        readonly Label lblTranscript = new Label();
        readonly Panel list = new DoubleBufferedPanel();
        readonly Button btnUndo, btnRestart, btnStop, btnOpen, btnHide;
        readonly System.Windows.Forms.Timer proximityTimer = new System.Windows.Forms.Timer { Interval = 50 };
        readonly OverlayProximity proximity = new OverlayProximity();
        readonly ToolTip tips = new ToolTip();
        bool dragging, resizing, clickThrough;
        Point dragOffset;
        Size resizeStart;
        public bool ExcludedFromCapture { get; private set; }

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
            MinimumSize = new Size(W, HeaderH + RowH);
            Padding = new Padding(1);

            lblTarget.SetBounds(10, TargetY, W - 50, 18);
            lblTarget.Font = new Font("Yu Gothic UI", 9f, FontStyle.Bold);
            lblTarget.ForeColor = Color.FromArgb(200, 210, 230);
            lblTarget.AutoEllipsis = true;
            lblTarget.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblTarget);

            lblStatus.SetBounds(10, StatusY, W - 20, 22);
            lblStatus.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            lblStatus.ForeColor = Color.White;
            Controls.Add(lblStatus);

            btnUndo = MakeButton("1 手取消", 10, (s, e) => UndoClicked?.Invoke());
            btnRestart = MakeButton("取り直し", 154, (s, e) => RestartClicked?.Invoke());
            btnStop = MakeButton("保存して終了", 298, (s, e) => StopClicked?.Invoke());
            btnOpen = MakeButton("…", W - 42, (s, e) => OpenMainClicked?.Invoke());
            btnOpen.SetBounds(W - 42, ButtonY, 32, 39);
            btnOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            tips.SetToolTip(btnOpen, "操作記録ツールの画面を開く");

            btnHide = MakeButton("×", W - 38, (_, _) => { HideOverlay(); HiddenByUser?.Invoke(); });
            btnHide.SetBounds(W - 38, 4, 28, 24);
            btnHide.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnHide.AccessibleName = "オーバーレイを非表示";
            tips.SetToolTip(btnHide, btnHide.AccessibleName);

            lblKeys.SetBounds(10, KeysY, W - 20, KeysH);
            lblKeys.Font = new Font("Yu Gothic UI", 8.5f);
            lblKeys.ForeColor = Color.FromArgb(185, 192, 205);
            lblKeys.Text = "近づくと透過します。Ctrl・Shift・Alt・Winを押すと操作できます。\nCtrl+Shiftだけで手動撮影";
            lblKeys.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblKeys);
            lblTranscript.SetBounds(10, KeysY + KeysH, W - 20, 23);
            lblTranscript.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblTranscript.AutoEllipsis = true;
            Controls.Add(lblTranscript);

            list.SetBounds(6, HeaderH, W - 12, Height - HeaderH - 18);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            list.BackColor = Color.FromArgb(44, 49, 59);
            list.Paint += PaintList;
            Controls.Add(list);

            // ボタン以外の場所をつかんでドラッグできる
            foreach (Control c in new Control[] { this, lblStatus, lblTarget, lblKeys, lblTranscript, list })
            {
                c.MouseDown += OnDragStart;
                c.MouseMove += OnDragMove;
                c.MouseUp += OnDragEnd;
            }
            var grip = new Label { Text = "◢", ForeColor = Color.Silver, Cursor = Cursors.SizeNWSE,
                AccessibleName = "サイズ変更", Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            grip.SetBounds(Width - 20, Height - 18, 18, 16);
            grip.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                resizing = true;
                grip.Capture = true;
                dragOffset = Cursor.Position;
                resizeStart = Size;
            };
            grip.MouseMove += (_, _) =>
            {
                if (!resizing) return;
                var now = Cursor.Position;
                Size = new Size(Math.Max(MinimumSize.Width, resizeStart.Width + now.X - dragOffset.X),
                    Math.Max(MinimumSize.Height, resizeStart.Height + now.Y - dragOffset.Y));
            };
            grip.MouseCaptureChanged += (_, _) =>
            {
                if (!resizing || grip.Capture) return;
                resizing = false;
                BoundsChangedByUser?.Invoke(Bounds);
            };
            grip.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) grip.Capture = false; };
            Controls.Add(grip);
            proximityTimer.Tick += (_, _) => UpdateProximity();
        }

        Button MakeButton(string text, int x, EventHandler click)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(62, 70, 84),
                Bounds = new Rectangle(x, ButtonY, 138, 39), TabStop = false,
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
        public void ShowAt(int x, int y, int width, int height)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (width > 0 && height > 0)
                Size = new Size(Math.Min(width, wa.Width), Math.Min(height, wa.Height));
            var pos = x == int.MinValue ? new Point(wa.Right - Width - 16, wa.Bottom - Height - 16) : new Point(x, y);
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(pos, Size))))
                pos = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            Location = pos;
            if (!Visible) Show();
            proximity.Reset();
            UpdateProximity();
            proximityTimer.Start();
        }

        public void HideOverlay()
        {
            proximityTimer.Stop();
            SetClickThrough(false);
            Hide();
        }

        // タイトル: 記録の対象(記録を始めたときに決まったウィンドウ・アプリ・ディスプレイの名前)
        public void SetTarget(string text)
        {
            string t = string.IsNullOrEmpty(text) ? "" : "対象: " + text;
            if (lblTarget.Text != t) lblTarget.Text = t;
        }

        public void SetShortcuts(HotkeySet keys)
        {
            btnUndo.Text = "1 手取消\n" + keys.Undo;
            btnRestart.Text = "取り直し\n" + keys.Restart;
            btnStop.Text = "保存して終了\n" + keys.Stop;
        }

        public void SetLiveTranscript(string text)
        {
            string display = string.IsNullOrWhiteSpace(text) ? "" : "文字起こし  " + text;
            if (lblTranscript.Text != display) lblTranscript.Text = display;
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
            BoundsChangedByUser?.Invoke(Bounds);
        }

        void UpdateProximity()
        {
            if (!Visible) return;
            bool modifier = Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5b) || Down(0x5c);
            SetClickThrough(proximity.IsTransparent(Bounds, Cursor.Position, modifier || dragging || resizing));
        }

        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        void SetClickThrough(bool value)
        {
            if (value == clickThrough || !IsHandleCreated) return;
            const int ExtendedStyle = -20, Transparent = 0x20;
            clickThrough = value;
            if (value) Opacity = 0;
            int style = GetWindowLong(Handle, ExtendedStyle);
            SetWindowLong(Handle, ExtendedStyle, value ? style | Transparent : style & ~Transparent);
            if (!value) Opacity = 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { proximityTimer.Dispose(); tips.Dispose(); ClearItems(); }
            base.Dispose(disposing);
        }
    }
}
