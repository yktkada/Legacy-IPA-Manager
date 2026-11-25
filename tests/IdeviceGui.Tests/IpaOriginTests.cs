using System.IO.Compression;
using System.Text;
using IdeviceGui.Resources;
using IdeviceGui.Services.Parsing;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>IPA 来源三类（2026-11-05）：App Store 原始包 / 重签名包 / 解密包。</summary>
public sealed class IpaOriginTests
{
    private const string InfoPlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleIdentifier</key><string>com.test.origin</string>
          <key>CFBundleShortVersionString</key><string>1.0</string>
          <key>CFBundleVersion</key><string>1</string>
          <key>CFBundleExecutable</key><string>Origin</string>
          <key>MinimumOSVersion</key><string>4.0</string>
        </dict></plist>
        """;

    private static string Build(TestWorkspace ws, string name, bool scInfo, bool mobileProvision)
    {
        var path = ws.Full(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, byte[] bytes)
        {
            var e = zip.CreateEntry(entry, CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(bytes);
        }

        Add("Payload/Origin.app/Info.plist", Encoding.UTF8.GetBytes(InfoPlist));
        Add("Payload/Origin.app/Origin", new byte[8]);
        if (scInfo) Add("Payload/Origin.app/SC_Info/Origin.sinf", new byte[8]);
        if (mobileProvision) Add("Payload/Origin.app/embedded.mobileprovision", new byte[8]);
        return path;
    }

    [Fact]
    public void SCInfo存在_判AppStore原始包()
    {
        using var ws = new TestWorkspace("ipa-origin-appstore");
        var deep = IpaDeepReader.Read(Build(ws, "a.ipa", scInfo: true, mobileProvision: false));
        Assert.Equal(PackageOrigin.Original, deep.OriginKind);
        Assert.Equal(Strings.Filter_Origin_AppStore, PackageOrigin.DisplayText(deep.OriginKind));
    }

    [Fact]
    public void 描述文件存在_判重签名包_优先于SCInfo()
    {
        using var ws = new TestWorkspace("ipa-origin-resigned");
        var deep = IpaDeepReader.Read(Build(ws, "b.ipa", scInfo: true, mobileProvision: true));
        Assert.Equal(PackageOrigin.Resigned, deep.OriginKind);
        Assert.Equal(Strings.Filter_Origin_Resigned, PackageOrigin.DisplayText(deep.OriginKind));
    }

    [Fact]
    public void 两者都没有_判解密包()
    {
        using var ws = new TestWorkspace("ipa-origin-decrypted");
        var deep = IpaDeepReader.Read(Build(ws, "c.ipa", scInfo: false, mobileProvision: false));
        Assert.Equal(PackageOrigin.Decrypted, deep.OriginKind);
        Assert.Equal(Strings.Filter_Origin_Decrypted, PackageOrigin.DisplayText(deep.OriginKind));
    }
}
