using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using OperationRecorderApp.ExternalEvidence;
using Xunit;

namespace OperationRecorderApp.Tests;

public sealed class ApprovedRoiCatalogTests
{
    const string ProfileId = "approved-fixture";
    static readonly string SampleHash = new('a', 64);

    [Fact]
    public void 登録済みの資材だけを選んで承認画素を読み取る()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(CreateProfile());

        var loaded = ApprovedRoiCatalog.Load(selection with { ProfileSha256 = selection.ProfileSha256.ToUpperInvariant() }, files.Root);

        Assert.Equal(ProfileId, loaded.ProfileId);
        var scene = Assert.Single(loaded.Scenes);
        Assert.Equal(new Rectangle(2, 3, 1, 1), scene.Regions[1].Region);
        Assert.Equal(new byte[] { 50, 100, 150, 255 }, scene.Regions[1].Bgra);
        Assert.True(scene.Regions[1].Publish);
        Assert.False(scene.Regions[0].Publish);
    }

    [Fact]
    public void 未登録パッケージはファイルが存在しても拒否する()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(CreateProfile());
        files.WriteCatalog();

        AssertRejected(files, selection, "roi-profile-unavailable");
    }

    [Fact]
    public void 指定版のハッシュはカタログの値に拘束する()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(CreateProfile());

        AssertRejected(files, selection with { Revision = 2 }, "roi-profile-unavailable");
        AssertRejected(files, selection with { ProfileSha256 = SampleHash }, "roi-profile-mismatch");
        AssertRejected(files, selection with { ProfileId = "../other" }, "roi-profile-invalid");
    }

    [Fact]
    public void 資材を差し替えると意味が同じJSONでも拒否する()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(CreateProfile());
        File.AppendAllText(files.ProfilePath, "\n");

        AssertRejected(files, selection, "roi-profile-mismatch");
    }

    [Fact]
    public void 資材内部の識別子は登録と一致しなければならない()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(CreateProfile() with { ProfileId = "other-profile" });

        AssertRejected(files, selection, "roi-profile-mismatch");
    }

    [Fact]
    public void カタログに同じ識別子と版があると拒否する()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(CreateProfile());
        files.WriteCatalog(selection, selection);

        AssertRejected(files, selection, "roi-profile-invalid");
    }

    [Theory]
    [InlineData("bounds")]
    [InlineData("overlap")]
    [InlineData("alpha")]
    [InlineData("pixel-count")]
    [InlineData("no-anchor")]
    [InlineData("black-only")]
    [InlineData("phase")]
    public void ハッシュが一致した資材でも公開条件に合わなければ拒否する(string kind)
    {
        using var files = new CatalogFiles();
        var profile = CreateProfile();
        var scene = profile.Scenes[0];
        var anchor = scene.Regions[0];
        var published = scene.Regions[1];
        scene = kind switch
        {
            "bounds" => scene with { Regions = new[] { anchor, published with { Region = new Rectangle(int.MaxValue, 3, 1, 1) } } },
            "overlap" => scene with { Regions = new[] { anchor, published with { Region = anchor.Region } } },
            "alpha" => scene with { Regions = new[] { anchor, published with { Bgra = new byte[] { 50, 100, 150, 254 } } } },
            "pixel-count" => scene with { Regions = new[] { anchor, published with { Bgra = new byte[3] } } },
            "no-anchor" => scene with { Regions = new[] { anchor with { Publish = true }, published } },
            "black-only" => scene with { Regions = new[] { anchor, published with { Bgra = new byte[] { 0, 0, 0, 255 } } } },
            "phase" => scene with { Phases = new[] { "before", "send" } },
            _ => throw new ArgumentException(nameof(kind))
        };
        var selection = files.Register(profile with { Scenes = new[] { scene } });

        AssertRejected(files, selection, "roi-profile-invalid");
    }

    [Theory]
    [InlineData("\"x\":2,\"x\":2,")]
    [InlineData("\"x\":2,\"location\":{\"x\":2,\"y\":3},")]
    [InlineData("")]
    public void ROI座標の重複と別名と省略を拒否する(string substitute)
    {
        using var files = new CatalogFiles();
        string json = JsonSerializer.Serialize(CreateProfile(), EvidenceProtocol.Json);
        Assert.Contains("\"x\":2,", json);
        var selection = files.Register(Encoding.UTF8.GetBytes(json.Replace("\"x\":2,", substitute, StringComparison.Ordinal)));

        AssertRejected(files, selection, "roi-profile-invalid");
    }

    [Fact]
    public void カタログの上限を超えたファイルを拒否する()
    {
        using var files = new CatalogFiles();
        var selection = files.Register(CreateProfile());
        using (var stream = File.OpenWrite(files.CatalogPath)) stream.SetLength(256 * 1024 + 1);

        AssertRejected(files, selection, "roi-profile-invalid");
    }

    static void AssertRejected(CatalogFiles files, RoiProfileSelection selection, string code) =>
        Assert.Equal(code, Assert.Throws<EvidenceUnavailableException>(() => ApprovedRoiCatalog.Load(selection, files.Root)).Code);

    static ApprovedRoiProfile CreateProfile()
    {
        var regions = new ApprovedRoiRegion[]
        {
            new("anchor", new Rectangle(0, 0, 1, 1), new byte[] { 50, 0, 0, 255 }, false),
            new("publish", new Rectangle(2, 3, 1, 1), new byte[] { 50, 100, 150, 255 }, true)
        };
        return new(1, ProfileId, 1, SampleHash, new ApprovedRoiScene[]
        {
            new("ready", "root", new[] { "before", "after" }, 96, 24, 24, SampleHash, regions)
        });
    }

    sealed class CatalogFiles : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("oprec-approved-roi-").FullName;
        public string CatalogPath => Path.Combine(Root, "approved-roi-catalog.json");
        public string ProfilePath => Path.Combine(Root, "approved-roi-assets", ProfileId + ".1.json");

        public RoiProfileSelection Register(ApprovedRoiProfile profile) => Register(JsonSerializer.SerializeToUtf8Bytes(profile, EvidenceProtocol.Json));

        public RoiProfileSelection Register(byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
            File.WriteAllBytes(ProfilePath, bytes);
            var selection = new RoiProfileSelection(ProfileId, 1, EvidenceFiles.Hash(bytes));
            WriteCatalog(selection);
            return selection;
        }

        public void WriteCatalog(params RoiProfileSelection[] entries) =>
            File.WriteAllBytes(CatalogPath, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, profiles = entries }, EvidenceProtocol.Json));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
