using System.IO;
using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 崩溃日志测试。crash.log 是**全进程共享路径**，必须串行跑，
/// 否则多个测试类并行时会互相覆盖（xunit 默认并行执行测试类）。
/// </summary>
[CollectionDefinition("CrashLog", DisableParallelization = true)]
public sealed class CrashLogCollection
{
}

[Collection("CrashLog")]
public sealed class CrashReporterTests
{
    private static string Path0 => CrashReporter.Path;

    private static void Cleanup()
    {
        try
        {
            if (File.Exists(Path0)) File.Delete(Path0);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    [Fact]
    public void Write_写进程序目录logs并带来源与异常文本()
    {
        Cleanup();
        try
        {
            CrashReporter.Write("UnitTest-Source", new InvalidOperationException("测试用异常"));

            Assert.True(File.Exists(Path0), "crash.log 没生成");
            var text = File.ReadAllText(Path0);
            Assert.Contains("UnitTest-Source", text);
            Assert.Contains("测试用异常", text);
            Assert.Contains("InvalidOperationException", text);
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public void Write_是覆盖写_只保留最后一次()
    {
        Cleanup();
        try
        {
            CrashReporter.Write("First", new InvalidOperationException("第一次"));
            Assert.Contains("第一次", File.ReadAllText(Path0));

            CrashReporter.Write("Second", new InvalidOperationException("第二次"));
            var text = File.ReadAllText(Path0);

            Assert.Contains("第二次", text);
            Assert.DoesNotContain("第一次", text);
            Assert.DoesNotContain("First", text);
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public void Write_不残留tmp文件()
    {
        Cleanup();
        try
        {
            CrashReporter.Write("TmpCheck", new Exception("x"));
            Assert.False(File.Exists(Path0 + ".tmp"), "不应残留 crash.log.tmp");
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public void Write_异常为null也不抛()
    {
        Cleanup();
        try
        {
            CrashReporter.Write("NoException", null);
            Assert.True(File.Exists(Path0));
            Assert.Contains("（无异常对象）", File.ReadAllText(Path0));
        }
        finally
        {
            Cleanup();
        }
    }
}
