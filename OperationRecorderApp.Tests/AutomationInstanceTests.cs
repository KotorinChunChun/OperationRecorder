using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Xunit;

namespace OperationRecorderApp.Tests
{
    public class AutomationInstanceTests
    {
        [Fact]
        public void 通常起動の識別子を保ち試験起動だけ三種の識別子を分離する()
        {
            var normal = AutomationInstance.Parse(Array.Empty<string>());
            Assert.Equal(@"Local\OperationRecorderApp", normal.MutexName);
            Assert.Equal(InstanceSignal.DefaultPrefix, normal.SignalPrefix);
            Assert.Equal("operation-recorder", normal.ManagementId);
            using var scope = new AutomationScope();
            var token = Guid.NewGuid().ToString("N");
            var isolated = AutomationInstance.Parse(new[] { "--test-instance", token, "--passive", "--settings-dir", scope.Root });
            Assert.Contains(token, isolated.MutexName); Assert.Contains(token, isolated.SignalPrefix); Assert.Contains(token, isolated.ManagementId);
            Assert.NotEqual(normal.MutexName, isolated.MutexName); Assert.NotEqual(normal.SignalPrefix, isolated.SignalPrefix);
            Assert.Equal(isolated.MutexName, AutomationInstance.Parse(new[] { "--test-instance", token.ToUpperInvariant(), "--passive", "--settings-dir", scope.Root }).MutexName);
        }

        [Fact]
        public void 不完全な試験起動は実EXEでも設定や常駐を始めず拒否する()
        {
            using var scope = new AutomationScope(); var token = Guid.NewGuid().ToString("N");
            Assert.Equal(2, scope.Process("--test-instance").Code);
            Assert.Equal(2, scope.Process("--test-instance", "invalid", "--passive", "--settings-dir", scope.Root).Code);
            Assert.Equal(2, scope.Process("--test-instance", token, "--settings-dir", scope.Root).Code);
            Assert.Equal(2, scope.Process("--test-instance", token, "--passive", "--settings-dir", "relative").Code);
            Assert.Equal(2, scope.Process("--test-instance", token, "--test-instance", token, "--passive", "--settings-dir", scope.Root).Code);
            var normalSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OperationRecorder");
            Assert.Equal(2, scope.Process("--test-instance", token, "--passive", "--settings-dir", normalSettings).Code);
            Assert.False(File.Exists(Path.Combine(scope.Root, "settings.json")));
        }

        [Fact]
        public void 隔離実プロセスは併存し同一識別子へ表示と停止を送り別PIDを拒否する()
        {
            using var scope = new AutomationScope();
            using var first = new OwnedInstance(scope, "first");
            using var second = new OwnedInstance(scope, "second");
            Assert.NotEqual(first.Process.Id, second.Process.Id);
            var status = first.Request("status");
            Assert.True(status["ready"].GetValue<bool>());
            Assert.Equal(first.Process.Id, status["pid"].GetValue<int>());
            Assert.Equal("0.1.5", status["version"].GetValue<string>());
            Assert.Equal("refused", first.Request("stop", second.Process.Id)["result"].GetValue<string>());
            Assert.False(first.Process.HasExited); Assert.False(second.Process.HasExited);
            Assert.Equal("unsupported", first.Request("unknown")["result"].GetValue<string>());

            // 同じトークンの二重起動は新しい常駐を作らず、既存インスタンスへ合図する。
            Assert.Equal(0, scope.Process(first.Arguments).Code);
            Assert.Equal(first.Process.Id, first.Request("status")["pid"].GetValue<int>());
            Assert.Equal("completed", first.Request("show", first.Process.Id)["result"].GetValue<string>());
            Assert.True(SpinWait.SpinUntil(() => { first.Process.Refresh(); return first.Process.MainWindowHandle != IntPtr.Zero; }, 5000));
            Assert.Equal("accepted", first.Request("stop", first.Process.Id)["result"].GetValue<string>());
            Assert.True(first.Process.WaitForExit(10000)); Assert.Equal(0, first.Process.ExitCode);
            Assert.False(second.Process.HasExited);
            Assert.Equal("accepted", second.Request("stop", second.Process.Id)["result"].GetValue<string>());
            Assert.True(second.Process.WaitForExit(10000)); Assert.Equal(0, second.Process.ExitCode);
        }

        // 起動して所有したPIDとpipeだけを扱い、通常のアプリや他の試験インスタンスに接続しない。
        sealed class OwnedInstance : IDisposable
        {
            public Process Process { get; }
            public string[] Arguments { get; }
            readonly string id;
            public OwnedInstance(AutomationScope scope, string name)
            {
                var token = Guid.NewGuid().ToString("N"); id = "operation-recorder-test-" + token;
                Arguments = new[] { "--startup", "--passive", "--settings-dir", scope.Workspace.EnsureDirectory("instances/" + name), "--test-instance", token };
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "OperationRecorder.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in Arguments) start.ArgumentList.Add(arg);
                Process = System.Diagnostics.Process.Start(start);
                try
                {
                    var deadline = Stopwatch.StartNew();
                    while (true)
                    {
                        if (Process.HasExited) throw new InvalidOperationException("隔離アプリが管理窓口を開く前に終了しました: " + Process.ExitCode);
                        try { Request("status"); break; }
                        catch (Exception error) when (error is TimeoutException || error is IOException)
                        {
                            if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw;
                            Thread.Sleep(50);
                        }
                    }
                }
                catch { Dispose(); throw; }
            }
            public JsonObject Request(string command, int? expectedPid = null)
            {
                var name = "KotorinToys." + WindowsIdentity.GetCurrent().User.Value + "." + id;
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                pipe.Connect(500);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                var request = new JsonObject { ["command"] = command, ["requestId"] = Guid.NewGuid().ToString("N") };
                if (expectedPid.HasValue) request["expectedPid"] = expectedPid.Value;
                var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
                pipe.Write(bytes); pipe.Flush();
                var response = JsonNode.Parse(reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()).AsObject();
                Assert.Equal(Process.Id, response["pid"].GetValue<int>());
                return response;
            }
            public void Dispose()
            {
                try
                {
                    if (!Process.HasExited)
                    {
                        try { Request("stop", Process.Id); } catch (Exception) { }
                        if (!Process.WaitForExit(5000)) { Process.Kill(true); Process.WaitForExit(5000); }
                    }
                }
                finally { Process.Dispose(); }
            }
        }
    }
}
