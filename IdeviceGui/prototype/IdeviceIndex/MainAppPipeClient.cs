using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdeviceIndex;

internal sealed record PipeHelloResult(
    bool Ok,
    string? Error,
    string AppVersion,
    int Protocol,
    string LibraryPath,
    IReadOnlyList<string> Roots);

internal sealed record PipeRefreshResult(
    bool Ok,
    string? Error,
    int Added,
    string Summary);

internal sealed record PipeImportResult(
    bool Ok,
    string? Error,
    long PackageId,
    string BundleId,
    string Name,
    string Version);

/// <summary>
/// 独立命名管道客户端（docs/12 §3.2 / §3.3）：
/// 只与同用户 <c>IdeviceGui.Import.v1</c> 通信，不引入主程序引用。
/// </summary>
internal sealed class MainAppPipeClient
{
    public const string DefaultPipeName = "IdeviceGui.Import.v1";
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 65536;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(120);

    private readonly string _pipeName;

    public MainAppPipeClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
    }

    public async Task<PipeHelloResult> HelloAsync(TimeSpan? connectTimeout = null, CancellationToken ct = default)
    {
        var req = new JsonObject
        {
            ["v"] = ProtocolVersion,
            ["op"] = "hello",
        };
        var (ok, err, doc) = await SendOneShotAsync(
            req,
            connectTimeout ?? DefaultConnectTimeout,
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        if (!ok || doc is null)
            return new PipeHelloResult(false, err ?? "main_not_running", "", 0, "", []);

        using (doc)
        {
            var root = doc.RootElement;
            if (!TryCheckEnvelope(root, out var envErr))
                return new PipeHelloResult(false, envErr, "", 0, "", []);

            var protocol = root.TryGetProperty("protocol", out var pEl) && pEl.TryGetInt32(out var pVal) ? pVal : 0;
            if (protocol != ProtocolVersion)
                return new PipeHelloResult(false, "protocol_mismatch", "", protocol, "", []);

            var app = ReadString(root, "app");
            var lib = ReadString(root, "library");
            var roots = new List<string>();
            if (root.TryGetProperty("roots", out var rootsEl) && rootsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rootsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var val = item.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) roots.Add(val);
                    }
                }
            }

            return new PipeHelloResult(true, null, app, protocol, lib, roots);
        }
    }

    public async Task<PipeRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var req = new JsonObject
        {
            ["v"] = ProtocolVersion,
            ["op"] = "refresh",
        };
        var (ok, err, doc) = await SendOneShotAsync(
            req,
            DefaultConnectTimeout,
            DefaultOperationTimeout,
            ct).ConfigureAwait(false);

        if (!ok || doc is null)
            return new PipeRefreshResult(false, err ?? "main_not_running", 0, "");

        using (doc)
        {
            var root = doc.RootElement;
            if (!TryCheckEnvelope(root, out var envErr))
                return new PipeRefreshResult(false, envErr, 0, "");

            var added = root.TryGetProperty("added", out var aEl) && aEl.TryGetInt32(out var aVal) ? aVal : 0;
            var summary = ReadString(root, "summary");
            return new PipeRefreshResult(true, null, added, summary);
        }
    }

    public async Task<PipeImportResult> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var req = new JsonObject
        {
            ["v"] = ProtocolVersion,
            ["op"] = "import",
            ["path"] = filePath,
        };
        var (ok, err, doc) = await SendOneShotAsync(
            req,
            DefaultConnectTimeout,
            DefaultOperationTimeout,
            ct).ConfigureAwait(false);

        if (!ok || doc is null)
            return new PipeImportResult(false, err ?? "main_not_running", 0, "", "", "");

        using (doc)
        {
            var root = doc.RootElement;
            if (!TryCheckEnvelope(root, out var envErr))
                return new PipeImportResult(false, envErr, 0, "", "", "");

            var pkgId = root.TryGetProperty("packageId", out var idEl) && idEl.TryGetInt64(out var idVal) ? idVal : 0;
            return new PipeImportResult(
                true,
                null,
                pkgId,
                ReadString(root, "bundleId"),
                ReadString(root, "name"),
                ReadString(root, "version"));
        }
    }

    public static bool IsPathInsideAnyRoot(string path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || roots.Count == 0) return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var normRoot = Path.GetFullPath(root.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normRoot.Length == 2 && normRoot[1] == ':')
                    normRoot += Path.DirectorySeparatorChar;
                var prefix = normRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? normRoot
                    : normRoot + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // 忽略单条坏扫描根路径
            }
        }
        return false;
    }

    private async Task<(bool Ok, string? Error, JsonDocument? Doc)> SendOneShotAsync(
        JsonObject request,
        TimeSpan connectTimeout,
        TimeSpan ioTimeout,
        CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(connectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "main_not_running", null);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return (false, "main_not_running", null);
        }

        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ioCts.CancelAfter(ioTimeout);
        var ioCt = ioCts.Token;

        try
        {
            var reqBytes = Encoding.UTF8.GetBytes(request.ToJsonString());
            if (reqBytes.Length <= 0 || reqBytes.Length > MaxFrameBytes)
                return (false, "invalid_request", null);

            var frame = new byte[4 + reqBytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), reqBytes.Length);
            Buffer.BlockCopy(reqBytes, 0, frame, 4, reqBytes.Length);
            await pipe.WriteAsync(frame, ioCt).ConfigureAwait(false);
            await pipe.FlushAsync(ioCt).ConfigureAwait(false);

            var header = new byte[4];
            if (!await TryReadExactAsync(pipe, header, ioCt).ConfigureAwait(false))
                return (false, "main_not_running", null);

            var len = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (len <= 0 || len > MaxFrameBytes)
                return (false, "invalid_response", null);

            var respBytes = new byte[len];
            if (!await TryReadExactAsync(pipe, respBytes, ioCt).ConfigureAwait(false))
                return (false, "main_not_running", null);

            var doc = JsonDocument.Parse(respBytes, new JsonDocumentOptions { MaxDepth = 16 });
            return (true, null, doc);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "timeout", null);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return (false, "main_not_running", null);
        }
    }

    private static bool TryCheckEnvelope(JsonElement root, out string error)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "invalid_response";
            return false;
        }

        if (root.TryGetProperty("v", out var vEl) && vEl.TryGetInt32(out var v) && v != ProtocolVersion)
        {
            error = "protocol_mismatch";
            return false;
        }

        var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            error = ReadString(root, "error");
            if (string.IsNullOrWhiteSpace(error)) error = "unknown";
            return false;
        }

        error = "";
        return true;
    }

    private static string ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";

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
}
