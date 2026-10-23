using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace OpRec
{
    public sealed partial class Recorder
    {
        readonly Ev[] openDown = new Ev[3];
        Ev lastClick;
        bool swallowPickUp;

        // テキスト入力の追跡。フォーカスインで「操作前」を固定し、フォーカスアウトで「〜と入力します」の手順にする
        FocusCandidate focusCand;
        bool composing;

        void DispatchLoop()
        {
            while (true)
            {
                Raw r;
                bool got;
                try { got = rawQ.TryTake(out r, 50); }
                catch (InvalidOperationException) { break; }
                if (got)
                {
                    try
                    {
                        switch (r.Type)
                        {
                            case 1: OnMouseDown(r); break;
                            case 2: OnMouseUp(r); break;
                            case 3: OnKeyDown(r); break;
                            case 4: OnFocus(r); break;
                            case 5: UndoLast(); break;
                            case 6: OnSnapshot(r); break;
                            case 7: OnWheel(r); break;
                        }
                    }
                    catch (Exception ex) { Log("振り分けエラー: " + ex.Message); }
                }
                else if (rawQ.IsCompleted) break;

                try { InputDebounce(); } catch { }
            }

            try { FinishCandidate(sw.ElapsedMilliseconds); } catch (Exception ex) { Log("入力の確定に失敗: " + ex.Message); }
            lock (pendingLock)
            {
                long now = sw.ElapsedMilliseconds;
                foreach (var ev in pending) if (ev.AfterDue == long.MaxValue) ev.AfterDue = now;
            }
        }

        Ev NewEv(long ts) { return new Ev { Seq = Interlocked.Increment(ref seqCounter), Ts = ts }; }

        // ---------- マウス ----------

        void OnMouseDown(Raw r)
        {
            composing = false;
            IntPtr hwnd = N.WindowFromPoint(new N.POINT { X = r.X, Y = r.Y });
            IntPtr root = WinUtil.Root(hwnd);

            if (!TargetReady.WaitOne(0))
            {
                if (!PickTarget || r.Button != 0) return;
                if (Mode == CaptureMode.Desktop)
                {
                    // Desktop モードの Pick は、クリックしたウィンドウが映っているディスプレイを対象にする
                    TargetMonitor = Monitors.OfWindow(root);
                    if (TargetMonitor == null) return;
                    Log("対象を選択しました: ディスプレイ " + TargetMonitor.Index + " " + TargetMonitor.Describe());
                }
                else
                {
                    TargetHwnd = root;
                    if (Mode == CaptureMode.App) RefreshPids();
                    Log("対象を選択しました: " + WinUtil.Describe(root));
                }
                WriteSession(false);
                swallowPickUp = true;
                TargetReady.Set();
                return;
            }

            if (!InScope(root, new Point(r.X, r.Y)))
            {
                openDown[r.Button] = null;
                lastClick = null;
                return;
            }

            // 撮影ルールで記録しない操作は、ここで捨てる
            var mrule = Rule(r.Button == 0 ? CaptureRules.LeftClick : r.Button == 1 ? CaptureRules.RightClick : CaptureRules.MiddleClick);
            if (!mrule.Record)
            {
                openDown[r.Button] = null;
                if (r.Button == 0) lastClick = null;
                return;
            }

            lock (pendingLock)
            {
                var lc = lastClick;
                int dcx = N.GetSystemMetrics(36), dcy = N.GetSystemMetrics(37);
                if (lc != null && r.Button == 0 && !lc.Sent && lc.Kind == "click"
                    && r.Ts - lc.Ts <= N.GetDoubleClickTime()
                    && Math.Abs(r.X - lc.X) <= dcx && Math.Abs(r.Y - lc.Y) <= dcy)
                {
                    lc.Kind = "doubleclick";
                    if (lc.After != null) { Unpin(lc.After); lc.After = null; }
                    lc.AfterDue = long.MaxValue;
                    openDown[0] = lc;
                    lastClick = null;
                    return;
                }
            }

            var ev = NewEv(r.Ts);
            ev.IsMouse = true;
            ev.Button = r.Button;
            ev.Kind = r.Button == 0 ? "click" : r.Button == 1 ? "rightclick" : "middleclick";
            ev.X = ev.EndX = r.X;
            ev.Y = ev.EndY = r.Y;
            ev.Root = root;
            ev.Before = mrule.Before ? PickBefore(r.Ts) : null;
            ev.WantAfter = mrule.After;
            lock (pendingLock) pending.Add(ev);
            openDown[r.Button] = ev;
            lastClick = r.Button == 0 ? ev : null;
            RaiseCaptured(ev.Kind, new Point(r.X, r.Y));

            try { FillMouseInfo(ev, hwnd, root); } catch (Exception ex) { Log("要素情報の取得に失敗: " + ex.Message); }
            lock (pendingLock) { ev.InfoDone = true; TrySendLocked(ev); }
        }

        void OnMouseUp(Raw r)
        {
            if (swallowPickUp && r.Button == 0) { swallowPickUp = false; return; }
            var ev = openDown[r.Button];
            openDown[r.Button] = null;
            if (ev == null) return;
            lock (pendingLock)
            {
                if (ev.Sent) return;
                int tx = Math.Max(8, N.GetSystemMetrics(68) * 2), ty = Math.Max(8, N.GetSystemMetrics(69) * 2);
                if (ev.Kind != "doubleclick" && (Math.Abs(r.X - ev.X) > tx || Math.Abs(r.Y - ev.Y) > ty))
                {
                    ev.Kind = ev.Button == 1 ? "rightdrag" : "drag";
                    ev.EndX = r.X;
                    ev.EndY = r.Y;
                    if (lastClick == ev) lastClick = null;

                    // ウィンドウを別のディスプレイへ(から)ドラッグで移動しただけの操作は、手順ではないので記録しない。
                    // 押した位置と離した位置のディスプレイが違い、ウィンドウ自体の位置が変わっていれば「ウィンドウの移動」とみなす
                    if (IsCrossMonitorWindowMove(ev, r))
                    {
                        pending.Remove(ev);
                        if (ev.Before != null) Unpin(ev.Before);
                        if (ev.After != null) Unpin(ev.After);
                        Log("記録しません: ウィンドウを別のディスプレイへ移動するドラッグ");
                        return;
                    }
                }
                ev.UpTs = r.Ts;
                if (ev.After != null) { Unpin(ev.After); ev.After = null; }
                if (ev.WantAfter) ev.AfterDue = r.Ts + AfterMs;
                else { ev.AfterMissing = true; TrySendLocked(ev); }   // 「操作後を撮る」が切られている
            }
        }

        // ---------- マウスホイール ----------
        // 同じウィンドウで続けて回したもの(前の回転から 1 秒以内)は 1 つの手順にまとめ、操作後は最後に回した後の画面にする。
        // 上下に回した分は差し引き、元の位置に戻した(合計 0)ときは記録しない

        Ev lastWheel;

        void OnWheel(Raw r)
        {
            if (!TargetReady.WaitOne(0) || r.Vk == 0) return;
            var rule = Rule(CaptureRules.MouseWheel);
            if (!rule.Record) return;
            IntPtr hwnd = N.WindowFromPoint(new N.POINT { X = r.X, Y = r.Y });
            IntPtr root = WinUtil.Root(hwnd);
            if (!InScope(root, new Point(r.X, r.Y))) return;
            int delta = WheelRules.Notches(r.Vk);

            lock (pendingLock)
            {
                var w = lastWheel;
                // 向きが逆でもまとめて差し引く(下に回して上に戻したら相殺され、0 なら記録しない)
                if (w != null && WheelRules.CanMerge(w.Sent, w.Root, w.WheelLastTs, root, r.Ts))
                {
                    w.WheelNet += delta;
                    w.WheelLastTs = r.Ts;
                    w.Rec["wheel"] = WheelRules.Info(w.WheelNet);
                    if (w.After != null) { Unpin(w.After); w.After = null; }
                    if (w.WantAfter) w.AfterDue = r.Ts + AfterMs;
                    return;
                }
            }

            var ev = NewEv(r.Ts);
            ev.IsMouse = true;
            ev.Kind = "wheel";
            ev.X = ev.EndX = r.X;
            ev.Y = ev.EndY = r.Y;
            ev.Root = root;
            ev.WheelNet = delta;
            ev.WheelLastTs = r.Ts;
            ev.Before = rule.Before ? PickBefore(r.Ts) : null;
            ev.WantAfter = rule.After;
            if (rule.After) ev.AfterDue = r.Ts + AfterMs; else ev.AfterMissing = true;
            ev.Rec["wheel"] = WheelRules.Info(ev.WheelNet);
            lock (pendingLock) { pending.Add(ev); lastWheel = ev; }
            RaiseCaptured("wheel", new Point(r.X, r.Y));
            try { FillMouseInfo(ev, hwnd, root); } catch (Exception ex) { Log("要素情報の取得に失敗: " + ex.Message); }
            lock (pendingLock) { ev.InfoDone = true; TrySendLocked(ev); }
        }


        static bool IsCrossMonitorWindowMove(Ev ev, Raw up)
        {
            IntPtr m1 = N.MonitorFromPoint(new N.POINT { X = ev.X, Y = ev.Y }, 2);
            IntPtr m2 = N.MonitorFromPoint(new N.POINT { X = up.X, Y = up.Y }, 2);
            if (m1 == m2 || ev.Root == IntPtr.Zero || !N.IsWindow(ev.Root)) return false;
            Rectangle now = WinUtil.GetFrame(ev.Root);
            return now.Location != ev.WinFrame.Location || now.Size != ev.WinFrame.Size;
        }

        void FillMouseInfo(Ev ev, IntPtr hwnd, IntPtr root)
        {
            Rectangle rf = WinUtil.GetFrame(root);
            ev.WinFrame = rf;
            ev.Rec["window"] = WindowInfo(root);
            ev.Rec["pointInWindow"] = WinUtil.Pt(ev.X - rf.X, ev.Y - rf.Y);
            var cp = new N.POINT { X = ev.X, Y = ev.Y };
            N.ScreenToClient(root, ref cp);
            ev.Rec["pointInClient"] = WinUtil.Pt(cp.X, cp.Y);
            ev.Rec["win32"] = Win32Info(hwnd, root);

            AutomationElement el = null;
            try { el = AutomationElement.FromPoint(new System.Windows.Point(ev.X, ev.Y)); } catch { }
            if (el != null) ev.Rec["uia"] = UiaInfo(el, root, rf, ev);
        }

        // ---------- キーボード ----------

        static bool IsModifier(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        static bool IsPrintable(int vk)
        {
            return (vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A) || (vk >= 0x60 && vk <= 0x6F)
                || (vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF) || vk == 0xE2 || vk == 0xE7;
        }

        static bool IsImeOrLockKey(int vk)
        {
            return vk == 0x15 || vk == 0x19 || vk == 0x1C || vk == 0x1D || vk == 0x1E || vk == 0x1F || vk == 0xE5
                || (vk >= 0xF0 && vk <= 0xF6) || vk == 0x14 || vk == 0x90 || vk == 0x91;
        }

        // テキストボックスの中では編集操作とみなすキー(外では操作として撮る)
        static bool IsEditingKey(int vk)
        {
            return vk == 0x20 || vk == 0x08 || vk == 0x2E || vk == 0x2D || (vk >= 0x21 && vk <= 0x28);
        }

        void OnKeyDown(Raw r)
        {
            if (!TargetReady.WaitOne(0)) return;
            int vk = r.Vk;
            if (IsModifier(vk)) return;
            IntPtr root = WinUtil.Root(r.Fg);
            if (!InScope(root)) return;

            bool ctrlOrAlt = r.Ctrl || r.Alt;
            bool typing;
            bool endComposition = false;
            if (IsImeOrLockKey(vk) || (r.Alt && vk == 0xC0)) typing = true;
            else if (ctrlOrAlt || r.Win) typing = false;
            else if (IsPrintable(vk))
            {
                // A〜Z・1〜0 は、そのグループを「記録する」にしたときだけ、入力欄の外で押したものを手順にする
                string g = CaptureRules.KeyGroupOf(vk, false, false, false);
                typing = !((g == CaptureRules.KeyLetters || g == CaptureRules.KeyDigits) && Rule(g).Record && !composing && !IsEditContext(r.Focus));
            }
            else if (IsEditingKey(vk)) typing = composing || IsEditContext(r.Focus);
            else if (composing && (vk == 0x0D || vk == 0x1B || (vk >= 0x75 && vk <= 0x79)))
            {
                // 日本語入力の変換中の Enter / Esc / F6〜F10 は、変換の確定・取り消しなので撮らない
                typing = true;
                endComposition = vk == 0x0D || vk == 0x1B;
            }
            else typing = false;

            if (typing)
            {
                // VK_PACKET(0xE7) は Unicode 文字を直接送る入力で、日本語入力の変換を通らない
                if (IsPrintable(vk) && vk != 0xE7 && !composing && ImeOpen(r.Fg)) composing = true;
                if (endComposition) composing = false;
                // 記録開始時から既にフォーカスがあった入力欄など、フォーカスインを見ていない場合はここで追跡を始める
                if (focusCand == null && Rule(CaptureRules.TextInput).Record) TryStartCandidate(root, r.Ts);
                if (focusCand != null) { focusCand.Typed = true; focusCand.LastTypeTs = r.Ts; }
                return;
            }
            if (r.Repeat) return;   // 押しっぱなしの自動リピートは1回目だけ撮る

            composing = false;
            // Win+Shift+S(Windows の切り取り)は、切り取り画面が出る前の、押した瞬間の画面をキャプチャする
            if (r.Win && r.Shift && !r.Ctrl && !r.Alt && vk == 0x53)
            {
                TakeSnapshot(r.Ts, r.Fg, CaptureRules.WinShiftS, "Win+Shift+S");
                return;
            }
            var krule = Rule(CaptureRules.KeyGroupOf(vk, r.Ctrl, r.Alt, r.Win));
            if (!krule.Record) return;
            var ev = NewEv(r.Ts);
            ev.Kind = "key";
            ev.Before = krule.Before ? PickBefore(r.Ts) : null;
            if (krule.After) ev.AfterDue = r.Ts + AfterMs;
            else ev.AfterMissing = true;
            lock (pendingLock) pending.Add(ev);
            RaiseCaptured("key", null);

            try
            {
                Rectangle rf = WinUtil.GetFrame(root);
                ev.WinFrame = rf;
                ev.Rec["key"] = KeyText(r);
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
            catch (Exception ex) { Log("要素情報の取得に失敗: " + ex.Message); }
            lock (pendingLock) { ev.InfoDone = true; TrySendLocked(ev); }
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
            // WM_IME_CONTROL / IMC_GETOPENSTATUS。応答しないアプリで固まらないようタイムアウト付き
            if (N.SendMessageTimeout(ime, 0x283, (IntPtr)5, IntPtr.Zero, 2, 100, out res) == IntPtr.Zero) return false;
            return res != IntPtr.Zero;
        }

        static string KeyText(Raw r)
        {
            var parts = new List<string>();
            if (r.Ctrl) parts.Add("Ctrl");
            if (r.Alt) parts.Add("Alt");
            if (r.Shift) parts.Add("Shift");
            if (r.Win) parts.Add("Win");
            parts.Add(VkName(r));
            return string.Join("+", parts);
        }

        static string VkName(Raw r)
        {
            int vk = r.Vk;
            switch (vk)
            {
                case 0x0D: return "Enter";
                case 0x09: return "Tab";
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0x08: return "Backspace";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x5D: return "Apps";
                case 0x2C: return "PrintScreen";
                case 0x13: return "Pause";
            }
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            var sb = new StringBuilder(64);
            if (N.GetKeyNameText((r.Scan << 16) | (r.Extended ? 1 << 24 : 0), sb, sb.Capacity) > 0) return sb.ToString();
            return "VK_0x" + vk.ToString("X2");
        }

        // ---------- テキストボックスのフォーカスイン / アウト ----------

        void OnFocus(Raw r)
        {
            AutomationElement el = null;
            try { el = AutomationElement.FocusedElement; } catch { }
            string key = RuntimeKey(el);
            if (focusCand != null && key != null && key == focusCand.Key) return;   // 同じ入力欄の中での通知

            // 前の入力欄からフォーカスが外れた(対象外のアプリに移った場合も含む)
            FinishCandidate(r.Ts);

            if (!TargetReady.WaitOne(0) || el == null || !Rule(CaptureRules.TextInput).Record) return;
            IntPtr root = WinUtil.Root(r.Fg);
            if (!InScope(root) || !SameProcess(el, root) || !IsEditableText(el)) return;
            StartCandidate(el, root, r.Ts);
        }

        void TryStartCandidate(IntPtr root, long ts)
        {
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el != null && SameProcess(el, root) && IsEditableText(el)) StartCandidate(el, root, ts);
            }
            catch { }
        }

        void StartCandidate(AutomationElement el, IntPtr root, long ts)
        {
            var c = new FocusCandidate { Key = RuntimeKey(el), Element = el, Root = root, TsIn = ts };
            c.Before = Rule(CaptureRules.TextInput).Before ? PickFrameAt(ts) : null;
            try
            {
                Rectangle rf = WinUtil.GetFrame(root);
                c.WinFrame = rf;
                c.Window = WindowInfo(root);
                var tmp = new Ev();
                c.Uia = UiaInfo(el, root, rf, tmp);
                c.MarkRect = tmp.MarkRect;
                IntPtr h = IntPtr.Zero;
                try { h = new IntPtr(el.Current.NativeWindowHandle); } catch { }
                c.Win32 = Win32Info(h != IntPtr.Zero ? h : WinUtil.FocusOf(root), root);
                c.ValueIn = ReadInput(el);
            }
            catch (Exception ex) { Log("入力欄の情報取得に失敗: " + ex.Message); }
            focusCand = c;
        }

        void InputDebounce()
        {
            var c = focusCand;
            if (c == null || !c.Typed || sw.ElapsedMilliseconds - c.LastTypeTs < 150) return;
            if (c.LastValue != null && c.LastValue.ContainsKey("readAt") && (long)c.LastValue["readAt"] >= c.LastTypeTs) return;
            var v = ReadInput(c.Element);
            if (v != null) { v["readAt"] = sw.ElapsedMilliseconds; c.LastValue = v; }
        }

        void FinishCandidate(long tsOut)
        {
            var c = focusCand;
            if (c == null) return;
            focusCand = null;

            var v = ReadInput(c.Element);                 // 入力欄が閉じられていれば失敗するので、打鍵中に読んだ値を使う
            if (v == null) v = c.LastValue;
            if (v != null) v.Remove("readAt");

            // 打鍵があったか、値が変わっていれば(右クリックの貼り付けなど)入力とみなす
            bool changed = c.Typed;
            if (!changed && v != null && c.ValueIn != null) changed = !Equals(v["value"], c.ValueIn["value"]);
            if (!changed || v == null)
            {
                if (c.Before != null) Unpin(c.Before);
                return;
            }

            var ev = NewEv(c.LastTypeTs > 0 ? c.LastTypeTs : tsOut);
            ev.Kind = "input";
            ev.WinFrame = c.WinFrame;
            ev.MarkRect = c.MarkRect;
            ev.Before = c.Before;
            ev.BeforeRefTs = c.TsIn; ev.BeforeRefName = "focusIn";
            ev.After = Rule(CaptureRules.TextInput).After ? PickFrameAt(tsOut) : null;
            ev.AfterRefTs = tsOut; ev.AfterRefName = "focusOut";
            if (ev.After == null) ev.AfterMissing = true;
            ev.Rec["window"] = c.Window;
            ev.Rec["uia"] = c.Uia;
            ev.Rec["win32"] = c.Win32;
            ev.Rec["input"] = v;
            RaiseCaptured("input", null);
            ev.InfoDone = true;
            lock (pendingLock) { pending.Add(ev); TrySendLocked(ev); }
        }

        static bool IsEditableText(AutomationElement el)
        {
            try
            {
                var ct = el.Current.ControlType;
                if (ct != ControlType.Edit && ct != ControlType.Document) return false;
                object pat;
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pat)) return !((ValuePattern)pat).Current.IsReadOnly;
                return el.TryGetCurrentPattern(TextPattern.Pattern, out pat);
            }
            catch { return false; }
        }

        static string RuntimeKey(AutomationElement el)
        {
            if (el == null) return null;
            try { return string.Join(".", el.GetRuntimeId()); } catch { return null; }
        }

        Dictionary<string, object> ReadInput(AutomationElement e)
        {
            if (e == null) return null;
            try
            {
                var d = new Dictionary<string, object>();
                d["name"] = WinUtil.Trunc(e.Current.Name, 200);
                d["automationId"] = e.Current.AutomationId;
                d["controlType"] = CtName(e.Current.ControlType);
                d["className"] = e.Current.ClassName;
                string label = null;
                try { var lb = e.Current.LabeledBy; if (lb != null) label = lb.Current.Name; } catch { }
                d["label"] = label;

                bool pw = e.Current.IsPassword;
                IntPtr h = new IntPtr(e.Current.NativeWindowHandle);
                if (!pw && h != IntPtr.Zero && WinUtil.ClassOf(h).ToUpperInvariant().Contains("EDIT")
                    && (N.GetWindowLongPtr(h, -16).ToInt64() & 0x20 /*ES_PASSWORD*/) != 0) pw = true;
                d["isPassword"] = pw;
                if (pw || !RecordInputValues) { d["value"] = null; return d; }

                object pat;
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                    d["value"] = WinUtil.Trunc(((ValuePattern)pat).Current.Value, 2000);
                else if (e.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                    d["value"] = WinUtil.Trunc(((TextPattern)pat).DocumentRange.GetText(2001), 2000);
                else return null;
                return d;
            }
            catch { return null; }
        }

        // ---------- 手動キャプチャ(Ctrl+Shift だけを押して離す) ----------

        void OnSnapshot(Raw r)
        {
            if (!TargetReady.WaitOne(0)) return;
            TakeSnapshot(r.Ts, r.Fg, CaptureRules.CtrlShift, "Ctrl+Shift");
        }

        // ts の時点の画面を 1 つの手順(kind = capture)にする。操作前・操作後は同じ画像だが、後で編集できるよう別ファイルに保存する
        void TakeSnapshot(long ts, IntPtr fg, string ruleKey, string trigger)
        {
            var rule = Rule(ruleKey);
            if (!rule.Record) return;
            Frame f = null;
            if (rule.Before || rule.After)
            {
                f = PickFrameAt(ts);   // 1 回固定済み
                if (f == null) { Log("キャプチャできませんでした(撮影できる画面がありません)"); return; }
                if (rule.Before && rule.After) Pin(f);   // 操作前・操作後の 2 か所で使うので、もう 1 回固定する
            }

            var ev = NewEv(ts);
            ev.Kind = "capture";
            ev.Before = rule.Before ? f : null;
            ev.After = rule.After ? f : null;
            if (ev.After == null) ev.AfterMissing = true;
            ev.AfterRefTs = ts; ev.AfterRefName = "eventStart";
            ev.Rec["trigger"] = trigger;
            RaiseCaptured("capture", null);
            IntPtr root = WinUtil.Root(fg);
            if (root != IntPtr.Zero)
            {
                ev.WinFrame = WinUtil.GetFrame(root);
                ev.Rec["window"] = WindowInfo(root);
            }
            ev.InfoDone = true;
            lock (pendingLock) { pending.Add(ev); TrySendLocked(ev); }
            N.MessageBeep(0);
        }

        // ---------- 直前 1 手の取消(Ctrl+Shift+F10) ----------

        void UndoLast()
        {
            // 保存中のものが無くなるまで待ってから、「まだ確定していない入力」「保存待ちの操作」「保存済みの操作」の
            // うち最も新しいものを 1 つ取り消す
            for (int i = 0; i < 150 && (saverQ.Count > 0 || saverBusy); i++) Thread.Sleep(20);

            long candTs = focusCand != null && focusCand.Typed ? focusCand.LastTypeTs : long.MinValue;
            Ev pend = null;
            lock (pendingLock) foreach (var e in pending) if (!e.Sent && (pend == null || e.Ts > pend.Ts)) pend = e;
            SavedRec saved = null;
            lock (savedLock) foreach (var s in savedRecs) if (saved == null || s.Ts > saved.Ts) saved = s;

            long pendTs = pend != null ? pend.Ts : long.MinValue, savedTs = saved != null ? saved.Ts : long.MinValue;
            if (candTs == long.MinValue && pend == null && saved == null) { Log("取り消す操作がありません"); N.MessageBeep(0x10); return; }

            if (candTs >= pendTs && candTs >= savedTs)
            {
                var c = focusCand;
                focusCand = null;
                if (c.Before != null) Unpin(c.Before);
                Log("取り消しました: 入力中の文字");
            }
            else if (pendTs >= savedTs)
            {
                lock (pendingLock)
                {
                    pending.Remove(pend);
                    if (pend.Before != null) Unpin(pend.Before);
                    if (pend.After != null) Unpin(pend.After);
                    for (int b = 0; b < openDown.Length; b++) if (openDown[b] == pend) openDown[b] = null;
                    if (lastClick == pend) lastClick = null;
                    if (lastWheel == pend) lastWheel = null;
                }
                Log("取り消しました: #" + pend.Seq + " " + pend.Kind);
            }
            else
            {
                lock (savedLock)
                {
                    savedRecs.Remove(saved);
                    jsonl.Dispose();
                    string path = Path.Combine(SessionDir, "events.jsonl");
                    File.WriteAllLines(path, savedRecs.OrderBy(s => s.Seq).Select(s => s.Line), new UTF8Encoding(false));
                    jsonl = new StreamWriter(path, true, new UTF8Encoding(false));
                    foreach (var f in saved.Files) { try { File.Delete(Path.Combine(SessionDir, f)); } catch { } }
                    Interlocked.Decrement(ref savedCount);
                }
                Log("取り消しました: #" + saved.Seq + " " + saved.Kind);
                RaiseStepRemoved(saved.Seq);
            }
            N.MessageBeep(0x40);
        }

        // ---------- 要素情報 ----------

        static bool SameProcess(AutomationElement el, IntPtr root)
        {
            try { return root != IntPtr.Zero && el.Current.ProcessId == (int)WinUtil.PidOf(root); }
            catch { return false; }
        }

        static string CtName(ControlType ct)
        {
            if (ct == null) return null;
            string n = ct.ProgrammaticName ?? "";
            return n.StartsWith("ControlType.") ? n.Substring(12) : n;
        }

        static Dictionary<string, object> WindowInfo(IntPtr root)
        {
            uint pid = WinUtil.PidOf(root);
            return new Dictionary<string, object>
            {
                { "hwnd", WinUtil.Hex(root) },
                { "title", WinUtil.TextOf(root) },
                { "className", WinUtil.ClassOf(root) },
                { "processName", WinUtil.ProcName(pid) },
                { "pid", pid },
                { "frame", WinUtil.Rc(WinUtil.GetFrame(root)) },
                { "program", WinUtil.ProgramInfo(pid) },
            };
        }

        static Dictionary<string, object> Win32Info(IntPtr h, IntPtr root)
        {
            string cls = WinUtil.ClassOf(h);
            var d = new Dictionary<string, object>
            {
                { "hwnd", WinUtil.Hex(h) },
                { "className", cls },
                { "controlId", h != root ? (object)N.GetDlgCtrlID(h) : null },
            };
            // 入力欄のウィンドウテキストは入力内容そのものなので記録しない
            string up = cls.ToUpperInvariant();
            if (h != root && !up.Contains("EDIT") && !up.Contains("MEMO")) d["text"] = WinUtil.Trunc(WinUtil.TextOf(h), 100);
            return d;
        }

        static Dictionary<string, object> UiaInfo(AutomationElement el, IntPtr root, Rectangle rf, Ev ev)
        {
            var d = new Dictionary<string, object>();
            var c = el.Current;
            d["controlType"] = CtName(c.ControlType);
            d["localizedControlType"] = c.LocalizedControlType;
            d["name"] = WinUtil.Trunc(c.Name, 200);
            d["automationId"] = c.AutomationId;
            d["className"] = c.ClassName;
            d["frameworkId"] = c.FrameworkId;
            d["isEnabled"] = c.IsEnabled;
            d["isPassword"] = c.IsPassword;
            var b = c.BoundingRectangle;
            if (!b.IsEmpty && !double.IsInfinity(b.X))
            {
                var sr = new Rectangle((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
                ev.MarkRect = sr;
                d["boundsInWindow"] = WinUtil.Rc(new Rectangle(sr.X - rf.X, sr.Y - rf.Y, sr.Width, sr.Height));
            }

            var ancestors = new List<object>();
            try
            {
                var walker = TreeWalker.ControlViewWalker;
                var p = walker.GetParent(el);
                for (int i = 0; p != null && i < 40; i++)
                {
                    if (Automation.Compare(p, AutomationElement.RootElement)) break;
                    var pc = p.Current;
                    ancestors.Add(new Dictionary<string, object>
                    {
                        { "controlType", CtName(pc.ControlType) },
                        { "name", WinUtil.Trunc(pc.Name, 80) },
                        { "automationId", pc.AutomationId },
                        { "className", pc.ClassName },
                    });
                    if (pc.NativeWindowHandle == root.ToInt32()) break;
                    p = walker.GetParent(p);
                }
            }
            catch { }
            d["ancestors"] = ancestors;
            return d;
        }
    }
}
