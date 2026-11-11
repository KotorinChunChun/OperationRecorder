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
        [InlineData(CaptureMode.App, 3, true)]
        [InlineData(CaptureMode.Window, 1, false)]
        public void 別PIDの表示と切り替えを実通知で記録し対象外を除外する(CaptureMode mode, int expected, bool images)
        {
            if (Environment.GetEnvironmentVariable("OPREC_PASSIVE_WINDOW_TESTS") != "1")
                throw new InvalidOperationException("DESKTOP_BLOCKED: OPREC_PASSIVE_WINDOW_TESTS=1の専用fixture環境が必要です。");
            using var fixture = new RecordingDesktopTests.FixtureProcess();
            fixture.Command("front");
            string root = Path.Combine(Path.GetTempPath(), "oprec-window-" + Guid.NewGuid().ToString("N"));
            var rules = CaptureRules.Defaults(); foreach (var rule in rules.Values) rule.Record = false;
            rules[CaptureRules.WindowChange] = new CaptureRule(true, images, images);
            var recorder = new Recorder { Mode = mode, TargetHwnd = fixture.Hwnd, Hotkeys = null, Rules = rules,
                IntervalMs = 50, BeforeMs = 50, AfterMs = 150 };
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
                using var outside = new RecordingDesktopTests.FixtureProcess();
                outside.Command("front");
                outside.Command("window-open");
                Thread.Sleep(450);
                // 対象外の画面を閉じると対象画面に前面が戻ることがある。除外の判定後に閉じる。
                recorder.Stop();
                var events = File.ReadAllLines(Path.Combine(recorder.SessionDir, "events.jsonl")).Select(x => JsonNode.Parse(x)).ToArray();
                Assert.Equal(expected, events.Length);
                Assert.All(events, e => Assert.Equal(fixture.Pid, e["window"]["pid"].GetValue<int>()));
                Assert.Equal(mode == CaptureMode.App ? new[] { "window-open", "window-switch", "window-switch" } : new[] { "window-switch" },
                    events.Select(e => e["kind"].GetValue<string>()));
                Assert.All(events, e =>
                {
                    foreach (string side in new[] { "before", "after" })
                    {
                        if (!images) Assert.Null(e[side]);
                        else
                        {
                            string file = Path.Combine(recorder.SessionDir, e[side]["file"].GetValue<string>());
                            using var image = System.Drawing.Image.FromFile(file);
                            Assert.True(image.Width > 0 && image.Height > 0);
                        }
                    }
                });
                string evidence = Environment.GetEnvironmentVariable("OPREC_RECORDING_ARTIFACTS");
                if (!string.IsNullOrEmpty(evidence))
                {
                    string destination = Path.Combine(evidence, "window-" + mode + "-" + Guid.NewGuid().ToString("N"));
                    foreach (string file in Directory.GetFiles(recorder.SessionDir, "*", SearchOption.AllDirectories))
                    {
                        string target = Path.Combine(destination, Path.GetRelativePath(recorder.SessionDir, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target);
                    }
                }
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
