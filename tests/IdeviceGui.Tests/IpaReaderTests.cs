using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IdeviceGui;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// IpaReader 的 golden 测试。
///
/// 每个 fixture 目录（<c>fixtures\&lt;slug&gt;\</c>）都带 <c>expected.json</c>：由生成脚本从
/// IPA 里提取并记录（bundleId / version / name / minOs / appDirectory / plistFormat /
/// executableName / arch / hasCgbi / fileSize）。本文件的断言只覆盖本轮（B6）能判定的字段；
/// <b>arch / hasCgbi 是给 M3 留的，本轮不断言</b>。
///
/// 关键：直接用 fixture 里的 Info.plist 原字节调 internal <c>IpaReader.ParseInfoPlist</c>，
/// 不依赖磁盘上的 IPA（CI/别的机器上没有 test scripts 也能跑）。
/// </summary>
public class IpaReaderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>定位 fixtures 目录：从测试输出目录向上找（不依赖 csproj 复制规则）。</summary>
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

    /// <summary>6 个 fixture 目录名（doom / flappy / gyroblox / lineart / memoryinfo / ziggy）。
    /// fixtures 下还有非 golden 包目录（如 readback\ —— 真机 list --xml 原文），这里按"含 expected.json"过滤。</summary>
    public static TheoryData<string> FixtureSlugs()
    {
        var data = new TheoryData<string>();
        var root = FixturesRoot();
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(dir, "expected.json")))
                data.Add(Path.GetFileName(dir));
        }

        return data;
    }

    private static ExpectedFixture ReadExpected(string slug)
    {
        var path = Path.Combine(FixturesRoot(), slug, "expected.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ExpectedFixture>(json, JsonOptions)
               ?? throw new InvalidOperationException("expected.json 解析失败：" + path);
    }

    [Theory]
    [MemberData(nameof(FixtureSlugs))]
    public void ParseInfoPlist_MatchesGolden(string slug)
    {
        var expected = ReadExpected(slug);
        var plistPath = Path.Combine(FixturesRoot(), slug, "Info.plist");
        var plistBytes = File.ReadAllBytes(plistPath);

        // fixture 格式与 expected.json 记录一致（xml / bplist00 原样保存）
        var looksBinary = plistBytes.Length >= 6 &&
                          Encoding.ASCII.GetString(plistBytes, 0, 6) == "bplist";
        Assert.Equal(expected.plistFormat == "bplist", looksBinary);

        var info = IpaReader.ParseInfoPlist(plistPath, plistBytes, expected.appDirectory);

        Assert.Null(info.Error);
        Assert.Equal(plistPath, info.Path);
        Assert.Equal((long)plistBytes.Length, info.Size);
        Assert.Equal(expected.bundleId, info.BundleId);
        Assert.Equal(expected.version, info.Version);
        Assert.Equal(expected.name, info.Name);
        Assert.Equal(expected.minOs, info.MinOs);
        Assert.Equal(expected.appDirectory, info.AppDirectory);
    }

    [Fact]
    public void ParseInfoPlist_Version_FallsBackToBuildVersion()
    {
        var info = IpaReader.ParseInfoPlist(
            "fixture.ipa",
            XmlPlist(
                "<key>CFBundleIdentifier</key><string>com.example.fallback</string>" +
                "<key>CFBundleVersion</key><string>7</string>"),
            "Example.app");

        Assert.Null(info.Error);
        Assert.Equal("7", info.Version);   // 没有 CFBundleShortVersionString → 回退 CFBundleVersion
    }

    [Fact]
    public void ParseInfoPlist_Version_PrefersShortVersion()
    {
        var info = IpaReader.ParseInfoPlist(
            "fixture.ipa",
            XmlPlist(
                "<key>CFBundleShortVersionString</key><string>2.0</string>" +
                "<key>CFBundleVersion</key><string>200</string>"),
            "Example.app");

        Assert.Null(info.Error);
        Assert.Equal("2.0", info.Version);
    }

    [Fact]
    public void ParseInfoPlist_Name_FallsBackToBundleName()
    {
        var info = IpaReader.ParseInfoPlist(
            "fixture.ipa",
            XmlPlist(
                "<key>CFBundleIdentifier</key><string>com.example.name</string>" +
                "<key>CFBundleName</key><string>OnlyName</string>"),
            "Example.app");

        Assert.Null(info.Error);
        Assert.Equal("OnlyName", info.Name);   // 无 CFBundleDisplayName → 用 CFBundleName
    }

    [Fact]
    public void ParseInfoPlist_Name_FallsBackToAppDirectory()
    {
        var info = IpaReader.ParseInfoPlist(
            "fixture.ipa",
            XmlPlist("<key>CFBundleIdentifier</key><string>com.example.noname</string>"),
            "Fallback.app");

        Assert.Null(info.Error);
        Assert.Equal("Fallback.app", info.Name);   // DisplayName / BundleName 都没有 → 用 .app 目录名
    }

    [Fact]
    public void Read_MissingFile_ReturnsErrorWithoutThrowing()
    {
        // 不用 Path.GetTempPath()：沙箱禁写用户 TEMP，测试临时目录统一放程序目录下（见 TestWorkspace）
        using var ws = new TestWorkspace();
        var path = ws.Full("idevicegui-missing-" + Guid.NewGuid().ToString("N") + ".ipa");

        var info = IpaReader.Read(path);   // 不抛异常，错误经 Error 返回

        Assert.False(string.IsNullOrWhiteSpace(info.Error));
        Assert.Equal(path, info.Path);
        Assert.Equal(0L, info.Size);
    }

    private static byte[] XmlPlist(string dictBody) => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
        "<plist version=\"1.0\"><dict>" + dictBody + "</dict></plist>");

    /// <summary>expected.json 的映射（字段名与生成脚本一致）。</summary>
    private sealed class ExpectedFixture
    {
        public string bundleId { get; init; } = "";

        public string version { get; init; } = "";

        public string name { get; init; } = "";

        public string minOs { get; init; } = "";

        public string appDirectory { get; init; } = "";

        public string plistFormat { get; init; } = "";

        public string executableName { get; init; } = "";

        public string arch { get; init; } = "";

        public bool hasCgbi { get; init; }

        public long fileSize { get; init; }

        public string sourceIpa { get; init; } = "";
    }
}
