using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

// The fixed colors are the fixture's reviewed assets. Its changing text and state are test data.
internal sealed class ApprovedRoiFixtureWindow : Form
{
    const string DummyPassword = "ROI_FIXTURE_PASSWORD_NOT_A_CREDENTIAL";
    readonly CancellationTokenSource stop = new();
    readonly string pipeName;
    TextBox? authentication;
    int actionCount;
    bool unknown;

    ApprovedRoiFixtureWindow(Guid fixtureId)
    {
        pipeName = "oprec-roi-fixture-" + fixtureId.ToString("N");
        Text = "固定領域の取得試験";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        var working = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(working.Left + 48, working.Top + 48);
        ClientSize = new Size(360, 230);
        BackColor = Color.White;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    internal static int Run(Guid fixtureId)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var fixture = new ApprovedRoiFixtureWindow(fixtureId);
        Application.Run(fixture);
        return 0;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        int disabled = 1;
        int doNotRound = 1;
        DwmSetWindowAttribute(Handle, 3, ref disabled, sizeof(int)); // DWMWA_TRANSITIONS_FORCEDISABLED
        DwmSetWindowAttribute(Handle, 33, ref doNotRound, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        Refresh();
        DwmFlush();
        _ = ServeAsync(stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { hwnd = Handle.ToInt64(), pid = Environment.ProcessId }));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.FillRectangle(Brushes.Blue, new Rectangle(16, 20, 64, 22));
        graphics.FillRectangle(Brushes.Lime, new Rectangle(24, 86, 48, 23));
        graphics.FillRectangle(unknown ? Brushes.Purple : Brushes.Red, new Rectangle(270, 16, 14, 14));
        graphics.DrawString("承認済み見出し", Font, Brushes.Black, 94, 24);
        graphics.DrawString("承認済み操作", Font, Brushes.Black, 86, 92);
        string stamp = actionCount == 0 ? "2031-02-03 04:05" : "2032-11-12 16:17";
        string exportPath = actionCount == 0 ? "C:/FakeData/result-A" : "C:/FakeData/result-B";
        graphics.DrawString("偽の日時 " + stamp, Font, Brushes.Black, 16, 151);
        graphics.DrawString("偽の保存先 " + exportPath, Font, Brushes.Black, 16, 177);
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
                string? command = await reader.ReadLineAsync(token);
                string result = Apply(command);
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
                break;
            case "unknown":
                unknown = true;
                break;
            case "authentication":
                unknown = false;
                authentication ??= new TextBox
                {
                    Name = "fixtureAuthenticationPassword",
                    UseSystemPasswordChar = true,
                    Text = DummyPassword,
                    Bounds = new Rectangle(18, 18, 130, 24)
                };
                if (!Controls.Contains(authentication)) Controls.Add(authentication);
                authentication.BringToFront();
                break;
            case "exit":
                BeginInvoke(Close);
                break;
            default:
                return "unsupported";
        }
        if (command is "advance" or "unknown" or "authentication")
        {
            Refresh();
            authentication?.Refresh();
            DwmFlush();
        }
        return JsonSerializer.Serialize(new { actionCount, unknown, authentication = authentication != null });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        stop.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) stop.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
