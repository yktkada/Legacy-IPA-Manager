using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdeviceIndex;

internal sealed record Aria2StatusSnapshot(
    string Gid,
    string Status,
    long TotalLength,
    long CompletedLength,
    long DownloadSpeed,
    int Connections,
    string? ErrorCode,
    string? ErrorMessage,
    string? FilePath);

internal sealed class Aria2RpcException(int code, string message) : Exception($"aria2 RPC error {code}: {message}")
{
    public int Code { get; } = code;
}

/// <summary>
/// aria2 JSON-RPC 2.0 客户端（仅走 127.0.0.1，强制关闭 HttpClient 代理避免本机回环被系统代理劫持）。
/// </summary>
internal sealed class Aria2RpcClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _tokenParam;
    private int _requestId;

    public Aria2RpcClient(int port, string secret)
    {
        _endpoint = new Uri($"http://127.0.0.1:{port}/jsonrpc");
        _tokenParam = $"token:{secret}";
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("aria2.getVersion", new JsonArray { _tokenParam }, ct).ConfigureAwait(false);
        return result?["version"]?.GetValue<string>() ?? "";
    }

    public async Task<string> AddUriAsync(
        string uri,
        IReadOnlyDictionary<string, string> options,
        CancellationToken ct = default)
    {
        var uris = new JsonArray { uri };
        var optObj = new JsonObject();
        foreach (var (k, v) in options)
            optObj[k] = v;

        var result = await CallAsync("aria2.addUri", new JsonArray { _tokenParam, uris, optObj }, ct).ConfigureAwait(false);
        return result?.GetValue<string>() ?? throw new InvalidOperationException("aria2.addUri returned null gid.");
    }

    public async Task<Aria2StatusSnapshot> TellStatusAsync(string gid, CancellationToken ct = default)
    {
        var keys = new JsonArray
        {
            "gid",
            "status",
            "totalLength",
            "completedLength",
            "downloadSpeed",
            "connections",
            "errorCode",
            "errorMessage",
            "files",
        };
        var result = await CallAsync("aria2.tellStatus", new JsonArray { _tokenParam, gid, keys }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("aria2.tellStatus returned null.");

        string? firstPath = null;
        if (result["files"] is JsonArray files && files.Count > 0)
            firstPath = files[0]?["path"]?.GetValue<string>();

        return new Aria2StatusSnapshot(
            result["gid"]?.GetValue<string>() ?? gid,
            result["status"]?.GetValue<string>() ?? "",
            ParseLong(result["totalLength"]),
            ParseLong(result["completedLength"]),
            ParseLong(result["downloadSpeed"]),
            (int)Math.Clamp(ParseLong(result["connections"]), 0, int.MaxValue),
            result["errorCode"]?.GetValue<string>(),
            result["errorMessage"]?.GetValue<string>(),
            firstPath);
    }

    public Task PauseAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.pause", new JsonArray { _tokenParam, gid }, ct);

    public Task UnpauseAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.unpause", new JsonArray { _tokenParam, gid }, ct);

    public Task ForceRemoveAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.forceRemove", new JsonArray { _tokenParam, gid }, ct);

    public Task RemoveDownloadResultAsync(string gid, CancellationToken ct = default)
        => CallAsync("aria2.removeDownloadResult", new JsonArray { _tokenParam, gid }, ct);

    public Task ChangeGlobalOptionAsync(IReadOnlyDictionary<string, string> options, CancellationToken ct = default)
    {
        var optObj = new JsonObject();
        foreach (var (k, v) in options)
            optObj[k] = v;
        return CallAsync("aria2.changeGlobalOption", new JsonArray { _tokenParam, optObj }, ct);
    }

    public Task ForceShutdownAsync(CancellationToken ct = default)
        => CallAsync("aria2.forceShutdown", new JsonArray { _tokenParam }, ct);

    public static Dictionary<string, string> ResolveProxyOptions(DownloadSettings settings, string targetUrl)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (settings.ProxyMode)
        {
            case DownloadSettings.ProxyNone:
                options["all-proxy"] = "";
                options["no-proxy"] = "*";
                break;

            case DownloadSettings.ProxyManual:
                if (!string.IsNullOrWhiteSpace(settings.ProxyUrl))
                    options["all-proxy"] = settings.ProxyUrl.Trim();
                var manualNoProxy = FormatAria2NoProxy(settings.ProxyBypass);
                if (!string.IsNullOrEmpty(manualNoProxy))
                    options["no-proxy"] = manualNoProxy;
                break;

            default:
                if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri))
                {
                    var sysProxy = HttpClient.DefaultProxy;
                    if (!sysProxy.IsBypassed(targetUri))
                    {
                        var proxyUri = sysProxy.GetProxy(targetUri);
                        if (proxyUri is not null && proxyUri != targetUri)
                            options["all-proxy"] = proxyUri.ToString();
                    }
                }
                var systemNoProxy = FormatAria2NoProxy(settings.ProxyBypass);
                if (!string.IsNullOrEmpty(systemNoProxy))
                    options["no-proxy"] = systemNoProxy;
                break;
        }
        return options;
    }

    internal static string FormatAria2NoProxy(string? proxyBypass)
    {
        if (string.IsNullOrWhiteSpace(proxyBypass)) return "";

        var tokens = proxyBypass.Split([';', ',', '\r', '\n', '\t', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return "";

        var result = new List<string>(tokens.Length + 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUnique(string host)
        {
            if (seen.Add(host)) result.Add(host);
        }

        foreach (var raw in tokens)
        {
            if (string.Equals(raw, "<local>", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique("localhost");
                AddUnique("127.0.0.1");
                AddUnique("::1");
                continue;
            }
            AddUnique(raw);
        }

        return string.Join(",", result);
    }

    private async Task<JsonNode?> CallAsync(string method, JsonArray parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId).ToString(CultureInfo.InvariantCulture);
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid JSON-RPC response ({(int)response.StatusCode}): {ex.Message}", ex);
        }

        if (doc?["error"] is JsonObject err)
        {
            var code = err["code"]?.GetValue<int>() ?? -1;
            var msg = err["message"]?.GetValue<string>() ?? "unknown RPC error";
            throw new Aria2RpcException(code, msg);
        }

        response.EnsureSuccessStatusCode();
        return doc?["result"];
    }

    private static long ParseLong(JsonNode? node)
    {
        if (node is not JsonValue jv) return 0;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }

    public void Dispose() => _http.Dispose();
}
