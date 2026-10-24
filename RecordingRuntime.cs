using System;
using System.Collections.Generic;
using System.Drawing;
using OpRec;

namespace OperationRecorderApp
{
    // 記録制御をOS・タイマー・保存先から分離する。通常実行ではRecorderをそのまま使用する。
    public sealed class RecordingTargetReuse
    {
        public IntPtr Hwnd;
        public string ProcessName;
        public int MonitorIndex;
    }

    public interface IRecordingSession
    {
        Recorder NativeRecorder { get; }
        string SessionDir { get; }
        int SavedCount { get; }
        bool StopRequested { get; }
        bool RestartRequested { get; }
        RecordingTargetReuse Target { get; }
        event Action<string> StepSaved;
        event Action<int> StepRemoved;
        event Action<string, Point?> StepCaptured;
        string Start(string outputRoot);
        void Stop();
        void Undo();
        void RequestStop();
        void RequestRestart();
    }

    public interface IStartHotkeyRegistration : IDisposable
    {
        string Error { get; }
        bool ConsumeStart();
    }

    public sealed class RecordingControllerDependencies
    {
        public bool EnableTimer { get; set; } = true;
        public bool RegisterStartHotkey { get; set; } = true;
        public Func<AppSettings, RecordingTargetReuse, IRecordingSession> SessionFactory { get; set; }
        public Func<HotkeySet, IStartHotkeyRegistration> HotkeyFactory { get; set; }
        public Action<Action> Dispatch { get; set; }
        public Action<Action> Background { get; set; }
        public Action<string> DeleteDirectory { get; set; }
        public Action<AppSettings, string, List<string>, List<string>> Export { get; set; }
    }

    sealed class NativeRecordingSession : IRecordingSession
    {
        readonly Recorder recorder;
        public NativeRecordingSession(Recorder recorder) { this.recorder = recorder; }
        public Recorder NativeRecorder => recorder;
        public string SessionDir => recorder.SessionDir;
        public int SavedCount => recorder.SavedCount;
        public bool StopRequested => recorder.StopRequested.WaitOne(0);
        public bool RestartRequested => recorder.RestartRequested.WaitOne(0);
        public RecordingTargetReuse Target => new RecordingTargetReuse
        {
            Hwnd = recorder.TargetHwnd, ProcessName = recorder.TargetProcessName,
            MonitorIndex = recorder.TargetMonitor?.Index ?? 0
        };
        public event Action<string> StepSaved { add { recorder.StepSaved += value; } remove { recorder.StepSaved -= value; } }
        public event Action<int> StepRemoved { add { recorder.StepRemoved += value; } remove { recorder.StepRemoved -= value; } }
        public event Action<string, Point?> StepCaptured { add { recorder.StepCaptured += value; } remove { recorder.StepCaptured -= value; } }
        public string Start(string outputRoot) => recorder.Start(outputRoot);
        public void Stop() => recorder.Stop();
        public void Undo() => recorder.RequestUndo();
        public void RequestStop() => recorder.StopRequested.Set();
        public void RequestRestart() => recorder.RestartRequested.Set();
    }

    sealed class NativeStartHotkeyRegistration : IStartHotkeyRegistration
    {
        readonly HotkeyListener listener = new HotkeyListener();
        public NativeStartHotkeyRegistration(HotkeySet keys)
        {
            try { listener.Start(keys, false); }
            catch { listener.Dispose(); throw; }
        }
        public string Error => listener.Error;
        public bool ConsumeStart()
        {
            if (!listener.StartPressed.WaitOne(0)) return false;
            listener.StartPressed.Reset(); return true;
        }
        public void Dispose() => listener.Dispose();
    }
}
