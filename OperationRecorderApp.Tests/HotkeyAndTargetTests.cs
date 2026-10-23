using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 記録の操作キー(ショートカットキー)
    public class HotkeyTests
    {
        [Fact]
        public void 既定はCtrlShiftF9からF12()
        {
            var k = HotkeySet.Defaults();
            Assert.Equal("Ctrl+Shift+F9", k.Start.ToString());
            Assert.Equal("Ctrl+Shift+F10", k.Undo.ToString());
            Assert.Equal("Ctrl+Shift+F11", k.Restart.ToString());
            Assert.Equal("Ctrl+Shift+F12", k.Stop.ToString());
            Assert.Empty(k.Validate());
        }

        [Fact]
        public void 修飾キーまで一致したときだけ操作キーになる()
        {
            var k = HotkeySet.Defaults();
            Assert.Equal(HotkeyAction.Stop, k.ActionOf(0x7B, true, true, false, false));
            Assert.Null(k.ActionOf(0x7B, true, false, false, false));   // Ctrl+F12
            Assert.Null(k.ActionOf(0x7B, true, true, true, false));     // Ctrl+Shift+Alt+F12
            Assert.Null(k.ActionOf(0x7B, true, true, false, true));     // Win も押している
            Assert.Null(k.ActionOf(0x41, true, true, false, false));    // Ctrl+Shift+A
        }

        [Fact]
        public void 変えたキーで判定する()
        {
            var k = HotkeySet.Defaults();
            k.Stop = new Hotkey(true, false, true, false, HotkeyKeys.Parse("Q"));   // Ctrl+Alt+Q
            Assert.Equal("Ctrl+Alt+Q", k.Stop.ToString());
            Assert.Equal(HotkeyAction.Stop, k.ActionOf('Q', true, false, true, false));
            Assert.Null(k.ActionOf(0x7B, true, true, false, false));   // 前の Ctrl+Shift+F12 はもう効かない
            Assert.Empty(k.Validate());
        }

        [Fact]
        public void RegisterHotKeyに渡す修飾キー()
        {
            Assert.Equal(0x4000u | 0x2 | 0x4, HotkeySet.Defaults().Start.Modifiers);                 // MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT
            Assert.Equal(0x4000u | 0x1 | 0x8, new Hotkey(false, false, true, true, 0x78).Modifiers);  // MOD_ALT | MOD_WIN
        }

        [Fact]
        public void キーが無いと使えない()
        {
            var k = HotkeySet.Defaults();
            k.Undo = new Hotkey(true, true, false, false, 0);
            Assert.Contains(k.Validate(), e => e.Contains("直前の 1 手を取り消す") && e.Contains("キーを選んで"));
        }

        [Theory]
        [InlineData(false, false, false, false)]   // 修飾キーなし
        [InlineData(false, true, false, false)]    // Shift だけ
        public void CtrlかAltかWinが要る(bool ctrl, bool shift, bool alt, bool win)
        {
            var k = HotkeySet.Defaults();
            k.Start = new Hotkey(ctrl, shift, alt, win, 0x78);
            Assert.Contains(k.Validate(), e => e.Contains("記録の開始・再開") && e.Contains("Ctrl・Alt・Win"));
        }

        [Fact]
        public void 同じキーを2つの操作に使えない()
        {
            var k = HotkeySet.Defaults();
            k.Restart = k.Stop.Clone();
            Assert.Contains(k.Validate(), e => e.Contains("最初から取り直す") && e.Contains("保存して終了") && e.Contains("Ctrl+Shift+F12"));
        }

        [Theory]
        [InlineData(false, false, true, false, "F4")]     // Alt+F4
        [InlineData(true, false, false, false, "S")]      // Ctrl+S
        [InlineData(false, false, false, true, "L")]      // Win+L
        [InlineData(true, false, true, false, "Delete")]  // Ctrl+Alt+Delete
        public void よく使うキーは使えない(bool ctrl, bool shift, bool alt, bool win, string key)
        {
            var k = HotkeySet.Defaults();
            k.Stop = new Hotkey(ctrl, shift, alt, win, HotkeyKeys.Parse(key));
            Assert.Contains(k.Validate(), e => e.Contains("よく使うキー"));
        }

        [Fact]
        public void 選べるキーの一覧()
        {
            Assert.Equal(0x78, HotkeyKeys.Parse("f9"));
            Assert.Equal('A', HotkeyKeys.Parse("A"));
            Assert.Equal(0, HotkeyKeys.Parse("NoSuchKey"));
            Assert.Equal("PageDown", HotkeyKeys.NameOf(0x22));
            Assert.Equal(24 + 26 + 10 + 8, HotkeyKeys.All.Length);
            Assert.Equal(HotkeyKeys.All.Length, HotkeyKeys.All.Select(i => i.Vk).Distinct().Count());
        }

        [Fact]
        public void 複製は元と独立している()
        {
            var a = HotkeySet.Defaults();
            var b = a.Clone();
            b.Start.Key = 'P';
            Assert.Equal(0x78, a.Start.Key);
        }
    }

    // 記録する対象の決め方(開始のキーを押したときに前面にあるもの)と、ディスプレイ全体を撮るモード
    public class TargetRulesTests
    {
        static MonitorInfo M(int index, int x, int y, int w, int h) => new MonitorInfo { Index = index, DeviceName = @"\\.\DISPLAY" + index, Bounds = new Rectangle(x, y, w, h) };
        static readonly List<MonitorInfo> Three = new List<MonitorInfo> { M(1, -1920, 0, 1920, 1080), M(2, 0, 0, 2560, 1440), M(3, 2560, 0, 1920, 1080) };

        [Fact]
        public void 自分とデスクトップとタスクバーは対象にしない()
        {
            var h = (IntPtr)0x100;
            Assert.True(TargetRules.IsUsableForeground(h, 10, 99, "Notepad"));
            Assert.False(TargetRules.IsUsableForeground(h, 99, 99, "WindowsForms10.Window"));   // 自分のプロセス
            Assert.False(TargetRules.IsUsableForeground(h, 10, 99, "Progman"));
            Assert.False(TargetRules.IsUsableForeground(h, 10, 99, "Shell_TrayWnd"));
            Assert.False(TargetRules.IsUsableForeground(h, 10, 99, "Shell_SecondaryTrayWnd"));
            Assert.False(TargetRules.IsUsableForeground(IntPtr.Zero, 10, 99, "Notepad"));
        }

        [Fact]
        public void ウィンドウがいちばん広く映っているディスプレイ()
        {
            Assert.Equal(2, TargetRules.MonitorFor(new Rectangle(100, 100, 800, 600), Three).Index);
            // 2 と 3 にまたがり、3 の方が広い
            Assert.Equal(3, TargetRules.MonitorFor(new Rectangle(2400, 100, 1000, 600), Three).Index);
            Assert.Equal(1, TargetRules.MonitorFor(new Rectangle(-1000, 200, 500, 500), Three).Index);
        }

        [Fact]
        public void どのディスプレイにも映っていなければ近いもの()
        {
            Assert.Equal(3, TargetRules.MonitorFor(new Rectangle(5000, 200, 300, 300), Three).Index);
            Assert.Null(TargetRules.MonitorFor(new Rectangle(0, 0, 10, 10), new List<MonitorInfo>()));
        }

        [Fact]
        public void すべてのディスプレイを合わせた範囲()
        {
            Assert.Equal(Rectangle.FromLTRB(-1920, 0, 4480, 1440), TargetRules.UnionBounds(Three.Select(m => m.Bounds)));
            Assert.Equal(Rectangle.Empty, TargetRules.UnionBounds(new Rectangle[0]));
        }

        [Fact]
        public void 広い範囲は撮影の間隔を広げる()
        {
            Assert.Equal(100, CaptureBudget.IntervalFor(1920, 1080, 100, 1500));      // フル HD 1 枚なら今のまま
            int all = CaptureBudget.IntervalFor(6400, 1440, 100, 1500);               // 3 枚を合わせた範囲
            Assert.True(all > 100, "間隔 " + all);
            long frames = 1500 / all + 1;
            Assert.True(frames * 6400L * 1440 * 3 <= CaptureBudget.DefaultBudgetBytes * 1.1, "持つ画面が多すぎる: " + frames);
            Assert.Equal(300, CaptureBudget.IntervalFor(0, 0, 300, 1500));            // 範囲が分からなければ今のまま
        }

        [Fact]
        public void 全ディスプレイの記録の対象名()
        {
            using (var s = new TestSession("AllDesktops"))
            {
                var mds = new List<string>();
                RecordingController.Export(Path.Combine(AppContext.BaseDirectory, "templates"), s.Dir, new[] { "psr" }, null, false, true, false, null, null, mds, new List<string>());
                Assert.Contains("- 対象: すべてのディスプレイ(3840x1080)", File.ReadAllText(mds[0]));
            }
        }

        [Fact]
        public void 記録の対象の名前()
        {
            var none = System.Drawing.Size.Empty;
            Assert.Equal("販売管理", TargetRules.Describe(CaptureMode.Window, false, "販売管理", "hanbai", 0, none));
            Assert.Equal("販売管理(hanbai 全体)", TargetRules.Describe(CaptureMode.App, false, "販売管理", "hanbai", 0, none));
            Assert.Equal("EXCEL(アプリ全体)", TargetRules.Describe(CaptureMode.App, false, null, "EXCEL", 0, none));
            Assert.Equal("ディスプレイ 2(2560x1440)", TargetRules.Describe(CaptureMode.Desktop, false, null, null, 2, new System.Drawing.Size(2560, 1440)));
            Assert.Equal("すべてのディスプレイ", TargetRules.Describe(CaptureMode.AllDesktops, true, null, null, 0, none));
            Assert.StartsWith("操作したウィンドウ", TargetRules.Describe(CaptureMode.Active, false, null, null, 0, none));
            Assert.Contains("クリックしてください", TargetRules.Describe(CaptureMode.App, true, null, null, 0, none));
            Assert.Contains("ディスプレイ", TargetRules.Describe(CaptureMode.Desktop, true, null, null, 0, none));
            Assert.Equal("(名前のないウィンドウ)", TargetRules.Describe(CaptureMode.Window, false, "  ", null, 0, none));
        }

        [Fact]
        public void デスクトップとタスクバーの見分け()
        {
            Assert.True(TargetRules.IsDesktopWindow("Progman"));
            Assert.True(TargetRules.IsDesktopWindow("WorkerW"));
            Assert.False(TargetRules.IsDesktopWindow("Shell_TrayWnd"));   // タスクバーはデスクトップではない
            Assert.True(TargetRules.IsShellWindow("Shell_TrayWnd"));
            Assert.True(TargetRules.IsShellWindow("Shell_SecondaryTrayWnd"));
        }

        [Theory]
        [InlineData("{\"mode\":\"AllDesktops\",\"virtualScreen\":{\"w\":3840,\"h\":1080}}", "すべてのディスプレイ(3840x1080)")]
        [InlineData("{\"mode\":\"Desktop\",\"targetMonitor\":{\"index\":2,\"bounds\":{\"w\":2560,\"h\":1440}}}", "ディスプレイ 2(2560x1440)")]
        [InlineData("{\"mode\":\"Window\",\"targetWindow\":{\"title\":\"販売管理\"}}", "販売管理")]
        [InlineData("{\"mode\":\"App\",\"targetProcess\":\"EXCEL\"}", "EXCEL(アプリ全体)")]
        [InlineData("{\"mode\":\"Active\"}", "操作したウィンドウ")]
        public void 記録の対象の名前は報告書と記録一覧で同じ(string sessionJson, string expected) =>
            Assert.Equal(expected, ReportGenerator.TargetText(System.Text.Json.Nodes.JsonNode.Parse(sessionJson).AsObject()));

        [Fact]
        public void 撮影モードにAllDesktopsがある()
        {
            Assert.True(Enum.TryParse("AllDesktops", out CaptureMode m));
            Assert.Equal(CaptureMode.AllDesktops, m);
        }
    }

    // 設定(対象の決め方・操作キー・撮影モード)
    public class TargetSettingsTests
    {
        [Fact]
        public void 以前のPickを対象の決め方に読み替える()
        {
            Assert.Equal(AppSettings.TargetPick, AppSettings.FromJson("{\"Pick\":true}").Target);
            Assert.Equal(AppSettings.TargetList, AppSettings.FromJson("{\"Pick\":false}").Target);
            Assert.Equal(AppSettings.TargetForeground, AppSettings.FromJson("{\"Target\":\"foreground\",\"Pick\":true}").Target);
            Assert.Equal(AppSettings.TargetPick, AppSettings.FromJson(null).Target);
        }

        [Fact]
        public void 操作キーを保存して読み直せる()
        {
            var s = AppSettings.FromJson(null);
            s.Hotkeys.Stop = new Hotkey(true, false, true, false, 'Q');
            var t = AppSettings.FromJson(s.ToJson());
            Assert.Equal("Ctrl+Alt+Q", t.Hotkeys.Stop.ToString());
            Assert.Equal("Ctrl+Shift+F9", t.Hotkeys.Start.ToString());
        }

        [Fact]
        public void 壊れた操作キーは既定に戻す()
        {
            var s = AppSettings.FromJson(null);
            s.Hotkeys.Undo = s.Hotkeys.Stop.Clone();   // 重なっている
            Assert.Equal("Ctrl+Shift+F10", AppSettings.FromJson(s.ToJson()).Hotkeys.Undo.ToString());
            Assert.Equal("Ctrl+Shift+F12", AppSettings.FromJson("{\"Hotkeys\":{\"Stop\":null}}").Hotkeys.Stop.ToString());
        }

        [Fact]
        public void 撮影モード()
        {
            Assert.Equal("AllDesktops", AppSettings.FromJson("{\"Mode\":\"AllDesktops\"}").Mode);
            Assert.Equal("App", AppSettings.FromJson("{\"Mode\":\"Everything\"}").Mode);
        }
    }

    // 画面・トレイ・通知の文字は、操作キーの設定に従う
    public class UiTextsTests
    {
        static HotkeySet Custom()
        {
            var k = HotkeySet.Defaults();
            k.Start = new Hotkey(true, false, true, false, 0x70);    // Ctrl+Alt+F1
            k.Undo = new Hotkey(true, false, true, false, 0x71);     // Ctrl+Alt+F2
            k.Restart = new Hotkey(true, false, true, false, 0x72);  // Ctrl+Alt+F3
            k.Stop = new Hotkey(true, false, true, false, 0x73);     // Ctrl+Alt+F4
            return k;
        }

        [Fact]
        public void 変えたキーが表示に出る()
        {
            var k = Custom();
            Assert.Contains("Ctrl+Alt+F1", UiTexts.ForegroundWindowOption(k));
            Assert.Contains("Ctrl+Alt+F1", UiTexts.ForegroundMonitorOption(k));
            Assert.Contains("Ctrl+Alt+F1", UiTexts.Status(RecState.Idle, k));
            Assert.Contains("Ctrl+Alt+F1", UiTexts.TrayTip(RecState.Idle, k));
            Assert.Contains("Ctrl+Alt+F1", UiTexts.MenuStart(k));
            Assert.Contains("Ctrl+Alt+F1", UiTexts.TrayHint(k));
            Assert.Contains("Ctrl+Alt+F1", UiTexts.ShortcutMenuNote(k));
            foreach (var t in new[] { UiTexts.Status(RecState.Recording, k), UiTexts.OverlayShortcuts(k) })
            {
                Assert.Contains("Ctrl+Alt+F2", t);
                Assert.Contains("Ctrl+Alt+F3", t);
                Assert.Contains("Ctrl+Alt+F4", t);
                Assert.DoesNotContain("F12", t);
            }
            Assert.Contains("Ctrl+Alt+F2", UiTexts.MenuUndo(k));
            Assert.Contains("Ctrl+Alt+F3", UiTexts.MenuRestart(k));
            Assert.Contains("Ctrl+Alt+F4", UiTexts.MenuStop(k));
            Assert.Contains("Ctrl+Alt+F4", UiTexts.NotifySaveKey(k));
        }

        [Fact]
        public void オーバーレイに使えるキーの一覧を出す()
        {
            var t = UiTexts.OverlayShortcuts(Custom());
            foreach (var key in new[] { "Ctrl+Alt+F1", "Ctrl+Alt+F2", "Ctrl+Alt+F3", "Ctrl+Alt+F4", "Ctrl+Shift だけ" }) Assert.Contains(key, t);
            Assert.True(t.Split('\n').Length <= 3, "オーバーレイの枠(3 行)に収まらない");
            Assert.Contains("Ctrl+Shift+F12: 保存して終了", UiTexts.OverlayShortcuts(HotkeySet.Defaults()));
        }

        [Fact]
        public void トレイの文字は127文字まで()
        {
            var k = HotkeySet.Defaults();
            k.Start = new Hotkey(true, true, true, true, 0x91);
            foreach (RecState st in Enum.GetValues(typeof(RecState))) Assert.True(UiTexts.TrayTip(st, k).Length <= 127);
        }
    }
}
