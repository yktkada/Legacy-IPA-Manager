using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

/// <summary>
/// 按需启动已通过四步校验的 <c>aria2c.exe</c> RPC 守护进程（严格绑定 127.0.0.1 + 随机密钥），
/// 并在释放时先发 forceShutdown、超时再整树强杀，确保零孤儿进程。
/// </summary>
internal sealed class Aria2Daemon : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdoutDrain;
    private readonly Task<string> _stderrDrain;
    private bool _disposed;

    public int Port { get; }
    public string Secret { get; }
    public string SessionFilePath { get; }
    public Aria2RpcClient Rpc { get; }
    public bool HasExited => _process.HasExited;

    private Aria2Daemon(
        Process process,
        Task stdoutDrain,
        Task<string> stderrDrain,
        int port,
        string secret,
        string sessionFilePath,
        Aria2RpcClient rpc)
    {
        _process = process;
        _stdoutDrain = stdoutDrain;
        _stderrDrain = stderrDrain;
        Port = port;
        Secret = secret;
        SessionFilePath = sessionFilePath;
        Rpc = rpc;
    }

    public static async Task<Aria2Daemon> StartAsync(
        Aria2VerifyResult verified,
        DownloadSettings settings,
        string programDir,
        string? settingsPath = null,
        CancellationToken ct = default)
    {
        if (!verified.Ok || string.IsNullOrWhiteSpace(verified.ExePath) || !File.Exists(verified.ExePath))
        {
            throw new InvalidOperationException(verified.Message
                ?? Strings.Format(nameof(Strings.Download_Error_DaemonFailedFormat), "aria2 not verified"));
        }

        var port = PickFreeLoopbackPort();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var sessionFile = DownloadPathPolicy.ResolveSessionFilePath(settings, programDir, settingsPath);

        // Delete any stale aria2.session before starting aria2c.exe so untracked GIDs are never resumed.
        CleanupStaleSessionFile(sessionFile);
        var defaultSessionFile = Path.Combine(Path.GetFullPath(programDir), "data", "aria2.session");
        if (!string.Equals(sessionFile, defaultSessionFile, StringComparison.OrdinalIgnoreCase))
            CleanupStaleSessionFile(defaultSessionFile);

        var psi = new ProcessStartInfo(verified.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(verified.ExePath) ?? programDir,
        };

        foreach (var arg in BuildArguments(settings, port, secret, sessionFile))
            psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException(
            Strings.Format(nameof(Strings.Download_Error_DaemonFailedFormat), "Process.Start returned null"));

        var stdoutDrain = DrainAsync(process.StandardOutput);
        var stderrDrain = DrainCaptureAsync(process.StandardError);
        var rpc = new Aria2RpcClient(port, secret);
        var daemon = new Aria2Daemon(process, stdoutDrain, stderrDrain, port, secret, sessionFile, rpc);

        try
        {
            await WaitForRpcReadyAsync(daemon, ct).ConfigureAwait(false);
            return daemon;
        }
        catch
        {
            await daemon.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WaitForRpcReadyAsync(Aria2Daemon daemon, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 5000;
        Exception? lastEx = null;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (daemon._process.HasExited)
            {
                var errText = await Task.WhenAny(daemon._stderrDrain, Task.Delay(300, ct)).ConfigureAwait(false) == daemon._stderrDrain &&
                              daemon._stderrDrain.IsCompletedSuccessfully
                    ? daemon._stderrDrain.Result
                    : "";
                throw new InvalidOperationException(
                    Strings.Format(nameof(Strings.Download_Error_DaemonFailedFormat),
                        $"exit={daemon._process.ExitCode} {errText}".Trim()));
            }

            try
            {
                await daemon.Rpc.GetVersionAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            Strings.Format(nameof(Strings.Download_Error_DaemonFailedFormat), lastEx?.Message ?? "RPC handshake timeout"));
    }

    internal static IReadOnlyList<string> BuildArguments(
        DownloadSettings settings,
        int port,
        string secret,
        string sessionFile)
    {
        var args = new List<string>
        {
            "--enable-rpc=true",
            "--rpc-listen-all=false",
            $"--rpc-listen-port={port.ToString(CultureInfo.InvariantCulture)}",
            $"--rpc-secret={secret}",
            "--auto-file-renaming=false",
            "--allow-overwrite=true",
            "--file-allocation=none",
            $"--max-concurrent-downloads={settings.Concurrency.ToString(CultureInfo.InvariantCulture)}",
            $"--max-connection-per-server={settings.Connections.ToString(CultureInfo.InvariantCulture)}",
            $"--split={settings.Splits.ToString(CultureInfo.InvariantCulture)}",
            $"--min-split-size={settings.MinSplitSize}",
            $"--max-overall-download-limit={settings.SpeedLimit}",
            $"--max-download-limit={settings.PerTaskSpeedLimit}",
            "--max-tries=1",
            $"--retry-wait={settings.RetryWait.ToString(CultureInfo.InvariantCulture)}",
            $"--timeout={settings.Timeout.ToString(CultureInfo.InvariantCulture)}",
            $"--connect-timeout={settings.Timeout.ToString(CultureInfo.InvariantCulture)}",
            $"--continue={(settings.Continue ? "true" : "false")}",
            $"--user-agent={settings.UserAgent}",
            $"--stop-with-process={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}",
            "--auto-save-interval=30",
        };

        if (string.Equals(settings.ControlFileMode, DownloadSettings.ControlSession, StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"--save-session={sessionFile}");
            args.Add("--save-session-interval=30");
        }

        args.Add("--quiet=true");
        return args;
    }

    private static void CleanupStaleSessionFile(string sessionFile)
    {
        try
        {
            DownloadPathPolicy.TryDeleteSingleFile(sessionFile);
        }
        catch
        {
            // Ignore locked or missing stale session file
        }
    }

    private static int PickFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // 持续抽干管道，防止阻塞子进程
            }
        }
        catch
        {
            // 进程退出或流关闭时忽略
        }
    }

    private static async Task<string> DrainCaptureAsync(StreamReader reader)
    {
        try
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                try
                {
                    await Rpc.ForceShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // 忽略关闭通知超时
                }

                using var waitCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                try
                {
                    await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // 超时后强杀进程树
                }
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch
        {
            // 清理阶段不抛异常
        }
        finally
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(_stdoutDrain, _stderrDrain), Task.Delay(500)).ConfigureAwait(false);
            }
            catch
            {
                // 忽略流抽干结束异常
            }
            Rpc.Dispose();
            _process.Dispose();
        }
    }
}
