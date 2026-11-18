using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class ApprovedRoiGateTests
{
    const int Width = 24, Height = 18, Dpi = 96;
    static readonly Rectangle Published = new(2, 2, 4, 3);
    static readonly Rectangle Anchor = new(12, 3, 3, 2);
    static readonly Rectangle Dynamic = new(2, 12, 14, 3);
    static readonly Color HeaderColor = Color.FromArgb(20, 90, 173);
    static readonly Color AnchorColor = Color.FromArgb(205, 62, 18);
    static readonly EvidenceUi KnownUi = new(new[]
    {
        new EvidenceNode(50032, 0, 0, Width, Height, false, true),
        new EvidenceNode(50020, 2, 12, 14, 3, false, true)
    }, false);

    [Fact]
    public void 承認済み画素だけを出力し日時や保存先の変化は公開画像に影響しない()
    {
        using var first = DrawFrame(Color.Gold);
        using var second = DrawFrame(Color.LimeGreen);
        var profile = Profile();
        var reversedUi = KnownUi with { Elements = KnownUi.Elements.Reverse().ToArray() };

        var before = Apply(first, profile);
        var after = ApprovedRoiGate.Apply(second, profile, "root", "after", Dpi, reversedUi, KnownUi, true);

        Assert.Equal("roi-approved", before.ReasonCode);
        Assert.Equal("known-state", before.SceneId);
        Assert.Equal(before.SceneId, after.SceneId);
        Assert.Equal(before.Png, after.Png);
        using var stream = new MemoryStream(before.Png);
        using var image = new Bitmap(stream);
        Assert.True(ApprovedRoiGate.VerifyProtected(image, profile, before.SceneId, "root", "before", Dpi, KnownUi));
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Assert.Equal((Published.Contains(x, y) ? HeaderColor : Color.Black).ToArgb(), image.GetPixel(x, y).ToArgb());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void 公開領域または照合専用領域の1画素変更を拒否する(bool publish, bool alphaOnly)
    {
        using var frame = DrawFrame(Color.Gold);
        var changed = publish ? Published : Anchor;
        var color = frame.GetPixel(changed.X, changed.Y);
        frame.SetPixel(changed.X, changed.Y, alphaOnly
            ? Color.FromArgb(254, color.R, color.G, color.B)
            : Color.FromArgb(255, color.R + 1, color.G, color.B));

        AssertBlocked(Apply(frame, Profile()), "roi-scene-unproven");
    }

    [Fact]
    public void 認証ノードが公開範囲外にあっても全部を黒化する()
    {
        using var frame = DrawFrame(Color.Gold);
        var auth = KnownUi with
        {
            Elements = KnownUi.Elements.Append(new EvidenceNode(50004, 2, 12, 14, 3, true, true)).ToArray()
        };
        var result = ApprovedRoiGate.Apply(frame, Profile(), "root", "before", Dpi, KnownUi, auth, true);
        AssertBlocked(result, "roi-authentication-present");
        Assert.Null(ApprovedRoiGate.StructureSha256(auth));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("duplicate")]
    [InlineData("truncated")]
    [InlineData("text")]
    [InlineData("empty")]
    [InlineData("wrong-role")]
    [InlineData("wrong-phase")]
    [InlineData("wrong-dpi")]
    [InlineData("uncertain")]
    public void 同じ画素でも構造と実行条件が承認されなければ黒化する(string difference)
    {
        using var frame = DrawFrame(Color.Gold);
        var after = difference switch
        {
            "changed" => KnownUi with { Elements = new[] { KnownUi.Elements[0], KnownUi.Elements[1] with { Enabled = false } } },
            "duplicate" => KnownUi with { Elements = KnownUi.Elements.Append(KnownUi.Elements[1]).ToArray() },
            "truncated" => KnownUi with { Truncated = true },
            "text" => KnownUi with { TextRead = true },
            "empty" => KnownUi with { Elements = Array.Empty<EvidenceNode>() },
            _ => KnownUi
        };
        var decision = ApprovedRoiGate.Apply(frame, Profile(), difference == "wrong-role" ? "other" : "root",
            difference == "wrong-phase" ? "failure" : "before", difference == "wrong-dpi" ? 120 : Dpi,
            KnownUi, after, difference != "uncertain");
        AssertBlocked(decision, difference == "uncertain" ? "context-unproven" : "roi-scene-unproven");
    }

    [Fact]
    public void 一致候補が複数なら公開しない()
    {
        using var frame = DrawFrame(Color.Gold);
        var profile = Profile();
        profile = profile with { Scenes = new[] { profile.Scenes[0], profile.Scenes[0] with { SceneId = "second-state" } } };
        AssertBlocked(Apply(frame, profile), "roi-scene-unproven");
    }

    [Theory]
    [InlineData(50004)]
    [InlineData(50025)]
    public void 承認済み構造でも領域と入力欄または不明コントロールが重なれば公開しない(int controlType)
    {
        using var frame = DrawFrame(Color.Gold);
        var overlap = KnownUi with
        {
            Elements = KnownUi.Elements.Append(new EvidenceNode(controlType, 2, 2, 4, 3, false, true)).ToArray()
        };
        var decision = ApprovedRoiGate.Apply(frame, Profile(overlap), "root", "before", Dpi, overlap, overlap, true);
        AssertBlocked(decision, "roi-denied-overlap");
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("anchor")]
    [InlineData("inside")]
    [InlineData("alpha")]
    [InlineData("scene")]
    [InlineData("phase")]
    public void 保存後の検証で画像の差し替えや別シーンへの流用を拒否する(string difference)
    {
        using var frame = DrawFrame(Color.Gold);
        var profile = Profile();
        var result = Apply(frame, profile);
        using var stream = new MemoryStream(result.Png);
        using var decoded = new Bitmap(stream);
        using var image = decoded.Clone(new Rectangle(0, 0, Width, Height), PixelFormat.Format32bppArgb);
        Assert.True(ApprovedRoiGate.VerifyProtected(image, profile, result.SceneId, "root", "before", Dpi, KnownUi));
        switch (difference)
        {
            case "outside": image.SetPixel(Dynamic.X, Dynamic.Y, Color.Gold); break;
            case "anchor": image.SetPixel(Anchor.X, Anchor.Y, AnchorColor); break;
            case "inside": image.SetPixel(Published.X, Published.Y, Color.Black); break;
            case "alpha": image.SetPixel(0, 0, Color.FromArgb(0, 0, 0, 0)); break;
        }
        string sceneId = difference == "scene" ? "other-state" : result.SceneId;
        string phase = difference == "phase" ? "failure" : "before";
        Assert.False(ApprovedRoiGate.VerifyProtected(image, profile, sceneId, "root", phase, Dpi, KnownUi));
    }

    [Fact]
    public void 未承認の可視窓や全黒画像の群を診断成功としない()
    {
        var first = new WindowObservation(Guid.NewGuid(), 1, 11, 0, 0, 77, false, true, false, false,
            new(0, 0, Width, Height), Dpi, "root");
        var second = first with { WindowId = Guid.NewGuid(), Hwnd = 22, Relation = "owned", OwnerHwnd = first.Hwnd };
        var plan = new GroupCapturePlan(new(5, false, new[] { first, second }),
            new(Approvals: new[]
            {
                new WindowApproval(first.Hwnd, "root", true, first.WindowId, first.Generation),
                new WindowApproval(second.Hwnd, "dialog", false, second.WindowId, second.Generation)
            }, RequirePublicDiagnostic: false, RoiProfile: new("fixture", 1, new string('b', 64))),
            new string('a', 64));
        var approved = new WindowEvidence(first, "root", "wgc-window", null, 10000, Width, Height,
            null, null, null, null, "fresh", "public-regions", true, "roi-approved", ApprovedSceneId: "known-state");
        var protectedDialog = approved with
        {
            Window = second, Role = "dialog", DisclosureStatus = "opaque-only", ApprovedSceneId = null, RequirementsMet = false
        };
        Assert.False(GroupEvidenceValidation.Evaluate(plan, new[] { approved, protectedDialog }, true).RequirementsMet);

        second = second with { Visible = false };
        plan = plan with { Snapshot = plan.Snapshot with { Windows = new[] { first, second } } };
        protectedDialog = protectedDialog with { Window = second };
        Assert.True(GroupEvidenceValidation.Evaluate(plan, new[] { approved, protectedDialog }, true).RequirementsMet);
        var blackRoot = approved with { DisclosureStatus = "opaque-only", ApprovedSceneId = null, RequirementsMet = false };
        Assert.False(GroupEvidenceValidation.Evaluate(plan, new[] { blackRoot, protectedDialog }, true).RequirementsMet);
    }

    static ApprovedRoiDecision Apply(Bitmap raw, ApprovedRoiProfile profile) =>
        ApprovedRoiGate.Apply(raw, profile, "root", "before", Dpi, KnownUi, KnownUi, true);

    static ApprovedRoiProfile Profile(EvidenceUi ui = null) =>
        new(1, "fixture", 1, new string('a', 64), new[]
        {
            new ApprovedRoiScene("known-state", "root", new[] { "before", "after" }, Dpi, Width, Height,
                ApprovedRoiGate.StructureSha256(ui ?? KnownUi), new[]
                {
                    new ApprovedRoiRegion("heading", Published, PixelAsset(Published, HeaderColor), true),
                    new ApprovedRoiRegion("anchor", Anchor, PixelAsset(Anchor, AnchorColor), false)
                })
        });

    static byte[] PixelAsset(Rectangle area, Color color) => Enumerable.Repeat(
        new[] { color.B, color.G, color.R, color.A }, area.Width * area.Height).SelectMany(pixel => pixel).ToArray();

    static Bitmap DrawFrame(Color value)
    {
        var frame = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.White);
        using var publicBrush = new SolidBrush(HeaderColor);
        using var anchorBrush = new SolidBrush(AnchorColor);
        using var dynamicBrush = new SolidBrush(value);
        graphics.FillRectangle(publicBrush, Published);
        graphics.FillRectangle(anchorBrush, Anchor);
        graphics.FillRectangle(dynamicBrush, Dynamic);
        return frame;
    }

    static void AssertBlocked(ApprovedRoiDecision decision, string reason)
    {
        Assert.Null(decision.SceneId);
        Assert.Equal(reason, decision.ReasonCode);
        using var stream = new MemoryStream(decision.Png);
        using var image = new Bitmap(stream);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Assert.Equal(Color.Black.ToArgb(), image.GetPixel(x, y).ToArgb());
    }
}
