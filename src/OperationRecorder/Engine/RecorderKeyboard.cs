using System;
using System.Drawing;
using System.Windows.Automation;

namespace OpRec
{
    // キーボード: 文字入力とそれ以外のキー操作の区別、キー操作の記録
    public sealed partial class Recorder
    {
        bool composing;   // 日本語入力で変換中

        void OnKeyDown(Raw r)
        {
            if (!TargetReady.WaitOne(0)) return;
            int vk = r.Vk;
            if (KeyClassifier.IsModifier(vk)) return;
            IntPtr root = WinUtil.Root(r.Fg);
            if (!InScope(root)) return;

            bool endComposition;
            if (IsTyping(r, out endComposition))
            {
                // VK_PACKET は Unicode 文字を直接送る入力で、日本語入力の変換を通らない
                if (KeyClassifier.IsPrintable(vk) && vk != VK.Packet && !composing && ImeOpen(r.Fg)) composing = true;
                if (endComposition) composing = false;
                // 記録開始時から既にフォーカスがあった入力欄など、フォーカスインを見ていない場合はここで追跡を始める
                if (focusCand == null && Rule(CaptureRules.TextInput).Record) TryStartCandidate(root, r.Ts);
                if (focusCand != null) { focusCand.Typed = true; focusCand.LastTypeTs = r.Ts; }
                return;
            }
            if (r.Repeat) return;   // 押しっぱなしの自動リピートは1回目だけ撮る

            composing = false;
            // Win+Shift+S(Windows の切り取り)は、切り取り画面が出る前の、押した瞬間の画面をキャプチャする
            if (KeyClassifier.IsWindowsSnip(vk, r.Ctrl, r.Shift, r.Alt, r.Win))
            {
                DispatchSnapshot(r.Ts, r.Fg, SnapshotTrigger.WindowsSnip);
                return;
            }
            var krule = Rule(CaptureRules.KeyGroupOf(vk, r.Ctrl, r.Alt, r.Win));
            if (!krule.Record) return;
            var ev = NewEv(r.Ts);
            ev.Kind = EventKinds.Key;
            ev.Root = root;
            HoldForWindowChange(ev);
            ev.Before = krule.Before ? PickBefore(r.Ts) : null;
            ScheduleAfter(ev, krule.After, r.Ts);
            lock (pendingLock) pending.Add(ev);
            RaiseCaptured(EventKinds.Key, null);

            try { FillKeyInfo(ev, r, root); }
            catch (Exception ex) { Log("要素情報の取得に失敗: " + ex.Message); }
            CompleteInfo(ev);
        }

        // 文字の入力(入力欄の手順にまとめる)なら true。キー操作として撮るものは false
        bool IsTyping(Raw r, out bool endComposition)
        {
            int vk = r.Vk;
            endComposition = false;
            if (KeyClassifier.IsImeOrLockKey(vk) || KeyClassifier.IsImeToggle(vk, r.Alt)) return true;
            if (r.Ctrl || r.Alt || r.Win) return false;
            if (KeyClassifier.IsPrintable(vk))
            {
                // A〜Z・1〜0 は、そのグループを「記録する」にしたときだけ、入力欄の外で押したものを手順にする
                string g = CaptureRules.KeyGroupOf(vk, false, false, false);
                return !((g == CaptureRules.KeyLetters || g == CaptureRules.KeyDigits) && Rule(g).Record && !composing && !IsEditContext(r.Focus));
            }
            if (KeyClassifier.IsEditingKey(vk)) return composing || IsEditContext(r.Focus);
            if (composing && KeyClassifier.IsCompositionKey(vk))
            {
                // 日本語入力の変換中の Enter / Esc / F6〜F10 は、変換の確定・取り消しなので撮らない
                endComposition = KeyClassifier.EndsComposition(vk);
                return true;
            }
            return false;
        }

        void FillKeyInfo(Ev ev, Raw r, IntPtr root)
        {
            Rectangle rf = WinUtil.GetFrame(root);
            ev.WinFrame = rf;
            ev.Rec["key"] = KeyClassifier.KeyText(r.Vk, r.Scan, r.Extended, r.Ctrl, r.Alt, r.Shift, r.Win);
            ev.Rec["keyCode"] = r.Vk;
            ev.Rec["window"] = WindowInfo(root);
            IntPtr fh = r.Focus != IntPtr.Zero ? r.Focus : r.Fg;
            ev.Rec["win32"] = Win32Info(fh, root);
            // FocusedElement はこの時点で既に次の要素へ移っていることがある(Tab など)。
            // フックの時点で取ったフォーカスが個別のコントロールならそちらを優先する
            AutomationElement el = null;
            try
            {
                if (r.Focus != IntPtr.Zero && r.Focus != root) el = AutomationElement.FromHandle(r.Focus);
                if (el == null) el = AutomationElement.FocusedElement;
            }
            catch { }
            if (el != null && SameProcess(el, root)) ev.Rec["uia"] = UiaInfo(el, root, rf, ev);
        }

        bool IsEditContext(IntPtr focus)
        {
            if (focus != IntPtr.Zero)
            {
                string c = WinUtil.ClassOf(focus).ToUpperInvariant();
                if (c.Contains("EDIT") || c.Contains("MEMO") || c.StartsWith("SCINTILLA")) return true;
            }
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el == null) return false;
                var ct = el.Current.ControlType;
                return ct == ControlType.Edit || ct == ControlType.Document;
            }
            catch { return false; }
        }

        static bool ImeOpen(IntPtr fg)
        {
            IntPtr ime = N.ImmGetDefaultIMEWnd(fg);
            if (ime == IntPtr.Zero) return false;
            IntPtr res;
            // 応答しないアプリで固まらないようタイムアウト付き
            if (N.SendMessageTimeout(ime, N.WM_IME_CONTROL, (IntPtr)N.IMC_GETOPENSTATUS, IntPtr.Zero, N.SMTO_ABORTIFHUNG, 100, out res) == IntPtr.Zero) return false;
            return res != IntPtr.Zero;
        }
    }
}
