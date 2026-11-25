using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 设置草稿（2026-11-02 设置窗口，docs/01 §2.4）：
/// 取消/关窗要回滚，所以克隆必须是深拷贝；每组「恢复默认」只动该组字段。
/// </summary>
public sealed class SettingsDraftTests
{
    [Fact]
    public void 默认值符合文档口径()
    {
        var draft = new SettingsDraft();

        Assert.Equal(32, draft.RowHeight);
        Assert.Equal(LibrarySortField.AppSize, draft.Sort.Field);
        Assert.True(draft.Sort.Descending);
        Assert.True(draft.MergeSameBundle);
        Assert.True(draft.ShowMissingFiles);
        Assert.Equal(30, draft.ScanBatchSize);
        Assert.False(draft.SkipStartupScan);
        Assert.Equal(2048, draft.IconCacheMaxMb);
        Assert.True(draft.IconMatchDisplayScale);
        Assert.True(draft.IconDownscaleCache);
        Assert.False(draft.LogToFile);
        Assert.False(draft.LogDetailMode);
        Assert.True(draft.LogHidden);
        Assert.Equal("system", draft.UiLanguage);
        Assert.Equal(2, draft.PollSeconds);
        Assert.True(draft.AutoResume);
        Assert.Equal(3, draft.ReconnectStableSeconds);
        Assert.Equal(2, draft.AutoRetryAttempts);
    }

    [Fact]
    public void 克隆是深拷贝_改克隆不影响原草稿()
    {
        var original = new SettingsDraft { RowHeight = 40, PollSeconds = 5, AutoResume = true, UiLanguage = "en", SkipStartupScan = true };
        original.Scope.IsName = false;

        var clone = original.Clone();
        clone.RowHeight = 20;
        clone.Scope.IsName = true;
        clone.PollSeconds = 9;
        clone.UiLanguage = "zh-CN";
        clone.SkipStartupScan = false;

        Assert.Equal(40, original.RowHeight);
        Assert.False(original.Scope.IsName);
        Assert.Equal(5, original.PollSeconds);
        Assert.Equal("en", original.UiLanguage);
        Assert.True(original.SkipStartupScan);
        Assert.True(clone.Scope.IsName);
        Assert.Equal("zh-CN", clone.UiLanguage);
        Assert.False(clone.SkipStartupScan);
    }

    [Fact]
    public void 恢复默认_资料库组只动资料库字段()
    {
        var draft = new SettingsDraft
        {
            RowHeight = 44,
            MergeSameBundle = false,
            ShowMissingFiles = false,
            ScanBatchSize = 77,
            PollSeconds = 7,
        };
        draft.Scope.IsName = false;

        draft.ResetGroup(SettingsGroup.Library);

        Assert.Equal(32, draft.RowHeight);
        Assert.True(draft.MergeSameBundle);
        Assert.True(draft.ShowMissingFiles);
        Assert.True(draft.Scope.IsName);
        Assert.Equal(77, draft.ScanBatchSize);   // 其它组不动
        Assert.Equal(7, draft.PollSeconds);
    }

    [Fact]
    public void 恢复默认_扫描图标日志布局设备各组()
    {
        var draft = new SettingsDraft
        {
            ScanBatchSize = 500,
            SkipStartupScan = true,
            IconCacheMaxMb = 4096,
            IconMatchDisplayScale = false,
            IconDownscaleCache = false,
            LogToFile = true,
            LogDetailMode = true,
            LogHidden = false,
            LogOverlayWidth = 700,
            UiLanguage = "en",
            PollSeconds = 9,
            AutoResume = false,
            ReconnectStableSeconds = 9,
            AutoRetryAttempts = 5,
        };

        draft.ResetGroup(SettingsGroup.Scan);
        Assert.Equal(30, draft.ScanBatchSize);
        Assert.False(draft.SkipStartupScan);

        draft.ResetGroup(SettingsGroup.Icons);
        Assert.Equal(2048, draft.IconCacheMaxMb);
        Assert.True(draft.IconMatchDisplayScale);
        Assert.True(draft.IconDownscaleCache);

        draft.ResetGroup(SettingsGroup.Log);
        Assert.False(draft.LogToFile);
        Assert.False(draft.LogDetailMode);

        draft.ResetGroup(SettingsGroup.Layout);
        Assert.True(draft.LogHidden);
        Assert.Equal(420, draft.LogOverlayWidth);
        Assert.Equal("system", draft.UiLanguage);

        draft.ResetGroup(SettingsGroup.Device);
        Assert.Equal(2, draft.PollSeconds);
        Assert.True(draft.AutoResume);
        Assert.Equal(3, draft.ReconnectStableSeconds);
        Assert.Equal(2, draft.AutoRetryAttempts);
    }
}
