using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace OpRec
{
    public sealed partial class Recorder
    {
        // 保存済みの操作(Ctrl+Shift+F10 で取り消すときに、events.jsonl の書き直しと画像の削除に使う)
        readonly List<SavedRec> savedRecs = new List<SavedRec>();
        readonly object savedLock = new object();
        volatile bool saverBusy;

        void SaverLoop()
        {
            foreach (var ev in saverQ.GetConsumingEnumerable())
            {
                saverBusy = true;
                try
                {
                    var rec = new Dictionary<string, object>();
                    rec["seq"] = ev.Seq;
                    rec["kind"] = ev.Kind;
                    rec["time"] = startWall.AddMilliseconds(ev.Ts).ToString("yyyy-MM-ddTHH:mm:ss.fff");
                    rec["tMs"] = ev.Ts;
                    if (ev.IsMouse)
                    {
                        if (ev.Button >= 0) rec["button"] = ev.Button == 0 ? "left" : ev.Button == 1 ? "right" : "middle";
                        rec["screen"] = WinUtil.Pt(ev.X, ev.Y);
                        if (ev.Kind.EndsWith("drag"))
                        {
                            rec["dragEnd"] = WinUtil.Pt(ev.EndX, ev.EndY);
                            rec["dragEndInWindow"] = WinUtil.Pt(ev.EndX - ev.WinFrame.X, ev.EndY - ev.WinFrame.Y);
                        }
                    }
                    foreach (var kv in ev.Rec) rec[kv.Key] = kv.Value;
                    // 操作した部品の画面上の位置。印(枠)は記録の画像には描かず、出力するときにこの位置へ描く(MarkPainter)
                    if (ev.MarkRect.HasValue) rec["markRect"] = WinUtil.Rc(ev.MarkRect.Value);
                    // 操作前と操作後が同じ画面か。報告書で 1 枚にまとめるのに使う
                    if (ev.Before != null && ev.After != null) rec["imagesSame"] = SameFrame(ev.Before, ev.After);
                    var before = SaveImage(ev, ev.Before, "before");
                    var after = SaveImage(ev, ev.After, "after");
                    rec["before"] = before;
                    rec["after"] = after;

                    var saved = new SavedRec { Seq = ev.Seq, Ts = ev.Ts, Kind = ev.Kind, Line = JsonSerializer.Serialize(rec, JsonLine) };
                    if (before != null) saved.Files.Add((string)before["file"]);
                    if (after != null) saved.Files.Add((string)after["file"]);
                    lock (savedLock)
                    {
                        jsonl.WriteLine(saved.Line);
                        jsonl.Flush();
                        savedRecs.Add(saved);
                        Interlocked.Increment(ref savedCount);
                    }
                    Log(string.Format("#{0} {1} {2}", ev.Seq, ev.Kind, Summary(ev)));
                    RaiseStepSaved(saved.Line);
                }
                catch (Exception ex) { Log("保存エラー #" + ev.Seq + ": " + ex.Message); }
                finally
                {
                    if (ev.Before != null) Unpin(ev.Before);
                    if (ev.After != null) Unpin(ev.After);
                    saverBusy = false;
                }
            }
        }

        static bool SameFrame(Frame a, Frame b) =>
            ReferenceEquals(a, b) || (a.Area == b.Area && ImageCompare.Same(a.Bmp, b.Bmp));

        static string Summary(Ev ev)
        {
            object o;
            string s = "";
            if (ev.Rec.TryGetValue("key", out o)) s += o + " ";
            if (ev.Rec.TryGetValue("uia", out o) && o is Dictionary<string, object>)
            {
                var u = (Dictionary<string, object>)o;
                s += u["controlType"] + " \"" + u["name"] + "\"";
            }
            if (ev.Rec.TryGetValue("input", out o) && o != null) s += " (入力あり)";
            return s;
        }

        Dictionary<string, object> SaveImage(Ev ev, Frame f, string tag)
        {
            if (f == null) return null;
            string rel = string.Format("img/{0:D4}_{1}.png", ev.Seq, tag);
            bool masked;
            using (var bmp = new Bitmap(f.Bmp))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var mask = new Region(new Rectangle(0, 0, bmp.Width, bmp.Height)))
                    using (var vis = f.Visible.Clone())
                    {
                        vis.Translate(-f.Area.X, -f.Area.Y);
                        mask.Exclude(vis);
                        masked = !mask.IsEmpty(g);
                        if (masked)
                        {
                            // 対象外の領域(重なった他ウィンドウ・背景)は斜線で塗りつぶす
                            using (var br = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(160, 160, 160), Color.FromArgb(220, 220, 220)))
                                g.FillRegion(br, mask);
                        }
                    }
                }
                bmp.Save(Path.Combine(SessionDir, "img", Path.GetFileName(rel)), ImageFormat.Png);
            }
            long basis;
            string basisName;
            if (tag == "before" && ev.BeforeRefTs > 0) { basis = ev.BeforeRefTs; basisName = ev.BeforeRefName; }
            else if (tag == "after" && ev.AfterRefTs > 0) { basis = ev.AfterRefTs; basisName = ev.AfterRefName; }
            else if (tag == "after" && ev.UpTs > 0) { basis = ev.UpTs; basisName = "mouseUp"; }
            else { basis = ev.Ts; basisName = "eventStart"; }
            return new Dictionary<string, object>
            {
                { "file", rel },
                { "offsetMs", f.Ts - basis },
                { "offsetBasis", basisName },
                { "area", WinUtil.Rc(f.Area) },
                { "masked", masked },
            };
        }
    }
}
