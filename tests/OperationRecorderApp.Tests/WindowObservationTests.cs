using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    [Collection("管理画面の隔離設定")]
    public class WindowObservationTests
    {
        [Theory, Trait("Category", "Desktop")]
        [InlineData(CaptureMode.App, 3)]
        [InlineData(CaptureMode.Window, 1)]
        public void 別PIDの表示と切り替えを実通知で記録し対象外を除外する(CaptureMode mode, int expected)
        {
            if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1")
                throw new InvalidOperationException("DESKTOP_BLOCKED: OPREC_PASSIVE_WINDOW_TESTS=1の専用fixture環境が必要です。");
            using var fixture = new RecordingDesktopTests.FixtureProcess();
            fixture.Command("foreground");
            string root = Path.Combine(Path.GetTempPath(), "oprec-window-" + Guid.NewGuid().ToString("N"));
            var rules = CaptureRules.Defaults(); foreach (var rule in rules.Values) rule.Record = false;
            rules[CaptureRules.WindowChange] = new CaptureRule(true, false, false);
            var recorder = new Recorder { Mode = mode, TargetHwnd = fixture.Hwnd, Hotkeys = null, Rules = rules, IntervalMs = 200 };
            try
            {
                recorder.Start(root); Thread.Sleep(350); Assert.Equal(0, recorder.SavedCount);
                fixture.Command("window-open");
                if (mode == CaptureMode.App) Wait(() => recorder.SavedCount >= 1);
                else { Thread.Sleep(450); Assert.Equal(0, recorder.SavedCount); }
                Thread.Sleep(800); fixture.Command("window-main");
                Wait(() => recorder.SavedCount >= (mode == CaptureMode.App ? 2 : 1));
                fixture.Command("window-secondary");
                if (mode == CaptureMode.App) Wait(() => recorder.SavedCount >= 3);
                using (var outside = new RecordingDesktopTests.FixtureProcess())
                { outside.Command("window-open"); Thread.Sleep(450); }
                recorder.Stop();
                var events = File.ReadAllLines(Path.Combine(recorder.SessionDir, "events.jsonl")).Select(x => JsonNode.Parse(x)).ToArray();
                Assert.Equal(expected, events.Length);
                Assert.All(events, e => Assert.Equal(fixture.Pid, e["window"]["pid"].GetValue<int>()));
                Assert.Equal(mode == CaptureMode.App ? "window-open" : "window-switch", events[0]["kind"].GetValue<string>());
                Assert.All(events, e => { Assert.Null(e["before"]); Assert.Null(e["after"]); });
            }
            finally
            {
                recorder.Stop(); if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static void Wait(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 6000) { if (ready()) return; Thread.Sleep(25); }
            throw new TimeoutException("ウィンドウの実通知を6秒以内に保存できませんでした。");
        }
    }
}
