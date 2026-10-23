using System;
using System.Drawing;
using System.IO;
using System.Linq;
using OpRec;
using Xunit;

namespace OperationRecorderApp.Tests
{
    // 設定ファイルの読み込み(前の版からの移行)
    public class AppSettingsTests
    {
        [Fact]
        public void 設定が無ければ既定値()
        {
            var s = AppSettings.FromJson(null);
            Assert.True(s.OverlayFlee);
            Assert.True(s.OpenHtmlAfterSave);   // 保存したら HTML を開く
            Assert.False(s.Rule(CaptureRules.MouseWheel).Record);
            Assert.Equal(new[] { "user" }, s.Templates);
        }

        [Fact]
        public void 壊れた設定ファイルは既定値()
        {
            Assert.Equal(new[] { "user" }, AppSettings.FromJson("{ これは JSON ではない").Templates);
        }

        [Fact]
        public void v11の設定のホイールはオフに直す()
        {
            var s = AppSettings.FromJson("{\"Rules\":{\"mouseWheel\":{\"Record\":true,\"Before\":true,\"After\":true}}}");
            Assert.False(s.Rule(CaptureRules.MouseWheel).Record);
            Assert.Equal(5, s.SettingsVersion);
            Assert.True(s.Rule(CaptureRules.LeftClick).Record);   // 設定ファイルに無い項目は補う
        }

        [Fact]
        public void 前の版の設定のキーはどれも記録しないに直す()
        {
            // v1.2(操作キーは記録する)・v1.3 の途中(F キーなどは記録する)のどちらの設定でも、キーのグループは記録しないになる
            foreach (var json in new[] {
                "{\"SettingsVersion\":2,\"Rules\":{\"key\":{\"Record\":true,\"Before\":true,\"After\":true}}}",
                "{\"SettingsVersion\":3,\"Rules\":{\"keyFunction\":{\"Record\":true},\"keyArrows\":{\"Record\":true},\"keySpecial\":{\"Record\":true},\"key\":{\"Record\":true}}}" })
            {
                var s = AppSettings.FromJson(json);
                foreach (var g in CaptureRules.KeyGroups) Assert.False(s.Rule(g).Record, g);
                Assert.True(s.Rule(CaptureRules.LeftClick).Record);
                Assert.Equal(5, s.SettingsVersion);
            }
        }

        [Fact]
        public void v13で自分でオンにしたキーのグループはそのまま()
        {
            var s = AppSettings.FromJson(null);
            s.Rule(CaptureRules.KeySpecial).Record = true;
            Assert.True(AppSettings.FromJson(s.ToJson()).Rule(CaptureRules.KeySpecial).Record);
        }

        [Fact]
        public void キーのグループの設定を保存して読み直せる()
        {
            var s = AppSettings.FromJson(null);
            s.Rule(CaptureRules.KeyLetters).Record = true;
            s.Rule(CaptureRules.KeyArrows).Record = false;
            var t = AppSettings.FromJson(s.ToJson());
            Assert.True(t.Rule(CaptureRules.KeyLetters).Record);
            Assert.False(t.Rule(CaptureRules.KeyArrows).Record);
            Assert.False(t.Rule(CaptureRules.KeyFunction).Record);
        }

        [Fact]
        public void v12で自分でオンにしたホイールはそのまま()
        {
            var json = AppSettings.FromJson(null);
            json.Rule(CaptureRules.MouseWheel).Record = true;
            var s = AppSettings.FromJson(json.ToJson());
            Assert.True(s.Rule(CaptureRules.MouseWheel).Record);
        }

        [Theory]
        [InlineData("user-table")]
        [InlineData("user-before")]
        public void 以前のテンプレートはuserに読み替える(string legacy)
        {
            var s = AppSettings.FromJson("{\"Templates\":[\"" + legacy + "\",\"ai\"],\"ImageLayout\":\"before\"}");
            Assert.Equal(new[] { "user", "ai" }, s.Templates);
        }

        [Fact]
        public void 以前の版の保存したらHTMLを開かない設定はオンに直す()
        {
            Assert.True(AppSettings.FromJson("{\"SettingsVersion\":4,\"OpenHtmlAfterSave\":false}").OpenHtmlAfterSave);
            // 今の版で自分でオフにしたものはそのまま
            var s = AppSettings.FromJson(null); s.OpenHtmlAfterSave = false;
            Assert.False(AppSettings.FromJson(s.ToJson()).OpenHtmlAfterSave);
        }

        [Fact]
        public void 作らない形式は保存しても開かない()
        {
            var s = AppSettings.FromJson(null);
            s.OutputHtml = true; s.OpenHtmlAfterSave = true;
            Assert.True(s.OpensHtml);
            s.OutputHtml = false;                 // HTML を作らないなら、開く設定でも開かない
            Assert.False(s.OpensHtml);
            s.OutputMd = true; s.OpenAfterSave = false;
            Assert.False(s.OpensMd);
            s.OpenAfterSave = true;
            Assert.True(s.OpensMd);
        }
        [Fact]
        public void 逃げる設定を保存して読み直せる()
        {
            var s = AppSettings.FromJson(null);
            s.OverlayFlee = false;
            Assert.False(AppSettings.FromJson(s.ToJson()).OverlayFlee);
        }
    }

    // オーバーレイがポインタのあるウィンドウから逃げるときの行き先
    public class OverlayPlacementTests
    {
        static readonly Rectangle Screen1 = new Rectangle(0, 0, 1920, 1040);   // 作業領域(タスクバーを除く)
        static readonly Size Overlay = new Size(360, 260);

        static Rectangle At(Point p) => new Rectangle(p, Overlay);

        [Fact]
        public void 逃げる設定がオフなら調べない()
        {
            Assert.False(OverlayPlacement.ShouldCheck(false, 10, At(new Point(0, 0)), new Point(1000, 500)));
            Assert.True(OverlayPlacement.ShouldCheck(true, 10, At(new Point(0, 0)), new Point(1000, 500)));
        }

        [Fact]
        public void 動かした直後とオーバーレイの上では調べない()
        {
            Assert.False(OverlayPlacement.ShouldCheck(true, 0.5, At(new Point(0, 0)), new Point(1000, 500)));
            Assert.False(OverlayPlacement.ShouldCheck(true, 10, At(new Point(0, 0)), new Point(10, 10)));
        }

        [Fact]
        public void デスクトップとタスクバーからは逃げない()
        {
            Assert.True(TargetRules.IsShellWindow("Progman"));
            Assert.True(TargetRules.IsShellWindow("Shell_TrayWnd"));
            Assert.False(TargetRules.IsShellWindow("Chrome_WidgetWin_1"));
        }

        [Fact]
        public void 重なっていなければ動かない()
        {
            var overlay = At(new Point(1548, 768));
            Assert.Null(OverlayPlacement.Choose(overlay, new Rectangle(0, 0, 800, 600), new[] { Screen1 }));
        }

        [Fact]
        public void 重ならない隅のうち近いところへ動く()
        {
            // 右下にいるオーバーレイに、右半分を占めるウィンドウが重なった → 左下へ
            var overlay = At(new Point(1548, 768));
            var to = OverlayPlacement.Choose(overlay, new Rectangle(960, 0, 960, 1040), new[] { Screen1 });
            Assert.Equal(new Point(OverlayPlacement.Margin, 1040 - 260 - OverlayPlacement.Margin), to);
        }

        [Fact]
        public void 全画面のウィンドウなら別のディスプレイへ()
        {
            var screen2 = new Rectangle(1920, 0, 1920, 1040);
            var overlay = At(new Point(1548, 768));
            var to = OverlayPlacement.Choose(overlay, Screen1, new[] { Screen1, screen2 });
            Assert.True(to.HasValue && screen2.Contains(new Rectangle(to.Value, Overlay)));
        }

        [Fact]
        public void どこも重なるなら今より重なりが減るときだけ動く()
        {
            var overlay = At(new Point(1548, 768));
            Assert.Null(OverlayPlacement.Choose(overlay, Screen1, new[] { Screen1 }));
        }
    }

    // 「出力する報告書」の選択肢
    public class OutputChoicesTests
    {


        [Fact]
        public void テンプレートはuserが先頭で説明を先に表示する()
        {
            var items = OutputChoices.LoadTemplates(Path.Combine(AppContext.BaseDirectory, "templates"));
            Assert.Equal(new[] { "user", "ai", "psr", "text" }, items.Select(t => t.Name));
            Assert.Equal(new[]
            {
                "ユーザー向けの手順書（操作前後画像）[user]",
                "AI向けの手順書（操作前後画像と詳細UI情報）[ai]",
                "ステップ記録ツール形式（簡易UI操作履歴）[psr]",
                "テキスト手順書（操作情報のみ）[text]",
            }, items.Select(t => t.ToString()));
        }

        [Theory]
        [InlineData("(ユーザー向けの手順書（操作前後画像）)", "ユーザー向けの手順書（操作前後画像）")]
        [InlineData("（全角で囲んだ説明）", "全角で囲んだ説明")]
        [InlineData("(前)と(後)", "(前)と(後)")]      // 先頭と末尾の括弧が対でなければ外さない
        [InlineData("括弧なし", "括弧なし")]
        public void 説明の外側の括弧だけを外す(string desc, string expected) => Assert.Equal(expected, OutputChoices.StripOuterParens(desc));

        [Theory]
        [InlineData(" 操作報告書テンプレート(ユーザー向けの手順書)  -Template user", "(ユーザー向けの手順書)")]
        [InlineData("社内様式", "社内様式")]
        [InlineData(null, "")]
        public void テンプレートの説明を取り出す(string line, string expected) =>
            Assert.Equal(expected, OutputChoices.DescriptionFromLine(line));
    }
}
