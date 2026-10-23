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
        readonly SynchronizationContext ui;
        HotkeyListener listener;
        Reuse reuse;                     // 取り直し(F11)のときに、前回決まった対象をそのまま使う

        sealed class Reuse { public IntPtr Hwnd; public string ProcessName; public int MonitorIndex; }

        public RecordingController(Func<AppSettings> settingsProvider)
        {
            settings = settingsProvider;
            ui = SynchronizationContext.Current;
            timer.Tick += OnTick;
            timer.Start();
            EnableStartHotkey();
        }

        void SetState(RecState s) { State = s; StateChanged?.Invoke(); }

        void EnableStartHotkey()
        {
            if (AppSettings.Passive) return;
            if (listener != null) return;
            listener = new HotkeyListener();
            listener.Start(settings().Hotkeys, false);
            HotkeyError = listener.Error;
            if (listener.Error != null)
            {
                Notify?.Invoke("ホットキーを登録できません", listener.Error + "\nボタンやトレイのメニューから記録を開始してください。", ToolTipIcon.Warning);
                listener.Dispose();
                listener = null;
            }
        }

        // 操作キーの設定を変えたとき。待機中なら開始のキーを登録し直す(記録中は、次に待機に戻ったときに反映される)
        public void ReloadHotkeys()
        {
            if (State != RecState.Idle) return;
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
            if (AppSettings.Passive) return;
            if (State != RecState.Idle) return;
            reuse = null;
            StartRecording();
        }

        public void RequestUndo() { if (State == RecState.Recording) Current.RequestUndo(); }
        public void RequestRestart() { if (State == RecState.Recording) Current.RestartRequested.Set(); }
        public void RequestStop() { if (State == RecState.Recording) Current.StopRequested.Set(); }

        void OnTick(object sender, EventArgs e)
        {
            if (State == RecState.Idle)
            {
                if (listener != null && listener.StartPressed.WaitOne(0))
                {
                    listener.StartPressed.Reset();
                    StartRecording();
                }
            }
            else if (State == RecState.Recording)
            {
                if (Current.StopRequested.WaitOne(0)) Finish(true);
                else if (Current.RestartRequested.WaitOne(0)) Finish(false);
            }
        }

        void StartRecording()
        {
            var s = settings();
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
            string targetNote = null;
            // 「0.2 秒前」の画面はリングから選ぶので、操作前の ms より長く画面を持っておく
            rec.RetainMs = Math.Max(1500, rec.BeforeMs + 1000);

            try
            {
                if (reuse != null && ApplyReuse(rec, reuse)) { }
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

                // 記録エンジンの保存・取消の通知は別スレッドから来るので、UI スレッドに渡す
                rec.StepSaved += line => ui.Post(_ => StepSaved?.Invoke(line, rec.SessionDir), null);
                rec.StepRemoved += seq => ui.Post(_ => StepRemoved?.Invoke(seq), null);
                rec.StepCaptured += (kind, pt) => ui.Post(_ => StepCaptured?.Invoke(kind, pt), null);

                Directory.CreateDirectory(s.OutDir);
                DisableStartHotkey();
                LastSessionDir = rec.Start(s.OutDir);
                Current = rec;
                SetState(RecState.Recording);
                string how = rec.PickTarget
                    ? (rec.Mode == CaptureMode.Desktop ? "記録したいディスプレイに映っているウィンドウを 1 回クリックしてください。" : "記録したいウィンドウを 1 回クリックしてください。")
                    : "ふだんどおりに操作してください。";
                Notify?.Invoke("記録を始めました", (targetNote != null ? targetNote + "\n" : "") + how + "\n" + UiTexts.NotifySaveKey(s.Hotkeys), ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Current = null;
                EnableStartHotkey();
                SetState(RecState.Idle);
                Notify?.Invoke("記録を開始できませんでした", ex.Message, ToolTipIcon.Error);
            }
        }

        static bool ApplyReuse(Recorder rec, Reuse r)
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
            var rec = Current;
            var s = settings();
            SetState(RecState.Saving);
            Task.Run(() =>
            {
                rec.Stop();
                var mds = new List<string>();
                var htmls = new List<string>();
                string error = null;
                if (!save)
                {
                    try { Directory.Delete(rec.SessionDir, true); } catch { }
                }
                else if (rec.SavedCount > 0 && s.Templates.Count > 0 && (s.OutputMd || s.OutputHtml))
                {
                    try { Export(s, rec.SessionDir, s.Templates, s.ReportDir, s.EmbedImages, s.OutputMd, s.OutputHtml, null, null, mds, htmls); }
                    catch (Exception ex) { error = ex.Message; }
                }
                return (mds, htmls, error);
            }).ContinueWith(t =>
            {
                var (mds, htmls, error) = t.Result;
                reuse = new Reuse
                {
                    Hwnd = rec.TargetHwnd,
                    ProcessName = rec.TargetProcessName,
                    MonitorIndex = rec.TargetMonitor != null ? rec.TargetMonitor.Index : 0,
                };
                Current = null;
                EnableStartHotkey();
                if (!save)
                {
                    // 取り直し: 待機に戻る。次に開始のキーを押すと、同じ対象で記録を始める
                    SetState(RecState.Idle);
                    Notify?.Invoke("記録を取り消しました", UiTexts.NotifyRestarted(s.Hotkeys), ToolTipIcon.Info);
                    return;
                }
                reuse = null;
                SetState(RecState.Idle);
                if (error != null) Notify?.Invoke("報告書を出力できませんでした", error, ToolTipIcon.Error);
                else if (rec.SavedCount == 0) Notify?.Invoke("記録を保存しました", "操作が 0 件だったので、報告書は出力していません。", ToolTipIcon.Warning);
                else Notify?.Invoke("記録を保存しました", rec.SavedCount + " 件の操作を保存しました。" + (mds.Count + htmls.Count > 0 ? "報告書を出力しました。" : ""), ToolTipIcon.Info);
                Saved?.Invoke(rec.SessionDir, mds, htmls);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void Dispose()
        {
            timer.Stop();
            if (Current != null) { try { Current.Stop(); } catch { } }
            DisableStartHotkey();
        }
    }
}
