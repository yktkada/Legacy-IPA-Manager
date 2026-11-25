using System.IO;
using IdeviceGui.Services.Imaging;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>图标选择规则（按缩放匹配最合适分辨率）——纯逻辑，不碰磁盘。</summary>
public sealed class IconSelectorTests
{
    private static IconCandidate C(string name, int size, IconSource source = IconSource.RootIconName, bool cgbi = false)
        => new(name, size, size, cgbi, source);

    [Fact]
    public void 按缩放取最小的够用尺寸_列表32px在150缩放时取48()
    {
        var candidates = new[]
        {
            C("Icon-1024.png", 1024),
            C("Icon-114.png", 114),
            C("Icon-57.png", 57),
            C("Icon-48.png", 48),
            C("Icon-32.png", 32),
        };

        var chosen = IconSelector.Select(candidates, new IconRequest(DisplayPx: 32, DpiScale: 1.5));

        Assert.NotNull(chosen);
        Assert.Equal("Icon-48.png", chosen!.Value.EntryName);   // 32*1.5=48，正好有 48
    }

    [Fact]
    public void 用户例子_候选128_256_512_目标270取512_目标250取256()
    {
        var candidates = new[] { C("Icon-128.png", 128), C("Icon-256.png", 256), C("Icon-512.png", 512) };

        // 目标 270：128/256 都不够（会放大），512 是"不小于目标且最接近"的那张
        var at270 = IconSelector.Select(candidates, new IconRequest(DisplayPx: 270, DpiScale: 1.0));
        Assert.Equal("Icon-512.png", at270!.Value.EntryName);

        // 目标 250：256 已经不小于 250，且比 512 更接近，选 256
        var at250 = IconSelector.Select(candidates, new IconRequest(DisplayPx: 250, DpiScale: 1.0));
        Assert.Equal("Icon-256.png", at250!.Value.EntryName);

        // 目标 100：128 是最小的不小于目标的（挑最近的够用图，避免解码 512）
        var at100 = IconSelector.Select(candidates, new IconRequest(DisplayPx: 100, DpiScale: 1.0));
        Assert.Equal("Icon-128.png", at100!.Value.EntryName);
    }

    [Fact]
    public void 缩放200时列表32px取64_而不是1024()
    {
        var candidates = new[] { C("Icon-1024.png", 1024), C("Icon-64.png", 64), C("Icon-32.png", 32) };
        var chosen = IconSelector.Select(candidates, new IconRequest(DisplayPx: 32, DpiScale: 2.0));
        Assert.Equal("Icon-64.png", chosen!.Value.EntryName);
    }

    [Fact]
    public void 没有够大的就放大取最大()
    {
        var candidates = new[] { C("Icon-57.png", 57), C("Icon-29.png", 29) };
        var chosen = IconSelector.Select(candidates, new IconRequest(DisplayPx: 256, DpiScale: 2.0));
        Assert.Equal("Icon-57.png", chosen!.Value.EntryName);
    }

    [Fact]
    public void 来源优先_storeArtwork压过plist点名和rootIcon()
    {
        var candidates = new[]
        {
            C("Payload/App.app/Icon-512.png", 512, IconSource.RootIconName),
            C("Payload/App.app/AppIcon60x60@3x.png", 180, IconSource.PlistNamed),
            C("iTunesArtwork", 1024, IconSource.StoreArtwork),
        };
        var chosen = IconSelector.Select(candidates, new IconRequest(DisplayPx: 32, DpiScale: 1.0));
        Assert.Equal("iTunesArtwork", chosen!.Value.EntryName);
    }

    [Fact]
    public void 关闭缩放匹配时每个来源取最大()
    {
        var candidates = new[] { C("Icon-57.png", 57), C("Icon-1024.png", 1024) };
        var chosen = IconSelector.Select(candidates, new IconRequest(DisplayPx: 32, DpiScale: 1.0, MatchDisplayScale: false));
        Assert.Equal("Icon-1024.png", chosen!.Value.EntryName);
    }

    [Fact]
    public void 超过MaxDecodePx的候选不入池()
    {
        // MaxDecodePx=1024 时 2048 被排除；32px@100% 目标 32 → 取最小的够用档 512 与 1024 里更小的 512
        var candidates = new[] { C("Icon-2048.png", 2048), C("Icon-1024.png", 1024), C("Icon-512.png", 512) };
        var chosen = IconSelector.Select(candidates, new IconRequest(DisplayPx: 32, DpiScale: 1.0, MaxDecodePx: 1024));
        Assert.Equal("Icon-512.png", chosen!.Value.EntryName);

        // 关掉缩放匹配后应在池内取最大（1024，而不是被排除的 2048）
        var biggest = IconSelector.Select(candidates,
            new IconRequest(DisplayPx: 32, DpiScale: 1.0, MatchDisplayScale: false, MaxDecodePx: 1024));
        Assert.Equal("Icon-1024.png", biggest!.Value.EntryName);
    }

    [Fact]
    public void 没有候选返回null()
    {
        Assert.Null(IconSelector.Select(Array.Empty<IconCandidate>(), new IconRequest(32, 1.0)));
    }
}

/// <summary>缓存档位（按缩放动态算）与 LRU 清理。</summary>
public sealed class IconCacheTests
{
    [Fact]
    public void 默认档位覆盖列表与信息区_含常见缩放()
    {
        var options = new IconCacheOptions();
        var buckets = options.Buckets;

        // 32 px 列表 × 100/125/150/175/200% → 32/40/48/56/64 向上取 16 的倍数
        Assert.Contains(32, buckets);      // 32×1.0
        Assert.Contains(48, buckets);      // 40 与 48 都归到 48
        Assert.Contains(64, buckets);      // 56 与 64 都归到 64
        // 256 px 信息区 × 100–200%
        Assert.Contains(256, buckets);
        Assert.Contains(320, buckets);
        Assert.Contains(384, buckets);
        Assert.Contains(448, buckets);
        Assert.Contains(512, buckets);
        Assert.True(buckets.SequenceEqual(buckets.OrderBy(b => b)), "档位应升序");
        Assert.All(buckets, b => Assert.Equal(0, b % 16));   // 都是 16 的倍数
    }

    [Fact]
    public void UI注册新显示尺寸后档位跟着变()
    {
        var options = new IconCacheOptions();
        var before = options.Buckets.Count;

        options.RegisterDisplaySize(96);    // 示例：信息区将来用 96 px

        Assert.True(options.Buckets.Count > before, "注册新显示尺寸后档位应增加");
        Assert.Contains(96, options.Buckets);       // 96×1.0
        Assert.Contains(128, options.Buckets);      // 96×1.25 = 120 → 128
        Assert.Contains(144, options.Buckets);      // 96×1.5
        Assert.Contains(176, options.Buckets);      // 96×1.75 = 168 → 176
        Assert.Contains(192, options.Buckets);      // 96×2.0
        Assert.True(options.Buckets.SequenceEqual(options.Buckets.OrderBy(b => b)), "仍应升序");
    }

    [Fact]
    public void BucketFor_返回最小的够用档()
    {
        var options = new IconCacheOptions();
        Assert.Equal(48, options.BucketFor(48));
        Assert.Equal(64, options.BucketFor(60));      // 60 没有档，往上取 64
        Assert.Equal(options.Buckets[^1], options.BucketFor(9999));   // 超上限取最大档
    }

    [Fact]
    public void 原图大于目标才缩小_否则直接用原图()
    {
        var options = new IconCacheOptions();

        // 原图 1024 / 目标 48 → 缩到 48
        Assert.Equal(48, options.ResolveCacheSize(sourcePx: 1024, targetPx: 48));

        // 原图 114 / 目标 48 → 缩到 48
        Assert.Equal(48, options.ResolveCacheSize(sourcePx: 114, targetPx: 48));

        // 原图 57 / 目标 64 → 原图不够大，直接用 57（不放大）
        Assert.Equal(57, options.ResolveCacheSize(sourcePx: 57, targetPx: 64));

        // 原图 32 / 目标 48 → 直接用 32
        Assert.Equal(32, options.ResolveCacheSize(sourcePx: 32, targetPx: 48));

        // 原图正好等于档位 → 就用它自己
        Assert.Equal(48, options.ResolveCacheSize(sourcePx: 48, targetPx: 48));

        // 原图 1024 / 目标 1024 → 不缩（目标超档位表时按需求本身，不压回 512）
        Assert.Equal(1024, options.ResolveCacheSize(sourcePx: 1024, targetPx: 1024));

        // 原图 2048 / 目标 1024 → 缩到 1024（受 MaxDecodePx 与目标约束）
        Assert.Equal(1024, options.ResolveCacheSize(sourcePx: 2048, targetPx: 1024));

        // 原图 512 / 目标 1024 → 原图不够大，直接用 512（不放大）
        Assert.Equal(512, options.ResolveCacheSize(sourcePx: 512, targetPx: 1024));
    }

    [Fact]
    public void 关掉DownscaleCache时一律存原图尺寸()
    {
        var options = new IconCacheOptions { DownscaleCache = false };
        Assert.Equal(1024, options.ResolveCacheSize(sourcePx: 1024, targetPx: 48));
        Assert.Equal(57, options.ResolveCacheSize(sourcePx: 57, targetPx: 256));
    }

    [Fact]
    public void LRU_超上限时删最旧的文件()
    {
        using var ws = new TestWorkspace("iconcache");
        var dataDir = ws.Full("data");
        var directory = IconCachePaths.CacheDirectory(dataDir);
        Directory.CreateDirectory(directory);

        // 造 3 个 1 MB 文件，访问时间依次递增
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(directory, IconCachePaths.FileName($"hash{i}", 64));
            File.WriteAllBytes(path, new byte[1024 * 1024]);
            File.SetLastAccessTimeUtc(path, now.AddMinutes(-10 + i));
        }

        var (removed, freed) = IconCachePaths.Trim(dataDir, maxMb: 2);

        Assert.Equal(1, removed);                 // 3 MB → 上限 2 MB，删最旧的 1 个
        Assert.True(freed >= 1024 * 1024);
        var (files, bytes) = IconCachePaths.Measure(dataDir);
        Assert.Equal(2, files);
        Assert.True(bytes <= 2L * 1024 * 1024);
        Assert.False(File.Exists(Path.Combine(directory, IconCachePaths.FileName("hash0", 64))));  // 最旧的被删
    }

    [Fact]
    public void LRU_目录不存在时不报错()
    {
        using var ws = new TestWorkspace("iconcache");
        var (removed, freed) = IconCachePaths.Trim(ws.Full("nope"), maxMb: 1);
        Assert.Equal(0, removed);
        Assert.Equal(0, freed);
    }

    [Fact]
    public void 引用计数清理_只删没人引用的旧图标()
    {
        using var ws = new TestWorkspace("iconcache");
        var dataDir = ws.Full("data");
        var directory = IconCachePaths.CacheDirectory(dataDir);
        Directory.CreateDirectory(directory);

        // 3 个文件：a 仍被引用、b 已无人引用（包被删/替换）、c 刚写入（太新，本轮不删）
        var referenced = Path.Combine(directory, IconCachePaths.FileName("shaA", 64));
        var orphanOld = Path.Combine(directory, IconCachePaths.FileName("shaB", 64));
        var orphanNew = Path.Combine(directory, IconCachePaths.FileName("shaC", 64));
        File.WriteAllBytes(referenced, new byte[1000]);
        File.WriteAllBytes(orphanOld, new byte[2000]);
        File.WriteAllBytes(orphanNew, new byte[4000]);
        var old = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(referenced, old);
        File.SetLastWriteTimeUtc(orphanOld, old);
        // orphanNew 保持"刚写入"

        var (removed, freed) = IconCachePaths.CollectOrphans(dataDir, new HashSet<string>(new[] { "shaA-64" }, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(1, removed);          // 只有 shaB 该删
        Assert.Equal(2000, freed);
        Assert.True(File.Exists(referenced), "被引用的不能删");
        Assert.False(File.Exists(orphanOld), "无人引用的旧文件必须删");
        Assert.True(File.Exists(orphanNew), "太新的留给下一轮，避免删掉别处刚写的");
    }

    [Fact]
    public void 引用计数清理_预算上限保护()
    {
        using var ws = new TestWorkspace("iconcache");
        var dataDir = ws.Full("data");
        var directory = IconCachePaths.CacheDirectory(dataDir);
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 10; i++)
        {
            var path = Path.Combine(directory, IconCachePaths.FileName($"dead{i}", 64));
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        }

        var (removed, _) = IconCachePaths.CollectOrphans(dataDir, new HashSet<string>(), minAgeMinutes: 10, budget: 3);

        Assert.Equal(3, removed);
        Assert.Equal(7, IconCachePaths.Measure(dataDir).Files);
    }
}