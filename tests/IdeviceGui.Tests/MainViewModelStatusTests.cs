using System.Threading;
using IdeviceGui.ViewModels;
using Xunit;

namespace IdeviceGui.Tests;

/// <summary>
/// 审核报告 P1-2：全局未处理异常必须"状态红字 + 日志"两处可见，且
/// ①从后台线程进来也不能把日志丢掉 ②不能因为重复订阅而出现两条。
/// 说明：测试进程里没有 WPF Application，<c>RunOnUi</c> 会直接在当前线程执行 ——
/// 这条覆盖的是**事件接线 + 双重订阅**，UI 线程切换由代码里的 Dispatcher 分支保证。
/// </summary>
public sealed class MainViewModelStatusTests
{
    [Fact]
    public void 全局异常从后台线程上报_状态红字且日志只有一条()
    {
        var vm = new MainViewModel(new FakeCliRunner(), new FakeAfcHelper());
        try
        {
            var thread = new Thread(() => App.ReportUnhandled(new InvalidOperationException("模拟后台崩溃")))
            {
                IsBackground = true,
            };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "后台线程应结束");

            Assert.True(vm.StatusIsError);
            Assert.Contains("模拟后台崩溃", vm.StatusMessage);

            // 双重订阅回归（原来 ClearDoneCommand.Failed += 了两次）
            Assert.Equal(1, vm.LogLines.Count(line => line.Text.Contains("模拟后台崩溃")));
            Assert.Contains(vm.LogLines.Select(l => l.Text), line => line.Contains("未处理异常"));
        }
        finally
        {
            vm.DetachAppEvents();
        }
    }
}
