using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 記録の判断(ホイールのまとめ方・同じ画面かどうか・撮影ルールの既定)
    public class WheelRulesTests
    {
        [Theory]
        [InlineData(120, 1)]
        [InlineData(-360, -3)]
        [InlineData(30, 1)]      // 高精度ホイールの小さな delta も 1 目盛り
        [InlineData(-30, -1)]
        [InlineData(0, 0)]
        public void 目盛りの数(int delta, int notches) => Assert.Equal(notches, WheelRules.Notches(delta));

        [Fact]
        public void 同じウィンドウで1秒以内ならまとめる()
        {
            var w = (IntPtr)0x100;
            Assert.True(WheelRules.CanMerge(false, w, 1000, w, 2000));
            Assert.False(WheelRules.CanMerge(false, w, 1000, w, 2001));          // 1 秒を過ぎた
            Assert.False(WheelRules.CanMerge(false, w, 1000, (IntPtr)0x200, 1500)); // 別のウィンドウ
            Assert.False(WheelRules.CanMerge(true, w, 1000, w, 1500));           // 前の手順はもう保存した
        }

        // 実際の記録と同じ順に足していく(RecorderDispatch.OnWheel と同じ使い方)
        static List<int> Simulate(params (long ts, int delta)[] events)
        {
            var steps = new List<int>();
            long last = long.MinValue / 2;
            var root = (IntPtr)1;
            foreach (var (ts, delta) in events)
            {
                int n = WheelRules.Notches(delta);
                if (steps.Count > 0 && WheelRules.CanMerge(false, root, last, root, ts)) steps[steps.Count - 1] += n;
                else steps.Add(n);
                last = ts;
            }
            return steps.Where(net => !WheelRules.IsCancelled(net)).ToList();
        }

        [Fact]
        public void 回して戻したら記録しない()
        {
            Assert.Empty(Simulate((0, -120), (200, -120), (400, -120), (700, 120), (900, 120), (1100, 120)));
        }

        [Fact]
        public void 戻しきらなければ差し引いた分だけ記録する()
        {
            var steps = Simulate((0, -120), (200, -120), (400, -120), (700, 120));
            Assert.Equal(new[] { -2 }, steps);
            Assert.Equal("down", WheelRules.Info(steps[0])["direction"]);
            Assert.Equal(2, WheelRules.Info(steps[0])["notches"]);
        }

        [Fact]
        public void 間が空いたら別の手順()
        {
            Assert.Equal(new[] { -1, 1 }, Simulate((0, -120), (3000, 120)));
        }
    }

    public class ImageCompareTests
    {
        static Bitmap Fill(Color c, int w = 40, int h = 30)
        {
            var b = new Bitmap(w, h);
            using (var g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        [Fact]
        public void 同じ画面()
        {
            using (var a = Fill(Color.White)) using (var b = Fill(Color.White)) Assert.True(ImageCompare.Same(a, b));
        }

        [Fact]
        public void 一ピクセルでも違えば別の画面()
        {
            using (var a = Fill(Color.White)) using (var b = Fill(Color.White))
            {
                b.SetPixel(39, 29, Color.Black);
                Assert.False(ImageCompare.Same(a, b));
            }
        }

        [Fact]
        public void 大きさが違えば別の画面()
        {
            using (var a = Fill(Color.White)) using (var b = Fill(Color.White, 41)) Assert.False(ImageCompare.Same(a, b));
        }
    }

    public class CaptureRulesTests
    {
        [Fact]
        public void ホイールとキーは既定で記録しない()
        {
            var d = CaptureRules.Defaults();
            var off = new[] { CaptureRules.MouseWheel }.Concat(CaptureRules.KeyGroups).ToArray();
            foreach (var k in off) Assert.False(d[k].Record, k);
            foreach (var k in CaptureRules.Order.Except(off)) Assert.True(d[k].Record, k);
        }

        [Theory]
        [InlineData(0x41, false, false, false, CaptureRules.KeyLetters)]   // A
        [InlineData(0x5A, false, false, false, CaptureRules.KeyLetters)]   // Z
        [InlineData(0x31, false, false, false, CaptureRules.KeyDigits)]    // 1
        [InlineData(0x30, false, false, false, CaptureRules.KeyDigits)]    // 0
        [InlineData(0x65, false, false, false, CaptureRules.KeyDigits)]    // テンキーの 5
        [InlineData(0x70, false, false, false, CaptureRules.KeyFunction)]  // F1
        [InlineData(0x87, false, false, false, CaptureRules.KeyFunction)]  // F24
        [InlineData(0x25, false, false, false, CaptureRules.KeyArrows)]   // ←
        [InlineData(0x28, false, false, false, CaptureRules.KeyArrows)]   // ↓
        [InlineData(0x0D, false, false, false, CaptureRules.KeySpecial)]  // Enter
        [InlineData(0x09, false, false, false, CaptureRules.KeySpecial)]  // Tab
        [InlineData(0x1B, false, false, false, CaptureRules.KeySpecial)]  // Esc
        [InlineData(0x2E, false, false, false, CaptureRules.KeySpecial)]  // Delete
        [InlineData(0x24, false, false, false, CaptureRules.KeySpecial)]  // Home
        [InlineData(0x53, true, false, false, CaptureRules.Key)]          // Ctrl+S
        [InlineData(0x70, false, true, false, CaptureRules.Key)]          // Alt+F1
        [InlineData(0x25, false, false, true, CaptureRules.Key)]          // Win+←
        public void キーのグループ(int vk, bool ctrl, bool alt, bool win, string group) =>
            Assert.Equal(group, CaptureRules.KeyGroupOf(vk, ctrl, alt, win));

        [Fact]
        public void キーのグループはすべて表に載る()
        {
            foreach (var g in CaptureRules.KeyGroups)
            {
                Assert.Contains(g, CaptureRules.Order);
                Assert.StartsWith("キー: ", CaptureRules.Label(g));
            }
        }
    }
}
