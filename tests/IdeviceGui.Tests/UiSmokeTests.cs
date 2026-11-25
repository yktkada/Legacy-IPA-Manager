using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IdeviceGui.Resources;
using IdeviceGui.Services;
using IdeviceGui.ViewModels;
using IdeviceGui.ViewModels.Library;
using IdeviceGui.Views;
using Xunit;

namespace IdeviceGui.Tests;

[Collection("WpfUiSmoke")]
public sealed class UiSmokeTests
{
    private const double SmokeWidth = 1280;
    private const double SmokeHeight = 760;

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void MainWindow_Constructs_Measures_And_Closes_WithoutBindingErrors(string cultureName)
    {
        using var ws = new TestWorkspace("ui-smoke-main");
        RunWithDataDirectory(ws.Root, () => RunOnStaAsync(async ct =>
        {
            using var _ = Strings.UseCultureScope(CultureInfo.GetCultureInfo(cultureName));
            using var bindingGuard = new DataBindingErrorScope();

            var cli = new FakeCliRunner();
            var afc = new FakeAfcHelper();
            var window = new MainWindow(cli, afc, scanRootOverride: ws.Root);
            var vm = Assert.IsType<MainViewModel>(window.DataContext);

            try
            {
                AddSampleDevices(vm, cli, afc);
                await MeasureAndLayoutAsync(window);

                var settingsBtn = Assert.IsType<Button>(window.FindName("SettingsButton"));
                var installToBtn = Assert.IsType<Button>(window.FindName("InstallToButton"));
                var logToggleBtn = Assert.IsType<Button>(window.FindName("LogToggleButton"));
                var libraryModeBtn = Assert.IsType<Button>(window.FindName("LibraryModeButton"));
                var deviceAppsModeBtn = Assert.IsType<Button>(window.FindName("DeviceAppsModeButton"));
                var deviceTabs = Assert.IsType<TabControl>(window.FindName("DeviceTabs"));

                Assert.Equal(Strings.Main_Toolbar_Settings, settingsBtn.Content);
                Assert.Equal(Strings.Main_Toolbar_InstallTo, installToBtn.Content);
                Assert.Equal(Strings.Main_Toolbar_HideLog, logToggleBtn.Content);
                Assert.Equal(Strings.Main_Library_ModeTitle, libraryModeBtn.Content);
                Assert.Equal(Strings.Main_DeviceApps_ModeTitle, deviceAppsModeBtn.Content);
                Assert.Equal(2, deviceTabs.Items.Count);
            }
            finally
            {
                await CloseMainWindowCleanlyAsync(window, vm, ct);
            }

            bindingGuard.AssertNoErrors($"MainWindow ({cultureName})");
        }));
    }

    [Fact]
    public void MainWindow_NoDeviceState_HidesDeviceControls_And_SelectsDefaultGroup()
    {
        using var ws = new TestWorkspace("ui-smoke-no-device");
        RunWithDataDirectory(ws.Root, () => RunOnStaAsync(async ct =>
        {
            using var _ = Strings.UseCultureScope(CultureInfo.GetCultureInfo("zh-CN"));
            using var bindingGuard = new DataBindingErrorScope();

            var cli = new FakeCliRunner { Udids = Array.Empty<string>() };
            var afc = new FakeAfcHelper();
            var window = new MainWindow(cli, afc, scanRootOverride: ws.Root);
            var vm = Assert.IsType<MainViewModel>(window.DataContext);

            try
            {
                await vm.InitializeAsync();
                await MeasureAndLayoutAsync(window);

                var groupCombo = Assert.IsType<ComboBox>(window.FindName("GroupFilterCombo"));
                var installToBtn = Assert.IsType<Button>(window.FindName("InstallToButton"));
                var moreDeviceBtn = Assert.IsType<Button>(window.FindName("MoreDeviceButton"));
                var deviceTabs = Assert.IsType<TabControl>(window.FindName("DeviceTabs"));
                var deviceAppsModeBtn = Assert.IsType<Button>(window.FindName("DeviceAppsModeButton"));
                var queueSuspendBtn = Assert.IsType<Button>(window.FindName("QueueSuspendButton"));
                var queueCancelBtn = Assert.IsType<Button>(window.FindName("QueueCancelButton"));
                var clearQueueBtn = Assert.IsType<Button>(window.FindName("ClearQueueButton"));
                var queueHintText = Assert.IsType<TextBlock>(window.FindName("QueueEmptyHintText"));

                var selectedGroup = Assert.IsType<MainViewModel.GroupFilterOption>(groupCombo.SelectedItem);
                Assert.Equal(vm.ActiveGroupName, selectedGroup.Name);

                Assert.False(installToBtn.IsEnabled);
                Assert.False(deviceAppsModeBtn.IsEnabled);
                Assert.Equal(Visibility.Collapsed, moreDeviceBtn.Visibility);
                Assert.Equal(Visibility.Collapsed, deviceTabs.Visibility);
                Assert.False(queueSuspendBtn.IsEnabled);
                Assert.False(queueCancelBtn.IsEnabled);
                Assert.False(clearQueueBtn.IsEnabled);
                Assert.Equal(Strings.Main_Queue_SuspendFallback, queueSuspendBtn.Content);
                Assert.Equal(Visibility.Visible, queueHintText.Visibility);
                Assert.Equal(Strings.Main_Queue_NoDeviceHint, queueHintText.Text);

                AddSampleDevices(vm, cli, afc);
                await MeasureAndLayoutAsync(window);

                Assert.True(installToBtn.IsEnabled);
                Assert.True(deviceAppsModeBtn.IsEnabled);
                Assert.Equal(Visibility.Visible, moreDeviceBtn.Visibility);
                Assert.Equal(Visibility.Visible, deviceTabs.Visibility);
                Assert.True(queueSuspendBtn.IsEnabled);
                Assert.True(clearQueueBtn.IsEnabled);
                Assert.Equal(Visibility.Visible, queueHintText.Visibility);
                Assert.Equal(Strings.Main_Queue_EmptyHint, queueHintText.Text);
            }
            finally
            {
                await CloseMainWindowCleanlyAsync(window, vm, ct);
            }

            bindingGuard.AssertNoErrors("MainWindow NoDeviceState");
        }));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void SettingsWindow_AllTabs_Localize_And_Measure_WithoutBindingErrors(string cultureName)
    {
        using var ws = new TestWorkspace("ui-smoke-settings");
        RunWithDataDirectory(ws.Root, () => RunOnStaAsync(async ct =>
        {
            using var _ = Strings.UseCultureScope(CultureInfo.GetCultureInfo(cultureName));
            using var bindingGuard = new DataBindingErrorScope();

            var cli = new FakeCliRunner();
            var afc = new FakeAfcHelper();
            var mainWindow = new MainWindow(cli, afc, scanRootOverride: ws.Root);
            var vm = Assert.IsType<MainViewModel>(mainWindow.DataContext);
            SettingsWindow? settingsWindow = null;

            try
            {
                settingsWindow = new SettingsWindow(vm, mainWindow);
                await MeasureAndLayoutAsync(settingsWindow);

                Assert.Equal(Strings.Settings_Title, settingsWindow.Title);
                var tabs = Assert.IsType<TabControl>(settingsWindow.FindName("Tabs"));
                Assert.Equal(9, tabs.Items.Count);

                var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header as string).ToList();
                Assert.Contains(Strings.Settings_Tab_Library, headers);
                Assert.Contains(Strings.Settings_Tab_Scan, headers);
                Assert.Contains(Strings.Settings_Tab_Icons, headers);
                Assert.Contains(Strings.Settings_Tab_Log, headers);
                Assert.Contains(Strings.Settings_Tab_Layout, headers);
                Assert.Contains(Strings.Settings_Tab_Queue, headers);
                Assert.Contains(Strings.Settings_Tab_Device, headers);
                Assert.Contains(Strings.Settings_Tab_Security, headers);
                Assert.Contains(Strings.Settings_Tab_About, headers);

                var expectedSecurityHeader = cultureName == "zh-CN" ? "安全" : "Security";
                Assert.Equal(expectedSecurityHeader, Strings.Settings_Tab_Security);

                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    await MeasureAndLayoutAsync(settingsWindow);
                }

                var allowOutsideCheck = Assert.IsType<CheckBox>(settingsWindow.FindName("AllowOutsideWriteCheck"));
                Assert.Equal(Strings.Settings_Security_AllowOutsideWrite, allowOutsideCheck.Content);
                Assert.False(allowOutsideCheck.IsChecked);

                var skipStartupScanCheck = Assert.IsType<CheckBox>(settingsWindow.FindName("SkipStartupScanCheck"));
                Assert.Equal(Strings.Settings_Scan_SkipStartup, skipStartupScanCheck.Content);
                Assert.False(skipStartupScanCheck.IsChecked);
            }
            finally
            {
                settingsWindow?.Close();
                await CloseMainWindowCleanlyAsync(mainWindow, vm, ct);
            }

            bindingGuard.AssertNoErrors($"SettingsWindow ({cultureName})");
        }));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void AdvancedFilterWindow_Constructs_Measures_And_Closes_WithoutBindingErrors(string cultureName)
    {
        using var ws = new TestWorkspace("ui-smoke-filter");
        RunWithDataDirectory(ws.Root, () => RunOnStaAsync(async ct =>
        {
            using var _ = Strings.UseCultureScope(CultureInfo.GetCultureInfo(cultureName));
            using var bindingGuard = new DataBindingErrorScope();

            var filterWindow = default(AdvancedFilterWindow);

            try
            {
                var sampleState = new AdvancedFilterState
                {
                    Enabled = true,
                    Groups =
                    {
                        new AdvancedFilterGroup
                        {
                            Join = FilterJoin.And,
                            Conditions =
                            {
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.Name, TextMode = TextFilterMode.Contains, Text = "App" },
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.Version, Min = "1.0", Max = "2.0" },
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.AppSizeMb, Min = "1", Max = "100" },
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.DeviceFamily, PhoneSide = "4\"", PadSide = "iPad" },
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.Arch, Selected = { "v7" } },
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.State, Selected = { "正常" } },
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.Origin, Selected = { "已解密" } },
                                new AdvancedFilterCondition { Column = AdvancedFilterColumn.Compatibility, Compatibility = CompatFilterMode.Installable },
                            },
                        },
                    },
                };

                // WPF rejects setting Window.Owner to an unshown Window; pass null when owner is off-screen.
                filterWindow = new AdvancedFilterWindow(sampleState, _ => { }, null!);
                await MeasureAndLayoutAsync(filterWindow);

                Assert.Equal(Strings.Filter_Title, filterWindow.Title);
                var groupsPanel = Assert.IsType<StackPanel>(filterWindow.FindName("GroupsPanel"));
                Assert.Single(groupsPanel.Children);
            }
            finally
            {
                filterWindow?.Close();
            }

            bindingGuard.AssertNoErrors($"AdvancedFilterWindow ({cultureName})");
            await Task.CompletedTask;
        }));
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public void InstallToWindow_WithSampleDevices_Binds_And_Closes_WithoutBindingErrors(string cultureName)
    {
        using var ws = new TestWorkspace("ui-smoke-install-to");
        RunWithDataDirectory(ws.Root, () => RunOnStaAsync(async ct =>
        {
            using var _ = Strings.UseCultureScope(CultureInfo.GetCultureInfo(cultureName));
            using var bindingGuard = new DataBindingErrorScope();

            var cli = new FakeCliRunner();
            var afc = new FakeAfcHelper();
            var vm = new MainViewModel(cli, afc, scanRootOverride: ws.Root, uiTimer: new DispatcherUiTimer(), pipeNameOverride: "");
            AddSampleDevices(vm, cli, afc);

            InstallToWindow? installToWindow = null;
            var closedFired = false;

            try
            {
                installToWindow = new InstallToWindow(vm);
                installToWindow.Closed += (_, _) => closedFired = true;
                await MeasureAndLayoutAsync(installToWindow);

                Assert.Equal(Strings.InstallTo_Title, installToWindow.Title);
                var choice = Assert.Single(installToWindow.Choices);
                Assert.Equal("online-udid-01", choice.Udid);
                Assert.False(installToWindow.AllSelected);

                installToWindow.AllSelected = true;
                await MeasureAndLayoutAsync(installToWindow);
                Assert.True(installToWindow.AllSelected);
                Assert.Equal(new[] { "online-udid-01" }, installToWindow.SelectedUdids);
            }
            finally
            {
                installToWindow?.Close();
                await vm.ShutdownAsync();
            }

            Assert.True(closedFired);
            bindingGuard.AssertNoErrors($"InstallToWindow ({cultureName})");
        }));
    }

    private static void AddSampleDevices(MainViewModel vm, ICliRunner cli, IAfcHelper afc)
    {
        var queue = new QueueService(
            cli,
            afc,
            new ReadbackService(cli),
            new Progress<QueueEvent>(_ => { }),
            new Progress<LogLine>(_ => { }));

        var online = new DeviceInfo
        {
            Udid = "online-udid-01",
            Name = "iPhone 5",
            Product = "iPhone5,2",
            Version = "8.1.2",
            Online = true,
        };
        var offline = new DeviceInfo
        {
            Udid = "offline-udid-02",
            Name = "iPad 2",
            Product = "iPad2,1",
            Version = "6.1.3",
            Online = false,
        };

        var onlineQueue = new DeviceQueueViewModel(queue, online);
        var offlineQueue = new DeviceQueueViewModel(queue, offline);
        vm.Devices.Add(onlineQueue);
        vm.Devices.Add(offlineQueue);
        vm.SelectedDevice = onlineQueue;
    }

    private static async Task MeasureAndLayoutAsync(Window window)
    {
        window.ApplyTemplate();
        window.Measure(new Size(SmokeWidth, SmokeHeight));
        window.Arrange(new Rect(0, 0, SmokeWidth, SmokeHeight));
        window.UpdateLayout();

        if (window.Content is UIElement content)
        {
            content.Measure(new Size(SmokeWidth, SmokeHeight));
            content.Arrange(new Rect(0, 0, SmokeWidth, SmokeHeight));
            content.UpdateLayout();
        }

        await Dispatcher.Yield(DispatcherPriority.Loaded);
    }

    private static async Task CloseMainWindowCleanlyAsync(MainWindow window, MainViewModel vm, CancellationToken ct)
    {
        window.Close();

        for (var i = 0; i < 100; i++)
        {
            if (vm.AllLogLines.Any(line => string.Equals(line.Text, "程序退出", StringComparison.Ordinal)))
                break;

            await Task.Delay(20, ct);
        }

        await vm.ShutdownAsync();
    }

    private static void RunWithDataDirectory(string dataDir, Action action)
    {
        var previousDataDir = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, dataDir);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvVar, previousDataDir);
        }
    }

    private static void RunOnStaAsync(Func<CancellationToken, Task> bodyAsync)
    {
        var ct = TestContext.Current.CancellationToken;
        Exception? captured = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            try
            {
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(new Action(() => _ = ExecuteAsync()));
                Dispatcher.PushFrame(frame);

                async Task ExecuteAsync()
                {
                    try
                    {
                        await bodyAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                }
            }
            catch (Exception ex)
            {
                captured ??= ex;
            }
            finally
            {
                try
                {
                    dispatcher.InvokeShutdown();
                }
                catch
                {
                    // Ignore dispatcher shutdown cleanup errors
                }

                done.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        done.Wait(ct);

        if (captured is not null)
        {
            ExceptionDispatchInfo.Throw(captured);
        }
    }

    private sealed class DataBindingErrorScope : TraceListener, IDisposable
    {
        private readonly SourceLevels _previousLevel;
        private readonly List<string> _errors = new();
        private readonly StringBuilder _currentLine = new();

        public DataBindingErrorScope()
        {
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            _previousLevel = source.Switch.Level;
            source.Switch.Level = SourceLevels.Error;
            source.Listeners.Add(this);
        }

        public override void Write(string? message)
        {
            if (message is not null) _currentLine.Append(message);
        }

        public override void WriteLine(string? message)
        {
            if (message is not null) _currentLine.Append(message);
            var text = _currentLine.ToString().Trim();
            _currentLine.Clear();
            if (text.Length > 0) _errors.Add(text);
        }

        public override void Fail(string? message) => WriteLine(message);

        public override void Fail(string? message, string? detailMessage)
            => WriteLine(string.IsNullOrEmpty(detailMessage) ? message : $"{message}: {detailMessage}");

        public void AssertNoErrors(string context)
        {
            if (_currentLine.Length > 0)
            {
                _errors.Add(_currentLine.ToString().Trim());
                _currentLine.Clear();
            }

            Assert.True(
                _errors.Count == 0,
                $"WPF DataBinding errors detected in {context}:{Environment.NewLine}{string.Join(Environment.NewLine, _errors)}");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var source = PresentationTraceSources.DataBindingSource;
                source.Listeners.Remove(this);
                source.Switch.Level = _previousLevel;
            }

            base.Dispose(disposing);
        }
    }
}
