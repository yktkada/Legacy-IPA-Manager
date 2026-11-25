using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using IdeviceGui;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// Plist 解析器的 golden / 边界测试。
///
/// fixture 来源：<c>fixtures\&lt;slug&gt;\Info.plist</c>（生成脚本 <c>FixtureGeneration\extract_fixtures.ps1</c>，
/// 6 个测试 IPA 的原字节，XML / bplist00 两种格式都保留原样）。
///
/// 边界用例断言的是 2026-10-29 的**当前行为**（B6 约定：先跑一遍看实际返回什么，把实际行为写成断言；
/// 不为了让测试好看去改主程序）：
/// - 空字节 / 非 plist 垃圾字节 → 走 XML 分支的 <see cref="System.Xml.Linq.XDocument.Parse(string)"/>，抛 <see cref="XmlException"/>；
/// - 截断的 bplist → 不抛异常，按截断后剩下的"垃圾 trailer"尽力解读，返回 null 或非字典值；
/// - 长度不足 32 字节的 bplist（Magic 完整但 trailer 不完整）→ 返回 null。
/// 如果 M3/M7 之后解析器改成"坏输入返回 null"，这些用例会失败——那是行为变更，届时按新实际行为更新断言。
/// </summary>
public class PlistTests
{
    /// <summary>
    /// 定位 fixtures 目录：从测试程序集输出目录（…\tests\IdeviceGui.Tests\bin\Release\net10.0-windows）
    /// 逐级向上找 <c>fixtures</c>，不依赖 csproj 复制规则（B6 约定不改 csproj）。
    /// </summary>
    private static string FixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"找不到 fixtures 目录（从 {AppContext.BaseDirectory} 向上查找）。" +
            "请确认 tests\\IdeviceGui.Tests\\fixtures 存在，或运行 FixtureGeneration\\extract_fixtures.ps1 重新生成。");
    }

    private static byte[] ReadFixture(string slug, string fileName) =>
        File.ReadAllBytes(Path.Combine(FixturesRoot(), slug, fileName));

    private static Dictionary<string, object?> ParseDict(byte[] plistBytes) =>
        Assert.IsType<Dictionary<string, object?>>(Plist.Parse(plistBytes));

    [Fact]
    public void XmlFixture_ParsesStringArrayBool_CurrentBehavior()
    {
        // doom 的 Info.plist 是 XML（实测 6 个样本里 doom / memoryinfo 两个是 XML）。
        var dict = ParseDict(ReadFixture("doom", "Info.plist"));

        Assert.Equal("com.escalationstudios.ResurrectionDoom", Assert.IsType<string>(dict["CFBundleIdentifier"]));
        Assert.Equal("1.0.1", Assert.IsType<string>(dict["CFBundleVersion"]));
        Assert.Equal("Resurrection", Assert.IsType<string>(dict["CFBundleDisplayName"]));

        // 数组：<array><string>iPhoneOS</string></array>
        var platforms = Assert.IsType<List<object?>>(dict["CFBundleSupportedPlatforms"]);
        Assert.Equal("iPhoneOS", Assert.IsType<string>(Assert.Single(platforms)));

        // 布尔：<true/>
        Assert.True(Assert.IsType<bool>(dict["LSRequiresIPhoneOS"]));
        Assert.True(Assert.IsType<bool>(dict["UIPrerenderedIcon"]));
    }

    [Fact]
    public void XmlCrafted_ParsesNestedDictionaryArrayIntegerBool_CurrentBehavior()
    {
        // doom 的 XML fixture 里没有 <integer>，整数覆盖用现造的 XML（任务 B6 允许 XML 字符串现造）。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Int</key><integer>42</integer>
              <key>Bool</key><true/>
              <key>Array</key><array><integer>1</integer><string>x</string></array>
              <key>Nested</key><dict><key>k</key><string>v</string></dict>
            </dict>
            </plist>
            """;

        var dict = ParseDict(Encoding.UTF8.GetBytes(xml));

        Assert.Equal(42L, Assert.IsType<long>(dict["Int"]));
        Assert.True(Assert.IsType<bool>(dict["Bool"]));

        var array = Assert.IsType<List<object?>>(dict["Array"]);
        Assert.Equal(1L, Assert.IsType<long>(array[0]));
        Assert.Equal("x", Assert.IsType<string>(array[1]));

        var nested = Assert.IsType<Dictionary<string, object?>>(dict["Nested"]);
        Assert.Equal("v", Assert.IsType<string>(nested["k"]));
    }

    [Fact]
    public void BinaryFixture_ParsesSameFields_CurrentBehavior()
    {
        // flappy 的 Info.plist 是 bplist00（实测 6 个样本里 4 个二进制）。
        var bytes = ReadFixture("flappy", "Info.plist");
        Assert.True(bytes.Length > 6);
        Assert.Equal("bplist", Encoding.ASCII.GetString(bytes, 0, 6));

        var dict = ParseDict(bytes);

        Assert.Equal("com.dotgears.flap", Assert.IsType<string>(dict["CFBundleIdentifier"]));
        Assert.Equal("1.2", Assert.IsType<string>(dict["CFBundleShortVersionString"]));

        // 整数数组：UIDeviceFamily = [1, 2]
        var families = Assert.IsType<List<object?>>(dict["UIDeviceFamily"]);
        Assert.Equal(new object?[] { 1L, 2L }, families);

        // 嵌套字典：CFBundleIcons → CFBundlePrimaryIcon → CFBundleIconFiles
        var icons = Assert.IsType<Dictionary<string, object?>>(dict["CFBundleIcons"]);
        var primary = Assert.IsType<Dictionary<string, object?>>(icons["CFBundlePrimaryIcon"]);
        var iconFiles = Assert.IsType<List<object?>>(primary["CFBundleIconFiles"]);
        Assert.NotEmpty(iconFiles);

        Assert.True(Assert.IsType<bool>(dict["LSRequiresIPhoneOS"]));
    }

    [Fact]
    public void Parse_EmptyBytes_ThrowsXmlException_CurrentBehavior()
    {
        Assert.Throws<XmlException>(() => Plist.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Parse_GarbageAsciiBytes_ThrowsXmlException_CurrentBehavior()
    {
        var garbage = Encoding.ASCII.GetBytes("this is not a plist at all");

        Assert.Throws<XmlException>(() => Plist.Parse(garbage));
    }

    [Fact]
    public void Parse_GarbageBinaryBytes_ThrowsXmlException_CurrentBehavior()
    {
        var garbage = new byte[] { 0xFF, 0xFE, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        Assert.Throws<XmlException>(() => Plist.Parse(garbage));
    }

    [Fact]
    public void Parse_TruncatedBplist_DoesNotThrow_ReturnsNonDictionary_CurrentBehavior()
    {
        var bytes = ReadFixture("flappy", "Info.plist");
        var truncated = bytes[..(bytes.Length / 2)];

        // 当前行为：截断后 trailer 是垃圾字节，解析器按它尽力解读，不抛异常，但结果不是字典。
        var result = Plist.Parse(truncated);

        Assert.IsNotType<Dictionary<string, object?>>(result);
    }

    [Fact]
    public void Parse_BplistShorterThan32Bytes_ReturnsNull_CurrentBehavior()
    {
        var bytes = ReadFixture("flappy", "Info.plist");
        var tooShort = bytes[..24];

        Assert.Null(Plist.Parse(tooShort));
    }

    [Fact]
    public void Parse_MalformedBplistHeadersAndCounts_ReturnNullWithoutThrowingOrOom()
    {
        byte[] BuildTrailer(byte offsetIntSize, byte objectRefSize, ulong numObjects, ulong topObject, ulong offsetTableOffset)
        {
            var data = new byte[48];
            "bplist00"u8.CopyTo(data);
            var t = data.Length - 32;
            data[t + 6] = offsetIntSize;
            data[t + 7] = objectRefSize;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(t + 8, 8), numObjects);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(t + 16, 8), topObject);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(t + 24, 8), offsetTableOffset);
            return data;
        }

        // 无效 offsetIntSize / objectRefSize
        Assert.Null(Plist.Parse(BuildTrailer(0, 1, 1, 0, 10)));
        Assert.Null(Plist.Parse(BuildTrailer(3, 1, 1, 0, 10)));
        Assert.Null(Plist.Parse(BuildTrailer(1, 0, 1, 0, 10)));
        Assert.Null(Plist.Parse(BuildTrailer(1, 5, 1, 0, 10)));

        // 超大 numObjects / topObject 越界 / offsetTableOffset 越界
        Assert.Null(Plist.Parse(BuildTrailer(1, 1, ulong.MaxValue, 0, 10)));
        Assert.Null(Plist.Parse(BuildTrailer(1, 1, 1, 5, 10)));
        Assert.Null(Plist.Parse(BuildTrailer(1, 1, 1, 0, 4)));
        Assert.Null(Plist.Parse(BuildTrailer(1, 1, 1, 0, 100)));
        Assert.Null(Plist.Parse(BuildTrailer(1, 1, 4, 0, 15)));   // 15 + 4 > 16 (len - 32)

        // 合法 trailer，但对象本身为 0x4F（扩展长度）且宣称超大字节长度
        var hugeCount = BuildTrailer(1, 1, 1, 0, 15);
        hugeCount[8] = 0x4F;    // data, extended length
        hugeCount[9] = 0x12;    // 4-byte int length
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugeCount.AsSpan(10, 4), 0x7FFF0000);
        hugeCount[15] = 8;      // offset[0] = 8
        Assert.Null(Plist.Parse(hugeCount));
    }
}
