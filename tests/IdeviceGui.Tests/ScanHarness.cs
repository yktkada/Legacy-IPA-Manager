using System.IO;
using IdeviceGui.Services.Data;
using IdeviceGui.Services.Scanning;

namespace IdeviceGui.Tests;

/// <summary>扫描测试夹具：临时目录 + 真库（临时文件）+ 扫描器，一根线搭好。</summary>
internal sealed class ScanHarness : IDisposable
{
    public TestWorkspace Ws { get; }
    public AppDatabase Db { get; }
    public IpaScanner Scanner { get; private set; }
    public ScanRootRow Root { get; }

    public ScanHarness(string tag = "scan")
    {
        Ws = new TestWorkspace(tag);
        Db = AppDatabase.Open(Ws.Full("library.db"));
        var (root, error) = ScanRootStore.Add(Db, Ws.Root);
        Root = root ?? throw new InvalidOperationException(error);
        Scanner = new IpaScanner(Db, new ScanOptions());
    }

    /// <summary>直接换一个已配好的扫描器实例（如图标缓存目录）。</summary>
    public void ReplaceScannerInstance(IpaScanner scanner) => Scanner = scanner;

    /// <summary>M4-a：替换扫描器（注入分批回调 / 批次大小）。</summary>
    public void ReplaceScanner(Action<IReadOnlyList<PackageRow>>? onRowsParsed = null,
        int batchSize = ScanOptions.DefaultBatchSize)
        => Scanner = new IpaScanner(Db, new ScanOptions { BatchSize = batchSize }, onRowsParsed: onRowsParsed);

    public ScanResult Scan(bool forceReparse = false) =>
        Scanner.ScanAsync(Root.Id, Root.Path, forceReparse: forceReparse).GetAwaiter().GetResult();

    public List<PackageRow> Rows() => PackageStore.LoadByRoot(Db, Root.Id);

    /// <summary>按**相对路径**取唯一一行（不存在时抛，便于断言消息定位）。</summary>
    public PackageRow Row(string relativeName)
    {
        var full = Path.GetFullPath(Ws.Full(relativeName));
        var rows = Rows().Where(r =>
            string.Equals(Path.GetFullPath(r.FilePath), full, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count != 1)
            throw new InvalidOperationException($"期望 {relativeName} 恰好一行，实际 {rows.Count} 行");
        return rows[0];
    }

    public void Dispose()
    {
        Db.Dispose();
        Ws.Dispose();
    }
}
