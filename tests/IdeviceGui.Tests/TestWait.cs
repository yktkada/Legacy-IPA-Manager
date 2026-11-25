using System.Diagnostics;

namespace IdeviceGui.Tests;

/// <summary>
/// 测试里的**有界等待**：任何"轮询直到某事发生"都必须走这里，不许写成 <c>while (...) await Task.Delay()</c>
/// 的死循环（假实现没被调用时那会变成永久卡住，而不是一次失败）。R5，见建议稿 §3。
/// </summary>
internal static class TestWait
{
    /// <summary>轮询等待条件成立；超时抛 <see cref="Xunit.Sdk.XunitException"/>，信息里带 describe() 的结果。</summary>
    public static async Task UntilAsync(Func<bool> condition, Func<string> describe, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"等待超时（{timeoutMs} ms）：{describe()}");
    }

    /// <summary>等价于 <see cref="UntilAsync"/> 的 Task 版本：等某个 Task 完成，超时同样抛 XunitException。</summary>
    public static async Task UntilCompleteAsync(Task task, Func<string> describe, int timeoutMs = 3000)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, task))
            throw new Xunit.Sdk.XunitException($"等待超时（{timeoutMs} ms）：{describe()}");
        await task.ConfigureAwait(false);
    }
}
