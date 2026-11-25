using System.IO;
using System.IO.Compression;
using System.Text;
using IdeviceGui.Services.Scanning;

namespace IdeviceGui.Tests;

/// <summary>
/// 临时工作目录夹具（B7 扫描测试的地基）。
/// 每个实例一个独立目录，析构时递归删除；文件操作都带断言友好的一次性 API。
/// <para>
/// 目录建在**测试程序目录下的 _test-tmp**，不用 <c>Path.GetTempPath()</c>：
/// 本机沙箱禁止测试进程写用户 TEMP（实测 `UnauthorizedAccessException`），
/// 而程序目录是可写的（与"只写程序目录"的硬约束也不冲突）。
/// </para>
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    private static readonly string RootBase =
        Path.Combine(AppContext.BaseDirectory, "_test-tmp");

    private const string AppDir = "Payload/App.app";

    public string Root { get; }

    public TestWorkspace(string tag = "idevicegui-test")
    {
        Root = Path.Combine(RootBase, $"{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    // ── 普通文件（对账用，不保证是合法 IPA） ─────────────────────────────────

    /// <summary>新建一个内容可控的假文件（填充字节；用于身份 / 对账测试）。</summary>
    public string AddFile(string relativeName, int sizeBytes, byte fill = 0x41)
    {
        var path = Full(relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Fill(sizeBytes, fill));
        return path;
    }

    /// <summary>批量造 N 个文件（1000 个文件的耗时口径），内容各不相同以避免哈希撞车。</summary>
    public string[] AddFiles(int count, string prefix = "bulk", int sizeBytes = 64, string extension = ".ipa")
    {
        var paths = new string[count];
        for (var index = 0; index < count; index++)
        {
            var path = AddFile($"{prefix}/{prefix}{index:D4}{extension}", sizeBytes, (byte)(index % 250));
            paths[index] = path;
        }

        return paths;
    }

    public string Full(string relativeName) => Path.Combine(Root, relativeName);

    public bool Exists(string relativeName) => File.Exists(Full(relativeName));

    /// <summary>改名 / 移动（同卷内，保持内容与文件身份）。</summary>
    public void Rename(string fromRelative, string toRelative)
    {
        var to = Full(toRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(Full(fromRelative), to);
    }

    /// <summary>同路径覆盖成不同内容（**原地写，FileId 不变**）。</summary>
    public void Replace(string relativeName, int newSizeBytes, byte fill = 0x42)
    {
        var path = Full(relativeName);
        File.WriteAllBytes(path, Fill(newSizeBytes, fill));
        // 保证 mtime 至少前进 2 秒，避免文件系统时间精度导致"看起来没变"
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
    }

    /// <summary>用另一个文件**移动覆盖**目标路径：目标行的 FileId 会变成源文件的（模拟"换成另一个文件"）。</summary>
    public void ReplaceByMove(string sourceRelative, string targetRelative)
    {
        var target = Full(targetRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Full(sourceRelative), target, overwrite: true);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(2));
    }

    public void Copy(string fromRelative, string toRelative)
    {
        var to = Full(toRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(Full(fromRelative), to, overwrite: true);
    }

    public void Delete(string relativeName) => File.Delete(Full(relativeName));

    public void SetMtime(string relativeName, DateTime utc) =>
        File.SetLastWriteTimeUtc(Full(relativeName), utc);

    public string[] IpaFiles() => Directory.GetFiles(Root, "*.ipa", SearchOption.AllDirectories);

    /// <summary>取文件身份（B7 起可用；拿不到返回 null，表示该卷退化路径身份）。</summary>
    public FileIdentity? IdentityOf(string relativeName) =>
        FileIdentityReader.TryRead(Full(relativeName), out var identity) ? identity : null;

    // ── 合法 IPA（能被 IpaReader 解析） ──────────────────────────────────────

    /// <summary>造一个**能被 IpaReader 解析**的最小 IPA（Payload/App.app/Info.plist + 可选填充）。</summary>
    public string AddIpa(string relativeName, string bundleId, string version = "1.0", string build = "1",
        string? displayName = null, int padBytes = 0)
    {
        var path = Full(relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, BuildIpa(bundleId, version, build, displayName, padBytes));
        return path;
    }

    /// <summary>
    /// 造一个**总大小等于 <paramref name="totalBytes"/>** 的合法 IPA。
    /// 用于"同大小、不同内容"的恢复映射用例（无压缩 zip，填充字节数线性可控）。
    /// </summary>
    public string AddIpaExactSize(string relativeName, string bundleId, int totalBytes,
        string version = "1.0", string build = "1", string? displayName = null)
    {
        // 无压缩 zip：填充字节数线性可控，但"多一个条目"会多出固定的头部开销，
        // 所以带着当前 pad 迭代修正（1–2 次收敛到目标大小）。
        var pad = 0;
        var bytes = BuildIpa(bundleId, version, build, displayName, pad);
        for (var attempt = 0; attempt < 6 && bytes.Length != totalBytes; attempt++)
        {
            pad += totalBytes - bytes.Length;
            if (pad < 0)
                throw new ArgumentOutOfRangeException(nameof(totalBytes),
                    $"目标大小 {totalBytes} 小于最小 IPA {bytes.Length} 字节");
            bytes = BuildIpa(bundleId, version, build, displayName, pad);
        }

        if (bytes.Length != totalBytes)
            throw new InvalidOperationException($"无法构造恰好 {totalBytes} 字节的 IPA（实际 {bytes.Length}）");

        var path = Full(relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>同路径重写同一个 bundleId 的 IPA（FileId 不变、大小变、BundleId 不变 → 不算替换）。</summary>
    public void RewriteIpaSameBundle(string relativeName, string bundleId, int padBytes,
        string version = "2.0")
    {
        var path = Full(relativeName);
        File.WriteAllBytes(path, BuildIpa(bundleId, version, "2", null, padBytes));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
    }

    private static byte[] BuildIpa(string bundleId, string version, string build, string? displayName, int padBytes)
    {
        var infoPlist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            <key>CFBundleIdentifier</key><string>{bundleId}</string>
            <key>CFBundleVersion</key><string>{build}</string>
            <key>CFBundleShortVersionString</key><string>{version}</string>
            <key>CFBundleDisplayName</key><string>{displayName ?? bundleId}</string>
            <key>MinimumOSVersion</key><string>6.0</string>
            </dict>
            </plist>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, AppDir + "/Info.plist", Encoding.UTF8.GetBytes(infoPlist));
            if (padBytes > 0) WriteEntry(zip, AppDir + "/pad.bin", Fill(padBytes, 0x50));
        }

        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] Fill(int size, byte value)
    {
        var buffer = new byte[size];
        Array.Fill(buffer, value);
        return buffer;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
