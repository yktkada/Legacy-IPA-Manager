using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// fixtures 的"数据契约"测试（R3 新增）。
///
/// 背景：B6 的 6 包 golden 里有 12 个文件（executable-head.bin / icon-slice.bin）当时没有任何测试引用，
/// 等于死数据；expected.json 里的 arch / hasCgbi 也只是记录、不断言。本文件让这些数据先变成
/// **可核对的契约**——不测生产代码（Mach-O / CgBI 解析在 M3 才落地），只测：
///   ① 每个 fixture 文件的 SHA256 与 expected.json 记录一致（换素材没更新期望、文件被手改都会红）；
///   ② executable-head.bin 的前 4 字节魔数与 expected.json 记录的 machoMagic 一致；
///   ③ icon-slice.bin 里是否存在 ASCII "CgBI"，与 expected.json 的 hasCgbi 一致。
///
/// M3 落地后：把 ②③ 换成"生产解析器的输出 vs expected.json"，期望值不用重做；
/// 本轮有意不断言 arch（archSource 已注明它来自脚本内独立实现，不能给未来实现背书）。
/// </summary>
public sealed class FixtureIntegrityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] FixtureFiles = { "Info.plist", "executable-head.bin", "icon-slice.bin" };

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
            $"找不到 fixtures 目录（从 {AppContext.BaseDirectory} 向上查找）。");
    }

    /// <summary>与生成脚本约定一致：fixtures 下含 expected.json 的子目录就是一个包。</summary>
    public static TheoryData<string> FixtureSlugs()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(FixturesRoot()).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(dir, "expected.json")))
                data.Add(Path.GetFileName(dir));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureSlugs))]
    public void FixtureFileSha256_MatchesExpected(string slug)
    {
        var expected = ReadExpected(slug);
        var dir = Path.Combine(FixturesRoot(), slug);

        foreach (var name in FixtureFiles)
        {
            Assert.True(expected.filesSha256.ContainsKey(name),
                $"{slug}/expected.json 缺 filesSha256[{name}]；请重跑 FixtureGeneration\\extract_fixtures.ps1。");

            var bytes = File.ReadAllBytes(Path.Combine(dir, name));
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expected.filesSha256[name], actual);
        }

        // 源 IPA 指纹必须存在且是 64 位十六进制（素材可追溯；IPA 本身不入库）
        Assert.Matches("^[0-9a-f]{64}$", expected.sourceIpaSha256);
        Assert.False(string.IsNullOrWhiteSpace(expected.sourceIpa));
        Assert.False(string.IsNullOrWhiteSpace(expected.generatedBy));
    }

    [Theory]
    [MemberData(nameof(FixtureSlugs))]
    public void MachOMagic_MatchesExecutableHead(string slug)
    {
        var expected = ReadExpected(slug);
        var head = File.ReadAllBytes(Path.Combine(FixturesRoot(), slug, "executable-head.bin"));

        Assert.True(head.Length >= 4, $"{slug}/executable-head.bin 太短（{head.Length} 字节），生成脚本应写前 4096 字节。");
        var actual = $"{head[0]:X2}{head[1]:X2}{head[2]:X2}{head[3]:X2}";
        Assert.Equal(expected.machoMagic, actual);

        // 6 包实测分布：4 个 fat(CAFEBABE) + 2 个反转魔数thin(CEFAEDFE)
        Assert.Contains(actual, new[] { "CAFEBABE", "CEFAEDFE", "FEEDFACE", "CAFEBABF", "BEBAFECA", "FEEDFACF", "CFFAEDFE" });
    }

    [Theory]
    [MemberData(nameof(FixtureSlugs))]
    public void CgbiFlag_MatchesIconSlice(string slug)
    {
        var expected = ReadExpected(slug);
        var icon = File.ReadAllBytes(Path.Combine(FixturesRoot(), slug, "icon-slice.bin"));

        var hasCgbiChunk = IndexOf(icon, Encoding.ASCII.GetBytes("CgBI")) >= 0;
        Assert.Equal(expected.hasCgbi, hasCgbiChunk);

        // hasCgbi=true 的切片必须是 PNG（前 8 字节签名），否则"切片"取错了文件
        if (expected.hasCgbi)
        {
            Assert.True(icon.Length >= 8, $"{slug}/icon-slice.bin 太短。");
            var signature = Convert.ToHexString(icon.Take(8).ToArray());
            Assert.Equal("89504E470D0A1A0A", signature);
        }
    }

    private static ExpectedFixture ReadExpected(string slug)
    {
        var path = Path.Combine(FixturesRoot(), slug, "expected.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ExpectedFixture>(json, JsonOptions)
               ?? throw new InvalidOperationException("expected.json 解析失败：" + path);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }

            if (hit) return i;
        }

        return -1;
    }

    /// <summary>expected.json 的映射（与生成脚本字段一致；R3 新增字段用 init 默认值兼容旧文件）。</summary>
    private sealed class ExpectedFixture
    {
        public string machoMagic { get; init; } = "";

        public bool hasCgbi { get; init; }

        public string sourceIpa { get; init; } = "";

        public string sourceIpaSha256 { get; init; } = "";

        public string archSource { get; init; } = "";

        public string generatedBy { get; init; } = "";

        public Dictionary<string, string> filesSha256 { get; init; } = new(StringComparer.Ordinal);
    }
}
