using System;
using System.Collections.Generic;
using System.Drawing;

namespace OpRec
{
    public sealed partial class Recorder
    {
        readonly OperationContextTracker contextTracker = new();
        IntPtr contextForeground;

        OperationInputSample CaptureInputState(IntPtr handle, Point? point = null)
        {
            var root = WinUtil.Root(handle);
            if (!InScope(root, point)) return null;
            var pid = WinUtil.PidOf(root);
            if (pid == 0) return null;
            var foreground = WinUtil.Root(N.GetForegroundWindow());
            var focus = WinUtil.FocusOf(root);
            OperationTargetSample Read(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero || !N.IsWindow(hwnd) || WinUtil.PidOf(hwnd) != pid) return null;
                var state = new OperationTargetState(N.IsWindowEnabled(hwnd), N.IsWindowVisible(hwnd), N.IsIconic(hwnd),
                    root == foreground, hwnd == focus);
                return N.IsWindow(hwnd) && WinUtil.PidOf(hwnd) == pid && WinUtil.Root(hwnd) == root
                    ? new(root, hwnd, pid, sw.ElapsedMilliseconds, state) : null;
            }
            return new(Read(root), handle == root ? null : Read(handle));
        }

        void ObserveContextState(Raw raw)
        {
            if (raw.Type == RawType.WindowDestroy)
            {
                contextTracker.Forget(raw.Fg);
                return;
            }
            if (!TargetReady.WaitOne(0)) return;
            if (raw.Type == RawType.Foreground && contextForeground != raw.Fg)
            {
                Observe(contextForeground);
                contextForeground = raw.Fg;
            }
            Observe(raw.Fg);

            void Observe(IntPtr hwnd)
            {
                var state = CaptureInputState(hwnd);
                contextTracker.Observe(state?.Window);
                contextTracker.Observe(state?.Control);
            }
        }

        Dictionary<string, object> RecordOperationContext(Raw raw, IntPtr root, IntPtr expectedControl)
        {
            var state = raw.InputState;
            if (state?.Control is { } control && control.Handle != expectedControl) state = state with { Control = null };
            return contextTracker.RecordInput(raw.Ts, state, root, WinUtil.PidOf(root));
        }
    }
}
