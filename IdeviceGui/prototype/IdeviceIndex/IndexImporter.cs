using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdeviceIndex.Resources;
using Microsoft.Data.Sqlite;

namespace IdeviceIndex;

public enum IndexImportMode
{
    FullReplace,
    Incremental,
}

public sealed record IndexDbValidationResult(
    bool IsValid,
    long EntryCount,
    string? Revision,
    string? SchemaVersion,
    string? ErrorMessage);

public sealed record IndexImportProgress(
    string Stage,
    long ProcessedCount,
    long? TotalCount,
    string? Message);

public sealed record IndexImportOptions(
    IndexImportMode Mode,
    bool MergeSources = true,
    bool VerifyBaseRevision = true);

public sealed record IndexImportResult(
    string TargetDbPath,
    long TotalEntries,
    long UpsertedCount,
    long DeletedCount,
    string? Revision,
    bool WasIncremental);

/// <summary>
/// Pure background importer for local `index.db` files and `manifest.json` / `.jsonl.gz` / `.jsonl` / `.json`
/// index packages (full rebuild + single-transaction incremental upsert/delete). Zero UI references.
/// </summary>
internal static class IndexImporter
{
    public const string ExpectedManifestSchema = "legacy-ipa-index";
    public const int ExpectedSchemaVersion = 1;

    private static readonly string[] RequiredTables = ["meta", "entries", "descriptions", "sources"];

    private static readonly Dictionary<string, int> DeviceBits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iPhone"] = 2,
        ["iPad"] = 4,
        ["AppleTV"] = 8,
    };

    private static readonly Dictionary<string, int> ArchBits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["armv6"] = 1,
        ["armv7"] = 2,
        ["armv7s"] = 4,
        ["armv7f"] = 8,
        ["arm64"] = 16,
        ["arm64e"] = 32,
        ["x86_64"] = 64,
    };

    public static string DefaultDbPath(string? programDir = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir;
        return Path.Combine(baseDir, "index", "index.db");
    }

    public static string ResolveStartupDbPath(string? cliDbArg, string? settingsPath = null, string? programDir = null)
    {
        if (!string.IsNullOrWhiteSpace(cliDbArg))
            return Path.GetFullPath(cliDbArg.Trim());

        var custom = IndexerSettings.LoadCustomDbPath(settingsPath);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom);
            }
            catch
            {
                // Fall back to default location if stored custom path is malformed.
            }
        }

        return DefaultDbPath(programDir);
    }

    public static IndexDbValidationResult ValidateDatabase(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            return new IndexDbValidationResult(false, 0, null, null, Strings.Import_Error_DbMissing);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(dbPath.Trim());
        }
        catch (Exception ex)
        {
            return new IndexDbValidationResult(false, 0, null, null, ex.Message);
        }

        if (!File.Exists(fullPath))
            return new IndexDbValidationResult(false, 0, null, null, Strings.Import_Error_DbMissing);

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();

            using var conn = new SqliteConnection(connStr);
            conn.Open();

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var tableCmd = conn.CreateCommand())
            {
                tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var reader = tableCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        tables.Add(reader.GetString(0));
                }
            }

            foreach (var required in RequiredTables)
            {
                if (!tables.Contains(required))
                {
                    return new IndexDbValidationResult(
                        false, 0, null, null,
                        Strings.Format(nameof(Strings.Import_Error_DbMissingTableFormat), required));
                }
            }

            string? schemaVer = null;
            string? revision = null;
            using (var metaCmd = conn.CreateCommand())
            {
                metaCmd.CommandText = "SELECT key, value FROM meta WHERE key IN ('schema_version', 'revision')";
                using var reader = metaCmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var val = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (string.Equals(key, "schema_version", StringComparison.Ordinal))
                        schemaVer = val;
                    else if (string.Equals(key, "revision", StringComparison.Ordinal))
                        revision = val;
                }
            }

            if (!string.Equals(schemaVer, ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return new IndexDbValidationResult(
                    false, 0, revision, schemaVer,
                    Strings.Format(nameof(Strings.Import_Error_SchemaMismatchFormat), schemaVer ?? "?"));
            }

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM entries";
            var count = Convert.ToInt64(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            return new IndexDbValidationResult(true, count, revision, schemaVer, null);
        }
        catch (Exception ex)
        {
            return new IndexDbValidationResult(false, 0, null, null, ex.Message);
        }
    }

    public static async Task<IndexDbValidationResult> ImportDatabaseFileAsync(
        string sourceDbPath,
        string programDir,
        bool copyToProgramDir,
        string? settingsPath = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var fullSource = Path.GetFullPath(sourceDbPath.Trim());
            var validation = ValidateDatabase(fullSource);
            if (!validation.IsValid)
                throw new InvalidDataException(validation.ErrorMessage ?? Strings.Import_Error_DbInvalid);

            if (!copyToProgramDir)
            {
                IndexerSettings.SaveCustomDbPath(fullSource, settingsPath);
                return validation;
            }

            var targetPath = DefaultDbPath(programDir);
            var targetDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(targetDir);

            if (!string.Equals(fullSource, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                var tmpPath = Path.Combine(targetDir, $"index.db.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(fullSource, tmpPath, overwrite: true);
                    ClearReadOnlyAttribute(tmpPath);
                    ct.ThrowIfCancellationRequested();
                    SqliteConnection.ClearAllPools();
                    File.Move(tmpPath, targetPath, overwrite: true);
                }
                catch
                {
                    TryDeleteFile(tmpPath);
                    throw;
                }
            }

            IndexerSettings.SaveCustomDbPath("", settingsPath);
            return validation;
        }, ct).ConfigureAwait(false);
    }

    public static async Task<IndexImportResult> ImportJsonAsync(
        string sourceFilePath,
        string targetDbPath,
        IndexImportOptions options,
        string? currentExternalDbPath = null,
        IProgress<IndexImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var fullSource = Path.GetFullPath(sourceFilePath.Trim());
            if (!File.Exists(fullSource))
                throw new FileNotFoundException(Strings.Import_Error_FileNotFound, fullSource);

            var fullTarget = Path.GetFullPath(targetDbPath.Trim());
            var targetDir = Path.GetDirectoryName(fullTarget)!;
            Directory.CreateDirectory(targetDir);

            var package = LoadPackageDescriptors(fullSource, ct);
            var effectiveMode = options.Mode;
            if (effectiveMode == IndexImportMode.Incremental &&
                !File.Exists(fullTarget) &&
                string.IsNullOrWhiteSpace(currentExternalDbPath))
            {
                // No base DB exists yet; build cleanly via atomic temp file so cancellation never leaves a broken DB.
                effectiveMode = IndexImportMode.FullReplace;
            }

            if (effectiveMode == IndexImportMode.FullReplace)
                return ExecuteFullReplace(package, fullTarget, options.MergeSources, progress, ct);

            return ExecuteIncremental(package, fullTarget, options, currentExternalDbPath, progress, ct);
        }, ct).ConfigureAwait(false);
    }

    public static async Task<IndexImportResult> DownloadAndApplyUpdateAsync(
        string updateUrl,
        string programDir,
        string? currentDbPath,
        IndexImportMode mode,
        bool mergeSources = true,
        string? settingsPath = null,
        IProgress<IndexImportProgress>? progress = null,
        CancellationToken ct = default,
        HttpMessageHandler? httpHandler = null)
    {
        if (!ExternalLinkOpener.TryCreateSafeHttpUri(updateUrl, out var rootUri) || rootUri is null)
            throw new InvalidDataException(Strings.Import_Error_UpdateUrlInvalid);

        var baseDir = string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : Path.GetFullPath(programDir);
        var tempDir = Path.Combine(baseDir, "data", "tmp", $"index-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var ownsHandler = httpHandler is null;
        var handler = httpHandler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None };
        using var http = new HttpClient(handler, disposeHandler: ownsHandler)
        {
            Timeout = TimeSpan.FromSeconds(120),
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new IndexImportProgress("download", 0, null, Strings.Import_Stage_Downloading));

            var targetDbPath = DefaultDbPath(baseDir);
            var externalSeed = !string.IsNullOrWhiteSpace(currentDbPath) &&
                               !string.Equals(Path.GetFullPath(currentDbPath), Path.GetFullPath(targetDbPath), StringComparison.OrdinalIgnoreCase)
                ? currentDbPath
                : null;

            var urlFileName = Path.GetFileName(rootUri.AbsolutePath);
            if (urlFileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                var downloadedDb = Path.Combine(tempDir, "downloaded.db");
                await DownloadFileAsync(http, rootUri, downloadedDb, ct).ConfigureAwait(false);
                var validation = await ImportDatabaseFileAsync(downloadedDb, baseDir, copyToProgramDir: true, settingsPath, ct).ConfigureAwait(false);
                return new IndexImportResult(
                    targetDbPath,
                    validation.EntryCount,
                    validation.EntryCount,
                    0,
                    validation.Revision,
                    WasIncremental: false);
            }

            if (string.Equals(urlFileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                var localManifest = Path.Combine(tempDir, "manifest.json");
                await DownloadFileAsync(http, rootUri, localManifest, ct).ConfigureAwait(false);
                await DownloadManifestArtifactsAsync(http, rootUri, localManifest, tempDir, ct).ConfigureAwait(false);
                var manifestRes = await ImportJsonAsync(
                    localManifest,
                    targetDbPath,
                    new IndexImportOptions(mode, mergeSources, VerifyBaseRevision: true),
                    externalSeed,
                    progress,
                    ct).ConfigureAwait(false);
                IndexerSettings.SaveCustomDbPath("", settingsPath);
                return manifestRes;
            }

            var safeLocalName = urlFileName.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase)
                ? "patch.jsonl.gz"
                : urlFileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    ? "patch.jsonl"
                    : "patch.json";
            var localPatch = Path.Combine(tempDir, safeLocalName);
            await DownloadFileAsync(http, rootUri, localPatch, ct).ConfigureAwait(false);

            var result = await ImportJsonAsync(
                localPatch,
                targetDbPath,
                new IndexImportOptions(mode, mergeSources, VerifyBaseRevision: true),
                externalSeed,
                progress,
                ct).ConfigureAwait(false);
            IndexerSettings.SaveCustomDbPath("", settingsPath);
            return result;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task DownloadManifestArtifactsAsync(
        HttpClient http,
        Uri manifestUri,
        string localManifestPath,
        string tempDir,
        CancellationToken ct)
    {
        var relPaths = new List<string>();
        await using (var fs = File.OpenRead(localManifestPath))
        using (var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);

            var schema = ReadOptionalString(root, "schema");
            var schemaVer = ReadOptionalInt(root, "schema_version");
            if (!string.Equals(schema, ExpectedManifestSchema, StringComparison.Ordinal) ||
                schemaVer != ExpectedSchemaVersion)
            {
                throw new InvalidDataException(
                    Strings.Format(
                        nameof(Strings.Import_Error_SchemaMismatchFormat),
                        $"{schema ?? "?"} v{schemaVer?.ToString(CultureInfo.InvariantCulture) ?? "?"}"));
            }

            if (!root.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);

            var entriesRel = ReadOptionalString(entriesEl, "path");
            if (string.IsNullOrWhiteSpace(entriesRel))
                throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);
            relPaths.Add(entriesRel);

            if (root.TryGetProperty("deltas", out var deltasEl) && deltasEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var deltaEl in deltasEl.EnumerateArray())
                {
                    if (deltaEl.ValueKind != JsonValueKind.Object) continue;
                    var deltaRel = ReadOptionalString(deltaEl, "path");
                    if (string.IsNullOrWhiteSpace(deltaRel))
                        throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);
                    relPaths.Add(deltaRel);
                }
            }
        }

        foreach (var relPath in relPaths)
        {
            var localChild = ResolveConfinedManifestChild(tempDir, relPath);
            var normalizedRel = relPath.Trim().Replace('\\', '/');
            if (!Uri.TryCreate(manifestUri, normalizedRel, out var childUri) ||
                !ExternalLinkOpener.TryCreateSafeHttpUri(childUri.AbsoluteUri, out _))
            {
                throw new InvalidDataException(Strings.Import_Error_UnsafePath);
            }

            var childDir = Path.GetDirectoryName(localChild);
            if (!string.IsNullOrWhiteSpace(childDir))
                Directory.CreateDirectory(childDir);
            await DownloadFileAsync(http, childUri, localChild, ct).ConfigureAwait(false);
        }
    }

    private static async Task DownloadFileAsync(HttpClient http, Uri uri, string destinationPath, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destinationPath);
        await src.CopyToAsync(dst, 65536, ct).ConfigureAwait(false);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temp directory.
        }
    }

    private static IndexImportResult ExecuteFullReplace(
        ImportPackage package,
        string targetDbPath,
        bool mergeSources,
        IProgress<IndexImportProgress>? progress,
        CancellationToken ct)
    {
        var targetDir = Path.GetDirectoryName(targetDbPath)!;
        var tmpPath = Path.Combine(targetDir, $"index.db.{Guid.NewGuid():N}.tmp");
        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = tmpPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();

            long upserted = 0;
            long deleted = 0;
            long totalCount;
            string? finalRevision;

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                ExecuteNonQuery(conn, "PRAGMA page_size=4096");
                ExecuteNonQuery(conn, "PRAGMA journal_mode=OFF");
                ExecuteNonQuery(conn, "PRAGMA synchronous=OFF");
                ExecuteNonQuery(conn, "PRAGMA temp_store=MEMORY");
                ExecuteNonQuery(conn, "PRAGMA foreign_keys=ON");

                CreateSchema(conn);

                using (var tx = conn.BeginTransaction())
                {
                    var writers = new SqliteStatementWriters(conn, tx);
                    try
                    {
                        foreach (var chunk in package.Inputs)
                        {
                            var (u, d) = ProcessJsonInputStream(
                                chunk,
                                writers,
                                incremental: false,
                                mergeSources,
                                progress,
                                upserted + deleted,
                                package.ExpectedEntryCount,
                                ct);
                            upserted += u;
                            deleted += d;
                        }
                    }
                    finally
                    {
                        writers.Dispose();
                    }

                    finalRevision = package.TargetRevision ?? package.ManifestSha256;
                    totalCount = WriteMetadata(
                        conn,
                        tx,
                        package.ManifestSha256,
                        package.GeneratedAt,
                        finalRevision);
                    tx.Commit();
                }

                CreateIndexes(conn);
                ExecuteNonQuery(conn, "PRAGMA user_version=1");
                ExecuteNonQuery(conn, "PRAGMA journal_mode=DELETE");
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new IndexImportProgress("swap", totalCount, totalCount, Strings.Import_Stage_Finalizing));
            SqliteConnection.ClearAllPools();
            File.Move(tmpPath, targetDbPath, overwrite: true);

            return new IndexImportResult(
                targetDbPath,
                totalCount,
                upserted,
                deleted,
                finalRevision,
                WasIncremental: false);
        }
        catch
        {
            TryDeleteFile(tmpPath);
            throw;
        }
    }

    private static IndexImportResult ExecuteIncremental(
        ImportPackage package,
        string targetDbPath,
        IndexImportOptions options,
        string? currentExternalDbPath,
        IProgress<IndexImportProgress>? progress,
        CancellationToken ct)
    {
        var externalFull = string.IsNullOrWhiteSpace(currentExternalDbPath)
            ? null
            : Path.GetFullPath(currentExternalDbPath.Trim());
        var copyFromExternal = externalFull is not null &&
                               !string.Equals(externalFull, targetDbPath, StringComparison.OrdinalIgnoreCase) &&
                               File.Exists(externalFull);

        var targetDir = Path.GetDirectoryName(targetDbPath)!;
        var workingDbPath = copyFromExternal
            ? Path.Combine(targetDir, $"index.db.{Guid.NewGuid():N}.tmp")
            : targetDbPath;

        if (copyFromExternal)
        {
            var extCheck = ValidateDatabase(externalFull!);
            if (!extCheck.IsValid)
                throw new InvalidDataException(extCheck.ErrorMessage ?? Strings.Import_Error_DbInvalid);
            File.Copy(externalFull!, workingDbPath, overwrite: true);
            ClearReadOnlyAttribute(workingDbPath);
        }
        else
        {
            var existingCheck = ValidateDatabase(targetDbPath);
            if (!existingCheck.IsValid)
                throw new InvalidDataException(existingCheck.ErrorMessage ?? Strings.Import_Error_DbInvalid);
        }

        try
        {
            long upserted = 0;
            long deleted = 0;
            long totalCount;
            string? finalRevision;

            // Clear pools so existing read-only handles don't hold locks during transactional update.
            SqliteConnection.ClearAllPools();

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = workingDbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                ExecuteNonQuery(conn, "PRAGMA busy_timeout=3000;");
                ExecuteNonQuery(conn, "PRAGMA foreign_keys=ON;");

                if (options.VerifyBaseRevision && !string.IsNullOrWhiteSpace(package.BaseRevision))
                {
                    var currentRev = ReadMetaValue(conn, "revision");
                    if (!string.Equals(currentRev, package.BaseRevision, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            Strings.Format(
                                nameof(Strings.Import_Error_RevisionMismatchFormat),
                                package.BaseRevision,
                                currentRev ?? "(none)"));
                    }
                }

                using var tx = conn.BeginTransaction();
                var writers = new SqliteStatementWriters(conn, tx);
                try
                {
                    foreach (var chunk in package.Inputs)
                    {
                        var (u, d) = ProcessJsonInputStream(
                            chunk,
                            writers,
                            incremental: true,
                            options.MergeSources,
                            progress,
                            upserted + deleted,
                            package.ExpectedEntryCount,
                            ct);
                        upserted += u;
                        deleted += d;
                    }
                }
                finally
                {
                    writers.Dispose();
                }

                finalRevision = package.TargetRevision ?? package.ManifestSha256 ?? ReadMetaValue(conn, "revision");
                totalCount = WriteMetadata(
                    conn,
                    tx,
                    package.ManifestSha256 ?? ReadMetaValue(conn, "manifest_sha256"),
                    package.GeneratedAt,
                    finalRevision);

                ct.ThrowIfCancellationRequested();
                tx.Commit();
            }

            if (copyFromExternal)
            {
                SqliteConnection.ClearAllPools();
                File.Move(workingDbPath, targetDbPath, overwrite: true);
            }

            return new IndexImportResult(
                targetDbPath,
                totalCount,
                upserted,
                deleted,
                finalRevision,
                WasIncremental: true);
        }
        catch
        {
            if (copyFromExternal)
                TryDeleteFile(workingDbPath);
            throw;
        }
    }

    private sealed record InputSourceDescriptor(
        string Path,
        bool IsGzip,
        bool IsJsonl,
        string? BaseRevision = null,
        string? TargetRevision = null);

    private sealed record ImportPackage(
        IReadOnlyList<InputSourceDescriptor> Inputs,
        string? ManifestSha256,
        string? GeneratedAt,
        string? BaseRevision,
        string? TargetRevision,
        long? ExpectedEntryCount);

    private static ImportPackage LoadPackageDescriptors(string fullSourcePath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(fullSourcePath);
        if (string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
            return LoadManifestPackage(fullSourcePath, ct);

        if (fullSourcePath.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase))
        {
            return new ImportPackage(
                [new InputSourceDescriptor(fullSourcePath, IsGzip: true, IsJsonl: true)],
                ComputeFileSha256Hex(fullSourcePath, ct),
                null,
                null,
                null,
                null);
        }

        if (fullSourcePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return new ImportPackage(
                [new InputSourceDescriptor(fullSourcePath, IsGzip: false, IsJsonl: true)],
                ComputeFileSha256Hex(fullSourcePath, ct),
                null,
                null,
                null,
                null);
        }

        if (fullSourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            // Peek optional top-level envelope metadata (base_revision, target_revision, revision, generated_at)
            string? baseRev = null;
            string? targetRev = null;
            string? generatedAt = null;
            using (var fs = File.OpenRead(fullSourcePath))
            using (var doc = JsonDocument.Parse(fs))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    baseRev = ReadOptionalString(doc.RootElement, "base_revision");
                    targetRev = ReadOptionalString(doc.RootElement, "target_revision")
                                ?? ReadOptionalString(doc.RootElement, "revision");
                    generatedAt = ReadOptionalString(doc.RootElement, "generated_at");
                }
            }

            return new ImportPackage(
                [new InputSourceDescriptor(fullSourcePath, IsGzip: false, IsJsonl: false, baseRev, targetRev)],
                ComputeFileSha256Hex(fullSourcePath, ct),
                generatedAt,
                baseRev,
                targetRev,
                null);
        }

        throw new InvalidDataException(Strings.Import_Error_UnsupportedFormat);
    }

    private static ImportPackage LoadManifestPackage(string manifestPath, CancellationToken ct)
    {
        var manifestDir = Path.GetDirectoryName(manifestPath)!;
        using var fs = File.OpenRead(manifestPath);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);

        var schema = ReadOptionalString(root, "schema");
        var schemaVer = ReadOptionalInt(root, "schema_version");
        if (!string.Equals(schema, ExpectedManifestSchema, StringComparison.Ordinal) ||
            schemaVer != ExpectedSchemaVersion)
        {
            throw new InvalidDataException(
                Strings.Format(
                    nameof(Strings.Import_Error_SchemaMismatchFormat),
                    $"{schema ?? "?"} v{schemaVer?.ToString(CultureInfo.InvariantCulture) ?? "?"}"));
        }

        if (!root.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);

        var relEntriesPath = ReadOptionalString(entriesEl, "path");
        if (string.IsNullOrWhiteSpace(relEntriesPath))
            throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);

        var fullEntriesPath = ResolveConfinedManifestChild(manifestDir, relEntriesPath);
        var expectedBytes = ReadOptionalLong(entriesEl, "bytes");
        var expectedSha = ReadOptionalString(entriesEl, "sha256");
        VerifyFileBytesAndSha256(fullEntriesPath, expectedBytes, expectedSha, ct);

        var compression = ReadOptionalString(entriesEl, "compression") ?? "";
        var isGzip = string.Equals(compression, "gzip", StringComparison.OrdinalIgnoreCase) ||
                     fullEntriesPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

        var inputs = new List<InputSourceDescriptor>
        {
            new(fullEntriesPath, isGzip, IsJsonl: true),
        };

        if (root.TryGetProperty("deltas", out var deltasEl) && deltasEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var deltaEl in deltasEl.EnumerateArray())
            {
                if (deltaEl.ValueKind != JsonValueKind.Object) continue;
                var dRel = ReadOptionalString(deltaEl, "path");
                if (string.IsNullOrWhiteSpace(dRel))
                    throw new InvalidDataException(Strings.Import_Error_ManifestInvalid);

                var dFull = ResolveConfinedManifestChild(manifestDir, dRel);
                var dBytes = ReadOptionalLong(deltaEl, "bytes");
                var dSha = ReadOptionalString(deltaEl, "sha256");
                VerifyFileBytesAndSha256(dFull, dBytes, dSha, ct);

                var dComp = ReadOptionalString(deltaEl, "compression") ?? "";
                var dGzip = string.Equals(dComp, "gzip", StringComparison.OrdinalIgnoreCase) ||
                            dFull.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                var dJsonl = dFull.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                             dFull.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase);
                inputs.Add(new InputSourceDescriptor(dFull, dGzip, dJsonl));
            }
        }

        var generatedAt = ReadOptionalString(root, "generated_at");
        var revision = ReadOptionalString(root, "revision") ?? ReadOptionalString(root, "target_revision");
        var baseRevision = ReadOptionalString(root, "base_revision");
        var entryCount = ReadOptionalLong(root, "entry_count");

        return new ImportPackage(inputs, expectedSha, generatedAt, baseRevision, revision, entryCount);
    }

    internal static string ResolveConfinedManifestChild(string manifestDir, string relativePath)
    {
        var trimmed = relativePath.Trim();
        if (trimmed.Length == 0 || Path.IsPathRooted(trimmed) || trimmed.Contains(':'))
            throw new InvalidDataException(Strings.Import_Error_UnsafePath);

        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg == "..")
                throw new InvalidDataException(Strings.Import_Error_UnsafePath);
        }

        var baseDir = Path.GetFullPath(manifestDir);
        var prefix = Path.EndsInDirectorySeparator(baseDir) ? baseDir : baseDir + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(baseDir, trimmed));
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Strings.Import_Error_UnsafePath);

        return candidate;
    }

    private static void VerifyFileBytesAndSha256(string path, long? expectedBytes, string? expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(Strings.Import_Error_FileNotFound, path);

        var fi = new FileInfo(path);
        if (expectedBytes.HasValue && fi.Length != expectedBytes.Value)
        {
            throw new InvalidDataException(
                Strings.Format(nameof(Strings.Import_Error_BytesMismatchFormat), expectedBytes.Value, fi.Length));
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actualSha = ComputeFileSha256Hex(path, ct);
            if (!string.Equals(actualSha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    Strings.Format(nameof(Strings.Import_Error_Sha256MismatchFormat), expectedSha256.Trim(), actualSha));
            }
        }
    }

    private static string ComputeFileSha256Hex(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var buffer = new byte[65536];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static (long Upserted, long Deleted) ProcessJsonInputStream(
        InputSourceDescriptor input,
        SqliteStatementWriters writers,
        bool incremental,
        bool mergeSources,
        IProgress<IndexImportProgress>? progress,
        long initialCount,
        long? expectedTotal,
        CancellationToken ct)
    {
        long upserted = 0;
        long deleted = 0;
        long processed = initialCount;

        if (input.IsJsonl)
        {
            using var fileStream = File.OpenRead(input.Path);
            using Stream stream = input.IsGzip
                ? new GZipStream(fileStream, CompressionMode.Decompress)
                : fileStream;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException(Strings.Import_Error_InvalidRow);

                var (u, d) = ApplyRecord(doc.RootElement, writers, incremental, mergeSources);
                upserted += u;
                deleted += d;
                processed++;
                if ((processed & 0x3FF) == 0)
                    progress?.Report(new IndexImportProgress("import", processed, expectedTotal, null));
            }
        }
        else
        {
            using var fs = File.OpenRead(input.Path);
            using var doc = JsonDocument.Parse(fs);
            JsonElement items;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                items = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("operations", out var opsEl) && opsEl.ValueKind == JsonValueKind.Array)
                    items = opsEl;
                else if (doc.RootElement.TryGetProperty("entries", out var entEl) && entEl.ValueKind == JsonValueKind.Array)
                    items = entEl;
                else
                    throw new InvalidDataException(Strings.Import_Error_UnsupportedFormat);
            }
            else
            {
                throw new InvalidDataException(Strings.Import_Error_UnsupportedFormat);
            }

            foreach (var el in items.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (el.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException(Strings.Import_Error_InvalidRow);

                var (u, d) = ApplyRecord(el, writers, incremental, mergeSources);
                upserted += u;
                deleted += d;
                processed++;
                if ((processed & 0x3FF) == 0)
                    progress?.Report(new IndexImportProgress("import", processed, expectedTotal, null));
            }
        }

        progress?.Report(new IndexImportProgress("import", processed, expectedTotal, null));
        return (upserted, deleted);
    }

    private static (long Upserted, long Deleted) ApplyRecord(
        JsonElement el,
        SqliteStatementWriters writers,
        bool incremental,
        bool mergeSources)
    {
        var op = ReadOptionalString(el, "op")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(op)) op = "upsert";

        if (op == "delete")
        {
            var delId = ReadEntryIdForDelete(el);
            if (string.IsNullOrWhiteSpace(delId))
                throw new InvalidDataException(Strings.Import_Error_InvalidRow);

            writers.DeleteEntry(delId);
            return (0, 1);
        }

        if (op != "upsert")
            throw new InvalidDataException(Strings.Format(nameof(Strings.Import_Error_UnknownOpFormat), op));

        var bundleId = (ReadOptionalString(el, "bundle_id") ?? "").Trim();
        if (bundleId.Length == 0)
            throw new InvalidDataException(Strings.Import_Error_MissingBundleId);

        var version = (ReadOptionalString(el, "version") ?? "").Trim();
        var rawId = (ReadOptionalString(el, "id") ?? "").Trim();
        var id = rawId.Length > 0
            ? rawId
            : $"{bundleId.ToLowerInvariant()}@{(version.Length > 0 ? version : "unknown")}";

        var title = ReadOptionalString(el, "title") ?? "";
        var build = ReadOptionalString(el, "build") ?? "";
        var minOs = ReadOptionalString(el, "min_os") ?? "";
        var minOsNum = IosVersionCatalog.TryParseVersionNum(minOs, out var parsedMinOs) ? parsedMinOs : 0;
        var deviceMask = ReadBitmask(el, "device_family", DeviceBits);
        var archMask = ReadBitmask(el, "arch", ArchBits);
        var origin = ReadOptionalString(el, "origin") ?? "unknown";
        var size = Math.Max(0L, ReadOptionalLong(el, "size") ?? 0L);
        var sizeExact = ReadOptionalBool(el, "size_exact") ? 1 : 0;
        var icon = SanitizeIconPath(ReadOptionalString(el, "icon"));
        var category = ReadOptionalString(el, "category") ?? "";
        var subgenre = ReadOptionalString(el, "subgenre") ?? "";
        var developer = ReadOptionalString(el, "developer") ?? "";
        var distribution = ReadOptionalString(el, "distribution");
        if (string.IsNullOrWhiteSpace(distribution)) distribution = "private";
        var updatedAt = ReadOptionalString(el, "updated_at") ?? "";

        List<SourceRecord>? existingSources = null;
        if (incremental && mergeSources)
            existingSources = writers.ReadExistingSources(id);

        writers.UpsertEntry(
            id, bundleId, bundleId.ToLowerInvariant(), title, version, build,
            minOs, minOsNum, deviceMask, archMask, origin, size, sizeExact, icon,
            category, subgenre, developer, distribution, updatedAt);

        // Descriptions
        var descriptions = ReadDescriptions(el);
        if (descriptions.Count > 0 || el.TryGetProperty("descriptions", out _))
        {
            writers.DeleteDescriptionsForEntry(id);
            foreach (var (lang, text) in descriptions)
                writers.InsertDescription(id, lang, text);
        }

        // Sources (strict http/https filter; optional URL-deduped merge with existing sources)
        var incomingSources = ReadSafeSources(el);
        var hasSourcesProp = el.TryGetProperty("sources", out _);
        if (hasSourcesProp || !incremental)
        {
            var merged = MergeSourceLists(existingSources, incomingSources, incremental && mergeSources);
            writers.DeleteSourcesForEntry(id);
            foreach (var src in merged)
                writers.InsertSource(id, src);
        }

        return (1, 0);
    }

    private static string? ReadEntryIdForDelete(JsonElement el)
    {
        var id = ReadOptionalString(el, "id")?.Trim();
        if (!string.IsNullOrWhiteSpace(id)) return id;

        var bundle = ReadOptionalString(el, "bundle_id")?.Trim();
        if (string.IsNullOrWhiteSpace(bundle)) return null;
        var version = (ReadOptionalString(el, "version") ?? "").Trim();
        return $"{bundle.ToLowerInvariant()}@{(version.Length > 0 ? version : "unknown")}";
    }

    internal static string SanitizeIconPath(string? rawIcon)
    {
        if (string.IsNullOrWhiteSpace(rawIcon)) return "";
        var trimmed = rawIcon.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.StartsWith('/') || trimmed.StartsWith('\\') || trimmed.Contains(':'))
            return "";

        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg == "..") return "";
        }
        return string.Join('/', segments);
    }

    private static List<(string Lang, string Text)> ReadDescriptions(JsonElement el)
    {
        var result = new List<(string, string)>();
        if (el.TryGetProperty("descriptions", out var descsEl) && descsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in descsEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var text = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(prop.Name) && !string.IsNullOrWhiteSpace(text))
                    result.Add((prop.Name.Trim(), text));
            }
        }
        else
        {
            var single = ReadOptionalString(el, "description");
            if (!string.IsNullOrWhiteSpace(single))
            {
                var lang = ReadOptionalString(el, "description_language");
                if (string.IsNullOrWhiteSpace(lang)) lang = "und";
                result.Add((lang.Trim(), single));
            }
        }
        return result;
    }

    private sealed record SourceRecord(
        string SourceId,
        string Url,
        string Page,
        long Size,
        string Sha256,
        string Status,
        string LastChecked);

    private static List<SourceRecord> ReadSafeSources(JsonElement el)
    {
        var result = new List<SourceRecord>();
        if (!el.TryGetProperty("sources", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var sEl in arr.EnumerateArray())
        {
            if (sEl.ValueKind != JsonValueKind.Object) continue;
            var rawUrl = ReadOptionalString(sEl, "url");
            if (!ExternalLinkOpener.TryCreateSafeHttpUri(rawUrl, out var safeUri) || safeUri is null)
                continue;

            var rawPage = ReadOptionalString(sEl, "page");
            var safePage = ExternalLinkOpener.TryCreateSafeHttpUri(rawPage, out var pageUri) && pageUri is not null
                ? pageUri.AbsoluteUri
                : "";

            var sourceId = ReadOptionalString(sEl, "source_id") ?? "";
            var size = Math.Max(0L, ReadOptionalLong(sEl, "size") ?? 0L);
            var sha256 = (ReadOptionalString(sEl, "sha256") ?? "").Trim().ToLowerInvariant();
            var status = ReadOptionalString(sEl, "status");
            if (string.IsNullOrWhiteSpace(status)) status = "ok";
            var lastChecked = ReadOptionalString(sEl, "last_checked") ?? "";

            result.Add(new SourceRecord(sourceId, safeUri.AbsoluteUri, safePage, size, sha256, status, lastChecked));
        }
        return result;
    }

    private static List<SourceRecord> MergeSourceLists(
        List<SourceRecord>? existing,
        List<SourceRecord> incoming,
        bool mergeExisting)
    {
        var byUrl = new Dictionary<string, SourceRecord>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void AddOrUpdate(SourceRecord s)
        {
            if (!byUrl.ContainsKey(s.Url))
                order.Add(s.Url);
            byUrl[s.Url] = s;
        }

        if (mergeExisting && existing is not null)
        {
            foreach (var old in existing)
                AddOrUpdate(old);
        }
        foreach (var inc in incoming)
            AddOrUpdate(inc);

        return order.Select(u => byUrl[u]).ToList();
    }

    private static int ReadBitmask(JsonElement el, string propName, Dictionary<string, int> bits)
    {
        if (!el.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return 0;

        var mask = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var key = item.GetString();
            if (!string.IsNullOrWhiteSpace(key) && bits.TryGetValue(key.Trim(), out var bit))
                mask |= bit;
        }
        return mask;
    }

    private static string? ReadOptionalString(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int? ReadOptionalInt(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
    }

    private static long? ReadOptionalLong(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var v) ? v : null;
    }

    private static bool ReadOptionalBool(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var prop)) return false;
        return prop.ValueKind == JsonValueKind.True;
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, """
            CREATE TABLE meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE entries (
                id           TEXT PRIMARY KEY,
                bundle_id    TEXT NOT NULL,
                bundle_lower TEXT NOT NULL,
                title        TEXT,
                version      TEXT NOT NULL,
                build        TEXT,
                min_os       TEXT,
                min_os_num   INTEGER NOT NULL DEFAULT 0,
                device_mask  INTEGER NOT NULL DEFAULT 0,
                arch_mask    INTEGER NOT NULL DEFAULT 0,
                origin       TEXT,
                size         INTEGER NOT NULL DEFAULT 0,
                size_exact   INTEGER NOT NULL DEFAULT 0,
                icon         TEXT,
                category     TEXT,
                subgenre     TEXT,
                developer    TEXT,
                distribution TEXT NOT NULL DEFAULT 'private',
                updated_at   TEXT
            );
            CREATE TABLE descriptions (
                entry_id TEXT NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                lang     TEXT NOT NULL,
                text     TEXT NOT NULL,
                PRIMARY KEY (entry_id, lang)
            );
            CREATE TABLE sources (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_id     TEXT NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                source_id    TEXT NOT NULL,
                url          TEXT NOT NULL,
                page         TEXT,
                size         INTEGER NOT NULL DEFAULT 0,
                sha256       TEXT,
                status       TEXT NOT NULL DEFAULT 'ok',
                last_checked TEXT
            );
            """);
    }

    private static void CreateIndexes(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, """
            CREATE INDEX ix_entries_bundle ON entries(bundle_lower);
            CREATE INDEX ix_entries_minos  ON entries(min_os_num);
            CREATE INDEX ix_entries_device ON entries(device_mask);
            CREATE INDEX ix_entries_arch   ON entries(arch_mask);
            CREATE INDEX ix_entries_size   ON entries(size);
            CREATE INDEX ix_entries_title  ON entries(title COLLATE NOCASE);
            CREATE INDEX ix_sources_entry  ON sources(entry_id);
            CREATE INDEX ix_sources_host   ON sources(url);
            CREATE INDEX ix_descriptions_lang ON descriptions(lang);
            """);
    }

    private static long WriteMetadata(
        SqliteConnection conn,
        SqliteTransaction tx,
        string? manifestSha256,
        string? generatedAt,
        string? revision)
    {
        long totalEntries;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM entries";
            totalEntries = Convert.ToInt64(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var upsertMeta = conn.CreateCommand();
        upsertMeta.Transaction = tx;
        upsertMeta.CommandText = """
            INSERT INTO meta (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        var pK = upsertMeta.Parameters.Add("$k", SqliteType.Text);
        var pV = upsertMeta.Parameters.Add("$v", SqliteType.Text);

        void Put(string key, string value)
        {
            pK.Value = key;
            pV.Value = value;
            upsertMeta.ExecuteNonQuery();
        }

        Put("schema_version", ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture));
        Put("entry_count", totalEntries.ToString(CultureInfo.InvariantCulture));
        Put("generated_at", string.IsNullOrWhiteSpace(generatedAt)
            ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : generatedAt);
        Put("builder", "IdeviceIndex-Importer/1.0");
        Put("fts5", "0");
        if (!string.IsNullOrWhiteSpace(manifestSha256))
            Put("manifest_sha256", manifestSha256);
        if (!string.IsNullOrWhiteSpace(revision))
            Put("revision", revision);

        return totalEntries;
    }

    private static string? ReadMetaValue(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of temp import files.
        }
    }

    private sealed class SqliteStatementWriters : IDisposable
    {
        private readonly SqliteCommand _upsertEntry;
        private readonly SqliteCommand _deleteEntry;
        private readonly SqliteCommand _deleteDescs;
        private readonly SqliteCommand _insertDesc;
        private readonly SqliteCommand _selectSources;
        private readonly SqliteCommand _deleteSources;
        private readonly SqliteCommand _insertSource;

        public SqliteStatementWriters(SqliteConnection conn, SqliteTransaction tx)
        {
            _upsertEntry = conn.CreateCommand();
            _upsertEntry.Transaction = tx;
            _upsertEntry.CommandText = """
                INSERT INTO entries (
                    id, bundle_id, bundle_lower, title, version, build, min_os, min_os_num,
                    device_mask, arch_mask, origin, size, size_exact, icon,
                    category, subgenre, developer, distribution, updated_at
                ) VALUES (
                    $id, $bundle_id, $bundle_lower, $title, $version, $build, $min_os, $min_os_num,
                    $device_mask, $arch_mask, $origin, $size, $size_exact, $icon,
                    $category, $subgenre, $developer, $distribution, $updated_at
                )
                ON CONFLICT(id) DO UPDATE SET
                    bundle_id = excluded.bundle_id,
                    bundle_lower = excluded.bundle_lower,
                    title = excluded.title,
                    version = excluded.version,
                    build = excluded.build,
                    min_os = excluded.min_os,
                    min_os_num = excluded.min_os_num,
                    device_mask = excluded.device_mask,
                    arch_mask = excluded.arch_mask,
                    origin = excluded.origin,
                    size = excluded.size,
                    size_exact = excluded.size_exact,
                    icon = excluded.icon,
                    category = excluded.category,
                    subgenre = excluded.subgenre,
                    developer = excluded.developer,
                    distribution = excluded.distribution,
                    updated_at = excluded.updated_at
                """;
            foreach (var name in new[]
            {
                "$id", "$bundle_id", "$bundle_lower", "$title", "$version", "$build", "$min_os", "$min_os_num",
                "$device_mask", "$arch_mask", "$origin", "$size", "$size_exact", "$icon",
                "$category", "$subgenre", "$developer", "$distribution", "$updated_at",
            })
            {
                _upsertEntry.Parameters.Add(new SqliteParameter(name, ""));
            }

            _deleteEntry = conn.CreateCommand();
            _deleteEntry.Transaction = tx;
            _deleteEntry.CommandText = "DELETE FROM entries WHERE id = $id";
            _deleteEntry.Parameters.Add("$id", SqliteType.Text);

            _deleteDescs = conn.CreateCommand();
            _deleteDescs.Transaction = tx;
            _deleteDescs.CommandText = "DELETE FROM descriptions WHERE entry_id = $id";
            _deleteDescs.Parameters.Add("$id", SqliteType.Text);

            _insertDesc = conn.CreateCommand();
            _insertDesc.Transaction = tx;
            _insertDesc.CommandText = """
                INSERT INTO descriptions (entry_id, lang, text) VALUES ($id, $lang, $text)
                ON CONFLICT(entry_id, lang) DO UPDATE SET text = excluded.text
                """;
            _insertDesc.Parameters.Add("$id", SqliteType.Text);
            _insertDesc.Parameters.Add("$lang", SqliteType.Text);
            _insertDesc.Parameters.Add("$text", SqliteType.Text);

            _selectSources = conn.CreateCommand();
            _selectSources.Transaction = tx;
            _selectSources.CommandText = """
                SELECT source_id, url, page, size, sha256, status, last_checked
                FROM sources WHERE entry_id = $id ORDER BY id
                """;
            _selectSources.Parameters.Add("$id", SqliteType.Text);

            _deleteSources = conn.CreateCommand();
            _deleteSources.Transaction = tx;
            _deleteSources.CommandText = "DELETE FROM sources WHERE entry_id = $id";
            _deleteSources.Parameters.Add("$id", SqliteType.Text);

            _insertSource = conn.CreateCommand();
            _insertSource.Transaction = tx;
            _insertSource.CommandText = """
                INSERT INTO sources (entry_id, source_id, url, page, size, sha256, status, last_checked)
                VALUES ($id, $source_id, $url, $page, $size, $sha256, $status, $last_checked)
                """;
            _insertSource.Parameters.Add("$id", SqliteType.Text);
            _insertSource.Parameters.Add("$source_id", SqliteType.Text);
            _insertSource.Parameters.Add("$url", SqliteType.Text);
            _insertSource.Parameters.Add("$page", SqliteType.Text);
            _insertSource.Parameters.Add("$size", SqliteType.Integer);
            _insertSource.Parameters.Add("$sha256", SqliteType.Text);
            _insertSource.Parameters.Add("$status", SqliteType.Text);
            _insertSource.Parameters.Add("$last_checked", SqliteType.Text);
        }

        public void DeleteEntry(string id)
        {
            _deleteEntry.Parameters["$id"].Value = id;
            _deleteEntry.ExecuteNonQuery();
        }

        public void UpsertEntry(
            string id, string bundleId, string bundleLower, string title, string version, string build,
            string minOs, int minOsNum, int deviceMask, int archMask, string origin, long size, int sizeExact,
            string icon, string category, string subgenre, string developer, string distribution, string updatedAt)
        {
            var p = _upsertEntry.Parameters;
            p["$id"].Value = id;
            p["$bundle_id"].Value = bundleId;
            p["$bundle_lower"].Value = bundleLower;
            p["$title"].Value = title;
            p["$version"].Value = version;
            p["$build"].Value = build;
            p["$min_os"].Value = minOs;
            p["$min_os_num"].Value = minOsNum;
            p["$device_mask"].Value = deviceMask;
            p["$arch_mask"].Value = archMask;
            p["$origin"].Value = origin;
            p["$size"].Value = size;
            p["$size_exact"].Value = sizeExact;
            p["$icon"].Value = icon;
            p["$category"].Value = category;
            p["$subgenre"].Value = subgenre;
            p["$developer"].Value = developer;
            p["$distribution"].Value = distribution;
            p["$updated_at"].Value = updatedAt;
            _upsertEntry.ExecuteNonQuery();
        }

        public void DeleteDescriptionsForEntry(string id)
        {
            _deleteDescs.Parameters["$id"].Value = id;
            _deleteDescs.ExecuteNonQuery();
        }

        public void InsertDescription(string id, string lang, string text)
        {
            _insertDesc.Parameters["$id"].Value = id;
            _insertDesc.Parameters["$lang"].Value = lang;
            _insertDesc.Parameters["$text"].Value = text;
            _insertDesc.ExecuteNonQuery();
        }

        public List<SourceRecord> ReadExistingSources(string id)
        {
            _selectSources.Parameters["$id"].Value = id;
            var list = new List<SourceRecord>();
            using var reader = _selectSources.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new SourceRecord(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? 0L : reader.GetInt64(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "ok" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6)));
            }
            return list;
        }

        public void DeleteSourcesForEntry(string id)
        {
            _deleteSources.Parameters["$id"].Value = id;
            _deleteSources.ExecuteNonQuery();
        }

        public void InsertSource(string id, SourceRecord src)
        {
            var p = _insertSource.Parameters;
            p["$id"].Value = id;
            p["$source_id"].Value = src.SourceId;
            p["$url"].Value = src.Url;
            p["$page"].Value = src.Page;
            p["$size"].Value = src.Size;
            p["$sha256"].Value = src.Sha256;
            p["$status"].Value = src.Status;
            p["$last_checked"].Value = src.LastChecked;
            _insertSource.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _upsertEntry.Dispose();
            _deleteEntry.Dispose();
            _deleteDescs.Dispose();
            _insertDesc.Dispose();
            _selectSources.Dispose();
            _deleteSources.Dispose();
            _insertSource.Dispose();
        }
    }
}
