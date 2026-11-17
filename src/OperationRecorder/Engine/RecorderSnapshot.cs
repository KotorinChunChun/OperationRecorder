using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace OpRec
{
    // 手動キャプチャ(Ctrl+Shift だけを押して離す / Win+Shift+S)と、直前 1 手の取消(Ctrl+Shift+F10)
    public sealed partial class Recorder
    {
        void OnSnapshot(Raw r)
        {
            DispatchSnapshot(r.Ts, r.Fg, (SnapshotTrigger)r.Vk);
        }

        void DispatchSnapshot(long timestamp, IntPtr foreground, SnapshotTrigger trigger)
        {
            if (!TargetReady.WaitOne(0)) return;
            bool windowsSnip = trigger == SnapshotTrigger.WindowsSnip;
            TakeSnapshot(timestamp, foreground, windowsSnip ? CaptureRules.WinShiftS : CaptureRules.CtrlShift,
                windowsSnip ? "Win+Shift+S" : "Ctrl+Shift");
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
            ev.Kind = EventKinds.Capture;
            ev.Before = rule.Before ? f : null;
            ev.After = rule.After ? f : null;
            if (ev.After == null) ev.AfterMissing = true;
            ev.AfterRefTs = ts; ev.AfterRefName = "eventStart";
            ev.Rec["trigger"] = trigger;
            RaiseCaptured(EventKinds.Capture, null);
            IntPtr root = WinUtil.Root(fg);
            if (root != IntPtr.Zero)
            {
                ev.WinFrame = WinUtil.GetFrame(root);
                ev.Rec["window"] = WindowInfo(root);
            }
            AddPendingAndSend(ev);
            N.MessageBeep(N.MB_OK);
        }

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
            if (candTs == long.MinValue && pend == null && saved == null) { Log("取り消す操作がありません"); N.MessageBeep(N.MB_ICONHAND); return; }

            if (candTs >= pendTs && candTs >= savedTs) UndoCandidate();
            else if (pendTs >= savedTs) UndoPending(pend);
            else UndoSaved(saved);
            N.MessageBeep(N.MB_ICONASTERISK);
        }

        // まだ確定していない入力欄の文字
        void UndoCandidate()
        {
            var c = focusCand;
            focusCand = null;
            if (c.Before != null) Unpin(c.Before);
            Log("取り消しました: 入力中の文字");
        }

        // 保存待ちの操作
        void UndoPending(Ev pend)
        {
            lock (pendingLock)
            {
                pending.Remove(pend);
                ReleaseFrames(pend);
                for (int b = 0; b < openDown.Length; b++) if (openDown[b] == pend) openDown[b] = null;
                if (lastClick == pend) lastClick = null;
                if (lastWheel == pend) lastWheel = null;
            }
            Log("取り消しました: #" + pend.Seq + " " + pend.Kind);
        }

        // 保存済みの操作。events.jsonl を書き直し、画像を消す
        void UndoSaved(SavedRec saved)
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
    }
}
