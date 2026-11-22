using System;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class DisclosureGateTests
{
    const string AppHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static readonly Rectangle PublicArea = new(1, 1, 4, 3);
    static Bitmap Reference()
    {
        var image = new Bitmap(12, 8, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image); graphics.Clear(Color.DarkGreen);
        using var brush = new SolidBrush(Color.Blue); graphics.FillRectangle(brush, PublicArea);
        return image;
    }
    static PublicRegionRule Rule(Bitmap reference, Rectangle? region = null, string id = "public-status")
    {
        var rectangle = region ?? PublicArea;
        return new(id, "main", 96, reference.Width, reference.Height, rectangle,
            new[] { DisclosureGate.PixelHash(reference, rectangle) });
    }
    static DisclosureProfile Profile(Bitmap reference, params PublicRegionRule[] rules) =>
        new(1, "test-profile", 1, AppHash, rules.Length == 0 ? new[] { Rule(reference) } : rules,
            new[] { DisclosureGate.PixelHash(reference, new Rectangle(Point.Empty, reference.Size)) });
    static DisclosureDecision Apply(Bitmap source, DisclosureProfile profile, Rectangle[] denied = null, bool coherent = true) =>
        DisclosureGate.Apply(source, AppHash, "main", 96, profile, denied ?? Array.Empty<Rectangle>(), coherent);
    static Bitmap Decode(DisclosureDecision result)
    {
        using var bytes = new MemoryStream(result.Png); using var decoded = new Bitmap(bytes);
        return new Bitmap(decoded);
    }
    static void AssertBlack(DisclosureDecision result)
    {
        Assert.False(result.RequirementsMet); Assert.Empty(result.PublishedRuleIds);
        using var bitmap = Decode(result);
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(x,y).ToArgb());
    }

    [Fact]
    public void 同じ承認済みフレームの公開領域だけを不透明PNGへ合成する()
    {
        using var source = Reference(); var profile = Profile(source);
        string before = DisclosureGate.PixelHash(source, new Rectangle(Point.Empty, source.Size));
        var result = Apply(source, profile); Assert.True(result.RequirementsMet); Assert.Equal("public-regions",result.DisclosureStatus);
        Assert.Equal("approved",result.ReasonCode); Assert.Equal(new[] {"public-status"},result.PublishedRuleIds);
        using var bitmap = Decode(result);
        for (int y=0;y<source.Height;y++) for(int x=0;x<source.Width;x++)
            Assert.Equal((PublicArea.Contains(x,y) ? Color.Blue : Color.Black).ToArgb(), bitmap.GetPixel(x,y).ToArgb());
        Assert.Equal(before,DisclosureGate.PixelHash(source,new Rectangle(Point.Empty,source.Size)));
        Assert.Equal(Color.DarkGreen.ToArgb(),source.GetPixel(10,6).ToArgb());
    }
    [Fact]
    public void 指定なしは全体マスクで診断成功にしない()
    {
        using var source=Reference(); var result=Apply(source,null);
        Assert.Equal("opaque-only",result.DisclosureStatus); Assert.Equal("profile-required",result.ReasonCode); AssertBlack(result);
    }
    [Fact]
    public void 公開領域外の認証表示変化でも未承認状態の全画素を保護する()
    {
        using var reference=new Bitmap(240,80); using(var graphics=Graphics.FromImage(reference))graphics.Clear(Color.Blue);
        var area=new Rectangle(0,0,30,30); var profile=Profile(reference,Rule(reference,area));
        using var source=new Bitmap(reference);
        using(var graphics=Graphics.FromImage(source)) using(var font=new Font(FontFamily.GenericMonospace,12))
            graphics.DrawString("DUMMY_SECRET_PASSWORD",font,Brushes.White,40,30);
        var result=Apply(source,profile);
        Assert.Equal("state-mismatch",result.ReasonCode); AssertBlack(result);
        // The public ROI itself is still byte-for-byte approved: whole-state verification is decisive.
        Assert.Equal(profile.Rules[0].ApprovedPixelSha256[0],DisclosureGate.PixelHash(source,area));
    }
    [Fact]
    public void 公開領域内の秘密混入を自動学習せず拒否する()
    {
        using var reference=Reference(); var profile=Profile(reference);
        string approved=profile.Rules[0].ApprovedPixelSha256[0]; using var source=new Bitmap(reference);
        source.SetPixel(PublicArea.X,PublicArea.Y,Color.Red);
        AssertBlack(Apply(source,profile)); Assert.Equal(approved,profile.Rules[0].ApprovedPixelSha256[0]);
        Assert.NotEqual(approved,DisclosureGate.PixelHash(source,PublicArea));
    }
    [Theory]
    [InlineData("application-mismatch")]
    [InlineData("role-not-approved")]
    [InlineData("dpi-mismatch")]
    [InlineData("size-mismatch")]
    public void 版や部品条件の不一致は公開しない(string expected)
    {
        using var source=Reference(); var profile=Profile(source);
        string app=expected=="application-mismatch" ? new string('b',64) : AppHash;
        string role=expected=="role-not-approved" ? "dialog" : "main";
        int dpi=expected=="dpi-mismatch" ? 144 : 96;
        if(expected=="size-mismatch") profile=profile with {Rules=new[]{Rule(source) with {WindowWidth=source.Width+1}}};
        var result=DisclosureGate.Apply(source,app,role,dpi,profile,Array.Empty<Rectangle>(),true);
        Assert.Equal(expected,result.ReasonCode); AssertBlack(result);
    }
    [Fact]
    public void 状態の事前承認がなければROI承認だけで公開しない()
    {
        using var source=Reference(); var profile=Profile(source) with {ApprovedFrameSha256=null};
        var result=Apply(source,profile); Assert.Equal("state-unproven",result.ReasonCode); AssertBlack(result);
    }
    [Fact]
    public void 認証や未知領域との重なりは画素承認より優先する()
    {
        using var source=Reference(); var profile=Profile(source);
        var result=Apply(source,profile,new[]{new Rectangle(PublicArea.Right-1,PublicArea.Y,1,1)});
        Assert.Equal("denied-overlap",result.ReasonCode); AssertBlack(result);
    }
    [Fact]
    public void 画面世代や保護領域が未確定なら承認画像でも公開しない()
    {
        using var source=Reference(); var profile=Profile(source);
        var context=Apply(source,profile,coherent:false); Assert.Equal("context-unproven",context.ReasonCode); AssertBlack(context);
        var unknown=DisclosureGate.Apply(source,AppHash,"main",96,profile,null,true);
        Assert.Equal("protection-unproven",unknown.ReasonCode); AssertBlack(unknown);
        var invalid=Apply(source,profile,new[]{new Rectangle(-1,0,1,1)});
        Assert.Equal("invalid-denied-regions",invalid.ReasonCode); AssertBlack(invalid);
    }
    [Fact]
    public void 一部の公開に成功しても必須領域不足を診断成功にしない()
    {
        using var source=Reference(); var first=Rule(source);
        var second=Rule(source,new Rectangle(8,1,2,2),"second") with {ApprovedPixelSha256=new[]{new string('b',64)}};
        var profile=Profile(source,first,second); var result=Apply(source,profile);
        Assert.False(result.RequirementsMet); Assert.Equal("partial-public",result.DisclosureStatus); Assert.Equal("pixel-mismatch",result.ReasonCode);
        Assert.Equal(new[]{"public-status"},result.PublishedRuleIds); using var output=Decode(result);
        Assert.Equal(Color.Blue.ToArgb(),output.GetPixel(2,2).ToArgb()); Assert.Equal(Color.Black.ToArgb(),output.GetPixel(8,1).ToArgb());
    }
    [Fact]
    public void 無効なプロファイルの内容を例外や判定理由へ写さない()
    {
        using var source=Reference(); var good=Profile(source);
        var badProfiles=new[] {
            good with {SchemaVersion=2}, good with {ProfileId="DUMMY_SECRET\nVALUE"}, good with {Revision=0},
            good with {AppSha256="invalid"},good with {Rules=null},good with {Rules=Array.Empty<PublicRegionRule>()},
            good with {Rules=new[]{Rule(source) with {Region=new Rectangle(int.MaxValue,0,2,2)}}},
            good with {Rules=new[]{Rule(source) with {ApprovedPixelSha256=new[]{"DUMMY_SECRET"}}}},
            good with {Rules=new[]{Rule(source),Rule(source)}},
            good with {Rules=Enumerable.Repeat(Rule(source),65).ToArray()},
            good with {ApprovedFrameSha256=new[]{"invalid"}}
        };
        foreach(var profile in badProfiles){var result=Apply(source,profile);Assert.Equal("invalid-profile",result.ReasonCode);AssertBlack(result);}
    }
    [Fact]
    public void 全黒の承認画像も診断成功と表示しない()
    {
        using var source=new Bitmap(12,8); using(var graphics=Graphics.FromImage(source))graphics.Clear(Color.Black);
        var result=Apply(source,Profile(source)); Assert.Equal("no-diagnostic-pixels",result.ReasonCode); Assert.Equal("opaque-only",result.DisclosureStatus); AssertBlack(result);
    }
    [Fact]
    public void 透明画素は黒背景へ合成し隠れたRGBを出力しない()
    {
        using var source=new Bitmap(2,1,PixelFormat.Format32bppArgb);
        source.SetPixel(0,0,Color.FromArgb(0,255,255,255));source.SetPixel(1,0,Color.FromArgb(128,200,0,0));
        using var canonical=new Bitmap(2,1);canonical.SetPixel(0,0,Color.Black);canonical.SetPixel(1,0,Color.FromArgb(255,100,0,0));
        var region=new Rectangle(0,0,2,1); var rule=new PublicRegionRule("public","main",96,2,1,region,new[]{DisclosureGate.PixelHash(canonical,region)});
        var result=Apply(source,Profile(canonical,rule)); Assert.True(result.RequirementsMet);using var output=Decode(result);
        Assert.Equal(Color.Black.ToArgb(),output.GetPixel(0,0).ToArgb());Assert.Equal(Color.FromArgb(255,100,0,0).ToArgb(),output.GetPixel(1,0).ToArgb());
        Assert.Equal(128,source.GetPixel(1,0).A);
    }
    [Fact]
    public void canonicalハッシュはLE寸法とstrideを除いた不透明BGRAである()
    {
        using var image=new Bitmap(1,1);image.SetPixel(0,0,Color.FromArgb(255,2,3,4));
        byte[] expected=new byte[12];BinaryPrimitives.WriteInt32LittleEndian(expected,1);BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(4),1);
        expected[8]=4;expected[9]=3;expected[10]=2;expected[11]=255;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),DisclosureGate.PixelHash(image,new Rectangle(0,0,1,1)));
        using var padded=new Bitmap(3,2,PixelFormat.Format24bppRgb);using var argb=new Bitmap(3,2,PixelFormat.Format32bppArgb);
        for(int y=0;y<2;y++)for(int x=0;x<3;x++){var color=Color.FromArgb(20+x,30+y,40+x+y);padded.SetPixel(x,y,color);argb.SetPixel(x,y,color);}
        Assert.Equal(DisclosureGate.PixelHash(argb,new Rectangle(0,0,3,2)),DisclosureGate.PixelHash(padded,new Rectangle(0,0,3,2)));
    }
    [Fact]
    public void 負のstrideでも画面上の行順序を同じハッシュにする()
    {
        IntPtr buffer=Marshal.AllocHGlobal(32);
        try
        {
            using var flipped=new Bitmap(3,2,-16,PixelFormat.Format32bppArgb,IntPtr.Add(buffer,16));
            using var normal=new Bitmap(3,2);
            for(int y=0;y<2;y++)for(int x=0;x<3;x++){var color=Color.FromArgb(100+x,50+y,20+x+y);flipped.SetPixel(x,y,color);normal.SetPixel(x,y,color);}
            Assert.Equal(DisclosureGate.PixelHash(normal,new Rectangle(0,0,3,2)),DisclosureGate.PixelHash(flipped,new Rectangle(0,0,3,2)));
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [Fact]
    public void 範囲外や使用不能フレームは固定コードで拒否する()
    {
        using var source=Reference();
        Assert.Throws<EvidenceUnavailableException>(()=>DisclosureGate.PixelHash(source,new Rectangle(-1,0,1,1)));
        Assert.Throws<EvidenceUnavailableException>(()=>DisclosureGate.PixelHash(source,new Rectangle(int.MaxValue,0,2,2)));
        var result=DisclosureGate.Apply(null,AppHash,"main",96,null,Array.Empty<Rectangle>(),true);
        Assert.Empty(result.Png);Assert.False(result.RequirementsMet);Assert.Equal("invalid-frame",result.ReasonCode);
    }
}
