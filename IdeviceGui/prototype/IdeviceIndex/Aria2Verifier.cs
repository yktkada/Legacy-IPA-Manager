using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

internal enum Aria2VerifyStage
{
    None,
    FilesMissing,
    ManifestInvalid,
    ProcessFailed,
    VersionMismatch,
    HashMismatch,
}

internal sealed record Aria2VerifyResult(
    bool Ok,
    Aria2VerifyStage FailedStage,
    string? ExePath,
    string? Version,
    string? Message);

/// <summary>
/// aria2c.exe 严格四步完整性校验（D23–D25）：
/// 只校验 &lt;programDir&gt;\aria2c.exe 与 aria2.manifest.json，不读 PATH，不回落外部路径。
/// </summary>
internal static partial class Aria2Verifier
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    [GeneratedRegex(@"^\s*aria2\s+version\s+([^\s\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VersionLineRegex();

    public static Aria2VerifyResult Verify(string? programDir = null)
    {
        var baseDir = Path.GetFullPath(string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir);
        var exePath = Path.Combine(baseDir, "aria2c.exe");
        var manifestPath = Path.Combine(baseDir, "aria2.manifest.json");
        return VerifyFiles(exePath, manifestPath, runVersion: null);
    }

    internal static Aria2VerifyResult VerifyFiles(
        string exePath,
        string manifestPath,
        Func<string, (int ExitCode, string StdOut, string StdErr)>? runVersion = null)
    {
        var fullExePath = Path.GetFullPath(exePath);
        var fullManifestPath = Path.GetFullPath(manifestPath);

        // Step 1: 两个文件必须存在，且 manifest JSON 合法
        if (!File.Exists(fullExePath))
        {
            return Fail(Aria2VerifyStage.FilesMissing,
                Strings.Format(nameof(Strings.Aria2_Error_FilesMissingFormat), Path.GetFileName(fullExePath)));
        }
        if (!File.Exists(fullManifestPath))
        {
            return Fail(Aria2VerifyStage.FilesMissing,
                Strings.Format(nameof(Strings.Aria2_Error_FilesMissingFormat), Path.GetFileName(fullManifestPath)));
        }

        if (!TryReadManifest(fullManifestPath, out var expectedVersion, out var expectedSha256, out var manifestError))
        {
            return Fail(Aria2VerifyStage.ManifestInvalid,
                Strings.Format(nameof(Strings.Aria2_Error_ManifestInvalidFormat), manifestError));
        }

        // Step 2: Compute SHA-256 and compare against manifest BEFORE executing the external binary
        string actualSha256;
        try
        {
            using var stream = File.OpenRead(fullExePath);
            actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return Fail(Aria2VerifyStage.ProcessFailed,
                Strings.Format(nameof(Strings.Aria2_Error_ProcessFailedFormat), ex.Message));
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(Aria2VerifyStage.HashMismatch,
                Strings.Format(nameof(Strings.Aria2_Error_HashMismatchFormat), expectedSha256, actualSha256));
        }

        // Step 3: Execute verified aria2c.exe --version with 5s timeout, requiring exit code 0
        int exitCode;
        string stdout;
        string stderr;
        try
        {
            (exitCode, stdout, stderr) = runVersion is not null
                ? runVersion(fullExePath)
                : RunVersionProcess(fullExePath);
        }
        catch (Exception ex)
        {
            return Fail(Aria2VerifyStage.ProcessFailed,
                Strings.Format(nameof(Strings.Aria2_Error_ProcessFailedFormat), ex.Message));
        }

        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr)
                ? $"exit={exitCode}"
                : $"exit={exitCode}: {stderr.Trim()}";
            return Fail(Aria2VerifyStage.ProcessFailed,
                Strings.Format(nameof(Strings.Aria2_Error_ProcessFailedFormat), detail));
        }

        // Step 4: Parse aria2 version <ver> from stdout and match manifest.version
        var actualVersion = ParseVersion(stdout);
        if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
        {
            return Fail(Aria2VerifyStage.VersionMismatch,
                Strings.Format(nameof(Strings.Aria2_Error_VersionMismatchFormat), expectedVersion, actualVersion ?? Strings.Common_Unknown));
        }

        return new Aria2VerifyResult(true, Aria2VerifyStage.None, fullExePath, actualVersion, null);
    }

    internal static string? ParseVersion(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var match = VersionLineRegex().Match(stdout);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool TryReadManifest(
        string manifestPath,
        out string version,
        out string sha256,
        out string error)
    {
        version = "";
        sha256 = "";
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root is not object";
                return false;
            }

            if (!root.TryGetProperty("version", out var verProp) || verProp.ValueKind != JsonValueKind.String)
            {
                error = "missing version";
                return false;
            }
            version = (verProp.GetString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                error = "empty version";
                return false;
            }

            if (!root.TryGetProperty("sha256", out var shaProp) || shaProp.ValueKind != JsonValueKind.String)
            {
                error = "missing sha256";
                return false;
            }
            sha256 = (shaProp.GetString() ?? "").Trim();
            if (!IsHex64(sha256))
            {
                error = "invalid sha256";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsHex64(string text)
    {
        if (text.Length != 64) return false;
        foreach (var ch in text)
        {
            var isHex = ch is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunVersionProcess(string exePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--version");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)VersionTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            catch
            {
                // 忽略强杀清理异常
            }
            throw new TimeoutException($"aria2c --version timed out after {VersionTimeout.TotalSeconds:0}s.");
        }

        Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(2));
        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
        return (process.ExitCode, stdout, stderr);
    }

    private static Aria2VerifyResult Fail(Aria2VerifyStage stage, string message)
        => new(false, stage, null, null, message);
}
