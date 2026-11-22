using System.IO.Pipes;
using System.Text.Json;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

// 専用試験hostからだけ操作される別PIDの記録対象。通常設定・ファイル・登録へアクセスしない。
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if (args.Length != 2 || !Guid.TryParseExact(args[1], "N", out _)) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var form = new FixtureForm(args[1]);
        form.Shown += (sender, e) => { _ = Serve(form, args[0], args[1]); };
        Application.Run(form);
    }

    static async Task Serve(FixtureForm form, string pipeName, string token)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var input = new StreamReader(pipe, leaveOpen: true);
            using var output = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            while (await input.ReadLineAsync(timeout.Token) is string line)
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.GetProperty("token").GetString() != token) return;
                var command = json.RootElement.GetProperty("command").GetString();
                var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.BeginInvoke(new Action(() =>
                {
                    try { ready.SetResult(form.Command(command ?? "")); }
                    catch (Exception error) { ready.SetException(error); }
                }));
                var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await output.WriteLineAsync(JsonSerializer.Serialize(result));
                if (command == "close") { form.Close(); return; }
            }
        }
        catch { if (!form.IsDisposed) form.BeginInvoke(new Action(form.Close)); }
    }

    sealed class FixtureForm : Form
    {
        readonly Button action = new() { Name = "fixtureAction", Text = "記録対象の操作", Location = new Point(30, 40), Size = new Size(230, 60) };
        readonly TextBox input = new() { Name = "fixtureInput", Location = new Point(30, 145), Width = 230 };
        readonly TextBox password = new() { Name = "fixturePassword", Location = new Point(30, 210), Width = 230, UseSystemPasswordChar = true };
        readonly Panel drag = new() { Name = "fixtureDrag", Location = new Point(330, 40), Size = new Size(230, 100), BackColor = Color.LightBlue, TabStop = true };
        readonly Panel wheel = new() { Name = "fixtureWheel", Location = new Point(330, 185), Size = new Size(230, 170), BackColor = Color.LightGreen, TabStop = true };
        readonly Label state = new() { Location = new Point(30, 310), AutoSize = true, Text = "操作前" };
        readonly List<Form> covers = new();
        Form? secondary;
        Panel? snapshotChild;
        bool snapshotDialogOpen;
        DialogResult snapshotDialogResult;
        int actions;
        int lastKey;
        Form? replayWindow;
        System.Windows.Forms.Timer? replayTimer;
        string? replayScenario;
        string? readyTitle;
        bool replayReady;
        int earlyActions;
        public FixtureForm(string token)
        {
            Text = "OperationRecorder fixture " + token;
            Name = "RecordingFixture";
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(40, 40), new Size(650, 440));
            BackColor = Color.FromArgb(246, 242, 220);
            Controls.AddRange(new Control[] { action, input, password, drag, wheel, state });
            Controls.Add(new Label { Text = "通常入力", AutoSize = true, Location = new Point(30, 120) });
            Controls.Add(new Label { Text = "password試験用", AutoSize = true, Location = new Point(30, 185) });
            action.Click += (_, _) =>
            {
                actions++;
                if (replayScenario != null && !replayReady) earlyActions++;
                state.Text = "操作後 " + actions;
                BackColor = actions % 2 == 0 ? Color.LightGoldenrodYellow : Color.LightSteelBlue;
            };
            action.KeyDown += (_, e) => lastKey = (int)e.KeyCode;
            action.PreviewKeyDown += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Up or Keys.Right or Keys.Down) e.IsInputKey = true; };
            drag.MouseDown += (_, _) => { drag.Focus(); drag.BackColor = Color.Salmon; };
            drag.MouseUp += (_, _) => drag.BackColor = Color.MediumPurple;
            wheel.MouseEnter += (_, _) => wheel.Focus();
            wheel.MouseWheel += (_, e) => { state.Text = "ホイール " + e.Delta; wheel.BackColor = wheel.BackColor == Color.LightGreen ? Color.Aquamarine : Color.LightGreen; };
            FormClosed += (_, _) =>
            {
                foreach (var cover in covers) cover.Dispose();
                replayTimer?.Dispose();
                replayWindow?.Dispose();
            };
        }

        void StageReplay(string scenario)
        {
            replayScenario = scenario;
            readyTitle = Text;
            switch (scenario)
            {
                case "startup": Hide(); break;
                case "title": Text += " 読み込み中"; break;
                case "window-disabled": Enabled = false; break;
                case "minimized": WindowState = FormWindowState.Minimized; break;
                case "control-disabled": action.Enabled = false; break;
                case "control-hidden": action.Hide(); break;
                case "control-offscreen": action.Location = new Point(ClientSize.Width + 100, action.Top); break;
                case "modal":
                    replayWindow = new Form { Text = readyTitle + " 読み込み確認", Size = new Size(300, 150) };
                    replayWindow.Controls.Add(new Label { Text = "準備中", AutoSize = true });
                    BeginInvoke(new Action(() => replayWindow.ShowDialog(this)));
                    break;
                case "duplicate-window":
                    replayWindow = new Form { Text = readyTitle, Size = new Size(300, 150) };
                    replayWindow.Controls.Add(DuplicateAction());
                    replayWindow.Show(this);
                    Command("front");
                    break;
                case "duplicate-control":
                    Controls.Add(DuplicateAction());
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        Button DuplicateAction()
        {
            var button = new Button { Name = action.Name, Text = action.Text, Location = new Point(30, 110) };
            button.Click += (_, _) => actions++;
            return button;
        }

        void ScheduleReplayReady()
        {
            replayTimer = new System.Windows.Forms.Timer { Interval = 220 };
            replayTimer.Tick += (_, _) =>
            {
                replayTimer.Stop();
                replayWindow?.Close();
                Enabled = action.Enabled = true;
                if (replayScenario == "minimized") WindowState = FormWindowState.Normal;
                Text = readyTitle;
                action.Location = new Point(30, 40);
                action.Show();
                Command("front");
                replayReady = true;
            };
            replayTimer.Start();
        }

        object RectangleOf(Control control)
        {
            var rect = control.RectangleToScreen(control.ClientRectangle);
            return new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
        }

        public object Command(string command)
        {
            if (command.StartsWith("replay-stage:", StringComparison.Ordinal)) StageReplay(command["replay-stage:".Length..]);
            if (command == "replay-ready-later") ScheduleReplayReady();
            if (command == "cover")
            {
                foreach (var screen in Screen.AllScreens.Where(screen => !screen.Primary))
                {
                    var cover = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                        Bounds = screen.Bounds, BackColor = Color.FromArgb(236, 232, 210), TopMost = true, Text = Text + " cover" };
                    cover.Show(); covers.Add(cover);
                }
                FormBorderStyle = FormBorderStyle.None; Bounds = Screen.PrimaryScreen!.Bounds; TopMost = true;
            }
            if (command is "cover" or "front")
            {
                Show(); BringToFront(); Activate(); action.Focus();
                // CLIの親が前面アプリではなくても、専用fixtureの初期前面化を完了させる。
                // 接続はこの要求の間だけとし、利用者の前面ロック設定は変更しない。
                uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                uint currentThread = GetCurrentThreadId();
                bool attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
                try { BringWindowToTop(Handle); SetForegroundWindow(Handle); }
                finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
            }
            if (command == "reset") { input.Text = password.Text = ""; actions = 0; lastKey = 0; state.Text = "操作前"; BackColor = Color.FromArgb(246, 242, 220); }
            if (command == "action") action.PerformClick();
            if (command == "window-open")
            {
                secondary?.Dispose();
                secondary = new Form { Text = "追加画面 " + Name, Name = "FixtureSecondary", StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(Left + 70, Top + 70, 340, 180) };
                secondary.Controls.Add(new Label { Text = "ウィンドウ監視の専用画面", AutoSize = true });
                secondary.Show(this);
            }
            if (command == "window-main") Activate();
            if (command == "window-secondary") secondary?.Activate();
            if (command == "window-close") { secondary?.Dispose(); secondary = null; }
            if (command == "snapshot-child-open" && snapshotChild == null)
            {
                snapshotChild = new Panel { Name = "fixtureSnapshotChild", BackColor = Color.LimeGreen,
                    Bounds = new Rectangle(326, 105, 148, 46) };
                Controls.Add(snapshotChild);
                snapshotChild.BringToFront();
                snapshotChild.Refresh();
            }
            if (command == "snapshot-password-fill") password.Text = "OPREC_TEST_PASSWORD_0a17f68e";
            if (command == "snapshot-password-clear") password.Clear();
            if (command == "snapshot-dialog-open" && !snapshotDialogOpen)
            {
                snapshotDialogOpen = true;
                snapshotDialogResult = DialogResult.None;
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        snapshotDialogResult = MessageBox.Show(this, "これは検証専用の確認です。", "専用システムダイアログ",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                    }
                    finally { snapshotDialogOpen = false; }
                }));
            }
            if (command == "snapshot-paint")
            {
                Refresh();
                secondary?.Refresh();
                DwmFlush();
            }
            if (command == "shift-layout")
            {
                Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location + new Size(40, 40), new Size(1000, 720));
                action.Location = new Point(600, 450);
            }
            if (command == "close") return new { ok = true };
            return new { ok = true, pid = Environment.ProcessId, hwnd = Handle.ToInt64(), title = Text,
                secondaryHwnd = secondary?.Handle.ToInt64() ?? 0,
                snapshotChildHwnd = snapshotChild?.Handle.ToInt64() ?? 0, snapshotDialogOpen,
                snapshotDialogResult = snapshotDialogResult.ToString(),
                action = RectangleOf(action), input = RectangleOf(input), password = RectangleOf(password),
                drag = RectangleOf(drag), wheel = RectangleOf(wheel), actions, lastKey, value = input.Text, passwordLength = password.Text.Length,
                replayReady, earlyActions };
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();
    }
}
