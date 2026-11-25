using IdeviceGui.Services;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// T3（2026-11-03）：无主异步任务收口。核心断言：**异常不许静默消失**，且失败信息带操作名。
/// </summary>
public sealed class SafeFireAndForgetTests
{
    [Fact]
    public async Task 成功的任务_会执行并正常完成()
    {
        var ran = false;
        await SafeFireAndForget.Run(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }, "测试操作");

        Assert.True(ran);
    }

    [Fact]
    public async Task 异步异常_会回调错误_且带操作名和异常类型()
    {
        string? captured = null;
        Exception? capturedException = null;

        await SafeFireAndForget.Run(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("炸了");
            },
            "后台加载",
            (message, exception) =>
            {
                captured = message;
                capturedException = exception;
            });

        Assert.NotNull(captured);
        Assert.Contains("后台加载未完成", captured);
        Assert.Contains("InvalidOperationException", captured);
        Assert.Contains("炸了", captured);
        Assert.IsType<InvalidOperationException>(capturedException);
    }

    [Fact]
    public async Task 同步异常_也会被捕获_不冒泡()
    {
        string? captured = null;

        await SafeFireAndForget.Run(
            () => throw new ArgumentException("参数错"),
            "同步动作",
            (message, _) => captured = message);

        Assert.Contains("同步动作未完成", captured);
        Assert.Contains("ArgumentException", captured);
    }

    [Fact]
    public async Task 取消异常_视为正常收尾_不回调错误()
    {
        var called = false;

        await SafeFireAndForget.Run(
            () => throw new OperationCanceledException("关窗取消"),
            "可取消操作",
            (_, _) => called = true);

        Assert.False(called);
    }

    [Fact]
    public async Task RunHandler_异常同样不冒泡_只进回调()
    {
        string? captured = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        SafeFireAndForget.RunHandler(
            () => throw new InvalidOperationException("handler 炸了"),
            "事件处理",
            (message, _) =>
            {
                captured = message;
                done.TrySetResult();
            });

        // RunHandler 是 async void 语义：等回调完成（不是靠 sleep 猜时间）
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("事件处理未完成", captured);
    }
}
