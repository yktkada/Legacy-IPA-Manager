using System.Collections.ObjectModel;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 设备 tab 排序（2026-11-02 用户定）：在线的排前面、离线的排最后，各自保持原有相对顺序。
/// </summary>
public sealed class DeviceOrderTests
{
    private sealed record Item(string Name, bool Online);

    [Fact]
    public void 在线先_离线后_组内保持原序()
    {
        var items = new[]
        {
            new Item("A", Online: true),
            new Item("B", Online: false),
            new Item("C", Online: true),
            new Item("D", Online: false),
        };

        var order = DeviceOrder.Compute(items, i => i.Online);
        Assert.Equal(new[] { 0, 2, 1, 3 }, order);
    }

    [Fact]
    public void 全在线或全离线时顺序不变()
    {
        var online = new[] { new Item("A", true), new Item("B", true) };
        Assert.Equal(new[] { 0, 1 }, DeviceOrder.Compute(online, i => i.Online));

        var offline = new[] { new Item("A", false), new Item("B", false) };
        Assert.Equal(new[] { 0, 1 }, DeviceOrder.Compute(offline, i => i.Online));
    }
}
