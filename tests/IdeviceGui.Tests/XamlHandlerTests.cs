using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IdeviceGui.Views;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// XAML 里的事件处理名是**字符串**，改名/重构 code-behind 时编译不会报错、运行时只是"不响应"
/// （`进度 §5.2` 第 5 条）。这条测试把"名字对不上"变成编译后立刻失败。
/// <para>
/// b3a 复审 P1-2 采纳（2026-10-31）：扫描范围从 `MainWindow.xaml` **扩到 `Views\*.xaml`**。
/// 2026-11-02 起有多个窗口（`MainWindow` / `SettingsWindow`）→ 按每个 XAML 的 `x:Class` 解析出
/// **对应窗口类**再校验方法，不再一律查 `MainWindow`。
/// </para>
/// </summary>
public sealed class XamlHandlerTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static readonly Regex HandlerPattern = new(
        @"\b(Click|TextChanged|ValueChanged|Checked|Unchecked|Sorting|SelectionChanged|MouseDoubleClick|Closing|Loaded|PreviewKeyDown|PreviewMouseLeftButtonDown|Tick|KeyDown)=""([A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    private static readonly Regex ClassPattern = new(@"x:Class=""([^""]+)""", RegexOptions.Compiled);


    /// <summary>
    /// 审查 N7：code-behind 里引用的模板键（`XxxCellTemplate`，最终交给 FindResource）必须在 XAML 里存在，
    /// 否则运行期启动即抛 ResourceReferenceKeyNotFoundException（现有用例只校验事件处理名）。
    /// </summary>
    [Fact]
    public void 代码里的模板键都能在XAML里找到()
    {
        var root = RepoRoot();
        var viewsDir = Path.Combine(root, "IdeviceGui", "prototype", "IdeviceGui", "Views");
        var xaml = string.Concat(Directory.GetFiles(viewsDir, "*.xaml").Select(File.ReadAllText));
        var code = Directory
            .GetFiles(Path.Combine(root, "IdeviceGui", "prototype", "IdeviceGui"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText);

        var keys = code
            .SelectMany(text => System.Text.RegularExpressions.Regex.Matches(text, "\"([A-Za-z0-9_]+CellTemplate)\""))
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(xaml.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal),
                $"XAML 里缺少资源键 {key}（FindResource 会在运行期抛异常）");
        }
    }

    /// <summary>
    /// 设备页签进度条**不能**再用"Width = TabItem.ActualWidth × 分数"（2026-11-02 实测踩坑）：
    /// 那是测量/排布反馈环——宽度越排越大，TabControl.ActualWidth 涨到 1 620 831，
    /// 整条页签栏渲染成一条绿色带。正确做法 = Rectangle 撑满整格 + RenderTransform.ScaleX=分数。
    /// 这条静态断言防止有人"顺手改回直观写法"。
    /// </summary>
    [Fact]
    public void 页签进度条用渲染变换而不是绑定实际宽度()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "IdeviceGui", "prototype", "IdeviceGui", "Views", "MainWindow.xaml"));

        // 2026-11-06 新模板：固定当前状态块 + TabBlocks 分块进度条，文字叠在条上。
        Assert.Contains("ItemsSource=\"{Binding TabBlocks}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CurrentStatusBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressFillFraction", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AncestorType=TabItem", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressFillWidth", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 所有Views里的XAML事件处理名都能在对应窗口类上找到同名方法()
    {
        var viewsDir = Path.Combine(RepoRoot(), "IdeviceGui", "prototype", "IdeviceGui", "Views");
        Assert.True(Directory.Exists(viewsDir), "找不到 Views 目录：" + viewsDir);

        var xamlFiles = Directory.GetFiles(viewsDir, "*.xaml", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(xamlFiles);     // 扫描面为空也要报，避免"测试假绿"

        var handlerTotal = 0;
        var missing = new List<string>();

        foreach (var xamlPath in xamlFiles)
        {
            var text = File.ReadAllText(xamlPath);

            // 每个 XAML 的根类（x:Class）→ 事件方法应在这个类上
            var className = ClassPattern.Match(text) is { Success: true } classMatch
                ? classMatch.Groups[1].Value
                : typeof(MainWindow).FullName!;
            var rootType = typeof(MainWindow).Assembly.GetType(className);
            Assert.True(rootType is not null, $"{Path.GetFileName(xamlPath)} 的 x:Class {className} 找不到对应类型");

            var handlers = HandlerPattern.Matches(text)
                .Select(m => m.Groups[2].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            handlerTotal += handlers.Count;

            // OnAnyChanged 在 SettingsWindow 里有两个重载（RoutedEventArgs / EventArgs）→ 用名字集合判断
            var methodNames = rootType!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);
            var fileMissing = handlers
                .Where(name => !methodNames.Contains(name))
                .Select(name => $"{Path.GetFileName(xamlPath)} → {className}.{name}")
                .ToList();

            missing.AddRange(fileMissing);
        }

        Assert.True(handlerTotal > 0, "在所有 Views\\*.xaml 里没匹配到任何事件处理名（正则可能失效）。");
        Assert.True(missing.Count == 0,
            "XAML 引用了窗口类上不存在的事件处理方法：" + string.Join("、", missing));
    }
}
