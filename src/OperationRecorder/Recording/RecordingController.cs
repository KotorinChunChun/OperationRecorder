using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpRec;

namespace OperationRecorderApp
{
    public enum RecState { Idle, Recording, Saving }

    // 記録 1 件の開始から保存までを受け持つ。UI スレッドのタイマーで記録エンジンの状態を見張る。
    //   待機中(Idle): 開始のキー(既定 Ctrl+Shift+F9)だけを RegisterHotKey で受け取り、押されたらいつでも記録を始める(常駐中は常に受け付ける)
    //   記録中: 取消・取り直し・終了のキー(既定 Ctrl+Shift+F10〜F12)は記録エンジンのキーボードフックが受け取る
    public sealed class RecordingController : IDisposable
    {
        public RecState State { get; private set; } = RecState.Idle;
        public Recorder Current { get; private set; }
        public string LastSessionDir { get; private set; }
        public string HotkeyError { get; private set; }   // 開始のキーを登録できなかった理由(登録できていれば null)

        public event Action StateChanged;
        public event Action<string, string, ToolTipIcon> Notify;              // タイトル・本文・種類
        public event Action<string, List<string>, List<string>> Saved;        // 記録フォルダ・出力した md・出力した HTML
        public event Action<string, string> StepSaved;                        // events.jsonl の 1 行・記録フォルダ(UI スレッドで呼ぶ)
        public event Action<int> StepRemoved;                                 // 取り消した手順の seq(UI スレッドで呼ぶ)
        public event Action<string, System.Drawing.Point?> StepCaptured;      // 撮影した瞬間(種類・マウスの位置)(UI スレッドで呼ぶ)

        readonly Func<AppSettings> settings;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        readonly RecordingControllerDependencies dependencies;
        readonly Action<Action> dispatch, background;
        IRecordingSession activeSession;
        bool disposed;
        volatile int generation;
        IStartHotkeyRegistration listener;
        RecordingTargetReuse reuse;                     // 取り直し(F11)のときに、前回決まった対象をそのまま使う

        public RecordingController(Func<AppSettings> settingsProvider, RecordingControllerDependencies dependencies = null)
        {
            settings = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            this.dependencies = dependencies ?? new RecordingControllerDependencies();
            var ui = SynchronizationContext.Current;
            dispatch = this.dependencies.Dispatch ?? (action => { if (ui != null) ui.Post(_ => action(), null); else action(); });
            background = this.dependencies.Background ?? (action => Task.Run(action));
            timer.Tick += OnTick;
            if (this.dependencies.EnableTimer) timer.Start();
            EnableStartHotkey();
        }
        void SetState(RecState s) { State = s; StateChanged?.Invoke(); }

        void EnableStartHotkey()
        {
            if (disposed || AppSettings.Passive) return;
            if (!dependencies.RegisterStartHotkey || listener != null) return;
            try
            {
                listener = dependencies.HotkeyFactory != null ? dependencies.HotkeyFactory(settings().Hotkeys) : new NativeStartHotkeyRegistration(settings().Hotkeys);
                if (listener == null) throw new InvalidOperationException("開始キーの登録処理を作成できませんでした。");
                HotkeyError = listener.Error;
            }
            catch (Exception error) { HotkeyError = error.Message; }
            if (HotkeyError != null)
            {
                Notify?.Invoke("ホットキーを登録できません", HotkeyError + "\nボタンやトレイのメニューから記録を開始してください。", ToolTipIcon.Warning);
                DisableStartHotkey();
            }
        }

        // 操作キーの設定を変えたとき。待機中なら開始のキーを登録し直す(記録中は、次に待機に戻ったときに反映される)
        public void ReloadHotkeys()
        {
            if (disposed || State != RecState.Idle) return;
            DisableStartHotkey();
            EnableStartHotkey();
        }

        void DisableStartHotkey()
        {
            listener?.Dispose();
            listener = null;
        }

        // 「記録を開始」ボタン・トレイの「記録を開始」・スタートメニューの「すぐに記録」。すぐに記録を始める
        // (開始のキーは、待機中ならいつでも受け付けるので、待つ状態は無い)
        public void Begin()
        {
            if (disposed || AppSettings.Passive) return;
            if (State != RecState.Idle) return;
            reuse = null;
            StartRecording();
        }

        public void RequestUndo() { if (!disposed && State == RecState.Recording) activeSession.Undo(); }
        public void RequestRestart() { if (!disposed && State == RecState.Recording) activeSession.RequestRestart(); }
        public void RequestStop() { if (!disposed && State == RecState.Recording) activeSession.RequestStop(); }

        void OnTick(object sender, EventArgs e) => Poll();

        // CLI試験ではタイマーを止め、同じ状態遷移を明示的に進められる。
        public void Poll()
        {
            if (disposed) return;
            if (State == RecState.Idle)
            {
                if (listener != null && listener.ConsumeStart()) StartRecording();
            }
            else if (State == RecState.Recording)
            {
                if (activeSession.StopRequested) Finish(true);
                else if (activeSession.RestartRequested) Finish(false);
            }
        }
        void StartRecording()
        {
            var s = settings();
            int sessionGeneration = ++generation;
            string targetNote = null;
            IRecordingSession session = null;
            try
            {
                session = dependencies.SessionFactory != null
                    ? dependencies.SessionFactory(s, reuse)
                    : CreateNativeSession(s, reuse, out targetNote);
                if (session == null) throw new InvalidOperationException("記録エンジンを作成できませんでした。");
                var rec = session.NativeRecorder;
                // 記録エンジンの保存・取消の通知は別スレッドから来るので、UI スレッドに渡す
                session.StepSaved += line => dispatch(() => { if (!disposed && generation == sessionGeneration) StepSaved?.Invoke(line, session.SessionDir); });
                session.StepRemoved += seq => dispatch(() => { if (!disposed && generation == sessionGeneration) StepRemoved?.Invoke(seq); });
                session.StepCaptured += (kind, pt) => dispatch(() => { if (!disposed && generation == sessionGeneration) StepCaptured?.Invoke(kind, pt); });

                Directory.CreateDirectory(s.OutDir);
                DisableStartHotkey();
                LastSessionDir = session.Start(s.OutDir);
                activeSession = session;
                Current = rec;
                SetState(RecState.Recording);
                string how = rec != null && rec.PickTarget
                    ? (rec.Mode == CaptureMode.Desktop ? "記録したいディスプレイに映っているウィンドウを 1 回クリックしてください。" : "記録したいウィンドウを 1 回クリックしてください。")
                    : "ふだんどおりに操作してください。";
                Notify?.Invoke("記録を始めました", (targetNote != null ? targetNote + "\n" : "") + how + "\n" + UiTexts.NotifySaveKey(s.Hotkeys), ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                try { session?.Stop(); } catch { }
                generation++;
                activeSession = null; Current = null;
                EnableStartHotkey();
                SetState(RecState.Idle);
                Notify?.Invoke("記録を開始できませんでした", ex.Message, ToolTipIcon.Error);
            }
        }

        IRecordingSession CreateNativeSession(AppSettings s, RecordingTargetReuse targetReuse, out string targetNote)
        {
            var rules = new Dictionary<string, CaptureRule>();
            foreach (var k in CaptureRules.Order) rules[k] = s.Rule(k).Clone();
            var rec = new Recorder
            {
                Mode = (CaptureMode)Enum.Parse(typeof(CaptureMode), s.Mode),
                MarkerRadius = s.MarkerRadius,
                Caption = s.Caption,
                Description = s.Description,
                CommandLine = null,     // アプリから記録したものには、起動コマンドを載せない
                Rules = rules,
                BeforeMs = Math.Max(0, s.BeforeMs),
                AfterMs = Math.Max(0, s.AfterMs),
                Hotkeys = s.Hotkeys.Clone(),
            };
            targetNote = null;
            // 「0.2 秒前」の画面はリングから選ぶので、操作前の ms より長く画面を持っておく
            rec.RetainMs = Math.Max(1500, rec.BeforeMs + 1000);

            if (targetReuse != null && ApplyReuse(rec, targetReuse)) { }
            else if (rec.Mode == CaptureMode.Active || rec.Mode == CaptureMode.AllDesktops) { }
            else if (s.Target == AppSettings.TargetForeground)
            {
                // 開始のキーを押したときに前面にあるウィンドウ(Desktop はそれが映っているディスプレイ)を対象にする
                if (!rec.UseForegroundTarget())
                {
                    rec.PickTarget = true;
                    targetNote = "前面に記録できるウィンドウが無かったので、最初にクリックしたものを対象にします。";
                }
            }
            else if (rec.Mode == CaptureMode.Desktop)
            {
                if (s.Target == AppSettings.TargetPick) rec.PickTarget = true;
                else
                {
                    rec.TargetMonitor = Monitors.Get(s.MonitorIndex);
                    if (rec.TargetMonitor == null) throw new InvalidOperationException("ディスプレイ " + s.MonitorIndex + " が見つかりません。設定し直してください。");
                }
            }
            else
            {
                if (s.Target == AppSettings.TargetPick) rec.PickTarget = true;
                else
                {
                    IntPtr h = string.IsNullOrEmpty(s.WindowTitle) ? IntPtr.Zero : WinUtil.FindWindowByTitle(s.WindowTitle);
                    if (rec.Mode == CaptureMode.App && s.AllSameProcess && !string.IsNullOrEmpty(s.ProcessName)) rec.TargetProcessName = s.ProcessName;
                    if (h == IntPtr.Zero && rec.TargetProcessName == null)
                        throw new InvalidOperationException("「" + s.WindowTitle + "」というウィンドウが見つかりません。アプリを起動してから、一覧を更新して選び直してください。");
                    rec.TargetHwnd = h;
                }
            }

            return new NativeRecordingSession(rec);
        }

        static bool ApplyReuse(Recorder rec, RecordingTargetReuse r)
        {
            if (rec.Mode == CaptureMode.Desktop && r.MonitorIndex > 0)
            {
                rec.TargetMonitor = Monitors.Get(r.MonitorIndex);
                return rec.TargetMonitor != null;
            }
            if (WinUtil.IsAlive(r.Hwnd)) { rec.TargetHwnd = r.Hwnd; rec.TargetProcessName = r.ProcessName; return true; }
            if (rec.Mode == CaptureMode.App && !string.IsNullOrEmpty(r.ProcessName)) { rec.TargetProcessName = r.ProcessName; return true; }
            return false;
        }

        // 記録フォルダから md / HTML を出力する(保存直後と、記録一覧の「出力」で使う)
        public static void Export(AppSettings s, string sessionDir, IList<string> templates, string reportDir, bool embed,
            bool md, bool html, string caption, string description, List<string> mdOut, List<string> htmlOut) =>
            Export(s.EffectiveTemplateDir, sessionDir, templates, reportDir, embed, md, html, caption, description, mdOut, htmlOut, s.ToMarkOptions());

        // 設定に頼らない版(画面を出さない出力 --export と自動テストで使う)。marks が null なら既定の印(丸・矢印・枠)
        public static void Export(string templateDir, string sessionDir, IList<string> templates, string reportDir, bool embed,
            bool md, bool html, string caption, string description, List<string> mdOut, List<string> htmlOut, MarkOptions marks = null)
        {
            var rendered = ReportGenerator.Render(new ReportGenerator.Options
            {
                SessionDir = sessionDir,
                Templates = templates,
                TemplateDir = templateDir,
                ReportDir = string.IsNullOrWhiteSpace(reportDir) ? null : reportDir,
                EmbedImages = embed,
                Marks = marks ?? MarkOptions.Default(),

                Caption = caption,
                Description = description,
            });
            foreach (var r in rendered)
            {
                if (md)
                {
                    File.WriteAllText(r.Key, r.Value, new System.Text.UTF8Encoding(false));
                    mdOut.Add(r.Key);
                }
                if (html) htmlOut.Add(HtmlExporter.Write(r.Key, r.Value));
            }
        }

        // save = true: 保存して md / HTML を出す(F12) / false: 捨てて待機に戻る。次の F9 で同じ対象を記録する(F11)
        void Finish(bool save)
        {
            var session = activeSession;
            var s = settings();
            SetState(RecState.Saving);
            background(() =>
            {
                var mds = new List<string>();
                var htmls = new List<string>();
                string error = null;
                bool stopped = false;
                try
                {
                    session.Stop(); stopped = true;
                    if (!save)
                    {
                        if (dependencies.DeleteDirectory != null) dependencies.DeleteDirectory(session.SessionDir);
                        else Directory.Delete(session.SessionDir, true);
                    }
                    else if (session.SavedCount > 0 && s.Templates.Count > 0 && (s.OutputMd || s.OutputHtml))
                    {
                        if (dependencies.Export != null) dependencies.Export(s, session.SessionDir, mds, htmls);
                        else Export(s, session.SessionDir, s.Templates, s.ReportDir, s.EmbedImages, s.OutputMd, s.OutputHtml, null, null, mds, htmls);
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                dispatch(() =>
                {
                    if (disposed) return;
                    reuse = !save && stopped ? session.Target : null;
                    generation++;
                    activeSession = null; Current = null;
                    EnableStartHotkey(); SetState(RecState.Idle);
                    if (error != null) Notify?.Invoke(save ? "記録の保存・出力に失敗しました" : "記録の取り消しに失敗しました", error, ToolTipIcon.Error);
                    else if (!save) Notify?.Invoke("記録を取り消しました", UiTexts.NotifyRestarted(s.Hotkeys), ToolTipIcon.Info);
                    else if (session.SavedCount == 0) Notify?.Invoke("記録を保存しました", "操作が 0 件だったので、報告書は出力していません。", ToolTipIcon.Warning);
                    else Notify?.Invoke("記録を保存しました", session.SavedCount + " 件の操作を保存しました。" + (mds.Count + htmls.Count > 0 ? "報告書を出力しました。" : ""), ToolTipIcon.Info);
                    if (save && stopped) Saved?.Invoke(session.SessionDir, mds, htmls);
                });
            });
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; generation++; timer.Stop(); timer.Dispose();
            if (activeSession != null) { try { activeSession.Stop(); } catch { } }
            activeSession = null; Current = null; State = RecState.Idle;
            DisableStartHotkey();
        }
    }
}
