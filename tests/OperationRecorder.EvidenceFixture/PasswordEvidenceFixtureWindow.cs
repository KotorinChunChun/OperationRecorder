using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

internal sealed class PasswordEvidenceFixtureWindow : Form
{
    const string StandardDummy = "OPREC_STANDARD_FAKE_PASSWORD";
    const string CustomDummy = "OPREC_CUSTOM_FAKE_PASSWORD";
    readonly CancellationTokenSource stop = new();
    readonly string pipeName;
    readonly TextBox standard;
    readonly Label standardLabel;
    readonly Label customLabel;
    readonly CustomPasswordControl custom;
    readonly bool allowTransition;
    readonly List<TransitionWindow> destinations = new();
    TransitionWindow? notice;
    Control? unknown;
    int actionCount;

    PasswordEvidenceFixtureWindow(Guid id, bool allowTransition)
    {
        this.allowTransition = allowTransition;
        pipeName = "oprec-password-fixture-" + id.ToString("N");
        Text = "パスワード証拠の専用試験";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        var working = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(working.Left + 48, working.Top + 48);
        ClientSize = new Size(440, 252);
        BackColor = Color.White;
        DoubleBuffered = true;
        standardLabel = CreateLabel("専用標準欄:", 74);
        customLabel = CreateLabel("専用カスタム欄:", 122);
        standard = new TextBox
        {
            Name = "standardPassword",
            UseSystemPasswordChar = true,
            Text = StandardDummy,
            AutoSize = false,
            Bounds = new Rectangle(164, 74, 220, 28)
        };
        custom = new CustomPasswordControl
        {
            Name = "customPassword",
            Bounds = new Rectangle(164, 122, 220, 28)
        };
        Controls.AddRange(new Control[] { standardLabel, standard, customLabel, custom });
    }

    static Label CreateLabel(string text, int top) => new()
    {
        Text = text, Bounds = new Rectangle(20, top, 140, 28),
        AutoSize = false, FlatStyle = FlatStyle.System, UseCompatibleTextRendering = false
    };

    protected override bool ShowWithoutActivation => true;

    internal static int Run(Guid id, bool allowTransition = false)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var fixture = new PasswordEvidenceFixtureWindow(id, allowTransition);
        if (allowTransition)
        {
            // Keep the fixture message pump and its pipe alive if the original login HWND is destroyed.
            using var lifetime = new ApplicationContext();
            void ShowFixture(object? sender, EventArgs args)
            {
                Application.Idle -= ShowFixture;
                fixture.Show();
            }
            fixture.FormClosed += (_, _) => lifetime.ExitThread();
            Application.Idle += ShowFixture;
            Application.Run(lifetime);
        }
        else Application.Run(fixture);
        return 0;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetCaptureAppearance(Handle);
        Repaint();
        _ = ServeAsync(stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { hwnd = Handle.ToInt64(), pid = Environment.ProcessId }));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.FillRectangle(actionCount == 0 ? Brushes.Blue : Brushes.Lime, 20, 18, 110, 24);
        e.Graphics.DrawString("専用フォームの公開見出し", Font, Brushes.Black, 146, 20);
        e.Graphics.DrawString(actionCount == 0 ? "公開状態 A" : "公開状態 B", Font, Brushes.Black, 20, 186);
    }

    async Task ServeAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string result = Apply(await reader.ReadLineAsync(token));
                await writer.WriteLineAsync(result.AsMemory(), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) when (token.IsCancellationRequested) { }
    }

    string Apply(string? command)
    {
        switch (command)
        {
            case "status":
                break;
            case "advance":
                actionCount++;
                Location = new Point(Left + 18, Top + 12);
                Repaint();
                break;
            case "resize-field":
                custom.Width += 32;
                Repaint();
                break;
            case "reset-field":
                custom.Width = 220;
                Repaint();
                break;
            case "unknown":
                unknown ??= new Button { Text = "未登録の専用操作", Bounds = new Rectangle(215, 182, 130, 28) };
                if (!Controls.Contains(unknown)) Controls.Add(unknown);
                Repaint();
                break;
            case "transition-known" or "transition-known-destroyed" or "transition-unrecognized" or "transition-ambiguous" or
                 "transition-known-owned" or "transition-unrecognized-owned" when allowTransition && actionCount == 0:
                actionCount++;
                var destination = ShowDestination(new Point(Left + 24, Top + 32), known: command != "transition-unrecognized");
                if (command == "transition-ambiguous") ShowDestination(new Point(Left + 72, Top + 80), known: true);
                if (command is "transition-known-owned" or "transition-unrecognized-owned")
                {
                    notice = new TransitionWindow(new Point(destination.Left + 88, destination.Top + 105),
                        known: command == "transition-known-owned", owned: true);
                    notice.Show(destination);
                    notice.Refresh();
                }
                Hide();
                if (command == "transition-known-destroyed")
                {
                    // Release the old controls before the parent handle so WinForms cannot reparent them.
                    standard.Dispose();
                    standardLabel.Dispose();
                    custom.Dispose();
                    customLabel.Dispose();
                    DestroyHandle();
                }
                DwmFlush();
                return TransitionStatus();
            case "transition-status" when allowTransition:
                return TransitionStatus();
            default:
                return "unsupported";
        }
        return JsonSerializer.Serialize(new
        {
            actionCount, x = Left, y = Top, standard = standard.Handle.ToInt64(), custom = custom.Handle.ToInt64(),
            standardLabel = standardLabel.Handle.ToInt64(), customLabel = customLabel.Handle.ToInt64(),
            customWidth = custom.Width, unknown = unknown != null
        });
    }

    string TransitionStatus() => JsonSerializer.Serialize(new
    {
        actionCount, originVisible = Visible, originDestroyed = !IsHandleCreated,
        destinations = destinations.Select(window => window.Handle.ToInt64()).ToArray(),
        notice = notice?.Handle.ToInt64()
    });

    TransitionWindow ShowDestination(Point location, bool known)
    {
        var destination = new TransitionWindow(location, known);
        destinations.Add(destination);
        destination.Show();
        destination.Refresh();
        return destination;
    }

    static void SetCaptureAppearance(IntPtr window)
    {
        int disabled = 1;
        int doNotRound = 1;
        DwmSetWindowAttribute(window, 3, ref disabled, sizeof(int)); // DWMWA_TRANSITIONS_FORCEDISABLED
        DwmSetWindowAttribute(window, 33, ref doNotRound, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    void Repaint()
    {
        Refresh();
        standard.Refresh();
        custom.Refresh();
        DwmFlush();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        stop.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            notice?.Dispose();
            foreach (var destination in destinations) destination.Dispose();
            stop.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    sealed class TransitionWindow : Form
    {
        readonly bool known;
        readonly bool owned;
        protected override bool ShowWithoutActivation => true;

        public TransitionWindow(Point location, bool known, bool owned = false)
        {
            this.known = known;
            this.owned = owned;
            Text = owned ? "遷移後の専用通知" : "遷移後の専用試験";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = location;
            ClientSize = new Size(360, 230);
            BackColor = Color.White;
            DoubleBuffered = true;
        }

        protected override void OnLoad(EventArgs e)
        {
            SetCaptureAppearance(Handle);
            base.OnLoad(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.FillRectangle(owned ? Brushes.Cyan : Brushes.Blue, 16, 20, 64, 22);
            e.Graphics.FillRectangle(owned ? Brushes.Yellow : Brushes.Lime, 24, 86, 48, 23);
            e.Graphics.FillRectangle(!known ? Brushes.Purple : owned ? Brushes.Orange : Brushes.Red, 270, 16, 14, 14);
            e.Graphics.DrawString(owned ? "承認された通知" : "承認された画面", Font, Brushes.Black, 94, 24);
            e.Graphics.DrawString("非公開の架空データ 2031-02-03", Font, Brushes.Black, 16, 151);
        }
    }

    sealed class CustomPasswordControl : Control
    {
        public CustomPasswordControl()
        {
            AccessibleRole = AccessibleRole.Pane;
            BackColor = Color.Magenta;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, CustomDummy, Font, new Rectangle(12, 0, Width - 12, Height), Color.Black,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
