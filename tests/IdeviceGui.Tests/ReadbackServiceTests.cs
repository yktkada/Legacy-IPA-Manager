using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 回读解析与缓存测试：不碰真机、不启子进程，只喂 XML 文本。
/// 版本口径见总览 §6.10：ShortVersion → Build 回退，键缺失（不是空串）是常态。
/// </summary>
public sealed class ReadbackServiceTests
{
    private const string XmlShortVersionPresent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0">
        <array>
          <dict>
            <key>CFBundleIdentifier</key><string>com.example.app</string>
            <key>CFBundleShortVersionString</key><string>2.2</string>
            <key>CFBundleVersion</key><string>999</string>
          </dict>
        </array>
        </plist>
        """;

    private const string XmlShortVersionKeyMissing = """
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0">
        <array>
          <dict>
            <key>CFBundleIdentifier</key><string>com.example.onlybuild</string>
            <key>CFBundleVersion</key><string>7936.3</string>
          </dict>
          <dict>
            <key>CFBundleIdentifier</key><string>com.example.neither</string>
          </dict>
          <dict>
            <key>CFBundleIdentifier</key><string>com.example.empty</string>
            <key>CFBundleShortVersionString</key><string></string>
            <key>CFBundleVersion</key><string>1.1</string>
          </dict>
          <dict>
            <key>CFBundleVersion</key><string>no-bundle-id</string>
          </dict>
        </array>
        </plist>
        """;

    [Fact]
    public void Parse_ShortVersion存在时优先用它()
    {
        var apps = ReadbackService.Parse(XmlShortVersionPresent);
        var app = Assert.Single(apps);
        Assert.Equal("com.example.app", app.BundleId);
        Assert.Equal("2.2", app.Version);
        Assert.True(app.VersionKnown);
    }

    [Fact]
    public void Parse_ShortVersion键缺失时回退Build()
    {
        var apps = ReadbackService.Parse(XmlShortVersionKeyMissing);
        var app = Assert.Single(apps, a => a.BundleId == "com.example.onlybuild");
        Assert.Equal("7936.3", app.Version);
        Assert.True(app.VersionKnown);
    }

    [Fact]
    public void Parse_ShortVersion为空串时回退Build()
    {
        var apps = ReadbackService.Parse(XmlShortVersionKeyMissing);
        var app = Assert.Single(apps, a => a.BundleId == "com.example.empty");
        Assert.Equal("1.1", app.Version);
        Assert.True(app.VersionKnown);
    }

    [Fact]
    public void Parse_两个版本字段都缺时标版本未知()
    {
        var apps = ReadbackService.Parse(XmlShortVersionKeyMissing);
        var app = Assert.Single(apps, a => a.BundleId == "com.example.neither");
        Assert.False(app.VersionKnown);
        Assert.Equal("", app.Version);
    }

    [Fact]
    public void Parse_没有BundleId的条目被跳过()
    {
        var apps = ReadbackService.Parse(XmlShortVersionKeyMissing);
        Assert.Equal(3, apps.Count);
        Assert.DoesNotContain(apps, a => string.IsNullOrWhiteSpace(a.BundleId));
    }

    [Fact]
    public void Parse_带UTF8BOM也能解析()
    {
        var withBom = "\uFEFF" + XmlShortVersionPresent;
        var apps = ReadbackService.Parse(withBom);
        Assert.Single(apps);
    }

    [Fact]
    public void Parse_空输出抛异常_由调用方标结果未知()
    {
        Assert.Throws<InvalidOperationException>(() => ReadbackService.Parse(""));
        Assert.Throws<InvalidOperationException>(() => ReadbackService.Parse("   "));
    }

    [Fact]
    public void Parse_非数组plist抛异常()
    {
        const string dictRoot = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>a</key><string>b</string></dict></plist>
            """;
        Assert.Throws<InvalidOperationException>(() => ReadbackService.Parse(dictRoot));
    }

    // ── 缓存行为 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListInstalledAsync_命中缓存时不再拉取()
    {
        var cli = new CountingCli(XmlShortVersionPresent);
        var service = new ReadbackService(cli);

        var first = await service.ListInstalledAsync("UDID-1", CancellationToken.None);
        var second = await service.ListInstalledAsync("UDID-1", CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(1, cli.ReadbackCalls);
    }

    [Fact]
    public async Task Invalidate之后会重新拉取()
    {
        var cli = new CountingCli(XmlShortVersionPresent);
        var service = new ReadbackService(cli);

        await service.ListInstalledAsync("UDID-1", CancellationToken.None);
        service.Invalidate("UDID-1");
        await service.ListInstalledAsync("UDID-1", CancellationToken.None);

        Assert.Equal(2, cli.ReadbackCalls);
    }

    [Fact]
    public async Task 缓存按UDID隔离()
    {
        var cli = new CountingCli(XmlShortVersionPresent);
        var service = new ReadbackService(cli);

        await service.ListInstalledAsync("UDID-A", CancellationToken.None);
        await service.ListInstalledAsync("UDID-B", CancellationToken.None);

        Assert.Equal(2, cli.ReadbackCalls);
    }

    [Fact]
    public async Task 回读命令返回null时抛异常_表示结果未知()
    {
        var cli = new CountingCli(null);
        var service = new ReadbackService(cli);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListInstalledAsync("UDID-1", CancellationToken.None));
    }

    /// <summary>只关心 ListInstalledXmlAsync；其余方法给安全默认值。</summary>
    private sealed class CountingCli : ICliRunner
    {
        private readonly string? _xml;
        public int ReadbackCalls;

        public CountingCli(string? xml) => _xml = xml;

        public Task<string?> ListInstalledXmlAsync(string udid, CancellationToken ct)
        {
            Interlocked.Increment(ref ReadbackCalls);
            return Task.FromResult(_xml);
        }

        public Task<IReadOnlyList<string>> ListUdidsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<DeviceProbe> ProbeAsync(string udid, CancellationToken ct)
            => Task.FromResult(DeviceProbe.Missing);

        public Task<InstallOutcome> InstallAsync(
            string udid, string ipaPath, IProgress<string>? log, IProgress<double>? percent, CancellationToken ct)
            => Task.FromResult(new InstallOutcome(0, false, null, false));

        public Task<UninstallOutcome> UninstallAsync(
            string udid, string bundleId, IProgress<string>? log, CancellationToken ct)
            => Task.FromResult(new UninstallOutcome(0, false, null, false));

        public Task<DiskUsage> QueryDiskUsageAsync(string udid, CancellationToken ct)
            => Task.FromResult(DiskUsage.Unknown);

        public Task<IdeviceGui.Services.Diagnostics.BatteryUsage> ReadBatteryUsageAsync(string udid, CancellationToken ct)
            => Task.FromResult(IdeviceGui.Services.Diagnostics.BatteryUsage.Unknown);

        public Task<IdeviceGui.Services.Diagnostics.BatteryHealth> ReadBatteryHealthAsync(string udid, CancellationToken ct)
            => Task.FromResult(IdeviceGui.Services.Diagnostics.BatteryHealth.Unknown);

        public Task<IdeviceGui.Services.Diagnostics.BatterySpec> ReadBatterySpecAsync(string udid, CancellationToken ct)
            => Task.FromResult(IdeviceGui.Services.Diagnostics.BatterySpec.Unknown);
    }

    // ── 缓存失效与在飞 fetch 的竞态（体检报告 P0-3）────────────────────────────
    [Fact]
    public async Task ListInstalled_在飞期间被Invalidate_不写回旧快照且返回新结果()
    {
        const string xmlA = "<?xml version=\"1.0\"?><plist><array><dict><key>CFBundleIdentifier</key><string>com.old.app</string></dict></array></plist>";
        const string xmlB = "<?xml version=\"1.0\"?><plist><array><dict><key>CFBundleIdentifier</key><string>com.new.app</string></dict></array></plist>";

        var cli = new FakeCliRunner();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        cli.ListInstalledXmlFactory = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                return gate.Task;
            }

            return Task.FromResult<string?>(xmlB);
        };

        var svc = new ReadbackService(cli);
        var first = svc.ListInstalledAsync("u1", CancellationToken.None);

        // 等第一轮 fetch 真正进入（TCS 精确通知，不再无上限轮询 —— R5）
        await TestWait.UntilCompleteAsync(entered.Task, () => $"第一轮回读没有被调用（calls={Volatile.Read(ref calls)}）");
        svc.Invalidate("u1");
        gate.SetResult(xmlA);

        var apps = await first;

        var app = Assert.Single(apps);
        Assert.Equal("com.new.app", app.BundleId);      // 旧结果被作废后必须重取
        Assert.Equal(2, cli.ListInstalledXmlCalls);     // 重取了一次
    }
}