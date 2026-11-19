using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OperationRecorderApp.ExternalEvidence;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--approved-roi-window" && Guid.TryParseExact(args[1], "N", out var fixtureId))
            return ApprovedRoiFixtureWindow.Run(fixtureId);
        if (args.Length == 2 && args[0] == "--password-evidence-window" && Guid.TryParseExact(args[1], "N", out var passwordFixtureId))
            return PasswordEvidenceFixtureWindow.Run(passwordFixtureId);
        if (args.Length == 1 && args[0] == "--window")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var form = new FixtureWindow();
            form.Shown += (_,_) => Console.WriteLine(JsonSerializer.Serialize(new { hwnd = form.Handle.ToInt64(), pid = Environment.ProcessId }));
            Application.Run(form); return 0;
        }
        return Run(args).GetAwaiter().GetResult();
    }
    static async Task<int> Run(string[] args)
    {
        if (args.Contains("-loglevel") && Path.GetFileName(Environment.ProcessPath!).StartsWith("encoder-hang-", StringComparison.Ordinal))
        { await Task.Delay(Timeout.Infinite); return 0; }
        if (args.Length == 1 && args[0] == "--idle") { await Task.Delay(Timeout.Infinite); return 0; }
        if (args.Length == 1 && args[0].StartsWith("--worker-", StringComparison.Ordinal))
        {
            var work = await EvidenceWire.ReadAsync<CaptureWork>(Console.OpenStandardInput(), CancellationToken.None);
            if (args[0] == "--worker-crash") { Console.Error.Write("DUMMY_SECRET_PROVIDER_ERROR"); return 17; }
            if (args[0] == "--worker-save-hang") EvidenceFiles.Save(work, EvidenceFiles.OpaquePng(new Size(12,12)), new EvidenceUi(Array.Empty<EvidenceNode>(),false), DateTimeOffset.UtcNow);
            await Task.Delay(Timeout.Infinite); return 0;
        }
        if (args.Length == 4 && args[0] is "--host" or "--evidence-host")
            return await EvidenceServer.RunAsync(args[1], int.Parse(args[2]), long.Parse(args[3]), new FakeBackend(), new AnyTarget());
        if (args.Length == 1 && args[0] == "--spawn-broker-then-exit")
        {
            using var owner = Process.GetCurrentProcess(); string name = "oprec-evidence-" + Guid.NewGuid().ToString("N");
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--host", name, owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString() }) info.ArgumentList.Add(arg);
            using var broker = Process.Start(info)!;
            using var connection = new EvidenceConnection(name); await connection.ConnectAsync(10000);
            Console.WriteLine(broker.Id); return 0;
        }
        return 2;
    }
    sealed class AnyTarget : IEvidenceTargetValidator { public bool Matches(EvidenceTarget target) => true; }
    sealed class FakeBackend : IEvidenceBackend
    {
        public async Task<string> CaptureAndSaveAsync(CaptureWork work, CancellationToken token)
        {
            if (work.Target.Pid == 99) await Task.Delay(Timeout.Infinite, token);
            return EvidenceFiles.Save(work, EvidenceFiles.OpaquePng(new Size(60,40)), new EvidenceUi(Array.Empty<EvidenceNode>(),false), DateTimeOffset.UtcNow);
        }
    }
    sealed class FixtureWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        public FixtureWindow()
        {
            Text = "OperationRecorder 専用証拠fixture"; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-18000, -18000, 480, 320); ShowInTaskbar = false;
            var button = new Button { Name = "fixtureAction", Text = "操作", Bounds = new Rectangle(20,20,120,50) };
            var password = new TextBox { Name = "fixturePassword", Text = "DUMMY_SECRET_PASSWORD_7419", UseSystemPasswordChar = true, Bounds = new Rectangle(20,90,300,30) };
            var unknown = new Label { Name = "unknownAuthentication", Text = "DUMMY_SECRET_UNKNOWN_8520", Bounds = new Rectangle(20,140,350,30) };
            var state = new Label { Name = "fixtureState", Text = "操作前", Bounds = new Rectangle(20,190,350,30) };
            button.Click += (_,_) => state.Text = "操作後";
            Controls.AddRange(new Control[] {button,password,unknown,state});
        }
    }
}
