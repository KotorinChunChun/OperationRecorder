using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Automation;

namespace OpRec
{
    // テキストボックスのフォーカスイン / アウト。
    // フォーカスインで「操作前」を固定し、フォーカスアウトで「〜と入力します」の手順にする
    public sealed partial class Recorder
    {
        FocusCandidate focusCand;

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

        // 打鍵が止まってから少し待って値を読んでおく(フォーカスアウトの時点で入力欄が閉じられていても値を残すため)
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
            ev.Kind = EventKinds.Input;
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
            RaiseCaptured(EventKinds.Input, null);
            AddPendingAndSend(ev);
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
                if (!pw && IsPasswordEdit(h)) pw = true;
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

        // UI Automation が IsPassword を返さない古い Edit でも、ES_PASSWORD スタイルならパスワード欄とみなす
        static bool IsPasswordEdit(IntPtr h)
        {
            return h != IntPtr.Zero && WinUtil.ClassOf(h).ToUpperInvariant().Contains("EDIT")
                && (N.GetWindowLongPtr(h, N.GWL_STYLE).ToInt64() & N.ES_PASSWORD) != 0;
        }
    }
}
