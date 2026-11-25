using IdeviceGui.Services.Scanning;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>2026-11-04 审查 P1：扫描结果"内容是否变化"必须包含 ParseFailed。</summary>
public sealed class ScanResultTests
{
    [Fact]
    public void 全零_不算变化()
    {
        Assert.False(new ScanResult().HasLibraryChange);
    }

    [Fact]
    public void 仅解析失败_也算变化()
    {
        Assert.True(new ScanResult { ParseFailed = 1 }.HasLibraryChange);
    }

    [Fact]
    public void 每个计数器单独置1_都算变化()
    {
        var setters = new Action<ScanResult>[]
        {
            r => r.Added = 1,
            r => r.Moved = 1,
            r => r.Replaced = 1,
            r => r.Touched = 1,
            r => r.Missing = 1,
            r => r.IdentityFlips = 1,
            r => r.ParseOk = 1,
            r => r.ParseFailed = 1,
        };

        foreach (var set in setters)
        {
            var result = new ScanResult();
            set(result);
            Assert.True(result.HasLibraryChange);
        }
    }
}
