using System.IO;
using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 路径口径测试（硬约束 #7：只写程序目录，严禁用户目录）。
/// 注意：<see cref="AppPaths.ProgramDirectory"/> 取的是**测试进程**的 BaseDirectory，
/// 所以这里断言的是"结构关系"（都在程序目录下），不写死具体路径。
/// </summary>
[Collection("ProcessEnv")]
public sealed class AppPathsTests
{
    [Fact]
    public void ProgramDirectory_以分隔符结尾()
    {
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), AppPaths.ProgramDirectory);
    }

    [Fact]
    public void LogsDirectory_在程序目录下()
    {
        Assert.Equal(Path.Combine(AppPaths.ProgramDirectory, "logs"), AppPaths.LogsDirectory);
    }

    [Fact]
    public void CrashLogPath_是程序目录_logs_crash_log()
    {
        Assert.Equal(Path.Combine(AppPaths.LogsDirectory, "crash.log"), AppPaths.CrashLogPath);
        Assert.StartsWith(AppPaths.ProgramDirectory, AppPaths.CrashLogPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataDirectory_未设环境变量时在程序目录下()
    {
        var before = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, null);
            Assert.Equal(Path.Combine(AppPaths.ProgramDirectory, "data"), AppPaths.DataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, before);
        }
    }

    [Fact]
    public void DataDirectory_设了环境变量就跟它走_但崩溃日志仍在程序目录()
    {
        var before = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        // 不用 Path.GetTempPath()：这里只做字符串比较，但避免后人照抄成真写 TEMP（沙箱禁写）
        var overridePath = Path.Combine(AppPaths.ProgramDirectory, "_test-tmp", "idevicegui-test-data");
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, overridePath);
            Assert.Equal(overridePath, AppPaths.DataDirectory);

            // 关键：日志目录不跟随 IDEVICEGUI_DATA（崩溃日志要能在部署目录找到）
            Assert.StartsWith(AppPaths.ProgramDirectory, AppPaths.CrashLogPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, before);
        }
    }

    [Fact]
    public void EnsureWritable_测试输出目录可写时返回true并建出logs()
    {
        Assert.True(AppPaths.EnsureWritable(out var problem), "测试输出目录应可写，实际：" + problem);
        Assert.Equal("", problem);
        Assert.True(Directory.Exists(AppPaths.LogsDirectory));
    }

    // 2026-11-01（踩坑 C27）：SQLite 临时文件必须落在程序目录，不能落用户 TEMP。
    // 只设 SQLITE_TMPDIR 无效——Windows 后端用 GetTempPathW，认 TMP/TEMP，所以三个都要断言。
    [Fact]
    public void UseProgramTempDirectory_把进程TEMP_TMP_SQLITE_TMPDIR都指到data下tmp()
    {
        var beforeData = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        var beforeTmp = Environment.GetEnvironmentVariable("TMP");
        var beforeTemp = Environment.GetEnvironmentVariable("TEMP");
        var beforeSqlite = Environment.GetEnvironmentVariable("SQLITE_TMPDIR");
        var dataDir = Path.Combine(AppPaths.ProgramDirectory, "_test-tmp", "tempdir-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, dataDir);

            Assert.True(AppPaths.UseProgramTempDirectory(out var problem), "应成功，实际：" + problem);
            Assert.Equal("", problem);

            var expected = Path.Combine(dataDir, "tmp");
            Assert.True(Directory.Exists(expected), "tmp 目录没建出来");
            Assert.Equal(expected, Environment.GetEnvironmentVariable("TMP"));
            Assert.Equal(expected, Environment.GetEnvironmentVariable("TEMP"));
            Assert.Equal(expected, Environment.GetEnvironmentVariable("SQLITE_TMPDIR"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, beforeData);
            Environment.SetEnvironmentVariable("TMP", beforeTmp);
            Environment.SetEnvironmentVariable("TEMP", beforeTemp);
            Environment.SetEnvironmentVariable("SQLITE_TMPDIR", beforeSqlite);
            try
            {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
            }
            catch
            {
                // 清理失败不影响断言结果
            }
        }
    }
}

/// <summary>
/// 这一组会改进程级环境变量（TEMP/TMP/SQLITE_TMPDIR/IDEVICEGUI_DATA），**必须串行**，
/// 否则会与其他并行测试互相踩环境（2026-11-01 加）。
/// </summary>
[CollectionDefinition("ProcessEnv", DisableParallelization = true)]
public sealed class ProcessEnvCollection
{
}
