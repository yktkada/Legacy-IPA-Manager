using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 设备应用页多选/批量卸载入口（2026-11-08）：只测选中集合与命令文案，
/// 不触发真实卸载（批量卸载需要在线设备，属于 UI/真机验证）。
/// </summary>
public sealed class DeviceAppSelectionTests
{
    private static MainViewModel NewVm() =>
        new(new FakeCliRunner(), new FakeAfcHelper());

    private static DeviceAppRow Row(string bundleId, string name) => new()
    {
        BundleId = bundleId,
        Name = name,
        VersionText = "1.0",
    };

    [Fact]
    public void 设备应用多选_单击切换与全选取消()
    {
        var vm = NewVm();
        var first = Row("com.example.one", "One");
        var second = Row("com.example.two", "Two");
        vm.DeviceApps.Add(first);
        vm.DeviceApps.Add(second);

        Assert.False(vm.IsDeviceAppsMultiSelectMode);
        vm.ToggleDeviceAppsMultiSelectMode();
        Assert.True(vm.IsDeviceAppsMultiSelectMode);

        Assert.True(vm.ToggleDeviceAppMultiSelect(first));
        Assert.True(first.IsMultiSelected);
        Assert.Equal(1, vm.DeviceAppsSelectedCount);
        Assert.True(vm.HasSelectedDeviceApps);
        Assert.Contains("1", vm.DeviceAppsBatchUninstallText);

        vm.SelectAllDeviceApps();
        Assert.Equal(2, vm.DeviceAppsSelectedCount);

        vm.ClearDeviceAppsSelection();
        Assert.Equal(0, vm.DeviceAppsSelectedCount);
        Assert.False(vm.HasSelectedDeviceApps);

        vm.ToggleDeviceAppsMultiSelectMode();
        Assert.False(vm.IsDeviceAppsMultiSelectMode);
    }

    [Fact]
    public void 设备应用多选_关闭模式清空选择且非多选不处理行()
    {
        var vm = NewVm();
        var row = Row("com.example.one", "One");
        vm.DeviceApps.Add(row);
        vm.ToggleDeviceAppsMultiSelectMode();
        Assert.True(vm.ToggleDeviceAppMultiSelect(row));
        Assert.True(row.IsMultiSelected);

        vm.ToggleDeviceAppsMultiSelectMode();
        Assert.False(vm.IsDeviceAppsMultiSelectMode);
        Assert.False(row.IsMultiSelected);
        Assert.False(vm.ToggleDeviceAppMultiSelect(row));
    }

    [Fact]
    public void 设备应用拖动多选_复用范围切换接口()
    {
        var vm = NewVm();
        var rows = new[]
        {
            Row("com.example.one", "One"),
            Row("com.example.two", "Two"),
            Row("com.example.three", "Three"),
        };
        foreach (var row in rows) vm.DeviceApps.Add(row);
        vm.ToggleDeviceAppsMultiSelectMode();

        Assert.True(vm.BeginDeviceAppDragSelection(rows[0]));
        vm.SelectDeviceAppDragRange(rows[0], rows[2]);
        Assert.True(rows[0].IsMultiSelected);
        Assert.True(rows[1].IsMultiSelected);
        Assert.True(rows[2].IsMultiSelected);

        // 再次从第一行拖到最后：相对拖动前基线反选，全部取消。
        Assert.True(vm.BeginDeviceAppDragSelection(rows[0]));
        vm.SelectDeviceAppDragRange(rows[0], rows[2]);
        Assert.False(rows[0].IsMultiSelected);
        Assert.False(rows[1].IsMultiSelected);
        Assert.False(rows[2].IsMultiSelected);
    }

    [Fact]
    public void 设备应用批量卸载_无选中设备时不抛异常()
    {
        var vm = NewVm();
        vm.DeviceApps.Add(Row("com.example.one", "One"));
        vm.ToggleDeviceAppsMultiSelectMode();
        vm.SelectAllDeviceApps();

        var exception = Record.Exception(vm.UninstallSelectedDeviceApps);

        Assert.Null(exception);
        Assert.Single(vm.DeviceApps);
    }
}
