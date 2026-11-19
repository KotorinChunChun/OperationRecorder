using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class PasswordEvidenceGateTests
{
    const string ProfileId = "reviewed-fixture";
    static readonly string ExampleHash = new('a', 64);
    static readonly EvidenceUi KnownUi = new(new EvidenceNode[]
    {
        new(50032, 0, 0, 36, 36, false, true),
        new(50004, 8, 7, 16, 4, true, true),
        new(50025, 9, 25, 16, 4, false, true)
    }, false);

    [Fact]
    public void 承認済みでも別の可視トップレベルがあればパスワード証拠を拒否する()
    {
        var target = new EvidenceTarget(77, DateTimeOffset.UnixEpoch, "C:/fixture.exe", 1, 11);
        var login = new WindowObservation(Guid.NewGuid(), 1, target.WindowHandle, 0, 0, target.Pid, false, true,
            false, false, new Rectangle(90, 90, 100, 60), 96, "root");
        var backdrop = new WindowObservation(Guid.NewGuid(), 1, 12, 0, 0, target.Pid, false, true, false, false,
            new Rectangle(10, 10, 320, 200), 96, "unowned");
        var approvals = new[]
        {
            new WindowApproval(login.Hwnd, "root", true, login.WindowId, login.Generation),
            new WindowApproval(backdrop.Hwnd, "support", false, backdrop.WindowId, backdrop.Generation)
        };
        var knownGroup = typeof(PasswordEvidenceGate).Assembly
            .GetType("OperationRecorderApp.ExternalEvidence.PasswordGroupEvidenceWorker", throwOnError: true)
            .GetMethod("IsKnownGroup", BindingFlags.Static | BindingFlags.NonPublic);

        bool Allows(WindowObservation window) => (bool)knownGroup.Invoke(null,
            new object[] { new WindowGraphSnapshot(1, false, new[] { login, window }), target, approvals });

        Assert.True(Allows(backdrop with { Visible = false }));
        Assert.False(Allows(backdrop));
    }

    [Fact]
    public void 登録ファイルの識別子と版とハッシュが一致した規則を読む()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(Profile());
        var profile = PasswordEvidenceCatalog.Load(selection with { ProfileSha256 = selection.ProfileSha256.ToUpperInvariant() }, files.Root);
        var scene = Assert.Single(profile.Scenes);
        Assert.Equal(ProfileId, profile.ProfileId);
        Assert.Equal(new Rectangle(4, 3, 24, 12), scene.Fields[0].Mask);
        Assert.True(PasswordEvidenceGate.MatchesUi(scene, KnownUi));
    }

    [Fact]
    public void 未登録と差し替えと任意パスを拒否する()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(Profile());
        AssertRejected(files, selection with { Revision = 2 }, "password-profile-unavailable");
        AssertRejected(files, selection with { ProfileSha256 = ExampleHash }, "password-profile-mismatch");
        AssertRejected(files, selection with { ProfileId = "../other" }, "password-profile-invalid");
        File.AppendAllText(files.ProfilePath, "\n");
        AssertRejected(files, selection, "password-profile-mismatch");
    }

    [Theory]
    [InlineData("bounds")]
    [InlineData("margin")]
    [InlineData("overlap")]
    [InlineData("dpi")]
    [InlineData("duplicate-label")]
    [InlineData("empty-fields")]
    public void ハッシュが登録と一致しても保護規則の不備を拒否する(string reason)
    {
        using var files = new CatalogFiles();
        var scene = Scene();
        var standard = scene.Fields[0];
        var custom = scene.Fields[1];
        scene = reason switch
        {
            "bounds" => scene with { Fields = new[] { standard with { Mask = new Rectangle(-1, 2, 30, 14) }, custom } },
            "margin" => scene with { Fields = new[] { standard with { Mask = standard.FieldBounds }, custom } },
            "overlap" => scene with { Fields = new[] { standard with { Mask = new Rectangle(4, 3, 24, 22) }, custom } },
            "dpi" => scene with { Dpi = 0 },
            "duplicate-label" => scene with { Fields = new[] { standard, custom with { PublicLabel = standard.PublicLabel } } },
            "empty-fields" => scene with { Fields = Array.Empty<PasswordFieldRule>() },
            _ => throw new ArgumentException(nameof(reason))
        };
        var selection = files.Register(Profile() with { Scenes = new[] { scene } });
        AssertRejected(files, selection, "password-profile-invalid");
    }

    [Fact]
    public void 承認資材に座標の重複キーを持たせても拒否する()
    {
        using var files = new CatalogFiles();
        var json = JsonSerializer.Serialize(Profile(), EvidenceProtocol.Json);
        Assert.Contains("\"x\":8,", json);
        var selection = files.Register(Encoding.UTF8.GetBytes(json.Replace("\"x\":8,", "\"x\":8,\"x\":8,", StringComparison.Ordinal)));
        AssertRejected(files, selection, "password-profile-invalid");
    }

    [Fact]
    public void 専用アプリのパス種別と実測ハッシュとファイル版を照合する()
    {
        using var files = new CatalogFiles();
        string path = ExternalEvidenceProcessTests.FixtureExe;
        string sha = EvidenceFiles.HashFile(path);
        string version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        var profile = Profile() with { AppSha256 = sha, AppFileVersion = version };
        Assert.True(PasswordEvidenceCatalog.MatchesApplication(profile, path, sha));
        Assert.False(PasswordEvidenceCatalog.MatchesApplication(profile, path, ExampleHash));
        Assert.False(PasswordEvidenceCatalog.MatchesApplication(profile with { AppFileVersion = "4.5.6.7" }, path, sha));
        Assert.False(PasswordEvidenceCatalog.MatchesApplication(profile with { AppScope = "xronos-local" }, path, sha));
        string renamed = Path.Combine(files.Root, "not-approved.exe");
        File.Copy(path, renamed);
        Assert.False(PasswordEvidenceCatalog.MatchesApplication(profile, renamed, sha));
    }

    [Fact]
    public void 属性ありと属性なしの欄を余白ごと黒化し他の可視画素を保存する()
    {
        var scene = Scene();
        using var source = Frame();
        using var png = new MemoryStream(PasswordEvidenceGate.Mask(source, scene));
        using var protectedFrame = new Bitmap(png);
        Assert.True(PasswordEvidenceGate.VerifyProtected(protectedFrame, scene, KnownUi));
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            var expected = scene.Fields.Any(field => field.Mask.Contains(x, y)) ? Color.Black : source.GetPixel(x, y);
            Assert.Equal(expected.ToArgb(), protectedFrame.GetPixel(x, y).ToArgb());
        }
        Assert.Equal(Color.Magenta.ToArgb(), source.GetPixel(8, 7).ToArgb());
        Assert.Equal(Color.Gold.ToArgb(), source.GetPixel(9, 25).ToArgb());
    }

    [Fact]
    public void マスク内の一画素改変と透過PNGと全黒を独立検証で拒否する()
    {
        var scene = Scene();
        using var source = Frame();
        byte[] bytes = PasswordEvidenceGate.Mask(source, scene);
        using var changedBytes = new MemoryStream(bytes);
        using var changed = new Bitmap(changedBytes);
        changed.SetPixel(scene.Fields[0].Mask.Left, scene.Fields[0].Mask.Top, Color.Red);
        Assert.False(PasswordEvidenceGate.VerifyProtected(changed, scene, KnownUi));
        using var transparentBytes = new MemoryStream(bytes);
        using var transparent = new Bitmap(transparentBytes);
        transparent.SetPixel(0, 0, Color.FromArgb(100, 21, 37, 62));
        Assert.False(PasswordEvidenceGate.VerifyProtected(transparent, scene, KnownUi));
        using var blackBytes = new MemoryStream(EvidenceFiles.OpaquePng(source.Size));
        using var black = new Bitmap(blackBytes);
        Assert.False(PasswordEvidenceGate.VerifyProtected(black, scene, KnownUi));
    }

    [Fact]
    public void 半透明と透明のマスク外は黒地に合成して不透明にする()
    {
        var scene = Scene();
        using var source = Frame();
        source.SetPixel(0, 0, Color.FromArgb(0, 200, 100, 77));
        source.SetPixel(1, 0, Color.FromArgb(128, 200, 100, 50));
        using var bytes = new MemoryStream(PasswordEvidenceGate.Mask(source, scene));
        using var image = new Bitmap(bytes);
        Assert.Equal(Color.Black.ToArgb(), image.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(255, 100, 50, 25).ToArgb(), image.GetPixel(1, 0).ToArgb());
        Assert.True(PasswordEvidenceGate.VerifyProtected(image, scene, KnownUi));
    }

    [Fact]
    public void 未登録欄と曖昧な欄と構造不一致を公開に使えない()
    {
        var scene = Scene();
        var shifted = KnownUi with { Elements = KnownUi.Elements.Select((node, index) => index == 2 ? node with { X = node.X + 1 } : node).ToArray() };
        var duplicate = KnownUi with { Elements = KnownUi.Elements.Append(KnownUi.Elements[2] with { Enabled = false }).ToArray() };
        var extraPassword = KnownUi with { Elements = KnownUi.Elements.Append(new EvidenceNode(50004, 2, 17, 8, 4, true, true)).ToArray() };
        Assert.False(PasswordEvidenceGate.MatchesUi(scene, shifted));
        Assert.False(PasswordEvidenceGate.MatchesUi(scene with { UiSha256 = PasswordEvidenceGate.StructureSha256(duplicate) }, duplicate));
        Assert.False(PasswordEvidenceGate.MatchesUi(scene with { UiSha256 = PasswordEvidenceGate.StructureSha256(extraPassword) }, extraPassword));
        Assert.Null(PasswordEvidenceGate.StructureSha256(KnownUi with { TextRead = true }));
        Assert.Null(PasswordEvidenceGate.StructureSha256(KnownUi with { Truncated = true }));
        Assert.Equal(scene.UiSha256, PasswordEvidenceGate.StructureSha256(KnownUi with { Elements = KnownUi.Elements.Reverse().ToArray() }));
        Assert.NotEqual(scene.UiSha256, PasswordEvidenceGate.StructureSha256(KnownUi with { Elements = KnownUi.Elements.Select(node => node with { IsPassword = false }).ToArray() }));
    }

    [Fact]
    public void 寸法が一致しない画像や不正な規則からPNGを作らない()
    {
        using var image = new Bitmap(35, 36);
        var error = Assert.Throws<EvidenceUnavailableException>(() => PasswordEvidenceGate.Mask(image, Scene()));
        Assert.Equal("password-mask-unavailable", error.Code);
        using var properImage = Frame();
        Assert.Throws<EvidenceUnavailableException>(() => PasswordEvidenceGate.Mask(properImage, Scene() with { Fields = Array.Empty<PasswordFieldRule>() }));
    }

    static PasswordEvidenceScene Scene() => new("reviewed-login", new[] { "before", "after" }, 96, 36, 36,
        "WindowsForms10.Window.", PasswordEvidenceGate.StructureSha256(KnownUi), new PasswordFieldRule[]
        {
            new("standard", "WindowsForms10.STATIC", "Standard field:", "WindowsForms10.EDIT", 50004,
                new Rectangle(8, 7, 16, 4), new Rectangle(4, 3, 24, 12)),
            new("custom", "WindowsForms10.STATIC", "Custom field:", "WindowsForms10.Window.", 50025,
                new Rectangle(9, 25, 16, 4), new Rectangle(5, 21, 24, 12))
        });

    static PasswordEvidenceProfile Profile() => new(1, ProfileId, 1, "synthetic-fixture", ExampleHash, "1.0.0.0", new[] { Scene() });

    static Bitmap Frame()
    {
        var bitmap = new Bitmap(36, 36, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.MediumSeaGreen);
        graphics.FillRectangle(Brushes.Magenta, Scene().Fields[0].FieldBounds);
        graphics.FillRectangle(Brushes.Gold, Scene().Fields[1].FieldBounds);
        return bitmap;
    }

    static void AssertRejected(CatalogFiles files, PasswordProfileSelection selection, string code) =>
        Assert.Equal(code, Assert.Throws<EvidenceUnavailableException>(() => PasswordEvidenceCatalog.Load(selection, files.Root)).Code);

    sealed class CatalogFiles : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("oprec-password-rules-").FullName;
        public string ProfilePath => Path.Combine(Root, "password-evidence-assets", ProfileId + ".1.json");

        public PasswordProfileSelection Register(PasswordEvidenceProfile profile) => Register(JsonSerializer.SerializeToUtf8Bytes(profile, EvidenceProtocol.Json));

        public PasswordProfileSelection Register(byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
            File.WriteAllBytes(ProfilePath, bytes);
            var selection = new PasswordProfileSelection(ProfileId, 1, EvidenceFiles.Hash(bytes));
            File.WriteAllBytes(Path.Combine(Root, "password-evidence-catalog.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                profiles = new[] { selection }
            }, EvidenceProtocol.Json));
            return selection;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
