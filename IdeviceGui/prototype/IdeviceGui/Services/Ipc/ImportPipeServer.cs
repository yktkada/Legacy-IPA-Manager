using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdeviceGui.Services.Scanning;

namespace IdeviceGui.Services.Ipc;

internal sealed record PipeHelloInfo(
    string AppVersion,
    int Protocol,
    string LibraryPath,
    IReadOnlyList<string> Roots);

internal sealed record PipeRefreshInfo(
    bool Ok,
    int Added,
    string Summary,
    string? Error = null);

internal interface IImportPipeHandler
{
    PipeHelloInfo GetHelloInfo();
    Task<PipeRefreshInfo> RefreshAsync(CancellationToken ct);
    Task<ImportFileResult> ImportFileAsync(string path, CancellationToken ct);
}

/// <summary>
/// 主程序本地命名管道服务端（<c>IdeviceGui.Import.v1</c>，docs/12 §3.2 / §3.3 / §3.5）：
/// 仅接受同用户本地连接，one-shot 短连接，只开放 hello / refresh / import 三个操作。
/// </summary>
internal sealed class ImportPipeServer : IAsyncDisposable
{
    public const string DefaultPipeName = "IdeviceGui.Import.v1";
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 65536;
    private static readonly TimeSpan RequestIoTimeout = TimeSpan.FromSeconds(120);

    private readonly IImportPipeHandler _handler;
    private readonly string _pipeName;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Task> _inFlight = new();
    private int _nextClientId;
    private Task? _acceptTask;
    private bool _disposed;

    public ImportPipeServer(
        IImportPipeHandler handler,
        string? pipeName = null,
        Action<string>? log = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        _log = log;
    }

    public string PipeName => _pipeName;

    public bool HasInFlightRequests => !_inFlight.IsEmpty;

    public void Start()
    {
        if (_disposed || _acceptTask is not null) return;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = CreateServerStream();
                await stream.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                var connected = stream;
                stream = null; // 移交给客户端任务释放
                var clientId = Interlocked.Increment(ref _nextClientId);
                // 先登记 _inFlight 再放行任务移除，消除短请求提前结束导致 _inFlight 残留的竞态
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var clientTask = Task.Run(() => HandleClientTrackedAsync(clientId, connected, registered.Task));
                _inFlight[clientId] = clientTask;
                registered.SetResult();
            }
            catch (OperationCanceledException)
            {
                stream?.Dispose();
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                stream?.Dispose();
                _log?.Invoke($"IPC 管道权限受限（同用户不同完整性级别或 ACL 拒绝）：{ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                stream?.Dispose();
                _log?.Invoke($"IPC 管道监听异常：{ex.GetType().Name}: {ex.Message}");
                try
                {
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientTrackedAsync(int clientId, NamedPipeServerStream stream, Task registered)
    {
        try
        {
            await registered.ConfigureAwait(false);
            await HandleClientAsync(stream).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(clientId, out _);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        reqCts.CancelAfter(RequestIoTimeout);
        var ct = reqCts.Token;

        JsonObject response;
        try
        {
            var payload = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (payload is null)
            {
                response = ErrorObject("invalid_request");
            }
            else
            {
                response = await DispatchAsync(payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            response = ErrorObject("timeout");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"IPC 请求处理异常：{ex.GetType().Name}: {ex.Message}");
            response = ErrorObject("invalid_request");
        }

        try
        {
            await WriteFrameAsync(stream, response, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 客户端提前断开时忽略写回异常
        }
    }

    private async Task<JsonObject> DispatchAsync(byte[] utf8Json, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch
        {
            return ErrorObject("invalid_request");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ErrorObject("invalid_request");

            if (!root.TryGetProperty("v", out var versionEl) ||
                versionEl.ValueKind != JsonValueKind.Number ||
                !versionEl.TryGetInt32(out var version) ||
                version != ProtocolVersion)
            {
                return ErrorObject("protocol_mismatch");
            }

            if (!root.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
                return ErrorObject("invalid_request");

            var op = opEl.GetString()?.Trim();
            switch (op)
            {
                case "hello":
                {
                    var info = _handler.GetHelloInfo();
                    var rootsArray = new JsonArray();
                    foreach (var r in info.Roots)
                        rootsArray.Add(r);

                    return new JsonObject
                    {
                        ["v"] = ProtocolVersion,
                        ["ok"] = true,
                        ["app"] = info.AppVersion,
                        ["protocol"] = info.Protocol,
                        ["library"] = info.LibraryPath,
                        ["roots"] = rootsArray,
                    };
                }

                case "refresh":
                {
                    var res = await _handler.RefreshAsync(ct).ConfigureAwait(false);
                    if (!res.Ok)
                        return ErrorObject(res.Error ?? "busy");

                    return new JsonObject
                    {
                        ["v"] = ProtocolVersion,
                        ["ok"] = true,
                        ["added"] = res.Added,
                        ["summary"] = res.Summary,
                    };
                }

                case "import":
                {
                    if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                        return ErrorObject("invalid_request");

                    var path = pathEl.GetString();
                    if (string.IsNullOrWhiteSpace(path))
                        return ErrorObject("invalid_request");

                    var res = await _handler.ImportFileAsync(path, ct).ConfigureAwait(false);
                    if (!res.Ok)
                        return ErrorObject(res.Error ?? "parse_failed");

                    return new JsonObject
                    {
                        ["v"] = ProtocolVersion,
                        ["ok"] = true,
                        ["packageId"] = res.PackageId,
                        ["bundleId"] = res.BundleId ?? "",
                        ["name"] = res.Name ?? "",
                        ["version"] = res.Version ?? "",
                    };
                }

                default:
                    return ErrorObject("invalid_request");
            }
        }
    }

    /// <summary>
    /// 创建本地命名管道实例。保持 <see cref="PipeOptions.CurrentUserOnly"/> 以强制同用户隔离；
    /// 若主程序以管理员提权启动而客户端未提权，Windows 默认完整性级别会拒绝跨级别连接（客户端需匹配权限）。
    /// </summary>
    private NamedPipeServerStream CreateServerStream() => new(
        _pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await TryReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
            return null;

        var buffer = new byte[length];
        if (!await TryReadExactAsync(stream, buffer, ct).ConfigureAwait(false))
            return null;

        return buffer;
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static async Task WriteFrameAsync(Stream stream, JsonObject obj, CancellationToken ct)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
        if (jsonBytes.Length > MaxFrameBytes)
            jsonBytes = Encoding.UTF8.GetBytes(ErrorObject("invalid_request").ToJsonString());

        var frame = new byte[4 + jsonBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), jsonBytes.Length);
        Buffer.BlockCopy(jsonBytes, 0, frame, 4, jsonBytes.Length);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private static JsonObject ErrorObject(string errorCode) => new()
    {
        ["v"] = ProtocolVersion,
        ["ok"] = false,
        ["error"] = errorCode,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 关闭管道接受循环不抛异常
            }
        }

        var remaining = _inFlight.Values.ToArray();
        if (remaining.Length > 0)
        {
            try
            {
                await Task.WhenAll(remaining).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 关闭在途请求不抛异常
            }
        }

        _cts.Dispose();
    }
}
