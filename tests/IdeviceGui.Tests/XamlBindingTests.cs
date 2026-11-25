using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// T6 接线测试静态部分（2026-11-03）：XAML 里的简单绑定路径（`{Binding Xxx}`）必须能在
/// 候选 DataContext 类型上找到对应属性——把"改属性名/写错名字 → 运行期静默不绑定"变成构建后立刻失败。
/// <para>
/// 只校验**简单名**（不含 `.` / 转换器 / RelativeSource）：嵌套路径与模板外的相对源误报太高，不在此列。
/// 允许清单只放 WPF 自身属性（例如 `Tag`），**不放业务属性**。
/// </para>
/// </summary>
public sealed class XamlBindingTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static readonly Regex SimpleBinding = new(
        @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)\s*[,}]",
        RegexOptions.Compiled);

    private static readonly HashSet<string> WpfOwnProperties = new(StringComparer.Ordinal)
    {
        // WPF 控件/FrameworkElement 自有属性，绑定到自身而不是 DataContext 时才允许
        "Tag",
    };

    [Fact]
    public void MainWindow里的简单绑定_都能在候选DataContext类型上找到属性()
    {
        var root = RepoRoot();
        var xamlPath = Path.Combine(root, "IdeviceGui", "prototype", "IdeviceGui", "Views", "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        var names = SimpleBinding.Matches(xaml)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var types = new[]
        {
            typeof(MainViewModel),
            typeof(DeviceQueueViewModel),
            typeof(JobViewModel),
            typeof(LibraryRow),
            typeof(DeviceAppRow),
            typeof(TabProgressBlock),
            typeof(LogLine),
            typeof(MainViewModel.GroupFilterOption),
            typeof(MainViewModel.StatusNotice),
        };
        var properties = types
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = names
            .Where(name => !properties.Contains(name) && !WpfOwnProperties.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "这些简单绑定在候选 DataContext 类型（MainViewModel/DeviceQueueViewModel/JobViewModel/LibraryRow）上找不到属性：" +
            string.Join("、", missing));
    }

    [Fact]
    public void SettingsWindow里的简单绑定_都能在候选DataContext类型上找到属性()
    {
        var root = RepoRoot();
        var xamlPath = Path.Combine(root, "IdeviceGui", "prototype", "IdeviceGui", "Views", "SettingsWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        var names = SimpleBinding.Matches(xaml)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0) return;   // 设置窗口的绑定都挂在代码里的 FieldItem 上：没有简单绑定也不算失败

        // 设置窗口的列表项模型是 XAML 代码里的私有嵌套类型（FieldItem）：用反射拿，别为了测试改可见性
        var settingsWindow = typeof(IdeviceGui.Views.SettingsWindow);
        var fieldItem = settingsWindow.GetNestedType("FieldItem", BindingFlags.NonPublic);
        var candidateTypes = new List<Type>
        {
            typeof(DeviceQueueViewModel), typeof(LibraryRow), typeof(MainViewModel), typeof(LogLine),
        };
        if (fieldItem is not null) candidateTypes.Add(fieldItem);

        var properties = candidateTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = names
            .Where(name => !properties.Contains(name) && !WpfOwnProperties.Contains(name))
            .ToList();

        Assert.True(missing.Count == 0,
            "SettingsWindow 的简单绑定找不到属性：" + string.Join("、", missing));
    }
}
