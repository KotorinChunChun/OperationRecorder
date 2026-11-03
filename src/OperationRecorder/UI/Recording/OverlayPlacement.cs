using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace OperationRecorderApp
{
    // オーバーレイを、マウスポインタのあるウィンドウと重ならない場所へ動かすときの行き先を決める(画面に頼らない計算だけ)
    public static class OverlayPlacement
    {
        public const int Margin = 12;   // 画面の端からの余白
        public const double UserMoveGraceSeconds = 1.5;   // 利用者がドラッグで動かした直後は、しばらく逃げない

        // 逃げるかどうかを調べる必要があるか。設定でオフ・動かした直後・ポインタがオーバーレイの上(ボタンを押すとき)なら調べない
        public static bool ShouldCheck(bool enabled, double secondsSinceUserMove, Rectangle overlay, Point cursor) =>
            enabled && secondsSinceUserMove >= UserMoveGraceSeconds && !overlay.Contains(cursor);

        // 各ディスプレイの作業領域の四隅(右下・左下・右上・左上の順)
        public static List<Point> Corners(Size size, IEnumerable<Rectangle> workAreas)
        {
            var list = new List<Point>();
            foreach (var wa in workAreas)
            {
                list.Add(new Point(wa.Right - size.Width - Margin, wa.Bottom - size.Height - Margin));
                list.Add(new Point(wa.Left + Margin, wa.Bottom - size.Height - Margin));
                list.Add(new Point(wa.Right - size.Width - Margin, wa.Top + Margin));
                list.Add(new Point(wa.Left + Margin, wa.Top + Margin));
            }
            return list;
        }

        // overlay: 今のオーバーレイの位置と大きさ / window: ポインタのあるウィンドウ。
        // 動かす先を返す。動かさないときは null(重なっていない・どこへ動いても今より重なりが減らない)
        public static Point? Choose(Rectangle overlay, Rectangle window, IEnumerable<Rectangle> workAreas)
        {
            if (!window.IntersectsWith(overlay)) return null;
            var candidates = Corners(overlay.Size, workAreas);
            if (candidates.Count == 0) return null;
            Func<Point, long> overlap = p =>
            {
                var r = Rectangle.Intersect(window, new Rectangle(p, overlay.Size));
                return r.IsEmpty ? 0 : (long)r.Width * r.Height;
            };
            Func<Point, double> dist = p => Math.Sqrt(Math.Pow(p.X - overlay.X, 2) + Math.Pow(p.Y - overlay.Y, 2));
            // 重ならない場所があれば、今の位置にいちばん近いところ
            var free = candidates.Where(p => overlap(p) == 0).OrderBy(dist).ToList();
            if (free.Count > 0) return free[0];
            // どこも重なるなら、重なりがいちばん小さい場所(今より小さくなるときだけ)
            var best = candidates.OrderBy(overlap).ThenBy(dist).First();
            return overlap(best) < overlap(overlay.Location) ? best : (Point?)null;
        }
    }
}
