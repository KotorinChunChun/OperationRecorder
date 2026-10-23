using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace OpRec
{
    // 記録の全体構成(スレッドは4本):
    //   フック   : マウス・キーボードの低レベルフック。コールバックは Raw を積むだけで即 return する
    //              (重い処理をすると LowLevelHooksTimeout を超えて Windows にフックを外される)
    //   撮影     : IntervalMs ごとに対象範囲を撮り、直近 RetainMs 分をリングに保持する。
    //              「0.2秒前」はこのリングから選ぶ。「0.5秒後」の画像もこのスレッドが割り当てる
    //   振り分け : Raw を操作として解釈し、撮影対象か判定し、UI Automation で要素情報を取る
    //   保存     : マスク・目印を描いて PNG と events.jsonl に書き出す
    public sealed partial class Recorder
    {
        public CaptureMode Mode = CaptureMode.Active;
        public int IntervalMs = 100, BeforeMs = 200, AfterMs = 500, RetainMs = 1500;
        public IntPtr TargetHwnd = IntPtr.Zero;
        public string TargetProcessName;
        public bool PickTarget;
        public bool RecordInputValues = true;
        public MonitorInfo TargetMonitor;      // Desktop モードで撮るディスプレイ
        public int MarkerRadius = 14;          // クリック位置の丸の半径(px)。session.json に残すだけで、丸は出力するときに描く
        public HotkeySet Hotkeys = HotkeySet.Defaults();   // 記録の操作キー(開始・取消・取り直し・終了)

        public readonly ManualResetEvent StopRequested = new ManualResetEvent(false);
        public readonly ManualResetEvent RestartRequested = new ManualResetEvent(false);
        public string CommandLine;             // この記録を起動したコマンド(AI 向け報告書に載せる)
        public string Caption;                 // 記録の名前。記録フォルダ名の末尾と、報告書の見出し「{caption}操作手順書」に使う
        public string Description;             // 報告書の冒頭に載せる解説
        // 撮影ルール(操作の種類ごとに、記録するか・操作前を撮るか・操作後を撮るか)。CaptureRules の定数がキー
        public Dictionary<string, CaptureRule> Rules = CaptureRules.Defaults();
        // 手順を 1 件保存するたびに、events.jsonl に書いた 1 行(JSON)を渡す(保存スレッドから呼ばれる)
        public event Action<string> StepSaved;
        // 保存済みの手順を取り消したときに、その seq を渡す(振り分けスレッドから呼ばれる)
        public event Action<int> StepRemoved;
        // 操作を手順として受け付けた瞬間に、種類とマウスの位置(キー操作などは null)を渡す(振り分けスレッドから呼ばれる)。
        // アプリが「撮影した」ことを画面に知らせるのに使う
        public event Action<string, Point?> StepCaptured;
        public readonly ManualResetEvent TargetReady = new ManualResetEvent(false);
        public string SessionDir { get; private set; }
        public int SavedCount { get { return savedCount; } }

        readonly Stopwatch sw = new Stopwatch();
        DateTime startWall;
        readonly BlockingCollection<Raw> rawQ = new BlockingCollection<Raw>();
        readonly BlockingCollection<Ev> saverQ = new BlockingCollection<Ev>();
        readonly List<Frame> ring = new List<Frame>();
        readonly object ringLock = new object();
        readonly List<Ev> pending = new List<Ev>();
        readonly object pendingLock = new object();
        volatile HashSet<uint> pids = new HashSet<uint>();
        volatile bool captureStop;
        bool stopped;
        int savedCount, seqCounter;

        Thread hookThread, captureThread, dispatchThread, saverThread;
        StreamWriter jsonl, logWriter;
        readonly object logLock = new object();
        static readonly JsonSerializerOptions JsonLine = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        static readonly JsonSerializerOptions JsonIndented = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

        public string Start(string outRoot)
        {
            sw.Start();
            startWall = DateTime.Now;
            string folder = "session-" + startWall.ToString("yyyyMMdd-HHmmss");
            string safe = SafeFolderPart(Caption);
            if (safe.Length > 0) folder += "_" + safe;
            SessionDir = Path.Combine(Path.GetFullPath(outRoot), folder);
            Directory.CreateDirectory(Path.Combine(SessionDir, "img"));
            var utf8 = new UTF8Encoding(false);
            jsonl = new StreamWriter(Path.Combine(SessionDir, "events.jsonl"), false, utf8);
            logWriter = new StreamWriter(Path.Combine(SessionDir, "recorder.log"), false, utf8);

            if (Mode == CaptureMode.AllDesktops) allMonitors = Monitors.GetAll().Select(m => m.Bounds).ToList();
            TuneInterval();
            if (Mode == CaptureMode.Active || Mode == CaptureMode.AllDesktops) TargetReady.Set();
            else if (!PickTarget)
            {
                if (Mode == CaptureMode.Window && TargetHwnd == IntPtr.Zero) throw new InvalidOperationException("Window モードには対象ウィンドウが必要です");
                if (Mode == CaptureMode.Desktop && TargetMonitor == null) throw new InvalidOperationException("Desktop モードには対象ディスプレイが必要です");
                if (Mode == CaptureMode.App)
                {
                    // プロセス名が指定されていなければ、指定ウィンドウのプロセス1つだけを対象にする
                    // (pwsh.exe のように同名プロセスが無関係に複数動いていることがあるため、名前には広げない)
                    if (TargetProcessName == null && TargetHwnd == IntPtr.Zero) throw new InvalidOperationException("App モードには対象プロセスかウィンドウが必要です");
                    RefreshPids();
                }
                TargetReady.Set();
            }
            WriteSession(false);

            captureThread = StartThread(CaptureLoop, "capture");
            dispatchThread = StartThread(DispatchLoop, "dispatch");
            saverThread = StartThread(SaverLoop, "saver");

            var hookReady = new ManualResetEvent(false);
            string hookError = null;
            hookThread = StartThread(() => HookLoop(hookReady, ref hookError), "hook");
            hookReady.WaitOne(5000);
            if (hookError != null) { Stop(); throw new InvalidOperationException(hookError); }
            Log("記録開始: mode=" + Mode + " dir=" + SessionDir);
            if (pendingLog != null) { Log(pendingLog); pendingLog = null; }
            return SessionDir;
        }

        List<Rectangle> allMonitors;   // AllDesktops で撮るディスプレイ(記録を始めた時点の構成)

        // ディスプレイ全体を撮るモードでは、広さに応じて撮影の間隔を広げる(直近の画面を持つメモリを抑える)
        void TuneInterval()
        {
            Rectangle area = Mode == CaptureMode.AllDesktops ? TargetRules.UnionBounds(allMonitors)
                           : Mode == CaptureMode.Desktop && TargetMonitor != null ? TargetMonitor.Bounds
                           : Mode == CaptureMode.Desktop ? Monitors.GetAll().Select(m => m.Bounds).OrderByDescending(b => (long)b.Width * b.Height).FirstOrDefault()
                           : Rectangle.Empty;
            int tuned = CaptureBudget.IntervalFor(area.Width, area.Height, IntervalMs, RetainMs);
            if (tuned != IntervalMs) pendingLog = "撮影の間隔を " + IntervalMs + " ms から " + tuned + " ms にしました(撮る範囲 " + area.Width + "x" + area.Height + " が広いため)";
            IntervalMs = tuned;
        }
        string pendingLog;

        // 記録を始める前に、いま前面にあるウィンドウを対象にする(「開始のキーを押したときに前面にあるもの」)。
        // Window / App はそのウィンドウ(のアプリ)、Desktop はそのウィンドウがいちばん広く映っているディスプレイ。
        // 対象にできない(自分自身・デスクトップ・タスクバー)ときは false を返し、何も変えない
        public bool UseForegroundTarget()
        {
            IntPtr fg = WinUtil.Root(N.GetForegroundWindow());
            if (!TargetRules.IsUsableForeground(fg, WinUtil.PidOf(fg), OwnPid, WinUtil.ClassOf(fg))) return false;
            switch (Mode)
            {
                case CaptureMode.Desktop:
                    var m = TargetRules.MonitorFor(WinUtil.GetFrame(fg), Monitors.GetAll());
                    if (m == null) return false;
                    TargetMonitor = m;
                    break;
                case CaptureMode.Window:
                case CaptureMode.App:
                    TargetHwnd = fg;
                    break;
            }
            PickTarget = false;
            return true;
        }

        // 記録の対象の名前(アプリがオーバーレイのタイトルに出す)。「最初にクリックしたもの」は選ばれるまで案内を返す
        public string TargetDescription()
        {
            bool pending = PickTarget && !TargetReady.WaitOne(0);
            string title = WinUtil.IsAlive(TargetHwnd) ? WinUtil.TextOf(TargetHwnd) : null;
            string proc = TargetProcessName ?? (WinUtil.IsAlive(TargetHwnd) ? WinUtil.ProcName(WinUtil.PidOf(TargetHwnd)) : null);
            var m = TargetMonitor;
            return TargetRules.Describe(Mode, pending, title, proc, m != null ? m.Index : 0, m != null ? m.Bounds.Size : Size.Empty);
        }

        public void Stop()
        {
            if (stopped) return;
            stopped = true;

            if (hookTid != 0) N.PostThreadMessage(hookTid, 0x12 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
            if (hookThread != null) hookThread.Join(3000);

            rawQ.CompleteAdding();
            if (dispatchThread != null) dispatchThread.Join(15000);

            // 「後」の画像がまだ撮れていない操作を、撮影スレッドが割り当て終えるまで待つ
            long deadline = sw.ElapsedMilliseconds + AfterMs + 2000;
            while (sw.ElapsedMilliseconds < deadline)
            {
                lock (pendingLock) { if (pending.Count == 0) break; }
                Thread.Sleep(50);
            }
            captureStop = true;
            if (captureThread != null) captureThread.Join(3000);

            lock (pendingLock)
            {
                foreach (var ev in pending.ToArray()) { ev.AfterMissing = true; ev.InfoDone = true; TrySendLocked(ev); }
            }
            saverQ.CompleteAdding();
            if (saverThread != null) saverThread.Join(60000);

            WriteSession(true);
            lock (ringLock)
            {
                foreach (var f in ring) { f.Evicted = true; if (f.Pins <= 0) DisposeFrame(f); }
                ring.Clear();
            }
            Log("記録終了: " + savedCount + " 件");
            jsonl.Dispose();
            lock (logLock) { logWriter.Dispose(); logWriter = null; }
        }

        // フォルダ名に使えない文字(\ / : * ? " < > | と制御文字)を _ に置き換え、長すぎる名前は切り詰める
        public static string SafeFolderPart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder();
            foreach (char c in s.Trim()) sb.Append(bad.Contains(c) || char.IsControl(c) ? '_' : c);
            string r = sb.ToString().TrimEnd('.', ' ');
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        Thread StartThread(ThreadStart body, string name)
        {
            var t = new Thread(() =>
            {
                N.SetThreadDpiAwarenessContext(N.DPI_PER_MONITOR_AWARE_V2);
                try { body(); } catch (Exception ex) { Log(name + " スレッドで例外: " + ex); }
            });
            t.IsBackground = true;
            t.Name = "OpRec-" + name;
            t.Start();
            return t;
        }

        internal void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
            lock (logLock)
            {
                Console.WriteLine(line);
                if (logWriter != null) { logWriter.WriteLine(line); logWriter.Flush(); }
            }
        }

        void WriteSession(bool final)
        {
            var d = new Dictionary<string, object>();
            d["tool"] = "operation-recorder";
            d["command"] = CommandLine;
            d["caption"] = Caption;
            d["description"] = Description;
            d["mode"] = Mode.ToString();
            d["start"] = startWall.ToString("yyyy-MM-ddTHH:mm:ss");
            d["end"] = final ? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") : null;
            d["intervalMs"] = IntervalMs; d["beforeMs"] = BeforeMs; d["afterMs"] = AfterMs;
            d["targetWindow"] = TargetHwnd != IntPtr.Zero ? new Dictionary<string, object> {
                { "hwnd", WinUtil.Hex(TargetHwnd) }, { "title", WinUtil.TextOf(TargetHwnd) }, { "className", WinUtil.ClassOf(TargetHwnd) } } : null;
            d["targetProcess"] = TargetProcessName;
            d["targetMonitor"] = TargetMonitor != null ? new Dictionary<string, object> {
                { "index", TargetMonitor.Index }, { "deviceName", TargetMonitor.DeviceName }, { "bounds", WinUtil.Rc(TargetMonitor.Bounds) },
                { "isPrimary", TargetMonitor.IsPrimary }, { "scalePercent", TargetMonitor.ScalePercent } } : null;
            d["markerRadius"] = MarkerRadius;
            // 画像には印を描かずに保存し、出力するときに合成する(v1.3 から。これが無い古い記録は、画像に丸などが描いてある)
            d["marks"] = "output";
            var rules = new Dictionary<string, object>();
            foreach (var k in CaptureRules.Order)
            {
                var r = Rule(k);
                rules[k] = new Dictionary<string, object> { { "record", r.Record }, { "before", r.Before }, { "after", r.After } };
            }
            d["captureRules"] = rules;
            d["virtualScreen"] = WinUtil.Rc(WinUtil.VirtualScreen());
            d["eventCount"] = savedCount;
            d["coordinates"] = "物理ピクセル(per-monitor DPI aware)。window 相対は DWM の描画枠左上、client 相対はクライアント領域左上が原点";
            File.WriteAllText(Path.Combine(SessionDir, "session.json"), JsonSerializer.Serialize(d, JsonIndented), new UTF8Encoding(false));
        }

        void RefreshPids()
        {
            string n = TargetProcessName;
            var set = new HashSet<uint>();
            if (n != null)
            {
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                foreach (var p in Process.GetProcessesByName(n)) { set.Add((uint)p.Id); p.Dispose(); }
            }
            if (TargetHwnd != IntPtr.Zero) set.Add(WinUtil.PidOf(TargetHwnd));
            pids = set;
        }

        static readonly Dictionary<string, CaptureRule> DefaultRules = CaptureRules.Defaults();

        // ルールが無い(前の版のアプリから渡された)ときは、その種類の既定を使う
        CaptureRule Rule(string kind)
        {
            CaptureRule r;
            if (Rules != null && Rules.TryGetValue(kind, out r) && r != null) return r;
            return DefaultRules.TryGetValue(kind, out r) ? r : new CaptureRule();
        }

        void RaiseStepSaved(string line) { try { StepSaved?.Invoke(line); } catch { } }
        void RaiseCaptured(string kind, Point? p) { try { StepCaptured?.Invoke(kind, p); } catch { } }

        // いま記録の対象になっているウィンドウ(Desktop はディスプレイ)の画面上の矩形。アプリが対象を枠で囲うのに使う
        public Rectangle? CurrentTargetRect()
        {
            if (!TargetReady.WaitOne(0)) return null;
            switch (Mode)
            {
                case CaptureMode.Desktop:
                    return TargetMonitor != null ? TargetMonitor.Bounds : (Rectangle?)null;
                case CaptureMode.AllDesktops:
                    return null;   // 画面全部なので、枠で囲うものがない
                case CaptureMode.Window:
                    return WinUtil.IsAlive(TargetHwnd) && !N.IsIconic(TargetHwnd) ? WinUtil.GetFrame(TargetHwnd) : (Rectangle?)null;
                default:
                    IntPtr fg = WinUtil.Root(N.GetForegroundWindow());
                    if (fg != IntPtr.Zero && InScope(fg))
                    {
                        string cls = WinUtil.ClassOf(fg);
                        if (!TargetRules.IsShellWindow(cls) && !N.IsIconic(fg)) return WinUtil.GetFrame(fg);
                    }
                    if (Mode == CaptureMode.App && WinUtil.IsAlive(TargetHwnd) && !N.IsIconic(TargetHwnd)) return WinUtil.GetFrame(TargetHwnd);
                    return null;
            }
        }
        void RaiseStepRemoved(int seq) { try { StepRemoved?.Invoke(seq); } catch { } }

        // 直前 1 手の取消を頼む(Ctrl+Shift+F10 と同じ。アプリのトレイメニューから呼ぶ)
        public void RequestUndo() { Enqueue(new Raw { Type = 5, Ts = sw.ElapsedMilliseconds }); }

        static readonly uint OwnPid = (uint)Environment.ProcessId;

        // point はマウス操作の位置。キー操作では null(Desktop モードでは前面ウィンドウの中心で判定する)
        bool InScope(IntPtr root, Point? point = null)
        {
            if (root == IntPtr.Zero) return false;
            // 記録している自分自身(アプリのウィンドウ・トレイのメニュー・pwsh のコンソール)での操作は記録しない
            if (WinUtil.PidOf(root) == OwnPid) return false;
            switch (Mode)
            {
                case CaptureMode.Window: return root == TargetHwnd;
                case CaptureMode.App: return pids.Contains(WinUtil.PidOf(root));
                case CaptureMode.Desktop:
                    if (point.HasValue) return TargetMonitor.Bounds.Contains(point.Value);
                    Rectangle f = WinUtil.GetFrame(root);
                    return TargetMonitor.Bounds.Contains(f.X + f.Width / 2, f.Y + f.Height / 2);
                default: return true;
            }
        }

        // ---------- フレームのリングと参照カウント ----------

        void Pin(Frame f) { lock (ringLock) f.Pins++; }

        void Unpin(Frame f)
        {
            lock (ringLock)
            {
                f.Pins--;
                if (f.Pins <= 0 && f.Evicted) DisposeFrame(f);
            }
        }

        static void DisposeFrame(Frame f)
        {
            if (f.Disposed) return;
            f.Disposed = true;
            f.Bmp.Dispose();
            f.Visible.Dispose();
        }

        Frame PickBefore(long ts) { return PickFrameAt(ts - BeforeMs); }

        // ts 以前で最も新しいフレームを固定して返す(無ければリングの最古)
        Frame PickFrameAt(long ts)
        {
            lock (ringLock)
            {
                Frame best = null;
                foreach (var f in ring) { if (f.Ts <= ts) best = f; else break; }
                if (best == null && ring.Count > 0) best = ring[0];
                if (best != null) best.Pins++;
                return best;
            }
        }

        void TrySendLocked(Ev ev)
        {
            // ホイールを回して元に戻した(合計 0)ものは、手順にせず捨てる
            if (!ev.Sent && ev.Kind == "wheel" && WheelRules.IsCancelled(ev.WheelNet) && ev.InfoDone && (ev.After != null || ev.AfterMissing))
            {
                ev.Sent = true;
                pending.Remove(ev);
                if (ev.Before != null) Unpin(ev.Before);
                if (ev.After != null) Unpin(ev.After);
                Log("記録しません: ホイールを回して元に戻した操作");
                return;
            }
            if (!ev.Sent && ev.InfoDone && (ev.After != null || ev.AfterMissing))
            {
                ev.Sent = true;
                pending.Remove(ev);
                saverQ.Add(ev);
            }
        }
    }
}
