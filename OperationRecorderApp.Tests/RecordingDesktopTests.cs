using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // この分類は既定suiteから除外する。明示実行時の環境不足をskipや成功として扱わない。
    [Collection("管理画面の隔離設定")]
    [Trait("Category", "Desktop")]
    public sealed class RecordingDesktopTests
    {
        [Theory]
        [InlineData(CaptureMode.Window)]
        [InlineData(CaptureMode.App)]
        [InlineData(CaptureMode.Active)]
        [InlineData(CaptureMode.Desktop)]
        [InlineData(CaptureMode.AllDesktops)]
        public void 五つの撮影範囲で操作前後のPNGとJSONと報告書を保存する(CaptureMode mode)
        {
            using var test = new DesktopCase("mode-" + mode);
            var recorder = test.Start(mode);
            test.Input.Click(test.Fixture.Rect("action"));
            test.Wait(() => recorder.SavedCount == 1, "左クリックの保存");
            test.Stop();
            var rows = ReadEvents(recorder.SessionDir);
            var row = Assert.Single(rows);
            Assert.Equal("click", row.GetProperty("kind").GetString());
            Assert.Equal(test.Fixture.Pid, row.GetProperty("window").GetProperty("pid").GetInt32());
            Assert.True(row.TryGetProperty("uia", out var element), "UI Automationの部品情報がありません。");
            Assert.Contains("fixtureAction", element.GetProperty("automationId").GetString());
            CheckPng(recorder.SessionDir, row.GetProperty("before"));
            CheckPng(recorder.SessionDir, row.GetProperty("after"));
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.SessionDir, "session.json")));
            Assert.Equal(mode.ToString(), metadata.RootElement.GetProperty("mode").GetString());
            Assert.Equal(1, metadata.RootElement.GetProperty("eventCount").GetInt32());
            Assert.Equal(JsonValueKind.String, metadata.RootElement.GetProperty("end").ValueKind);
            var markdown = new List<string>(); var html = new List<string>();
            RecordingController.Export(Path.Combine(AppContext.BaseDirectory, "templates"), recorder.SessionDir,
                new[] { "user" }, null, false, true, true, "fixture試験", "自動試験だけの記録", markdown, html);
            Assert.NotEmpty(markdown); Assert.NotEmpty(html);
            Assert.All(markdown.Concat(html), path => { Assert.True(File.Exists(path)); Assert.True(new FileInfo(path).Length > 100); });
            test.Complete();
        }

        [Theory]
        [InlineData(CaptureMode.Window, false)]
        [InlineData(CaptureMode.App, false)]
        [InlineData(CaptureMode.Desktop, false)]
        [InlineData(CaptureMode.Window, true)]
        [InlineData(CaptureMode.App, true)]
        [InlineData(CaptureMode.Desktop, true)]
        public void 前面指定と最初のクリック指定が専用対象を解決する(CaptureMode mode, bool pick)
        {
            using var test = new DesktopCase("target-" + mode + "-" + pick);
            var recorder = test.NewRecorder(mode);
            recorder.TargetHwnd = IntPtr.Zero; recorder.TargetMonitor = null;
            if (pick) recorder.PickTarget = true;
            else Assert.True(recorder.UseForegroundTarget(), "専用fixtureを前面の対象として解決できません。");
            test.Start(recorder);
            if (pick)
            {
                Assert.False(recorder.TargetReady.WaitOne(0));
                test.Input.Click(test.Fixture.Rect("action"));
                test.Wait(() => recorder.TargetReady.WaitOne(0), "最初のクリックによる対象選択");
                Assert.Equal(0, recorder.SavedCount);
                test.WaitFrame();
            }
            if (mode == CaptureMode.Desktop) Assert.Equal(Monitors.OfWindow(test.Fixture.Hwnd).Index, recorder.TargetMonitor.Index);
            else Assert.Equal(test.Fixture.Hwnd, recorder.TargetHwnd);
            test.Input.Click(test.Fixture.Rect("action"));
            test.Wait(() => recorder.SavedCount == 1, "対象選択後の操作保存");
            test.Stop(); Assert.Single(ReadEvents(recorder.SessionDir)); test.Complete();
        }

        [Theory]
        [InlineData("doubleclick", "doubleclick")]
        [InlineData("rightclick", "rightclick")]
        [InlineData("middleclick", "middleclick")]
        [InlineData("drag", "drag")]
        [InlineData("rightdrag", "rightdrag")]
        [InlineData("wheel", "wheel")]
        [InlineData("letter", "key")]
        [InlineData("digit", "key")]
        [InlineData("function", "key")]
        [InlineData("arrow", "key")]
        [InlineData("special", "key")]
        [InlineData("combination", "key")]
        [InlineData("manual", "capture")]
        public void ネイティブ入力フックで各操作種別を検出する(string gesture, string kind)
        {
            using var test = new DesktopCase("gesture-" + gesture);
            var recorder = test.Start(CaptureMode.Window);
            switch (gesture)
            {
                case "doubleclick": test.Input.Click(test.Fixture.Rect("action"), twice: true); break;
                case "rightclick": test.Input.Click(test.Fixture.Rect("drag"), button: 1); break;
                case "middleclick": test.Input.Click(test.Fixture.Rect("drag"), button: 2); break;
                case "drag": test.Input.Drag(test.Fixture.Rect("drag"), false); break;
                case "rightdrag": test.Input.Drag(test.Fixture.Rect("drag"), true); break;
                case "wheel": test.Input.Wheel(test.Fixture.Rect("wheel")); break;
                case "letter": test.Input.Key(0x41); break;
                case "digit": test.Input.Key(0x31); break;
                case "function": test.Input.Key(0x80); break; // F17
                case "arrow": test.Input.Key(0x27); break;
                case "special": test.Input.Key(0x09); break;
                case "combination": test.Input.Chord(0x11, 0x12, 0x80); break;
                case "manual": test.Input.Chord(0x11, 0x10); break;
                default: throw new InvalidOperationException(gesture);
            }
            test.Wait(() => ReadLiveEvents(recorder.SessionDir).Any(row => row.GetProperty("kind").GetString() == kind), gesture + "の検出");
            test.Stop();
            var row = Assert.Single(ReadEvents(recorder.SessionDir), row => row.GetProperty("kind").GetString() == kind);
            Assert.Equal(test.Fixture.Pid, row.GetProperty("window").GetProperty("pid").GetInt32());
            Assert.True(row.GetProperty("before").ValueKind == JsonValueKind.Object || row.GetProperty("after").ValueKind == JsonValueKind.Object);
            test.Complete();
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void 入力内容とpasswordと入力値保存OFFを実際の入力欄で検証する(bool password, bool recordValues)
        {
            using var test = new DesktopCase("input-" + password + "-" + recordValues);
            var recorder = test.NewRecorder(CaptureMode.Window); recorder.RecordInputValues = recordValues;
            test.Start(recorder);
            test.Input.Click(test.Fixture.Rect(password ? "password" : "input"));
            test.Delay(350);
            test.Input.Text("fixture-safe-value");
            test.Delay(150);
            test.Input.Click(test.Fixture.Rect("action"));
            test.Wait(() => ReadLiveEvents(recorder.SessionDir).Any(row => row.GetProperty("kind").GetString() == "input"), "入力確定の記録");
            test.Stop();
            var row = Assert.Single(ReadEvents(recorder.SessionDir), row => row.GetProperty("kind").GetString() == "input");
            var input = row.GetProperty("input");
            if (password || !recordValues) Assert.Equal(JsonValueKind.Null, input.GetProperty("value").ValueKind);
            else Assert.Equal("fixture-safe-value", input.GetProperty("value").GetString());
            Assert.Equal(password, input.GetProperty("isPassword").GetBoolean());
            if (password) Assert.DoesNotContain("fixture-safe-value", File.ReadAllText(Path.Combine(recorder.SessionDir, "events.jsonl")));
            test.Complete();
        }

        [Fact]
        public void 直前取消がJSONとPNGを削除し次の記録は残せる()
        {
            using var test = new DesktopCase("undo");
            var recorder = test.Start(CaptureMode.Window);
            test.Input.Click(test.Fixture.Rect("action"));
            test.Wait(() => recorder.SavedCount == 1, "取消前の保存");
            var files = Directory.GetFiles(Path.Combine(recorder.SessionDir, "img")); Assert.NotEmpty(files);
            recorder.RequestUndo(); test.Wait(() => recorder.SavedCount == 0, "直前取消");
            Assert.Empty(ReadLiveEvents(recorder.SessionDir)); Assert.All(files, path => Assert.False(File.Exists(path)));
            test.Delay((int)NativeInput.GetDoubleClickTime() + 100);
            test.Input.Click(test.Fixture.Rect("action")); test.Wait(() => recorder.SavedCount == 1, "取消後の保存");
            test.Stop(); Assert.Single(ReadEvents(recorder.SessionDir)); test.Complete();
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, true)]
        public void 撮影ルールの記録と操作前後の指定が保存内容に反映される(bool record, bool before, bool after)
        {
            using var test = new DesktopCase("rule-" + record + "-" + before + "-" + after);
            var recorder = test.NewRecorder(CaptureMode.Window);
            recorder.Rules[CaptureRules.LeftClick] = new CaptureRule(record, before, after); test.Start(recorder);
            test.Input.Click(test.Fixture.Rect("action"));
            if (record) test.Wait(() => recorder.SavedCount == 1, "撮影ルールを適用した操作保存");
            else test.Delay(1200);
            test.Stop(); var rows = ReadEvents(recorder.SessionDir);
            if (!record) Assert.Empty(rows);
            else
            {
                var row = Assert.Single(rows);
                Assert.Equal(before ? JsonValueKind.Object : JsonValueKind.Null, row.GetProperty("before").ValueKind);
                Assert.Equal(after ? JsonValueKind.Object : JsonValueKind.Null, row.GetProperty("after").ValueKind);
            }
            test.Complete();
        }

        [Fact]
        public void 記録中の専用操作キーは開始無視と取消と取り直しと終了を要求する()
        {
            using var test = new DesktopCase("recorder-hotkeys"); var recorder = test.Start(CaptureMode.Window); var keys = TestKeys();
            test.Input.Click(test.Fixture.Rect("action")); test.Wait(() => recorder.SavedCount == 1, "操作キー試験の記録");
            test.Input.Chord(0x11, 0x10, 0x12, keys.Start.Key); test.Delay(300); Assert.Equal(1, recorder.SavedCount);
            test.Input.Chord(0x11, 0x10, 0x12, keys.Undo.Key); test.Wait(() => recorder.SavedCount == 0, "操作キーによる取消");
            test.Input.Chord(0x11, 0x10, 0x12, keys.Restart.Key); test.Wait(() => recorder.RestartRequested.WaitOne(0), "取り直しキー");
            test.Input.Chord(0x11, 0x10, 0x12, keys.Stop.Key); test.Wait(() => recorder.StopRequested.WaitOne(0), "保存終了キー");
            test.Stop(); Assert.Empty(ReadEvents(recorder.SessionDir)); test.Complete();
        }

        [Theory]
        [InlineData(SnapshotTrigger.Manual, true)]
        [InlineData(SnapshotTrigger.WindowsSnip, true)]
        [InlineData(SnapshotTrigger.WindowsSnip, false)]
        public void OSの切り取りを起動せず手動撮影とWinShiftSの同じ振り分けを検証する(SnapshotTrigger trigger, bool record)
        {
            using var test = new DesktopCase("snapshot-" + trigger + "-" + record);
            var recorder = test.NewRecorder(CaptureMode.Window);
            recorder.Rules[trigger == SnapshotTrigger.WindowsSnip ? CaptureRules.WinShiftS : CaptureRules.CtrlShift].Record = record;
            test.Start(recorder); test.Input.AssertForeground(); recorder.RequestSnapshot(trigger);
            if (record) test.Wait(() => recorder.SavedCount == 1, "撮影要求の保存");
            else test.Delay(700);
            test.Stop(); recorder.RequestSnapshot(trigger); var rows = ReadEvents(recorder.SessionDir);
            if (!record) Assert.Empty(rows);
            else
            {
                var row = Assert.Single(rows);
                Assert.Equal("capture", row.GetProperty("kind").GetString());
                Assert.Equal(trigger == SnapshotTrigger.WindowsSnip ? "Win+Shift+S" : "Ctrl+Shift", row.GetProperty("trigger").GetString());
                CheckPng(recorder.SessionDir, row.GetProperty("before")); CheckPng(recorder.SessionDir, row.GetProperty("after"));
            }
            test.Complete();
        }

        [Fact]
        public void 記録制御の取り直しは旧データを破棄して同じ対象で再開できる()
        {
            using var test = new DesktopCase("controller-restart");
            bool passive = AppSettings.Passive; AppSettings.Passive = false;
            try
            {
                var settings = new AppSettings { OutDir = test.Root, Mode = "Window", Target = AppSettings.TargetPick, Hotkeys = TestKeys() };
                var key = new PulseHotkey();
                using var controller = new RecordingController(() => settings, new RecordingControllerDependencies
                {
                    EnableTimer = false, HotkeyFactory = keys => key, Dispatch = action => action(), Background = action => action()
                });
                controller.Begin(); Assert.Equal(RecState.Recording, controller.State);
                test.Adopt(controller.Current);
                test.Input.Click(test.Fixture.Rect("action")); test.Wait(() => controller.Current.TargetReady.WaitOne(0), "取り直し対象選択");
                test.WaitFrame(); test.Input.Click(test.Fixture.Rect("action")); test.Wait(() => controller.Current.SavedCount == 1, "取り直し前の保存");
                var old = controller.LastSessionDir;
                controller.RequestRestart(); controller.Poll(); test.ReleaseStopped();
                Assert.Equal(RecState.Idle, controller.State); Assert.False(Directory.Exists(old));
                key.Pressed = true; controller.Poll(); Assert.Equal(RecState.Recording, controller.State);
                test.Adopt(controller.Current); Assert.Equal(test.Fixture.Hwnd, controller.Current.TargetHwnd); Assert.False(controller.Current.PickTarget);
                test.WaitFrame(); test.Input.Click(test.Fixture.Rect("action")); test.Wait(() => controller.Current.SavedCount == 1, "取り直し後の保存");
                controller.RequestStop(); controller.Poll(); test.ReleaseStopped();
                Assert.Equal(RecState.Idle, controller.State); Assert.True(Directory.Exists(controller.LastSessionDir)); test.Complete();
            }
            finally { AppSettings.Passive = passive; }
        }

        [Fact]
        public void 操作ゼロでも開始終了のメタデータを保存する()
        {
            using var test = new DesktopCase("zero-steps"); var recorder = test.Start(CaptureMode.Window); test.Stop();
            Assert.Empty(ReadEvents(recorder.SessionDir)); Assert.Empty(Directory.GetFiles(Path.Combine(recorder.SessionDir, "img")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(recorder.SessionDir, "session.json")));
            Assert.Equal(0, json.RootElement.GetProperty("eventCount").GetInt32()); test.Complete();
        }

        [Fact]
        public void 無効な一覧対象はフックを開始せずエラーへ戻る()
        {
            using var test = new DesktopCase("invalid-target");
            bool passive = AppSettings.Passive; AppSettings.Passive = false;
            try
            {
                var settings = new AppSettings { OutDir = test.Root, Mode = "Window", Target = AppSettings.TargetList, WindowTitle = "存在しないfixture-" + Guid.NewGuid().ToString("N"), Hotkeys = TestKeys() };
                var errors = new List<string>();
                using var controller = new RecordingController(() => settings, new RecordingControllerDependencies
                { EnableTimer = false, RegisterStartHotkey = false, Dispatch = action => action() });
                controller.Notify += (title, message, icon) => { if (icon == ToolTipIcon.Error) errors.Add(message); };
                controller.Begin(); Assert.Equal(RecState.Idle, controller.State); Assert.Null(controller.Current);
                Assert.Single(errors); Assert.Contains(settings.WindowTitle, errors[0]); test.Complete();
            }
            finally { AppSettings.Passive = passive; }
        }

        [Fact]
        public void 専用ホットキーの受信と二重登録の競合と解放を検証する()
        {
            using var test = new DesktopCase("hotkey-registration"); var keys = TestKeys();
            using (var first = new HotkeyListener())
            {
                first.Start(keys, false); Assert.Null(first.Error);
                using (var collision = new HotkeyListener()) { collision.Start(keys, false); Assert.NotNull(collision.Error); }
                test.Input.Chord(0x11, 0x10, 0x12, keys.Start.Key);
                test.Wait(() => first.StartPressed.WaitOne(0), "専用開始ホットキーの受信");
            }
            using (var retry = new HotkeyListener()) { retry.Start(keys, false); Assert.Null(retry.Error); }
            test.Complete();
        }

        static HotkeySet TestKeys() => new HotkeySet
        {
            Start = new Hotkey(true, true, true, false, 0x7C), Undo = new Hotkey(true, true, true, false, 0x7D),
            Restart = new Hotkey(true, true, true, false, 0x7E), Stop = new Hotkey(true, true, true, false, 0x7F)
        };

        sealed class PulseHotkey : IStartHotkeyRegistration
        {
            public string Error => null;
            public bool Pressed;
            public bool ConsumeStart() { bool result = Pressed; Pressed = false; return result; }
            public void Dispose() { }
        }

        static JsonElement[] ReadEvents(string root) => File.ReadAllLines(Path.Combine(root, "events.jsonl")).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();

        static JsonElement[] ReadLiveEvents(string root)
        {
            using var stream = new FileStream(Path.Combine(root, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream); var rows = new List<JsonElement>();
            while (reader.ReadLine() is string line)
            {
                try { using var document = JsonDocument.Parse(line); rows.Add(document.RootElement.Clone()); }
                catch (JsonException) { } // 保存スレッドの最終行は次回のpollで読む。
            }
            return rows.ToArray();
        }

        static void CheckPng(string root, JsonElement image)
        {
            Assert.Equal(JsonValueKind.Object, image.ValueKind);
            var file = Path.GetFullPath(Path.Combine(root, image.GetProperty("file").GetString()));
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase);
            using var bitmap = new Bitmap(file); Assert.True(bitmap.Width > 0); Assert.True(bitmap.Height > 0);
            Assert.Equal(System.Drawing.Imaging.ImageFormat.Png.Guid, bitmap.RawFormat.Guid);
        }

        sealed class DesktopCase : IDisposable
        {
            public readonly FixtureProcess Fixture;
            public readonly NativeInput Input;
            public readonly string Root;
            Recorder active;
            bool complete;
            readonly IntPtr oldDpi;
            public DesktopCase(string name)
            {
                RequireDesktop();
                oldDpi = NativeInput.SetThreadDpiAwarenessContext(new IntPtr(-4));
                try
                {
                    ProbeHotkeys();
                    var baseDirectory = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS") ?? Path.Combine(AppContext.BaseDirectory, "recording-evidence");
                    Root = Path.Combine(Path.GetFullPath(baseDirectory), name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
                    Fixture = new FixtureProcess(); Fixture.Command("cover");
                    Input = new NativeInput(Fixture); Input.WaitForInitialForeground();
                    Delay(600);
                }
                catch
                {
                    Fixture?.Dispose();
                    if (Root != null && Directory.Exists(Root)) Directory.Delete(Root, true);
                    if (oldDpi != IntPtr.Zero) NativeInput.SetThreadDpiAwarenessContext(oldDpi);
                    throw;
                }
            }

            public Recorder NewRecorder(CaptureMode mode)
            {
                var rules = CaptureRules.Defaults(); foreach (var rule in rules.Values) rule.Record = true;
                return new Recorder { Mode = mode, TargetHwnd = Fixture.Hwnd, TargetMonitor = Monitors.OfWindow(Fixture.Hwnd),
                    Hotkeys = TestKeys(), IntervalMs = 50, BeforeMs = 50, AfterMs = 150, RetainMs = 1000, Rules = rules };
            }
            public Recorder Start(CaptureMode mode) { var recorder = NewRecorder(mode); Start(recorder); return recorder; }
            public void Start(Recorder recorder) { active = recorder; recorder.Start(Root); if (!recorder.PickTarget) WaitFrame(); }
            public void Adopt(Recorder recorder) { Assert.Null(active); active = recorder; }
            public void ReleaseStopped() { active = null; }
            public void WaitFrame() => Delay(700);
            public void Wait(Func<bool> condition, string operation)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(12)) { Input.AssertForeground(); if (condition()) return; Thread.Sleep(25); }
                throw new TimeoutException(operation + "が12秒以内に完了しませんでした。");
            }
            public void Delay(int milliseconds)
            {
                var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Input.AssertForeground(); Thread.Sleep(25); }
            }
            public void Stop() { var recorder = active; active = null; recorder?.Stop(); }
            public void Complete() { Input.AssertForeground(); complete = true; }
            public void Dispose()
            {
                try { Stop(); }
                finally
                {
                    Input?.Dispose(); Fixture?.Dispose();
                    // フォーカス逸脱などで失敗した試験の画像は残さない。通常の利用者の記録は参照しない。
                    if (!complete && Root != null && Directory.Exists(Root)) Directory.Delete(Root, true);
                    if (oldDpi != IntPtr.Zero) NativeInput.SetThreadDpiAwarenessContext(oldDpi);
                }
            }

            static void RequireDesktop()
            {
                if (Environment.GetEnvironmentVariable("OPREC_DESKTOP_TESTS") != "1") throw Blocked("OPREC_DESKTOP_TESTS=1を指定した明示実行が必要です。");
                if (!Environment.UserInteractive) throw Blocked("対話デスクトップがありません。");
                var desktop = NativeInput.OpenInputDesktop(0, false, 0x0100);
                if (desktop == IntPtr.Zero) throw Blocked("入力デスクトップにアクセスできません。ロックを解除して実行してください。");
                NativeInput.CloseDesktop(desktop);
                RequireNoExternalRecorder();
                var exe = Environment.GetEnvironmentVariable("OPREC_FIXTURE_EXE");
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) throw Blocked("OPREC_FIXTURE_EXEにビルド済みの専用fixtureを指定してください。");
                if (!string.Equals(Path.GetFileName(exe), "OperationRecorder.RecordingFixture.exe", StringComparison.OrdinalIgnoreCase))
                    throw Blocked("OPREC_FIXTURE_EXEが専用fixtureの実行ファイル名と一致しません。");
            }

            static void ProbeHotkeys()
            {
                int id = 0x6F00;
                foreach (var action in HotkeySet.Order)
                {
                    var key = TestKeys().Get(action);
                    if (!NativeInput.RegisterHotKey(IntPtr.Zero, id, key.Modifiers, (uint)key.Key)) throw Blocked("専用キー" + key + "が他のアプリに予約されています。");
                    NativeInput.UnregisterHotKey(IntPtr.Zero, id++);
                }
            }
        }

        static InvalidOperationException Blocked(string reason) => new InvalidOperationException("DESKTOP_BLOCKED: " + reason);

        static void RequireNoExternalRecorder()
        {
            var existing = Process.GetProcessesByName("OperationRecorder");
            try
            {
                if (existing.Length != 0) throw Blocked("利用者のOperationRecorderが存在し、安全な待機状態を判定できません。既存プロセスへは何も送信していません。");
            }
            finally { foreach (var process in existing) process.Dispose(); }
        }

        sealed class FixtureProcess : IDisposable
        {
            readonly Process process;
            readonly string executable, token;
            readonly DateTime started;
            readonly NamedPipeClientStream pipe;
            readonly StreamReader reader;
            readonly StreamWriter writer;
            JsonElement state;
            public int Pid => process.Id;
            public IntPtr Hwnd => new IntPtr(state.GetProperty("hwnd").GetInt64());
            public FixtureProcess()
            {
                executable = Path.GetFullPath(Environment.GetEnvironmentVariable("OPREC_FIXTURE_EXE"));
                token = Guid.NewGuid().ToString("N"); string pipeName = "OpRec.RecordingFixture." + token;
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) };
                info.ArgumentList.Add(pipeName); info.ArgumentList.Add(token);
                process = Process.Start(info) ?? throw Blocked("専用fixtureを起動できません。"); started = process.StartTime;
                try
                {
                    pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(10000); reader = new StreamReader(pipe, leaveOpen: true); writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    Command("status"); VerifyIdentity();
                }
                catch { Dispose(); throw; }
            }
            public void VerifyIdentity()
            {
                if (process.HasExited || process.StartTime != started || !string.Equals(process.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    throw Blocked("専用fixtureの実行ファイルまたはPIDが一致しません。");
            }
            public JsonElement Command(string command)
            {
                VerifyIdentity();
                writer.WriteLine(JsonSerializer.Serialize(new { token, command }));
                var line = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (line == null) throw new IOException("専用fixtureから応答がありません。");
                using var document = JsonDocument.Parse(line); var response = document.RootElement.Clone();
                if (command != "close") { Assert.Equal(Pid, response.GetProperty("pid").GetInt32()); state = response; }
                return response;
            }
            public Rectangle Rect(string name)
            {
                var value = state.GetProperty(name); return new Rectangle(value.GetProperty("x").GetInt32(), value.GetProperty("y").GetInt32(), value.GetProperty("width").GetInt32(), value.GetProperty("height").GetInt32());
            }
            public void Dispose()
            {
                try
                {
                    if (!process.HasExited)
                    {
                        VerifyIdentity();
                        if (writer != null && reader != null) { try { Command("close"); } catch { } }
                        if (!process.WaitForExit(3000)) { VerifyIdentity(); process.Kill(); process.WaitForExit(3000); }
                    }
                }
                finally { writer?.Dispose(); reader?.Dispose(); pipe?.Dispose(); process.Dispose(); }
            }
        }

        // OS入力を送るコードは試験専用。送信直前に毎回、専用hostのPID・実体・前面を確認する。
        sealed class NativeInput : IDisposable
        {
            readonly FixtureProcess fixture;
            readonly Point originalCursor;
            Point lastCursor;
            bool moved;
            public NativeInput(FixtureProcess fixture)
            {
                this.fixture = fixture; GetCursorPos(out originalCursor);
                foreach (int vk in new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C })
                    if ((GetAsyncKeyState(vk) & 0x8000) != 0) throw Blocked("利用者のキーまたはマウスボタンが押されています。");
            }
            public void AssertForeground()
            {
                RequireNoExternalRecorder();
                fixture.VerifyIdentity(); GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid != fixture.Pid) throw Blocked("前面が専用fixtureから離れたため、入力と試験を中断しました。expectedPid=" + fixture.Pid + ", actualPid=" + pid);
            }
            public void WaitForInitialForeground()
            {
                // 前面要求に伴うWindowsメッセージの処理を待つ。試験開始後の逸脱は再前面化せず中断する。
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(5))
                {
                    RequireNoExternalRecorder(); fixture.VerifyIdentity();
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                    if (pid == fixture.Pid) return;
                    Thread.Sleep(25);
                }
                AssertForeground();
            }
            void Send(params INPUT[] inputs)
            {
                AssertForeground();
                if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) throw new IOException("専用fixtureへのSendInputに失敗しました。");
            }
            void Move(Point point)
            {
                AssertForeground(); GetWindowThreadProcessId(WindowFromPoint(point), out uint pid);
                if (pid != fixture.Pid) throw Blocked("送信先の位置を別のウィンドウが覆っています。");
                Send(Movement(point));
                lastCursor = point; moved = true; Thread.Sleep(60); AssertForeground();
            }
            static INPUT Movement(Point point)
            {
                int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
                return new INPUT { type = 0, union = new InputUnion { mouse = new MOUSEINPUT { dx = (int)Math.Round((point.X - left) * 65535d / Math.Max(1, width - 1)),
                    dy = (int)Math.Round((point.Y - top) * 65535d / Math.Max(1, height - 1)), flags = 0x8001 | 0x4000 } } };
            }
            static Point Center(Rectangle rect) => new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            static INPUT Mouse(uint flag, uint data = 0) => new INPUT { type = 0, union = new InputUnion { mouse = new MOUSEINPUT { flags = flag, data = data } } };
            static INPUT Keyboard(int key, uint flags = 0, char unicode = '\0') => new INPUT { type = 1,
                union = new InputUnion { keyboard = new KEYBDINPUT { key = (ushort)key, scan = unicode, flags = flags } } };
            public void Click(Rectangle rectangle, int button = 0, bool twice = false)
            {
                Move(Center(rectangle)); uint down = button == 1 ? 0x0008u : button == 2 ? 0x0020u : 0x0002u;
                uint up = down << 1;
                Send(Mouse(down), Mouse(up)); if (twice) { Thread.Sleep(60); Send(Mouse(down), Mouse(up)); }
            }
            public void Drag(Rectangle rectangle, bool right)
            {
                var start = new Point(rectangle.X + 30, rectangle.Y + 30); Move(start);
                var end = start + new Size(100, 30); GetWindowThreadProcessId(WindowFromPoint(end), out uint pid);
                if (pid != fixture.Pid) throw Blocked("ドラッグ先を別のウィンドウが覆っています。");
                // 一括送信にして、試験の中断時にマウスボタンを押したままにしない。
                Send(Mouse(right ? 0x0008u : 0x0002u), Movement(end), Mouse(right ? 0x0010u : 0x0004u));
                lastCursor = end;
            }
            public void Wheel(Rectangle rectangle) { Move(Center(rectangle)); Send(Mouse(0x0800, 120)); }
            public void Key(int vk) { Send(Keyboard(vk), Keyboard(vk, 0x0002)); }
            public void Chord(params int[] keys)
            {
                // 押下と解放を一括送信し、例外やユーザー操作の割り込みで修飾キーを押したままにしない。
                var inputs = keys.Select(key => Keyboard(key)).Concat(keys.Reverse().Select(key => Keyboard(key, 0x0002))).ToArray(); Send(inputs);
            }
            public void Text(string value)
            {
                foreach (char character in value) { Send(Keyboard(0, 0x0004, character), Keyboard(0, 0x0006, character)); Thread.Sleep(15); }
            }
            public void Dispose()
            {
                // 利用者がマウスを動かした場合は、その新しい位置を尊重する。
                GetCursorPos(out var current);
                if (moved && current == lastCursor) SetCursorPos(originalCursor.X, originalCursor.Y);
            }

            [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion union; }
            [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT keyboard; }
            [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
            [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
            [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
            [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
            [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
            [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
            [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
            [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
            [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
            [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
            [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
            [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
            [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
        }
    }
}
