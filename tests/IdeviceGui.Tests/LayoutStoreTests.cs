using IdeviceGui.Services;
using IdeviceGui.Services.Data;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// M4-b1：三栏布局的持久化（docs/01 §2 / §2.1、docs/09 §3.2）。
/// 纯逻辑 + 一次真库往返；WPF 的拖动/双击/快捷键属 L3，不在单测里。
/// </summary>
public sealed class LayoutStoreTests
{
    [Fact]
    public void 库没打开时_回默认布局()
    {
        var state = LayoutStore.Load(null);

        Assert.Equal(new[] { 45d, 27d, 28d }, state.Columns);
        Assert.False(state.LogHidden);
        Assert.Equal(LayoutStore.DefaultOverlayWidth, state.LogOverlayWidth);
    }

    [Fact]
    public void 保存后读回_比例_隐藏_浮层宽度都不变()
    {
        using var workspace = new TestWorkspace("layout");
        using var db = AppDatabase.Open(workspace.Full("library.db"));

        LayoutStore.Save(db, new LayoutState(new[] { 50d, 25d, 25d }, LogHidden: true, LogOverlayWidth: 380d));
        var state = LayoutStore.Load(db);

        Assert.Equal(new[] { 50d, 25d, 25d }, state.Columns);
        Assert.True(state.LogHidden);
        Assert.Equal(380d, state.LogOverlayWidth);

        // 三行都要落库（空库读出默认 = 没写成功的信号）
        Assert.NotNull(SettingsStore.GetString(db, LayoutStore.ColumnsKey));
        Assert.Equal("true", SettingsStore.GetString(db, LayoutStore.LogHiddenKey));
        Assert.Equal("380", SettingsStore.GetString(db, LayoutStore.LogOverlayWidthKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("[1]")]
    [InlineData("[45,27]")]
    [InlineData("0,27,28")]
    [InlineData("-1,50,50")]
    [InlineData("NaN,50,50")]
    [InlineData("[1,2,3,4]")]
    public void 比例坏值_一律回默认(string? raw)
        => Assert.Equal(new[] { 45d, 27d, 28d }, LayoutStore.ParseColumns(raw));

    [Theory]
    [InlineData("[45,27,28]", new[] { 45d, 27d, 28d })]
    [InlineData("45,27,28", new[] { 45d, 27d, 28d })]
    [InlineData(" 45 ; 27 ; 28 ", new[] { 45d, 27d, 28d })]
    [InlineData("30,30,60", new[] { 25d, 25d, 50d })]
    [InlineData("50,25,25", new[] { 50d, 25d, 25d })]
    public void 比例解析_支持括号与逗号_并归一化到一百(string raw, double[] expected)
        => Assert.Equal(expected, LayoutStore.ParseColumns(raw));

    [Fact]
    public void 比例写库_统一成JSON数组且两位小数()
    {
        Assert.Equal("[45,27,28]", LayoutStore.FormatColumns(new[] { 45d, 27d, 28d }));
        Assert.Equal("[33.33,33.33,33.33]", LayoutStore.FormatColumns(new[] { 1d, 1d, 1d }));
    }

    [Theory]
    [InlineData("abc", 420d)]
    [InlineData("", 420d)]
    [InlineData("0", 120d)]
    [InlineData("-50", 120d)]
    [InlineData("500", 500d)]
    [InlineData("5000", 1200d)]
    public void 浮层宽度_坏值回默认_越界夹紧(string? raw, double expected)
        => Assert.Equal(expected, LayoutStore.ParseWidth(raw, LayoutStore.DefaultOverlayWidth));

    [Fact]
    public void 隐藏状态_缺省为false_非法值也用默认()
    {
        using var workspace = new TestWorkspace("layout-hidden");
        using var db = AppDatabase.Open(workspace.Full("library.db"));

        Assert.False(LayoutStore.Load(db).LogHidden);

        SettingsStore.SetString(db, LayoutStore.LogHiddenKey, "随便写的");
        Assert.False(LayoutStore.Load(db).LogHidden);

        SettingsStore.SetString(db, LayoutStore.LogHiddenKey, "true");
        Assert.True(LayoutStore.Load(db).LogHidden);
    }
}
