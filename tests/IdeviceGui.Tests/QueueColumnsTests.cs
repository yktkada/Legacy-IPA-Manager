using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>队列行字段配置与强制安装入队（2026-11-08）。</summary>
public sealed class QueueColumnsTests
{
    private static JobViewModel Job(bool force = false)
    {
        var item = new QueueItem
        {
            Udid = "udid",
            Kind = JobKind.Install,
            BundleId = "com.example.demo",
            FilePath = @"D:\ipa\Demo.ipa",
            PackageVersion = "1.2.3",
            ExpectedUncompressedBytes = 128L * 1024 * 1024,
            PackageMinOs = "8.0",
            PackageOriginText = "已解密",
            PackageDeviceFamilyText = "Universal Retina",
            ForceInstall = force,
        };
        return new JobViewModel(item, "Fake iPhone");
    }

    [Fact]
    public void 默认队列字段_状态备注名称版本大小_且备注为空时跳过()
    {
        var job = Job();
        var columns = QueueColumns.OrderedVisible(QueueColumns.Default);

        var text = QueueColumns.Format(job, columns);

        Assert.StartsWith($"[{Strings.Queue_Status_Pending}]", text, StringComparison.Ordinal);
        Assert.Contains("Demo.ipa", text, StringComparison.Ordinal);
        Assert.Contains("1.2.3", text, StringComparison.Ordinal);
        Assert.Contains("128 MB", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" |  | ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 名称列_优先取资料库App名称而不是文件名()
    {
        var item = new QueueItem
        {
            Udid = "udid",
            Kind = JobKind.Install,
            BundleId = "com.example.demo",
            FilePath = @"D:\ipa\Demo.ipa",
            PackageDisplayName = "射苹果",
            PackageVersion = "1.3",
            ExpectedUncompressedBytes = 24L * 1024 * 1024,
        };
        var job = new JobViewModel(item, "Fake iPhone");

        var text = QueueColumns.Format(job, QueueColumns.OrderedVisible(QueueColumns.Default));

        Assert.Contains("射苹果", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo.ipa", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 备注字段_有备注时紧跟状态()
    {
        var job = Job();
        job.Status = QueueStatus.Failed;
        job.Note = "安装未完成";
        var columns = QueueColumns.OrderedVisible(QueueColumns.Default);

        var text = QueueColumns.Format(job, columns);

        Assert.Contains($"[{Strings.Queue_Status_Failed}] | 安装未完成 | Demo.ipa", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 备注列_暂停和挂起显示_完成隐藏()
    {
        var job = Job();
        var columns = QueueColumns.OrderedVisible(QueueColumns.Default);

        job.Status = QueueStatus.Paused;
        job.Note = "设备已离线";
        Assert.Contains("设备已离线", QueueColumns.Format(job, columns), StringComparison.Ordinal);

        job.Status = QueueStatus.Suspended;
        job.Note = "队列已挂起";
        Assert.Contains("队列已挂起", QueueColumns.Format(job, columns), StringComparison.Ordinal);

        job.Status = QueueStatus.Done;
        job.Note = "完成回读确认";
        Assert.DoesNotContain("完成回读确认", QueueColumns.Format(job, columns), StringComparison.Ordinal);
    }

    [Fact]
    public void 全部列隐藏时_回落状态列()
    {
        var hidden = QueueColumns.Default.Select(s => s with { Visible = false }).ToList();
        var visible = QueueColumns.OrderedVisible(hidden);

        Assert.Equal(new[] { QueueColumn.Status }, visible);
    }

    [Fact]
    public void 队列字段配置_坏JSON回默认_序列化可往返()
    {
        Assert.Equal(QueueColumns.Default.Count, QueueColumns.Parse("{bad").Count);

        var custom = QueueColumns.Default
            .Select(s => s.Column == QueueColumn.FilePath ? s with { Visible = true, Order = 0 } : s)
            .ToList();
        var parsed = QueueColumns.Parse(QueueColumns.Serialize(custom));

        Assert.Contains(parsed, s => s.Column == QueueColumn.FilePath && s.Visible);
    }

    [Fact]
    public void 强制安装入队_标记会传到QueueItem()
    {
        var queue = new QueueService(
            new FakeCliRunner(),
            new FakeAfcHelper(),
            readback: null,
            new Progress<QueueEvent>(_ => { }),
            new Progress<LogLine>(_ => { }));

        var result = queue.Enqueue(
            "udid",
            JobKind.Install,
            "com.example.demo",
            @"D:\ipa\Demo.ipa",
            @"D:\ipa\Demo.ipa",
            packageVersion: "1.0",
            forceInstall: true);

        Assert.True(result.Item.ForceInstall);
    }

    [Fact]
    public void 所有列枚举_ToId与TryParseColumn及序列化全量往返一致()
    {
        var all = Enum.GetValues<QueueColumn>();
        var custom = all
            .Select((col, idx) => new QueueColumnSetting(col, Visible: true, Order: idx))
            .ToList();

        foreach (var col in all)
        {
            var id = QueueColumns.ToId(col);
            Assert.True(QueueColumns.TryParseColumn(id, out var parsed), $"列 {col} (id={id}) 往返解析失败");
            Assert.Equal(col, parsed);
        }

        // 兼容历史拼写 "minios"
        Assert.True(QueueColumns.TryParseColumn("minios", out var legacyMinOs));
        Assert.Equal(QueueColumn.MinOs, legacyMinOs);

        var roundtrip = QueueColumns.Parse(QueueColumns.Serialize(custom));
        Assert.Equal(all.Length, roundtrip.Count);
        foreach (var col in all)
        {
            Assert.Contains(roundtrip, s => s.Column == col && s.Visible);
        }
    }
}
