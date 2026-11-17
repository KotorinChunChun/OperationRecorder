using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Automation;

namespace OpRec
{
    // 操作した部品・ウィンドウの情報(events.jsonl の window / win32 / uia)
    public sealed partial class Recorder
    {
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
                { "dpi", N.GetDpiForWindow(root) },
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

        // ev.MarkRect に部品の画面上の位置を入れる(出力するときに印を描く位置)
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
            d["ancestors"] = Ancestors(el, root);
            return d;
        }

        // 部品の親を、トップレベルウィンドウまで(最大 40 段)
        static List<object> Ancestors(AutomationElement el, IntPtr root)
        {
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
            return ancestors;
        }
    }
}
