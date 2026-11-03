using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    [Collection("管理画面の隔離設定")]
    public sealed class RecordingLifecycleTests
    {
        [Fact]
        public void 撮影要求は開始前にOSへ触れず不正な種類を拒否する()
        {
            var recorder = new Recorder();
            recorder.RequestSnapshot(); recorder.RequestSnapshot(SnapshotTrigger.WindowsSnip);
            Assert.Throws<ArgumentOutOfRangeException>(() => recorder.RequestSnapshot((SnapshotTrigger)99));
            Assert.Equal(0, recorder.SavedCount); Assert.Null(recorder.SessionDir);
        }

        sealed class Session : IRecordingSession
        {
            public Recorder NativeRecorder => null;
            public string SessionDir { get; private set; }
            public int SavedCount { get; set; } = 2;
            public bool StopRequested { get; private set; }
            public bool RestartRequested { get; private set; }
            public RecordingTargetReuse Target { get; } = new RecordingTargetReuse { Hwnd = new IntPtr(123), ProcessName = "fixture", MonitorIndex = 2 };
            public int Starts, Stops, Undos;
            public bool FailStart, FailStop;
            public event Action<string> StepSaved;
            public event Action<int> StepRemoved;
            public event Action<string, Point?> StepCaptured;
            public string Start(string root)
            {
                Starts++; SessionDir = Path.Combine(root, "session-fake-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(SessionDir);
                if (FailStart) throw new IOException("開始失敗");
                return SessionDir;
            }
            public void Stop() { Stops++; if (FailStop) throw new IOException("停止失敗"); }
            public void Undo() { Undos++; StepRemoved?.Invoke(7); }
            public void RequestStop() { StopRequested = true; }
            public void RequestRestart() { RestartRequested = true; }
            public void Raise() { StepSaved?.Invoke("記録行"); StepCaptured?.Invoke("click", new Point(5, 6)); }
        }

        sealed class Hotkey : IStartHotkeyRegistration
        {
            public string Error { get; set; }
            public bool Pressed;
            public int Disposals;
            public bool ConsumeStart() { var result = Pressed; Pressed = false; return result; }
            public void Dispose() { Disposals++; }
        }

        sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "oprec-lifecycle-" + Guid.NewGuid().ToString("N"));
            public readonly AppSettings Settings;
            public readonly Queue<Action> Work = new Queue<Action>();
            public readonly List<Session> Sessions = new List<Session>();
            public readonly List<Hotkey> Hotkeys = new List<Hotkey>();
            public readonly List<RecordingTargetReuse> Targets = new List<RecordingTargetReuse>();
            public readonly List<ToolTipIcon> Notifications = new List<ToolTipIcon>();
            public readonly List<RecState> States = new List<RecState>();
            public RecordingControllerDependencies Dependencies;
            public RecordingController Controller;
            public Action<Session> Configure;
            public int Exports, Saved;
            readonly bool passive = AppSettings.Passive;
            public Fixture()
            {
                AppSettings.Passive = false;
                Settings = new AppSettings { OutDir = Root };
                Dependencies = new RecordingControllerDependencies
                {
                    EnableTimer = false, RegisterStartHotkey = true,
                    Dispatch = action => action(), Background = action => Work.Enqueue(action),
                    SessionFactory = (settings, reuse) =>
                    {
                        Targets.Add(reuse); var session = new Session(); Configure?.Invoke(session); Sessions.Add(session); return session;
                    },
                    HotkeyFactory = keys => { var hotkey = new Hotkey(); Hotkeys.Add(hotkey); return hotkey; },
                    Export = (settings, directory, md, html) => { Exports++; if (settings.OutputMd) md.Add("fixture.md"); if (settings.OutputHtml) html.Add("fixture.html"); }
                };
            }
            public void Create()
            {
                Controller = new RecordingController(() => Settings, Dependencies);
                Controller.Notify += (title, text, icon) => Notifications.Add(icon);
                Controller.StateChanged += () => States.Add(Controller.State);
                Controller.Saved += (directory, md, html) => Saved++;
            }
            public void Finish() { Controller.Poll(); Assert.Equal(RecState.Saving, Controller.State); Assert.Single(Work); Work.Dequeue()(); }
            public void Dispose()
            {
                try { Controller?.Dispose(); }
                finally { AppSettings.Passive = passive; if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            }
        }

        [Fact]
        public void 開始取消停止保存の状態遷移をOSなしで検証する()
        {
            using var f = new Fixture(); f.Create(); f.Controller.Begin(); f.Controller.Begin();
            Assert.Single(f.Sessions); Assert.Null(f.Controller.Current); // fakeだけNativeRecorderがない。通常実行のCurrentはRecorder。
            f.Controller.RequestUndo(); Assert.Equal(1, f.Sessions[0].Undos);
            f.Controller.RequestStop(); f.Controller.Poll(); f.Controller.Begin();
            Assert.Equal(RecState.Saving, f.Controller.State); Assert.Single(f.Sessions);
            f.Work.Dequeue()();
            Assert.Equal(new[] { RecState.Recording, RecState.Saving, RecState.Idle }, f.States);
            Assert.Equal(1, f.Exports); Assert.Equal(1, f.Saved);
            Assert.Equal(1, f.Hotkeys[0].Disposals); Assert.Equal(2, f.Hotkeys.Count);
        }

        [Fact]
        public void 取り直しは旧記録だけを削除し次の開始キーで対象を再利用する()
        {
            using var f = new Fixture(); f.Create(); f.Hotkeys[0].Pressed = true; f.Controller.Poll();
            var first = f.Sessions[0]; f.Controller.RequestRestart(); f.Finish();
            Assert.False(Directory.Exists(first.SessionDir)); Assert.Equal(0, f.Saved); Assert.Equal(0, f.Exports);
            f.Hotkeys.Last().Pressed = true; f.Controller.Poll(); Assert.Same(first.Target, f.Targets[1]);
        }

        [Fact]
        public void 取り直し後の明示開始ボタンは保存した対象を使い回さない()
        {
            using var f = new Fixture(); f.Create(); f.Controller.Begin(); f.Controller.RequestRestart(); f.Finish(); f.Controller.Begin();
            Assert.Null(f.Targets.Last());
        }

        [Fact]
        public void Passiveでは登録も開始も行わず通常の意味を維持する()
        {
            using var f = new Fixture(); AppSettings.Passive = true; f.Create(); f.Controller.Begin(); f.Controller.Poll();
            Assert.Empty(f.Hotkeys); Assert.Empty(f.Sessions); Assert.Equal(RecState.Idle, f.Controller.State);
        }

        [Fact]
        public void 開始キー登録だけを抑止してボタン開始を試験できる()
        {
            using var f = new Fixture(); f.Dependencies.RegisterStartHotkey = false; f.Create(); f.Controller.Begin();
            Assert.Empty(f.Hotkeys); Assert.Single(f.Sessions); Assert.Equal(RecState.Recording, f.Controller.State);
        }

        [Fact]
        public void 開始失敗は部分開始を停止し待機へ戻す()
        {
            using var f = new Fixture(); f.Configure = session => session.FailStart = true; f.Create(); f.Controller.Begin();
            Assert.Equal(1, f.Sessions[0].Stops); Assert.Null(f.Controller.Current);
            Assert.Equal(RecState.Idle, f.Controller.State); Assert.Contains(ToolTipIcon.Error, f.Notifications);
        }

        [Fact]
        public void エンジン作成失敗も通知して開始キーを残す()
        {
            using var f = new Fixture(); f.Dependencies.SessionFactory = (settings, reuse) => throw new IOException("対象が無効");
            f.Create(); f.Controller.Begin(); Assert.Equal(RecState.Idle, f.Controller.State); Assert.Contains(ToolTipIcon.Error, f.Notifications);
            Assert.Equal(0, f.Hotkeys[0].Disposals);
        }

        [Fact]
        public void 停止処理失敗は成功した保存として通知しない()
        {
            using var f = new Fixture(); f.Configure = session => session.FailStop = true; f.Create(); f.Controller.Begin();
            f.Controller.RequestStop(); f.Finish();
            Assert.Equal(0, f.Saved); Assert.Equal(0, f.Exports); Assert.Equal(RecState.Idle, f.Controller.State);
            Assert.Equal(ToolTipIcon.Error, f.Notifications.Last());
        }

        [Fact]
        public void 出力失敗でも保存済み記録の通知と待機状態を保持する()
        {
            using var f = new Fixture(); f.Dependencies.Export = (settings, directory, md, html) => throw new IOException("出力先ロック");
            f.Create(); f.Controller.Begin(); f.Controller.RequestStop(); f.Finish();
            Assert.Equal(1, f.Saved); Assert.Equal(RecState.Idle, f.Controller.State); Assert.Equal(ToolTipIcon.Error, f.Notifications.Last());
            Assert.True(Directory.Exists(f.Controller.LastSessionDir));
        }

        [Fact]
        public void 取り直しの削除失敗を隠さず元記録を残す()
        {
            using var f = new Fixture(); f.Dependencies.DeleteDirectory = directory => throw new IOException("削除先ロック");
            f.Create(); f.Controller.Begin(); f.Controller.RequestRestart(); f.Finish();
            Assert.True(Directory.Exists(f.Sessions[0].SessionDir)); Assert.Equal(ToolTipIcon.Error, f.Notifications.Last());
        }

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(2, false, false)]
        public void 操作なし又は出力形式なしでは報告書を作らない(int count, bool md, bool html)
        {
            using var f = new Fixture(); f.Configure = session => session.SavedCount = count;
            f.Settings.OutputMd = md; f.Settings.OutputHtml = html; f.Create(); f.Controller.Begin(); f.Controller.RequestStop(); f.Finish();
            Assert.Equal(0, f.Exports); Assert.Equal(1, f.Saved);
        }

        [Fact]
        public void 開始キー競合の表示と再登録成功を検証する()
        {
            using var f = new Fixture(); int registrations = 0;
            f.Dependencies.HotkeyFactory = keys => { var key = new Hotkey { Error = registrations++ == 0 ? "競合" : null }; f.Hotkeys.Add(key); return key; };
            f.Create(); Assert.Equal("競合", f.Controller.HotkeyError); Assert.Equal(1, f.Hotkeys[0].Disposals);
            f.Controller.ReloadHotkeys(); Assert.Null(f.Controller.HotkeyError); Assert.Equal(2, registrations);
            f.Controller.Begin(); f.Controller.ReloadHotkeys(); Assert.Equal(2, registrations);
        }

        [Fact]
        public void 開始キー登録の例外でも待機してボタンから記録できる()
        {
            using var f = new Fixture(); f.Dependencies.HotkeyFactory = keys => throw new IOException("登録処理失敗"); f.Create();
            Assert.Equal("登録処理失敗", f.Controller.HotkeyError); Assert.Equal(RecState.Idle, f.Controller.State);
            f.Controller.Begin(); Assert.Equal(RecState.Recording, f.Controller.State); Assert.Single(f.Sessions);
        }

        [Fact]
        public void 終了した記録からの遅い通知を次の記録へ混入させない()
        {
            using var f = new Fixture(); f.Create(); int saved = 0, removed = 0, captured = 0;
            f.Controller.StepSaved += (line, directory) => saved++; f.Controller.StepRemoved += seq => removed++; f.Controller.StepCaptured += (kind, point) => captured++;
            f.Controller.Begin(); var old = f.Sessions[0]; old.Raise(); f.Controller.RequestStop(); f.Finish();
            f.Controller.Begin(); old.Raise(); old.Undo();
            Assert.Equal(1, saved); Assert.Equal(1, captured); Assert.Equal(0, removed);
            f.Sessions[1].Raise(); Assert.Equal(2, saved); Assert.Equal(2, captured);
        }

        [Fact]
        public void 破棄後は進行中保存の遅延通知と新規操作を行わない()
        {
            using var f = new Fixture(); f.Create(); f.Controller.Begin(); f.Controller.RequestStop(); f.Controller.Poll();
            f.Controller.Dispose(); f.Work.Dequeue()(); f.Controller.Begin(); f.Controller.RequestUndo(); f.Controller.Poll();
            Assert.Equal(RecState.Idle, f.Controller.State); Assert.Equal(0, f.Saved); Assert.Single(f.Sessions); Assert.All(f.Hotkeys, key => Assert.Equal(1, key.Disposals));
        }

        [Fact]
        public void 手順の保存取消撮影通知は指定したdispatchを通す()
        {
            using var f = new Fixture(); var callbacks = new Queue<Action>();
            f.Dependencies.Dispatch = action => callbacks.Enqueue(action); f.Create();
            int saved = 0, removed = 0, captured = 0;
            f.Controller.StepSaved += (line, dir) => { Assert.Equal("記録行", line); Assert.Equal(f.Controller.LastSessionDir, dir); saved++; };
            f.Controller.StepRemoved += seq => removed = seq;
            f.Controller.StepCaptured += (kind, point) => { Assert.Equal("click", kind); Assert.Equal(new Point(5, 6), point); captured++; };
            f.Controller.Begin(); f.Sessions[0].Raise(); f.Controller.RequestUndo();
            Assert.Equal(0, saved); while (callbacks.Count > 0) callbacks.Dequeue()();
            Assert.Equal(1, saved); Assert.Equal(7, removed); Assert.Equal(1, captured);
        }
    }
}
