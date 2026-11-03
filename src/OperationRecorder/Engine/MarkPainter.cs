using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.Json.Nodes;

namespace OpRec
{
    // 画像に描く印(マウスポインタ・クリック位置の丸・ドラッグの矢印・操作した部品の枠)の選び方。
    // 記録した画像には印を描かずに保存し、md / HTML を出力するときに、操作ログ(座標・部品の位置)から合成する
    public sealed class MarkOptions
    {
        public bool Pointer { get; set; }            // マウスポインタ(矢印の形)
        public bool Circle { get; set; } = true;     // クリック位置の丸(左 = 赤 / 右 = 青 / 中 = 緑)
        public bool Arrow { get; set; } = true;      // ドラッグの移動先への矢印
        public bool Frame { get; set; } = true;      // 操作した部品を囲う枠(橙)
        public int Radius { get; set; } = 14;        // 丸の半径(px)。0 なら丸を描かない

        public static MarkOptions Default() => new MarkOptions();
        public static MarkOptions None() => new MarkOptions { Circle = false, Arrow = false, Frame = false, Pointer = false };

        public bool Any => Pointer || (Circle && Radius > 0) || Arrow || Frame;

        public MarkOptions Clone() => new MarkOptions { Pointer = Pointer, Circle = Circle, Arrow = Arrow, Frame = Frame, Radius = Radius };

        // "circle,arrow,frame,pointer" / "none" の形(CLI の -Marks、--marks)
        public static MarkOptions Parse(string text, int radius = 14)
        {
            var o = None();
            o.Radius = radius;
            foreach (var p in (text ?? "").Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0))
            {
                switch (p)
                {
                    case "none": break;
                    case "pointer": o.Pointer = true; break;
                    case "circle": o.Circle = true; break;
                    case "arrow": o.Arrow = true; break;
                    case "frame": o.Frame = true; break;
                    default: throw new ArgumentException("印は pointer / circle / arrow / frame / none のどれかです: " + p);
                }
            }
            return o;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (Pointer) parts.Add("pointer");
            if (Circle) parts.Add("circle");
            if (Arrow) parts.Add("arrow");
            if (Frame) parts.Add("frame");
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }
    }

    // 1 枚の画像に描く印の位置(画像の左上を原点にした座標)
    public sealed class MarkSpec
    {
        public Point? Click;          // マウス操作の位置
        public Point? DragEnd;        // ドラッグで離した位置
        public Rectangle? Element;    // 操作した部品
        public int Button;            // 0 = 左 / 1 = 右 / 2 = 中

        public bool IsEmpty => Click == null && Element == null;

        // events.jsonl の 1 行と、その画像(before / after)の情報から作る。area は画像の画面上の範囲
        public static MarkSpec FromEvent(JsonObject e, JsonObject image)
        {
            var area = image?["area"] as JsonObject;
            if (e == null || area == null) return null;
            int ax = Int(area["x"]), ay = Int(area["y"]);
            var s = new MarkSpec();
            if (e["screen"] is JsonObject sc) s.Click = new Point(Int(sc["x"]) - ax, Int(sc["y"]) - ay);
            if (e["dragEnd"] is JsonObject de) s.DragEnd = new Point(Int(de["x"]) - ax, Int(de["y"]) - ay);
            string b = (e["button"] as JsonValue)?.ToString();
            s.Button = b == "right" ? 1 : b == "middle" ? 2 : 0;
            if (e["markRect"] is JsonObject mr)
                s.Element = new Rectangle(Int(mr["x"]) - ax, Int(mr["y"]) - ay, Int(mr["w"]), Int(mr["h"]));
            else if (e["uia"]?["boundsInWindow"] is JsonObject bw && e["window"]?["frame"] is JsonObject wf)
                s.Element = new Rectangle(Int(wf["x"]) + Int(bw["x"]) - ax, Int(wf["y"]) + Int(bw["y"]) - ay, Int(bw["w"]), Int(bw["h"]));
            return s;
        }

        static int Int(JsonNode n)
        {
            try { return n == null ? 0 : (int)Math.Round(n.GetValue<double>()); } catch { return 0; }
        }
    }

    public static class MarkPainter
    {
        public static Color ButtonColor(int button) =>
            button == 1 ? Color.FromArgb(0, 90, 255) : button == 2 ? Color.FromArgb(0, 170, 60) : Color.Red;

        public static void Draw(Graphics g, Size image, MarkSpec s, MarkOptions o)
        {
            if (s == null || o == null || !o.Any) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 操作した部品の枠。ウィンドウ全体のような大きな要素に枠を描いても意味がないので、小さい要素だけ囲む
            if (o.Frame && s.Element.HasValue)
            {
                var r = s.Element.Value;
                if ((long)r.Width * r.Height < (long)image.Width * image.Height / 3)
                    using (var p = new Pen(Color.FromArgb(230, 255, 140, 0), 2)) g.DrawRectangle(p, r);
            }
            if (s.Click == null) return;
            var c = s.Click.Value;
            var color = ButtonColor(s.Button);
            int rr = o.Circle ? Math.Max(0, o.Radius) : 0;
            if (rr > 0)
                using (var p = new Pen(color, rr >= 6 ? 3 : 2)) g.DrawEllipse(p, c.X - rr, c.Y - rr, rr * 2, rr * 2);
            // ドラッグは、離した位置(移動先)を矢印で示す。どんな背景でも見えるよう、白い縁取りの上に描く
            PointF from, to; float head;
            if (o.Arrow && s.DragEnd.HasValue && DragArrow.TryGet(c, s.DragEnd.Value, rr, out from, out to, out head))
            {
                var tri = DragArrow.Head(from, to, head);
                var shaftEnd = new PointF((tri[1].X + tri[2].X) / 2, (tri[1].Y + tri[2].Y) / 2);
                using (var outline = new Pen(Color.FromArgb(230, 255, 255, 255), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLine(outline, from, shaftEnd);
                    g.DrawPolygon(outline, tri);
                }
                using (var shaft = new Pen(color, 4) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                    g.DrawLine(shaft, from, shaftEnd);
                using (var fill = new SolidBrush(color)) g.FillPolygon(fill, tri);
            }
            if (o.Pointer) DrawPointer(g, c);
        }

        // Windows の標準の矢印ポインタの形(先端が tip)。白く塗って黒で縁取る
        public static PointF[] PointerShape(Point tip, float scale = 1.25f)
        {
            var pts = new[] { new PointF(0, 0), new PointF(0, 17), new PointF(4, 13), new PointF(7, 20), new PointF(10, 19), new PointF(7, 12), new PointF(12, 12) };
            return pts.Select(p => new PointF(tip.X + p.X * scale, tip.Y + p.Y * scale)).ToArray();
        }

        static void DrawPointer(Graphics g, Point tip)
        {
            var shape = PointerShape(tip);
            using (var fill = new SolidBrush(Color.White)) g.FillPolygon(fill, shape);
            using (var pen = new Pen(Color.Black, 1.5f) { LineJoin = LineJoin.Miter }) g.DrawPolygon(pen, shape);
        }
    }
}
