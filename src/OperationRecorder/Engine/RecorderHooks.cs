using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpRec
{
    public sealed partial class Recorder
    {
        uint hookTid;
        IntPtr mouseHook, keyHook;
        N.HookProc mouseDel, keyDel;   // GC に回収されないようフィールドで保持する
        N.WinEventProc focusDel;
        IntPtr focusHook;
        N.WinEventProc windowDel;
        IntPtr foregroundHook, windowObjectHook;
        readonly HashSet<int> keysDown = new HashSet<int>();
        bool hShift, hCtrl, hAlt, hWin;
        // Ctrl+Shift だけを押して離したら手動キャプチャ。途中で他のキーやクリックがあれば取りやめる
        bool snapArmed, snapSpoiled;
        long snapTs;
        const int StateEventQueueLimit = 1024;

        void HookLoop(ManualResetEvent ready, ref string error)
        {
            mouseDel = MouseProc;
            keyDel = KeyProc;
            IntPtr hMod = N.GetModuleHandle(null);
            mouseHook = N.SetWindowsHookEx(N.WH_MOUSE_LL, mouseDel, hMod, 0);
            int e1 = Marshal.GetLastWin32Error();
            keyHook = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, keyDel, hMod, 0);
            int e2 = Marshal.GetLastWin32Error();
            hookTid = N.GetCurrentThreadId();
            if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero)
            {
                error = "フックを設定できませんでした (mouse err=" + e1 + ", key err=" + e2 + ")。セキュリティソフトに止められている可能性があります";
                if (mouseHook != IntPtr.Zero) N.UnhookWindowsHookEx(mouseHook);
                if (keyHook != IntPtr.Zero) N.UnhookWindowsHookEx(keyHook);
                ready.Set();
                return;
            }
            // フォーカスの移動(テキストボックスのフォーカスイン / アウト)を知るための WinEvent。
            // WINEVENT_OUTOFCONTEXT なので、このスレッドのメッセージループでコールバックが呼ばれる
            focusDel = (h, evt, hwnd, idObject, idChild, thread, time) =>
                Enqueue(new Raw { Type = RawType.Focus, Fg = hwnd, Ts = sw.ElapsedMilliseconds });
            focusHook = N.SetWinEventHook(N.EVENT_OBJECT_FOCUS, N.EVENT_OBJECT_FOCUS, IntPtr.Zero, focusDel, 0, 0,
                N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);
            windowDel = (h, evt, hwnd, obj, child, thread, time) =>
            {
                var type = ClassifyWindowEvent(evt, obj, child);
                if (type != RawType.None)
                    Enqueue(new Raw { Type = type, Fg = hwnd, Ts = sw.ElapsedMilliseconds });
            };
            foregroundHook = N.SetWinEventHook(N.EVENT_SYSTEM_FOREGROUND, N.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, windowDel, 0, 0, N.WINEVENT_SKIPOWNPROCESS);
            windowObjectHook = N.SetWinEventHook(N.EVENT_OBJECT_DESTROY, N.EVENT_OBJECT_STATECHANGE, IntPtr.Zero, windowDel, 0, 0, N.WINEVENT_SKIPOWNPROCESS);
            if (Rule(CaptureRules.WindowChange).Record && (foregroundHook == IntPtr.Zero || windowObjectHook == IntPtr.Zero))
                error = "ウィンドウの表示・切り替えの監視を設定できませんでした。";
            ready.Set();

            N.MSG msg;
            while (error == null && N.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }

            N.UnhookWindowsHookEx(mouseHook);
            N.UnhookWindowsHookEx(keyHook);
            if (focusHook != IntPtr.Zero) N.UnhookWinEvent(focusHook);
            if (foregroundHook != IntPtr.Zero) N.UnhookWinEvent(foregroundHook);
            if (windowObjectHook != IntPtr.Zero) N.UnhookWinEvent(windowObjectHook);
        }

        // 表示・破棄・前面化は、状態通知が滞っていても受け付ける。
        RawType ClassifyWindowEvent(uint eventId, int objectId, int childId)
        {
            if (eventId == N.EVENT_SYSTEM_FOREGROUND) return RawType.Foreground;
            if (childId != N.CHILDID_SELF) return RawType.None;

            return eventId switch
            {
                N.EVENT_OBJECT_SHOW when objectId == N.OBJID_WINDOW => RawType.WindowShow,
                N.EVENT_OBJECT_DESTROY when objectId == N.OBJID_WINDOW => RawType.WindowDestroy,
                N.EVENT_OBJECT_HIDE or N.EVENT_OBJECT_STATECHANGE
                    when (objectId is N.OBJID_WINDOW or N.OBJID_CLIENT) && rawQ.Count < StateEventQueueLimit
                    => RawType.TargetState,
                _ => RawType.None
            };
        }

        static RawType MouseMessageType(int message, out int button)
        {
            button = RawButton.Left;
            switch (message)
            {
                case N.WM_LBUTTONDOWN: return RawType.MouseDown;
                case N.WM_LBUTTONUP: return RawType.MouseUp;
                case N.WM_RBUTTONDOWN: button = RawButton.Right; return RawType.MouseDown;
                case N.WM_RBUTTONUP: button = RawButton.Right; return RawType.MouseUp;
                case N.WM_MBUTTONDOWN: button = RawButton.Middle; return RawType.MouseDown;
                case N.WM_MBUTTONUP: button = RawButton.Middle; return RawType.MouseUp;
                case N.WM_XBUTTONDOWN: return RawType.MouseDown;
                case N.WM_XBUTTONUP: return RawType.MouseUp;
                case N.WM_MOUSEWHEEL: return RawType.Wheel;   // 縦のホイール
                default: return RawType.None;
            }
        }

        IntPtr MouseProc(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                int btn;
                int message = (int)w;
                RawType type = MouseMessageType(message, out btn);
                if (type != RawType.None)
                {
                    if (hCtrl || hShift) snapSpoiled = true;   // Ctrl+Shift+クリックはキャプチャではない
                    var s = Marshal.PtrToStructure<N.MSLLHOOKSTRUCT>(l);
                    if (message is N.WM_XBUTTONDOWN or N.WM_XBUTTONUP)
                    {
                        uint extended = s.mouseData >> 16;
                        if (extended is not (N.XBUTTON1 or N.XBUTTON2))
                            return N.CallNextHookEx(IntPtr.Zero, nCode, w, l);
                        btn = extended == N.XBUTTON1 ? RawButton.Back : RawButton.Forward;
                    }
                    var raw = new Raw { Type = type, Button = btn, X = s.pt.X, Y = s.pt.Y, Ts = sw.ElapsedMilliseconds };
                    if (type == RawType.Wheel) raw.Vk = (short)(s.mouseData >> 16);   // ホイールの回転量(正 = 上 / 奥、負 = 下 / 手前。1 ノッチ = 120)
                    if (type != RawType.MouseUp && TargetReady.WaitOne(0))
                    {
                        raw.InputState = CaptureInputState(N.WindowFromPoint(s.pt), new Point(s.pt.X, s.pt.Y));
                        raw.Ts = sw.ElapsedMilliseconds;
                    }
                    Enqueue(raw);
                }
            }
            return N.CallNextHookEx(IntPtr.Zero, nCode, w, l);
        }

        IntPtr KeyProc(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                int m = (int)w;
                bool down = m == N.WM_KEYDOWN || m == N.WM_SYSKEYDOWN;
                bool up = m == N.WM_KEYUP || m == N.WM_SYSKEYUP;
                if (down || up)
                {
                    var s = Marshal.PtrToStructure<N.KBDLLHOOKSTRUCT>(l);
                    int vk = (int)s.vkCode;
                    bool wasCtrlOrShift = hCtrl || hShift;
                    switch (vk)
                    {
                        case VK.Shift: case VK.LShift: case VK.RShift: hShift = down; break;
                        case VK.Control: case VK.LControl: case VK.RControl: hCtrl = down; break;
                        case VK.Menu: case VK.LMenu: case VK.RMenu: hAlt = down; break;
                        case VK.LWin: case VK.RWin: hWin = down; break;
                    }
                    bool isCtrlOrShift = vk == VK.Shift || vk == VK.Control || (vk >= VK.LShift && vk <= VK.RControl);
                    if (!wasCtrlOrShift && down) { snapArmed = false; snapSpoiled = false; }
                    if (down && !isCtrlOrShift) snapSpoiled = true;
                    if (down && isCtrlOrShift && hCtrl && hShift && !snapArmed) { snapArmed = true; snapTs = sw.ElapsedMilliseconds; }
                    if (up && isCtrlOrShift && snapArmed)
                    {
                        // 片方を離した時点で判定する。他のキーもクリックも無ければキャプチャ
                        if (!snapSpoiled && !hAlt && !hWin)
                            Enqueue(new Raw { Type = RawType.Snapshot, Vk = (int)SnapshotTrigger.Manual, Ts = snapTs, Fg = N.GetForegroundWindow() });
                        snapArmed = false;
                        snapSpoiled = true;   // もう片方を離すまで、再度のキャプチャはしない
                    }
                    if (down)
                    {
                        bool repeat = !keysDown.Add(vk);
                        bool alt = hAlt || (s.flags & N.LLKHF_ALTDOWN) != 0;
                        var action = Hotkeys != null ? Hotkeys.ActionOf(vk, hCtrl, hShift, alt, hWin) : null;
                        if (action.HasValue)
                        {
                            // 記録の操作キー(既定は Ctrl+Shift+F9〜F12)。操作としては記録しない
                            //   開始・再開 : 記録中は何もしない(待機中は HotkeyListener が受ける)
                            //   取消       : 直前 1 手の取消(振り分けスレッドで処理する)
                            //   取り直し   : 同じ設定で最初から取り直す
                            //   終了       : 停止・終了
                            if (!repeat)
                            {
                                if (action == HotkeyAction.Undo) Enqueue(new Raw { Type = RawType.Undo, Ts = sw.ElapsedMilliseconds });
                                else if (action == HotkeyAction.Restart) RestartRequested.Set();
                                else if (action == HotkeyAction.Stop) StopRequested.Set();
                            }
                        }
                        else
                        {
                            IntPtr fg = N.GetForegroundWindow();
                            IntPtr focus = WinUtil.FocusOf(fg);
                            var inputState = TargetReady.WaitOne(0) && !KeyClassifier.IsModifier(vk) ? CaptureInputState(focus == IntPtr.Zero ? fg : focus) : null;
                            Enqueue(new Raw
                            {
                                Type = RawType.KeyDown, Vk = vk, Scan = (int)s.scanCode, Extended = (s.flags & N.LLKHF_EXTENDED) != 0,
                                Ctrl = hCtrl, Shift = hShift, Alt = alt, Win = hWin,
                                Repeat = repeat, Ts = sw.ElapsedMilliseconds, Fg = fg, Focus = focus, InputState = inputState
                            });
                        }
                    }
                    else keysDown.Remove(vk);
                }
            }
            return N.CallNextHookEx(IntPtr.Zero, nCode, w, l);
        }

        void Enqueue(Raw r)
        {
            try { rawQ.TryAdd(r); } catch (InvalidOperationException) { }
        }
    }
}
