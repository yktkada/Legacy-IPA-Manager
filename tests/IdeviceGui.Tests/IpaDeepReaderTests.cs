using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using IdeviceGui.Services.Imaging;
using IdeviceGui.Services.Parsing;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// M3 深解析：用一个"合成 IPA"（zip）验证 深字段 / 体积 / 架构 / 图标候选与 CgBI 还原 / 落盘键。
/// 合成包结构可控，不依赖 E 盘真实素材（真实素材留给 L3 全库验收，见测试计划 §3）。
/// </summary>
public sealed class IpaDeepReaderTests
{
    private const string InfoPlistXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>CFBundleIdentifier</key><string>com.example.deep</string>
        <key>CFBundleVersion</key><string>42</string>
        <key>CFBundleShortVersionString</key><string>1.4.2</string>
        <key>CFBundleDisplayName</key><string>Deep Test</string>
        <key>CFBundleExecutable</key><string>Deep</string>
        <key>MinimumOSVersion</key><string>6.1</string>
        <key>DTSDKName</key><string>iphoneos7.1</string>
        <key>UIDeviceFamily</key><array><integer>1</integer><integer>2</integer></array>
        <key>UIRequiredDeviceCapabilities</key><array><string>armv7</string></array>
        <key>CFBundleIconFiles</key><array><string>Icon-72.png</string><string>Icon-144.png</string></array>
        </dict></plist>
        """;

    private static byte[] PngRect(int width, int height, byte r, byte g, byte b)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }

        return PngCodec.Encode(new PngCodec.DecodedPng(width, height, rgba));
    }

    private static byte[] Png(int size, byte r, byte g, byte b, bool cgbi = false)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }

        var png = PngCodec.Encode(new PngCodec.DecodedPng(size, size, rgba));
        return cgbi ? ToCgbi(png) : png;
    }

    /// <summary>把标准 PNG 转成 CgBI（仅供测试；真实文件结构见 PngCodec 注释）。</summary>
    private static byte[] ToCgbi(byte[] png)
    {
        var decoded = PngCodec.Decode(png);
        var stride = decoded.Width * 4;
        var filtered = new byte[decoded.Height * (stride + 1)];
        for (var y = 0; y < decoded.Height; y++)
        {
            filtered[y * (stride + 1)] = 0;
            for (var x = 0; x < decoded.Width; x++)
            {
                var s = (y * decoded.Width + x) * 4;
                var d = y * (stride + 1) + 1 + x * 4;
                var a = decoded.Rgba[s + 3];
                filtered[d] = (byte)(decoded.Rgba[s + 2] * a / 255);       // B（预乘）
                filtered[d + 1] = (byte)(decoded.Rgba[s + 1] * a / 255);   // G
                filtered[d + 2] = (byte)(decoded.Rgba[s] * a / 255);       // R
                filtered[d + 3] = a;
            }
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(filtered);
            }

            compressed = ms.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), decoded.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), decoded.Height);
        ihdr[8] = 8;
        ihdr[9] = 6;

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(output, "CgBI", new byte[] { 0x50, 0x00, 0x20, 0x06 });
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(body);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typeBytes, body));
        stream.Write(crc);
    }

    private static uint Crc(byte[] type, byte[] body)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = Step(crc, b);
        foreach (var b in body) crc = Step(crc, b);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Step(uint crc, byte value)
    {
        crc ^= value;
        for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    /// <summary>造一个结构完整的合成 IPA（fat 双架构 + 图标/启动图 + Frameworks/PlugIns）。</summary>
    private static string BuildIpa(TestWorkspace ws, string name = "Deep.ipa", bool cgbiIcon = true)
    {
        var path = ws.Full(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string entry, byte[] bytes)
        {
            var e = zip.CreateEntry(entry, CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(bytes);
        }

        Add("Payload/Deep.app/Info.plist", Encoding.UTF8.GetBytes(InfoPlistXml));
        // fat 头：CAFEBABE + 2 个架构（armv6 / armv7），每项 20 字节
        var fat = new byte[8 + 2 * 20];
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(0, 4), 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(12, 4), 6);    // armv6
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(28, 4), 12);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(32, 4), 9);    // armv7
        Add("Payload/Deep.app/Deep", fat);

        Add("Payload/Deep.app/Icon-72.png", Png(72, 200, 10, 10));
        Add("Payload/Deep.app/Icon-144.png", Png(144, 10, 200, 10, cgbiIcon));
        Add("Payload/Deep.app/AppIcon60x60@3x.png", Png(180, 10, 10, 200));
        Add("Payload/Deep.app/Default-Landscape@2x.png", Png(256, 1, 2, 3));   // 启动图：必须被排除
        Add("Payload/Deep.app/icon_background.png", PngRect(300, 264, 9, 9, 9));   // UI 素材：非方形，必须排除
        Add("Payload/Deep.app/Frameworks/F.framework/F", new byte[100]);
        Add("Payload/Deep.app/PlugIns/P.appex/P", new byte[50]);
        Add("Payload/Deep.app/Assets.car", new byte[20]);
        Add("iTunesArtwork", Png(512, 7, 7, 7));
        return path;
    }

    [Fact]
    public void 深字段与体积与架构()
    {
        using var ws = new TestWorkspace("deep");
        var path = BuildIpa(ws);

        var result = IpaDeepReader.Read(path);

        Assert.Null(result.Error);
        Assert.Equal("com.example.deep", result.BundleId);
        Assert.Equal("1.4.2", result.Version);
        Assert.Equal("42", result.Build);
        Assert.Equal("Deep Test", result.DisplayName);
        Assert.Equal("6.1", result.MinOs);
        Assert.Equal("iphoneos7.1", result.Sdk);
        Assert.Equal("Universal", result.DeviceFamily);
        Assert.Contains("armv7", result.RequiredCapsJson);
        Assert.Equal("armv6,armv7", result.Arch);

        Assert.True(result.CompressedSize > 0);
        Assert.True(result.UncompressedSize > 0);
        Assert.True(result.AppSize > 0);
        Assert.True(result.PayloadSize >= result.AppSize);
        Assert.True(result.UncompressedSize >= result.PayloadSize);
        Assert.True(result.ExecutableSize > 0);
        Assert.Equal(100, result.FrameworksSize);
        Assert.Equal(50, result.PlugInsSize);
    }

    [Fact]
    public void 图标候选_按来源分类并排除启动图()
    {
        using var ws = new TestWorkspace("deep");
        var path = BuildIpa(ws);

        var result = IpaDeepReader.Read(path);
        var names = result.IconCandidates.Select(c => c.EntryName).ToList();

        Assert.Contains("iTunesArtwork", names);                                     // 商店图标
        Assert.Contains("Payload/Deep.app/Icon-72.png", names);                      // plist 点名
        Assert.Contains("Payload/Deep.app/Icon-144.png", names);                     // plist 点名
        Assert.Contains("Payload/Deep.app/AppIcon60x60@3x.png", names);              // 名字含 icon
        Assert.DoesNotContain("Payload/Deep.app/Default-Landscape@2x.png", names);   // 启动图必须排除
        Assert.DoesNotContain("Payload/Deep.app/icon_background.png", names);        // 非方形素材排除

        var artwork = result.IconCandidates.Single(c => c.EntryName == "iTunesArtwork");
        Assert.Equal(IconSource.StoreArtwork, artwork.Source);
        Assert.Equal(512, artwork.Width);

        var cgbi = result.IconCandidates.Single(c => c.EntryName == "Payload/Deep.app/Icon-144.png");
        Assert.True(cgbi.IsCgbi);
        Assert.Equal(IconSource.PlistNamed, cgbi.Source);
    }

    [Fact]
    public void 选图标_按目标尺寸取最近够用_并解码还原CgBI()
    {
        using var ws = new TestWorkspace("deep");
        var path = BuildIpa(ws, cgbiIcon: true);
        var result = IpaDeepReader.Read(path);

        var candidates = result.IconCandidates
            .Select(c => new IconCandidate(c.EntryName, c.Width, c.Height, c.IsCgbi, c.Source))
            .ToList();
        var chosen = IconSelector.Select(candidates, new IconRequest(DisplayPx: 96, DpiScale: 1.0));

        Assert.NotNull(chosen);
        Assert.Equal(IconSource.StoreArtwork, chosen!.Value.Source);   // 商店图标优先

        var meta = result.IconCandidates.Single(c => c.EntryName == chosen.Value.EntryName);
        var loaded = IpaDeepReader.LoadIcon(path, meta, bucketPx: 96, fallbackDecoder: null, out var error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(96, loaded!.Width);
        Assert.Equal(96, loaded.Height);
        Assert.Matches("^[0-9a-f]{40}$", loaded.IconKey);   // sha1；扫描器再拼 "-<档位>" 作为缓存键

        // 产物应是标准 PNG，且能再解码
        var decoded = PngCodec.Decode(loaded.PngBytes);
        Assert.Equal(96, decoded.Width);
        Assert.False(PngCodec.IsCgbi(loaded.PngBytes));
    }

    [Fact]
    public void 坏包_返回Error而不是抛异常()
    {
        using var ws = new TestWorkspace("deep");
        var path = ws.AddFile("bad.ipa", 100);
        var result = IpaDeepReader.Read(path);
        Assert.NotNull(result.Error);
        Assert.Contains("zip", result.Error);
    }

    [Fact]
    public void 调色板图标_自写解码不支持时走兜底解码器()
    {
        using var ws = new TestWorkspace("deep");
        var path = ws.Full("Palette.ipa");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string entry, byte[] bytes)
            {
                var e = zip.CreateEntry(entry, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(bytes);
            }

            Add("Payload/Pal.app/Info.plist", Encoding.UTF8.GetBytes(InfoPlistXml
                .Replace("Deep.app", "Pal.app")
                .Replace("<string>Deep</string>", "<string>Pal</string>")));
            Add("Payload/Pal.app/Pal", new byte[8]);
            Add("Payload/Pal.app/Icon-144.png", MakePalettePng());
        }

        var result = IpaDeepReader.Read(path);
        Assert.Null(result.Error);
        var meta = result.IconCandidates.Single(c => c.EntryName == "Payload/Pal.app/Icon-144.png");

        // 不给兜底：应当失败（不算解析失败），错误说明是 PNG 变体
        var withoutFallback = IpaDeepReader.LoadIcon(path, meta, 64, null, out var error1);
        Assert.Null(withoutFallback);
        Assert.Contains("PNG", error1);

        // 给兜底：能出 64 档的标准 PNG
        var fake = new PngCodec.DecodedPng(144, 144, MakeSolidRgba(144, 144));
        var withFallback = IpaDeepReader.LoadIcon(path, meta, 64, _ => fake, out var error2);
        Assert.Null(error2);
        Assert.NotNull(withFallback);
        Assert.Equal(64, withFallback!.Width);
        Assert.Matches("^[0-9a-f]{40}$", withFallback.IconKey);
    }

    [Fact]
    public void 反转fat64魔数也能解出架构()
    {
        // 0xBFBAFECA = fat64 字节序反转；1 个 arm64 切片，每项 32 字节
        var header = new byte[8 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0xBFBAFECA);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 0x0100000C);   // CPU_TYPE_ARM64
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 0);           // arm64

        Assert.Equal("arm64", MachO.ReadArch(header));
    }

    [Fact]
    public void JPEG头里EXIF很长时也能读到尺寸()
    {
        // FFD8 + APP1（长度 8000）+ SOF0
        var app1 = new byte[2 + 8000];   // marker 2 + length 2 + payload 7998
        app1[0] = 0xFF;
        app1[1] = 0xE1;
        app1[2] = (byte)((8000 >> 8) & 0xFF);   // 段长度字段 = payload + 2
        app1[3] = (byte)(8000 & 0xFF);

        var sof = new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x00, 0x04, 0x00, 0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 };
        // SOF0: height=0x0200(512), width=0x0400(1024)
        var jpeg = new byte[2 + app1.Length + sof.Length];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        app1.CopyTo(jpeg, 2);
        sof.CopyTo(jpeg, 2 + app1.Length);

        var (width, height, isCgbi, isJpeg) = ImageProbe.Probe(jpeg);
        Assert.True(isJpeg);
        Assert.False(isCgbi);
        Assert.Equal(1024, width);
        Assert.Equal(512, height);
    }

    private static byte[] MakeSolidRgba(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 10;
            rgba[i + 1] = 20;
            rgba[i + 2] = 30;
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    private static byte[] MakePalettePng()
    {
        const int size = 64;
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), size);
        ihdr[8] = 8;
        ihdr[9] = 3;
        var rows = new byte[size * (1 + size)];
        for (var y = 0; y < size; y++)
        {
            rows[y * (size + 1)] = 0;   // filter 0，后面全是索引 0
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(rows);
            }

            compressed = ms.ToArray();
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "PLTE", new byte[] { 255, 0, 0 });
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    [Fact]
    public void 适配证据_记录启动图与图标实际尺寸()
    {
        using var ws = new TestWorkspace("deep-evidence");
        var path = BuildIpa(ws);

        var result = IpaDeepReader.Read(path);
        var evidence = LaunchEvidenceCodec.Parse(result.LaunchImageJson);

        Assert.NotNull(evidence);
        Assert.Contains(evidence!.L, s => s is [256, 256]);          // Default-Landscape@2x.png
        Assert.Contains(72, evidence.I);                              // Icon-72.png
        Assert.Contains(144, evidence.I);                             // Icon-144.png
        Assert.Contains(180, evidence.I);                             // AppIcon60x60@3x.png
        Assert.DoesNotContain(300, evidence.I);                       // icon_background 非方形，不是图标
    }

    [Fact]
    public void 畸形PNG块长不溢出_且单张坏图不把有效IPA标Failed()
    {
        // P2-5: 构造 chunk length = 0xFFFFFFFF 的伪 PNG 头，旧版 int 溢出为负数会抛 ArgumentOutOfRangeException
        var badPng = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(badPng, 0);
        BinaryPrimitives.WriteUInt32BigEndian(badPng.AsSpan(8, 4), 0xFFFFFFFFu);
        "CgBI"u8.CopyTo(badPng.AsSpan(12, 4));
        Assert.Equal((0, 0, true, false), ImageProbe.Probe(badPng));

        // 截断 IHDR (< 13 字节) 不越界
        var shortIhdr = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(shortIhdr, 0);
        BinaryPrimitives.WriteUInt32BigEndian(shortIhdr.AsSpan(8, 4), 4u);
        "IHDR"u8.CopyTo(shortIhdr.AsSpan(12, 4));
        Assert.Equal((0, 0, false, false), ImageProbe.Probe(shortIhdr));

        // 含坏图的有效 IPA 仍应解析成功（Error == null）
        using var ws = new TestWorkspace("deep-bad-img");
        var path = ws.Full("WithBadImg.ipa");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string entry, byte[] bytes)
            {
                var e = zip.CreateEntry(entry, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(bytes);
            }

            Add("Payload/Deep.app/Info.plist", Encoding.UTF8.GetBytes(InfoPlistXml));
            Add("Payload/Deep.app/Deep", new byte[8]);
            Add("Payload/Deep.app/Icon-72.png", badPng);
            Add("Payload/Deep.app/Icon-144.png", Png(144, 10, 200, 10));
        }

        var result = IpaDeepReader.Read(path);
        Assert.Null(result.Error);
        Assert.Equal("com.example.deep", result.BundleId);
        Assert.Single(result.IconCandidates);
    }
}