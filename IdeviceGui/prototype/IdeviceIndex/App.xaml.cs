using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

public partial class App : Application
{
    internal const string SingleInstanceMutexName = "IdeviceIndex.SingleInstance.v1";

    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var cliLang = GetArgument(e.Args, "--lang");
        UiLanguage.Apply(cliLang, UiLanguage.SystemSetting);

        // Probe BEFORE UAC elevation so double-clicking when already running never prompts UAC needlessly (C39).
        if (!HasElevatedMarker(e.Args) && IsAnotherInstanceRunning())
        {
            MessageBox.Show(
                Strings.App_SingleInstance_AlreadyRunning,
                Strings.App_SingleInstance_Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        if (ShouldRequestElevation(e.Args) && TryRelaunchElevated(e.Args))
        {
            Shutdown(0);
            return;
        }

        // Acquire session-scoped single-instance mutex BEFORE any disk read/write.
        if (!TryAcquireSingleInstanceMutex(SingleInstanceMutexName, out _singleInstance))
        {
            MessageBox.Show(
                Strings.App_SingleInstance_AlreadyRunning,
                Strings.App_SingleInstance_Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // Redirect SQLite/process temp dirs into <programDir>\data\tmp (C25/C27).
        if (!TryUseProgramTempDirectory(AppContext.BaseDirectory, out var tempWarning) &&
            !string.IsNullOrWhiteSpace(tempWarning))
        {
            MessageBox.Show(
                tempWarning,
                Strings.App_Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var savedLang = IndexerSettings.LoadLanguage();
        UiLanguage.Apply(cliLang, savedLang);

        var rawDbPath = GetArgument(e.Args, "--db");
        var resolvedDbPath = IndexImporter.ResolveStartupDbPath(rawDbPath);
        var effectiveDbPath = File.Exists(resolvedDbPath) ? resolvedDbPath : null;

        try
        {
            var window = new MainWindow(effectiveDbPath);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Strings.Format(nameof(Strings.App_DbOpenFailedFormat), ex.Message),
                Strings.App_Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
        }
    }

    internal static bool HasElevatedMarker(string[] args)
        => args.Any(a => string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase));

    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(SingleInstanceMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool ShouldRequestElevation(string[] args, string? programDir = null)
    {
        if (Environment.GetEnvironmentVariable("IDEVICEGUI_SKIP_ELEVATION") == "1") return false;

        var baseDir = string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir;
        try
        {
            if (File.Exists(Path.Combine(baseDir, "data", ".skip-elevation"))) return false;
        }
        catch
        {
            // Ignore malformed path checks
        }

        if (HasElevatedMarker(args)) return false;
        return !IsElevated();
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = BuildRelaunchArguments(args),
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User declined UAC prompt -> fall back cleanly to standard privileges
            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildRelaunchArguments(string[] args)
    {
        var list = new List<string> { "--elevated" };
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--elevated", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(QuoteArg(arg));
        }
        return string.Join(' ', list);
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;

        var sb = new StringBuilder(arg.Length + 8);
        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(ch);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    internal static bool TryUseProgramTempDirectory(string programDir, out string? warningMessage)
    {
        warningMessage = null;
        var tmpDir = Path.Combine(programDir, "data", "tmp");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var probe = Path.Combine(tmpDir, $".write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);

            Environment.SetEnvironmentVariable("TEMP", tmpDir);
            Environment.SetEnvironmentVariable("TMP", tmpDir);
            Environment.SetEnvironmentVariable("SQLITE_TMPDIR", tmpDir);
            return true;
        }
        catch (Exception ex)
        {
            warningMessage = Strings.Format(nameof(Strings.App_TempDirWarningFormat), tmpDir, ex.Message);
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceMutex(ref _singleInstance);
        base.OnExit(e);
    }

    internal static bool TryAcquireSingleInstanceMutex(string mutexName, out Mutex? mutex)
    {
        mutex = null;
        try
        {
            var acquired = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (createdNew)
            {
                mutex = acquired;
                return true;
            }

            acquired.Dispose();
            return false;
        }
        catch
        {
            return true;
        }
    }

    internal static void ReleaseSingleInstanceMutex(ref Mutex? mutex)
    {
        var current = mutex;
        mutex = null;
        if (current is null) return;

        try
        {
            current.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Ignore when not owned by current thread
        }
        finally
        {
            current.Dispose();
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim('"');
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..].Trim('"');
        }
        return null;
    }
}
