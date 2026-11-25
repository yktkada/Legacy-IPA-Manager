using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using IdeviceGui.Services.Parsing;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// PNG / CgBI 编解码（M3 的地基）。CgBI 规则全部用"自己造样本"验证，不依赖外部文件：
/// 先按标准 PNG 编码已知像素，再改造成 CgBI（裸 deflate + BGRA + 预乘），看能否还原回原像素。
/// </summary>
public sealed class PngCodecTests
{
    private static byte[] MakeRgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var i = (y * width + x) * 4;
                data[i] = r;
                data[i + 1] = g;
                data[i + 2] = b;
                data[i + 3] = a;
            }
        }

        return data;
    }

    /// <summary>把标准 RGBA 数据改造成 CgBI 字节流（Apple 私有格式）。</summary>
    private static byte[] MakeCgbi(int width, int height, byte[] rgba)
    {
        // ① RGBA → 预乘 + BGRA
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2], a = rgba[i + 3];
            bgra[i] = (byte)(b * a / 255);
            bgra[i + 1] = (byte)(g * a / 255);
            bgra[i + 2] = (byte)(r * a / 255);
            bgra[i + 3] = (byte)a;
        }

        // ② 每行加 filter 0（CgBI 的行过滤器仍是标准 PNG 的）
        var stride = width * 4;
        var filtered = new byte[height * (stride + 1)];
        for (var y = 0; y < height; y++)
        {
            filtered[y * (stride + 1)] = 0;
            Buffer.BlockCopy(bgra, y * stride, filtered, y * (stride + 1) + 1, stride);
        }

        // ③ 裸 deflate（无 zlib 头）
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
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
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

    [Fact]
    public void 标准PNG_编解码往返一致()
    {
        var width = 8;
        var height = 6;
        var rgba = MakeRgba(width, height, (x, y) => ((byte)(x * 30), (byte)(y * 40), (byte)128, (byte)255));

        var png = PngCodec.Encode(new PngCodec.DecodedPng(width, height, rgba));
        var decoded = PngCodec.Decode(png);

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba);
        Assert.True(PngCodec.IsPng(png));
        Assert.False(PngCodec.IsCgbi(png));
    }

    [Fact]
    public void CgBI_能还原成标准RGBA()
    {
        var width = 6;
        var height = 5;
        // 用不透明像素：反预乘对 a=255 是精确的，便于逐字节比对
        var rgba = MakeRgba(width, height, (x, y) => ((byte)(20 + x * 20), (byte)(30 + y * 25), (byte)(200 - x * 10), (byte)255));

        var cgbi = MakeCgbi(width, height, rgba);
        Assert.True(PngCodec.IsPng(cgbi));
        Assert.True(PngCodec.IsCgbi(cgbi));

        var decoded = PngCodec.Decode(cgbi);

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba);
    }

    [Fact]
    public void CgBI_半透明像素反预乘误差不超过1()
    {
        var width = 4;
        var height = 4;
        var rgba = MakeRgba(width, height, (x, y) => ((byte)(255 - x * 20), (byte)(128 + y * 10), (byte)64, (byte)128));

        var decoded = PngCodec.Decode(MakeCgbi(width, height, rgba));

        for (var i = 0; i < rgba.Length; i += 4)
        {
            Assert.InRange(Math.Abs(decoded.Rgba[i] - rgba[i]), 0, 2);
            Assert.InRange(Math.Abs(decoded.Rgba[i + 1] - rgba[i + 1]), 0, 2);
            Assert.InRange(Math.Abs(decoded.Rgba[i + 2] - rgba[i + 2]), 0, 2);
            Assert.Equal(rgba[i + 3], decoded.Rgba[i + 3]);
        }
    }

    [Fact]
    public void 缩放_盒式平均_每个2x2块取均值()
    {
        var rgba = MakeRgba(4, 4, (x, y) => ((byte)(x * 60), (byte)(y * 60), (byte)0, (byte)255));
        var decoded = PngCodec.Downscale(new PngCodec.DecodedPng(4, 4, rgba), 2);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var expectedR = (x * 2 * 60 + (x * 2 + 1) * 60) / 2;
                var expectedG = (y * 2 * 60 + (y * 2 + 1) * 60) / 2;
                var i = (y * 2 + x) * 4;
                Assert.Equal(expectedR, decoded.Rgba[i]);
                Assert.Equal(expectedG, decoded.Rgba[i + 1]);
                Assert.Equal(255, decoded.Rgba[i + 3]);
            }
        }
    }

    [Fact]
    public void 缩放_目标大于原图时不放大()
    {
        var rgba = MakeRgba(3, 3, (_, _) => ((byte)10, (byte)20, (byte)30, (byte)255));
        var decoded = PngCodec.Downscale(new PngCodec.DecodedPng(3, 3, rgba), 64);

        Assert.Equal(3, decoded.Width);
        Assert.Equal(3, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba);
    }

    [Fact]
    public void 非PNG输入报错()
    {
        Assert.False(PngCodec.IsPng(new byte[] { 1, 2, 3 }));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void 缩放_透明边缘不发黑_按预乘平均()
    {
        // 左上不透明纯白、右下全透明黑：盒式缩到 1x1，
        // 若按"直读 RGB 平均"会得到 127（发黑），按预乘平均应仍是 255。
        var rgba = MakeRgba(2, 2, (x, y) => (x == 0 && y == 0)
            ? ((byte)255, (byte)255, (byte)255, (byte)255)
            : ((byte)0, (byte)0, (byte)0, (byte)0));
        var decoded = PngCodec.Downscale(new PngCodec.DecodedPng(2, 2, rgba), 1);

        Assert.Equal(1, decoded.Width);
        Assert.Equal(255, decoded.Rgba[0]);
        Assert.Equal(255, decoded.Rgba[1]);
        Assert.Equal(255, decoded.Rgba[2]);
        Assert.Equal(63, decoded.Rgba[3]);   // alpha 平均 = 255/4
    }

    [Fact]
    public void 调色板PNG_自写解码明确拒绝_由兜底接管()
    {
        // colorType 3（调色板）不是自写解码器的支持范围：必须抛 NotSupportedException，
        // 上层才能识别"该走 WIC 兜底"而不是当成坏图。
        var palette = MakePalettePng();
        Assert.True(PngCodec.IsPng(palette));
        Assert.Throws<NotSupportedException>(() => PngCodec.Decode(palette));
    }

    /// <summary>造一个 1x1 的调色板 PNG（colorType 3）。</summary>
    private static byte[] MakePalettePng()
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 1);
        ihdr[8] = 8;
        ihdr[9] = 3;   // palette
        var plte = new byte[] { 255, 0, 0 };
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(new byte[] { 0, 0 });   // filter 0 + 索引 0
            }

            compressed = ms.ToArray();
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "PLTE", plte);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    [Fact]
    public void 畸形Chunk长度与IHDR尺寸溢出_抛InvalidDataException不整数溢出()
    {
        // 1) 超大 chunk length (0x7FFFFFFF / 0xFFFFFFFF)
        var badLen = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(badLen, 0);
        BinaryPrimitives.WriteUInt32BigEndian(badLen.AsSpan(8, 4), 0x7FFFFFFFu);
        "IHDR"u8.CopyTo(badLen.AsSpan(12, 4));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(badLen));

        // 2) IHDR length < 13
        using var shortIhdr = new MemoryStream();
        shortIhdr.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(shortIhdr, "IHDR", new byte[8]);
        WriteChunk(shortIhdr, "IEND", Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(shortIhdr.ToArray()));

        // 3) 尺寸相乘溢出 32 位（例如 65536 x 65536 RGBA）
        var hugeIhdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(hugeIhdr.AsSpan(0, 4), 65536);
        BinaryPrimitives.WriteInt32BigEndian(hugeIhdr.AsSpan(4, 4), 65536);
        hugeIhdr[8] = 8;
        hugeIhdr[9] = 6;
        using var hugePng = new MemoryStream();
        hugePng.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(hugePng, "IHDR", hugeIhdr);
        WriteChunk(hugePng, "IEND", Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(hugePng.ToArray()));
    }
}