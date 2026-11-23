using System;
using System.Threading;

namespace OpRec
{
    // 振り分けスレッド: フックが積んだ Raw を種類ごとの処理へ渡す。
    // 種類ごとの処理は RecorderMouse / RecorderKeyboard / RecorderTextInput / RecorderSnapshot / RecorderWindowChanges にある
    public sealed partial class Recorder
    {
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
                    try { Dispatch(r); }
                    catch (Exception ex) { Log("振り分けエラー: " + ex.Message); }
                }
                else if (rawQ.IsCompleted) break;

                try { InputDebounce(); } catch { }
                try { FlushWindowChanges(sw.ElapsedMilliseconds, false); } catch (Exception ex) { Log("画面変化の確定に失敗: " + ex.Message); }
            }

            FlushWindowChanges(sw.ElapsedMilliseconds, true);
            try { FinishCandidate(sw.ElapsedMilliseconds); } catch (Exception ex) { Log("入力の確定に失敗: " + ex.Message); }
            lock (pendingLock)
            {
                long now = sw.ElapsedMilliseconds;
                foreach (var ev in pending) if (ev.AfterDue == long.MaxValue) ev.AfterDue = now;
            }
        }

        void Dispatch(Raw r)
        {
            switch (r.Type)
            {
                case RawType.MouseDown: OnMouseDown(r); break;
                case RawType.MouseUp: OnMouseUp(r); break;
                case RawType.KeyDown: OnKeyDown(r); break;
                case RawType.Focus: ObserveContextState(r); OnFocus(r); break;
                case RawType.Undo: FlushWindowChanges(r.Ts, true); UndoLast(); break;
                case RawType.Snapshot: OnSnapshot(r); break;
                case RawType.Wheel: OnWheel(r); break;
                case RawType.WindowShow:
                case RawType.Foreground:
                case RawType.WindowDestroy:
                    ObserveContextState(r);
                    if (Rule(CaptureRules.WindowChange).Record) OnWindowChange(r);
                    break;
                case RawType.TargetState: ObserveContextState(r); break;
            }
        }

        Ev NewEv(long ts) { return new Ev { Seq = Interlocked.Increment(ref seqCounter), Ts = ts }; }

        // 撮影ルールに従って操作後の撮影を予約する。撮らないときは操作後なしで確定できる状態にする
        void ScheduleAfter(Ev ev, bool wantAfter, long ts)
        {
            ev.WantAfter = wantAfter;
            if (wantAfter) ev.AfterDue = ts + AfterMs;
            else ev.AfterMissing = true;
        }

        // 要素情報を取り終えた。他の条件(操作後の画像など)も揃っていれば保存へ回す
        void CompleteInfo(Ev ev)
        {
            lock (pendingLock) { ev.InfoDone = true; TrySendLocked(ev); }
        }

        // 新しい操作を保存待ちに加え、揃っていれば保存へ回す(要素情報を先に取り終えた操作)
        void AddPendingAndSend(Ev ev)
        {
            ev.InfoDone = true;
            lock (pendingLock) { pending.Add(ev); TrySendLocked(ev); }
        }
    }
}
