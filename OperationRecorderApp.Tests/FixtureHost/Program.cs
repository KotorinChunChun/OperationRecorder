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
        int actions;
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
            action.Click += (_, _) => { actions++; state.Text = "操作後 " + actions; BackColor = actions % 2 == 0 ? Color.LightGoldenrodYellow : Color.LightSteelBlue; };
            drag.MouseDown += (_, _) => { drag.Focus(); drag.BackColor = Color.Salmon; };
            drag.MouseUp += (_, _) => drag.BackColor = Color.MediumPurple;
            wheel.MouseEnter += (_, _) => wheel.Focus();
            wheel.MouseWheel += (_, e) => { state.Text = "ホイール " + e.Delta; wheel.BackColor = wheel.BackColor == Color.LightGreen ? Color.Aquamarine : Color.LightGreen; };
            FormClosed += (_, _) => { foreach (var cover in covers) cover.Dispose(); };
        }

        object RectangleOf(Control control)
        {
            var rect = control.RectangleToScreen(control.ClientRectangle);
            return new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
        }

        public object Command(string command)
        {
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
            if (command == "reset") { input.Text = password.Text = ""; actions = 0; state.Text = "操作前"; }
            if (command == "close") return new { ok = true };
            return new { ok = true, pid = Environment.ProcessId, hwnd = Handle.ToInt64(), title = Text,
                action = RectangleOf(action), input = RectangleOf(input), password = RectangleOf(password),
                drag = RectangleOf(drag), wheel = RectangleOf(wheel), actions, value = input.Text, passwordLength = password.Text.Length };
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    }
}
