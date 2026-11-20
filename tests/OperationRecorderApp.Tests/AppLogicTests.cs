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
            Assert.True(s.ShowClickRipple);
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
        public void 以前のテンプレートはuserに読み替える(string legacy)
        {
            var s = AppSettings.FromJson("{\"Templates\":[\"" + legacy + "\",\"ai\"],\"ImageLayout\":\"before\"}");
            Assert.Equal(new[] { "user", "ai" }, s.Templates);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        public void 操作前だけの選択は設定の再読込でも保持する(int version)
        {
            var settings = AppSettings.FromJson("{\"SettingsVersion\":" + version + ",\"Templates\":[\"user-before\",\"ai\"]}");
            Assert.Equal(new[] { "user-before", "ai" }, settings.Templates);
            Assert.Equal(settings.Templates, AppSettings.FromJson(settings.ToJson()).Templates);
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
        public void クリックの丸を個別に保存して読み直せる()
        {
            var s = AppSettings.FromJson(null);
            s.ShowClickRipple = false;
            Assert.False(AppSettings.FromJson(s.ToJson()).ShowClickRipple);
        }
    }

    public class OverlayProximityTests
    {
        static readonly Rectangle Bounds = new Rectangle(300, 200, 490, 370);

        [Fact]
        public void 百ピクセルまで近づくと透過する()
        {
            var proximity = new OverlayProximity();
            Assert.False(proximity.IsTransparent(Bounds, new Point(199, 350), false));
            Assert.True(proximity.IsTransparent(Bounds, new Point(200, 350), false));
            Assert.True(proximity.IsTransparent(Bounds, new Point(390, 350), false));
        }

        [Fact]
        public void 修飾キーを離しても離れるまで操作できる()
        {
            var proximity = new OverlayProximity();
            Assert.True(proximity.IsTransparent(Bounds, new Point(250, 300), false));
            Assert.False(proximity.IsTransparent(Bounds, new Point(250, 300), true));
            Assert.False(proximity.IsTransparent(Bounds, new Point(250, 300), false));
            Assert.False(proximity.IsTransparent(Bounds, new Point(150, 300), false));
            Assert.False(proximity.IsTransparent(Bounds, new Point(250, 300), false));
            Assert.False(proximity.IsTransparent(Bounds, new Point(139, 300), false));
            Assert.True(proximity.IsTransparent(Bounds, new Point(250, 300), false));
        }

        [Fact]
        public void 新しい記録では修飾キーによる解除を持ち越さない()
        {
            var proximity = new OverlayProximity();
            Assert.False(proximity.IsTransparent(Bounds, Bounds.Location, true));
            proximity.Reset();
            Assert.True(proximity.IsTransparent(Bounds, Bounds.Location, false));
        }
    }


    // 「出力する報告書」の選択肢
    public class OutputChoicesTests
    {


        [Fact]
        public void テンプレートはuserが先頭で説明を先に表示する()
        {
            var items = OutputChoices.LoadTemplates(Path.Combine(AppContext.BaseDirectory, "templates"));
            Assert.Equal(new[] { "user", "ai", "psr", "text", "user-before" }, items.Select(t => t.Name));
            Assert.Equal(new[]
            {
                "ユーザー向けの手順書（操作前後画像）[user]",
                "AI向けの手順書（操作前後画像と詳細UI情報）[ai]",
                "ステップ記録ツール形式（簡易UI操作履歴）[psr]",
                "テキスト手順書（操作情報のみ）[text]",
                "ユーザー向けの手順書（操作前画像だけ）[user-before]",
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
