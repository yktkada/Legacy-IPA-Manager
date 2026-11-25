using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 日志类别（LogKind）是简洁模式的分界依据（docs/08 §3）。
/// 审查报告 T1：原来的"扫源码里出现几次"测不出真问题（A2/A3 两个漏标它全绿）。
/// 现在改成**调用点清单规则**：`AppendLog(...)` 的每个调用点都必须**显式**传 `LogKind.`，
/// 新加一条不写 kind 的日志会立刻红（生产代码已全部改成显式传 kind）。
/// </summary>
public sealed class LogKindCoverageTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { RepoRoot(), "IdeviceGui", "prototype", "IdeviceGui" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "找不到源文件：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void MainViewModel_每个AppendLog调用点都显式带LogKind()
    {
        var source = ReadSource("ViewModels", "MainViewModel.cs");
        var missing = new List<string>();

        foreach (var (line, snippet) in ExtractCalls(source, "AppendLog("))
        {
            // 方法定义 / 重载实现不算调用点
            if (snippet.Contains("new LogLine(", StringComparison.Ordinal)) continue;
            if (snippet.StartsWith("AppendLog(LogLine ", StringComparison.Ordinal)) continue;
            if (!snippet.Contains("LogKind.", StringComparison.Ordinal))
                missing.Add($"MainViewModel.cs:{line} → {Collapse(snippet)}");
        }

        Assert.True(missing.Count == 0,
            "以下日志调用点没有显式 LogKind（简洁模式会漏行）：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void QueueService_日志都经带类别的helper()
    {
        var source = ReadSource("Services", "QueueService.cs");

        // 所有 _log.Report(...) / DeviceLine(...) 都必须带 LogKind（helper 定义处已固定类别）
        foreach (var (line, snippet) in ExtractCalls(source, "_log.Report("))
        {
            Assert.True(snippet.Contains("LogKind.", StringComparison.Ordinal) ||
                        snippet.Contains("LogLine.Detail(", StringComparison.Ordinal),
                $"QueueService.cs:{line} 的日志没有 LogKind → {Collapse(snippet)}");
        }

        Assert.Contains("LogKind.Start", source, StringComparison.Ordinal);
        Assert.Contains("LogKind.End", source, StringComparison.Ordinal);
        Assert.Contains("LogKind.Error", source, StringComparison.Ordinal);
    }

    [Fact]
    public void 日志筛选只按类别_不再保留关键词表()
    {
        var source = ReadSource("ViewModels", "LogFilter.cs");

        Assert.DoesNotContain("ProminentTokens", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsProminent", source, StringComparison.Ordinal);
        Assert.Contains("LogKind.Start", source, StringComparison.Ordinal);
    }

    /// <summary>提取 <paramref name="name"/> 的调用段（含括号内全部内容）。</summary>
    private static IEnumerable<(int Line, string Snippet)> ExtractCalls(string source, string name)
    {
        var index = 0;
        while (true)
        {
            index = source.IndexOf(name, index, StringComparison.Ordinal);
            if (index < 0) yield break;

            var depth = 0;
            var end = index + name.Length - 1;
            while (end < source.Length)
            {
                if (source[end] == '(') depth++;
                else if (source[end] == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
                end++;
            }

            var line = source[..index].Count(c => c == '\n') + 1;
            yield return (line, source[index..(end + 1)]);
            index = end + 1;
        }
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))[..Math.Min(90, string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Length)];
}
