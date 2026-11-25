using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

internal sealed record WriteBoundaryCheckResult(bool Ok, string NormalizedPath, string? ErrorMessage);

internal sealed record PartVerifyResult(bool Ok, string? ErrorMessage);

/// <summary>
/// 下载路径、写盘安全边界（D31）、文件名清洗（D19）、预检/后验（D15/D21）与单文件安全删除（D32）。
/// </summary>
internal static class DownloadPathPolicy
{
    public const int MaxFileNameLength = 180;
    private static readonly byte[] ZipLocalHeaderMagic = [0x50, 0x4B, 0x03, 0x04];

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 每次执行写操作前现场读取 <c>Security.AllowOutsideProgramDirWrite</c>，严禁缓存到进程生命周期（D31）。
    /// </summary>
    public static WriteBoundaryCheckResult ValidateWriteDirectory(
        string directoryPath,
        string? programDir = null,
        string? settingsPath = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new WriteBoundaryCheckResult(false, "",
                Strings.Format(nameof(Strings.Policy_Error_DirNotWritableFormat), "", "empty path"));
        }

        string fullTarget;
        string fullProgramDir;
        try
        {
            fullTarget = TrimTrailingSeparators(Path.GetFullPath(directoryPath.Trim()));
            fullProgramDir = TrimTrailingSeparators(Path.GetFullPath(string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir));
        }
        catch (Exception ex)
        {
            return new WriteBoundaryCheckResult(false, directoryPath,
                Strings.Format(nameof(Strings.Policy_Error_DirNotWritableFormat), directoryPath, ex.Message));
        }

        // 拒绝任何目标或现有祖先路径上的 reparse point / symlink
        if (TryFindReparsePoint(fullTarget, out var reparsePath))
        {
            return new WriteBoundaryCheckResult(false, fullTarget,
                Strings.Format(nameof(Strings.Policy_Error_ReparsePointFormat), reparsePath));
        }

        var isUnc = IsUncPath(fullTarget);
        var insideProgramDir = !isUnc && IsUnderDirectory(fullTarget, fullProgramDir);
        if (!isUnc && IsForbiddenSystemOrProfileRoot(fullTarget, insideProgramDir))
        {
            return new WriteBoundaryCheckResult(false, fullTarget,
                Strings.Format(nameof(Strings.Policy_Error_ForbiddenSystemDirFormat), fullTarget));
        }

        var allowOutside = IndexerSettings.LoadAllowOutsideProgramDirWrite(settingsPath);
        if (!insideProgramDir && !allowOutside)
        {
            return new WriteBoundaryCheckResult(false, fullTarget,
                Strings.Format(nameof(Strings.Policy_Error_OutsideProgramDirFormat), fullTarget));
        }

        return new WriteBoundaryCheckResult(true, fullTarget, null);
    }

    public static WriteBoundaryCheckResult EnsureWritableDirectory(
        string directoryPath,
        string? programDir = null,
        string? settingsPath = null)
    {
        var boundary = ValidateWriteDirectory(directoryPath, programDir, settingsPath);
        if (!boundary.Ok) return boundary;

        try
        {
            Directory.CreateDirectory(boundary.NormalizedPath);
            if (TryFindReparsePoint(boundary.NormalizedPath, out var reparsePath))
            {
                return new WriteBoundaryCheckResult(false, boundary.NormalizedPath,
                    Strings.Format(nameof(Strings.Policy_Error_ReparsePointFormat), reparsePath));
            }

            var probe = Path.Combine(boundary.NormalizedPath, $".__write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            TryDeleteSingleFile(probe);
            return boundary;
        }
        catch (Exception ex)
        {
            return new WriteBoundaryCheckResult(false, boundary.NormalizedPath,
                Strings.Format(nameof(Strings.Policy_Error_DirNotWritableFormat), boundary.NormalizedPath, ex.Message));
        }
    }

    public static string BuildSafeFileName(string? title, string? version, string bundleId)
    {
        var safeTitle = SanitizeSegment(string.IsNullOrWhiteSpace(title) ? bundleId : title);
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = SanitizeSegment(bundleId);
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "app";

        var safeVersion = SanitizeSegment(version);
        var safeBundle = SanitizeSegment(bundleId);
        if (string.IsNullOrWhiteSpace(safeBundle)) safeBundle = "unknown";

        var stem = string.IsNullOrWhiteSpace(safeVersion)
            ? $"{safeTitle} ({safeBundle})"
            : $"{safeTitle} {safeVersion} ({safeBundle})";

        return ClampIpaFileName(stem, suffix: "");
    }

    public static string AllocateUniqueTargetPath(
        string targetDir,
        string? title,
        string? version,
        string bundleId,
        ISet<string>? reservedPaths = null)
    {
        var fullDir = Path.GetFullPath(targetDir);
        var baseName = BuildSafeFileName(title, version, bundleId);
        // 去掉 ".ipa"
        var baseStem = baseName[..^4];

        var candidate = Path.Combine(fullDir, baseName);
        if (!IsPathTaken(candidate, reservedPaths))
            return candidate;

        for (var i = 2; ; i++)
        {
            var suffix = $" ({i})";
            var fileName = ClampIpaFileName(baseStem, suffix);
            candidate = Path.Combine(fullDir, fileName);
            if (!IsPathTaken(candidate, reservedPaths))
                return candidate;
        }
    }

    public static string ResolveSessionFilePath(
        DownloadSettings settings,
        string? programDir = null,
        string? settingsPath = null)
    {
        var fullProgramDir = Path.GetFullPath(string.IsNullOrWhiteSpace(programDir) ? AppContext.BaseDirectory : programDir);
        var defaultSessionDir = Path.Combine(fullProgramDir, "data");

        string? candidateDir = settings.SessionDirMode switch
        {
            DownloadSettings.SessionTarget => Path.Combine(DownloadSettings.NormalizeDir(settings.Dir, DownloadSettings.DefaultDir(fullProgramDir)), ".aria2"),
            DownloadSettings.SessionCustom => settings.SessionDirCustom,
            _ => defaultSessionDir,
        };

        if (settings.SessionDirMode is DownloadSettings.SessionTarget or DownloadSettings.SessionCustom)
        {
            if (IsDriveRootOrEmpty(candidateDir))
            {
                candidateDir = defaultSessionDir;
            }
            else
            {
                var check = EnsureWritableDirectory(candidateDir!, fullProgramDir, settingsPath);
                if (!check.Ok)
                {
                    candidateDir = defaultSessionDir;
                }
                else
                {
                    return Path.Combine(check.NormalizedPath, "aria2.session");
                }
            }
        }

        Directory.CreateDirectory(defaultSessionDir);
        return Path.Combine(defaultSessionDir, "aria2.session");
    }

    public static (bool Ok, string? ErrorMessage) CheckPreDownload(
        string targetDir,
        long expectedBytes,
        DownloadSettings settings,
        string? programDir = null,
        string? settingsPath = null)
    {
        var dirCheck = EnsureWritableDirectory(targetDir, programDir, settingsPath);
        if (!dirCheck.Ok) return (false, dirCheck.ErrorMessage);

        return CheckSizeAndSpace(dirCheck.NormalizedPath, expectedBytes, expectedBytes, settings);
    }

    internal static (bool Ok, string? ErrorMessage) CheckSizeAndSpace(
        string normalizedTargetDir,
        long remainingBytesToWrite,
        long totalFileBytes,
        DownloadSettings settings)
    {
        if (settings.MaxFileSizeMB > 0 && totalFileBytes > 0)
        {
            var maxBytes = (long)settings.MaxFileSizeMB * 1024L * 1024L;
            if (totalFileBytes > maxBytes)
            {
                var sizeMb = Math.Max(1L, (long)Math.Ceiling(totalFileBytes / (1024.0 * 1024.0)));
                return (false, Strings.Format(nameof(Strings.Policy_Error_MaxFileSizeFormat), sizeMb, settings.MaxFileSizeMB));
            }
        }

        var requiredBytes = Math.Max(0L, remainingBytesToWrite) + (long)settings.SpaceMarginMB * 1024L * 1024L;
        if (requiredBytes > 0)
        {
            try
            {
                var root = Path.GetPathRoot(normalizedTargetDir);
                if (!string.IsNullOrWhiteSpace(root) && !IsUncPath(normalizedTargetDir))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                    {
                        var deficit = requiredBytes - drive.AvailableFreeSpace;
                        return (false, Strings.Format(nameof(Strings.Policy_Error_InsufficientSpaceFormat), FormatBytes(deficit)));
                    }
                }
            }
            catch
            {
                // 无法获取盘符空间（如虚拟路径）时不阻塞写盘已成功的目录
            }
        }

        return (true, null);
    }

    public static PartVerifyResult VerifyPartFile(string partPath, long expectedBytes, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(partPath) || Directory.Exists(partPath) || !File.Exists(partPath))
            return new PartVerifyResult(false, Strings.Policy_Error_EmptyFile);

        var info = new FileInfo(partPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return new PartVerifyResult(false, Strings.Format(nameof(Strings.Policy_Error_ReparsePointFormat), partPath));

        if (info.Length <= 0)
            return new PartVerifyResult(false, Strings.Policy_Error_EmptyFile);

        if (expectedBytes > 0 && info.Length != expectedBytes)
        {
            return new PartVerifyResult(false,
                Strings.Format(nameof(Strings.Policy_Error_SizeMismatchFormat), expectedBytes, info.Length));
        }

        using var stream = File.OpenRead(partPath);
        Span<byte> header = stackalloc byte[4];
        var read = stream.Read(header);
        if (read < 4 || !header.SequenceEqual(ZipLocalHeaderMagic))
            return new PartVerifyResult(false, Strings.Policy_Error_InvalidZipHeader);

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            stream.Position = 0;
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return new PartVerifyResult(false, Strings.Policy_Error_Sha256Mismatch);
        }

        return new PartVerifyResult(true, null);
    }

    /// <summary>
    /// 严格只删单个常规文件（D32），永不递归删除目录。
    /// </summary>
    public static (bool Deleted, string? ErrorMessage) TryDeleteSingleFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, Strings.Format(nameof(Strings.Policy_Error_NotSingleFileFormat), path ?? ""));

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            return (false, Strings.Format(nameof(Strings.Policy_Error_NotSingleFileFormat), fullPath));

        if (!File.Exists(fullPath))
            return (false, null);

        var attrs = File.GetAttributes(fullPath);
        if ((attrs & FileAttributes.Directory) != 0 || (attrs & FileAttributes.ReparsePoint) != 0)
            return (false, Strings.Format(nameof(Strings.Policy_Error_NotSingleFileFormat), fullPath));

        File.Delete(fullPath);
        return (true, null);
    }

    public static int CleanupExpiredPartFiles(
        string downloadDir,
        int partTtlHours,
        string? programDir = null,
        string? settingsPath = null,
        DateTime? utcNow = null)
    {
        var check = ValidateWriteDirectory(downloadDir, programDir, settingsPath);
        if (!check.Ok || !Directory.Exists(check.NormalizedPath)) return 0;

        var cutoff = (utcNow ?? DateTime.UtcNow).AddHours(-Math.Max(1, partTtlHours));
        var removed = 0;
        try
        {
            foreach (var part in Directory.EnumerateFiles(check.NormalizedPath, "*.ipa.part", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(part);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (info.LastWriteTimeUtc >= cutoff) continue;

                    var (deleted, _) = TryDeleteSingleFile(part);
                    if (deleted) removed++;
                    TryDeleteSingleFile(part + ".aria2");
                }
                catch
                {
                    // 单文件被占用时跳过，不阻塞启动
                }
            }
        }
        catch
        {
            // 启动清理失败不阻塞 UI
        }
        return removed;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024L) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
    }

    private static bool IsPathTaken(string candidateIpaPath, ISet<string>? reservedPaths)
    {
        if (reservedPaths is not null && reservedPaths.Contains(candidateIpaPath)) return true;
        return File.Exists(candidateIpaPath) || File.Exists(candidateIpaPath + ".part");
    }

    internal static string ClampIpaFileName(string stem, string suffix)
    {
        const string ext = ".ipa";
        var maxStemLen = MaxFileNameLength - ext.Length - suffix.Length;
        if (maxStemLen < 1) maxStemLen = 1;

        var clampedStem = stem.Length > maxStemLen ? stem[..maxStemLen].TrimEnd(' ', '.') : stem;
        if (string.IsNullOrWhiteSpace(clampedStem)) clampedStem = "app";

        var candidateStem = clampedStem + suffix;
        if (IsReservedDeviceName(candidateStem))
        {
            var prefixed = "_" + candidateStem;
            if (prefixed.Length + ext.Length > MaxFileNameLength)
            {
                var availableForStem = Math.Max(1, MaxFileNameLength - ext.Length - suffix.Length - 1);
                var trimmed = clampedStem.Length > availableForStem ? clampedStem[..availableForStem].TrimEnd(' ', '.') : clampedStem;
                prefixed = "_" + trimmed + suffix;
            }
            candidateStem = prefixed;
        }

        return candidateStem + ext;
    }

    private static bool IsReservedDeviceName(string stem)
    {
        var dotIndex = stem.IndexOf('.');
        var root = dotIndex >= 0 ? stem[..dotIndex] : stem;
        return ReservedWindowsNames.Contains(root.Trim());
    }

    private static string SanitizeSegment(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        var prevSpace = false;
        foreach (var ch in raw.Trim())
        {
            var replaced = ch < 0x20 || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || Array.IndexOf(invalid, ch) >= 0
                ? '_'
                : ch;
            if (char.IsWhiteSpace(replaced))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(replaced);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim(' ', '.');
    }

    private static bool TryFindReparsePoint(string fullPath, out string offendingPath)
    {
        offendingPath = "";
        var root = TrimTrailingSeparators(Path.GetPathRoot(fullPath) ?? "");
        string? current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    offendingPath = current;
                    return true;
                }
            }
            if (string.Equals(TrimTrailingSeparators(current), root, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static bool IsDriveRootOrEmpty(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        try
        {
            var full = TrimTrailingSeparators(Path.GetFullPath(dir.Trim()));
            var root = TrimTrailingSeparators(Path.GetPathRoot(full) ?? "");
            return string.IsNullOrEmpty(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsUncPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return Uri.TryCreate(fullPath, UriKind.Absolute, out var uri) && uri.IsUnc;
    }

    internal static bool IsUnderDirectory(string candidate, string root)
    {
        var a = TrimTrailingSeparators(candidate);
        var b = TrimTrailingSeparators(root);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar;
        return a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenSystemOrProfileRoot(string fullTarget, bool insideProgramDir)
    {
        foreach (var tempCandidate in new[]
        {
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("TEMP"),
            Environment.GetEnvironmentVariable("TMP"),
        })
        {
            if (TryNormalizeDir(tempCandidate, out var tempRoot) && IsUnderDirectory(fullTarget, tempRoot))
                return true;
        }

        if (insideProgramDir) return false;

        foreach (var folder in new[]
        {
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
        })
        {
            var raw = Environment.GetFolderPath(folder);
            if (TryNormalizeDir(raw, out var protectedRoot) && IsUnderDirectory(fullTarget, protectedRoot))
                return true;
        }

        var userProfileRaw = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (TryNormalizeDir(userProfileRaw, out var userProfileRoot) &&
            string.Equals(TrimTrailingSeparators(fullTarget), userProfileRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TryNormalizeDir(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            normalized = TrimTrailingSeparators(Path.GetFullPath(raw.Trim()));
            return !string.IsNullOrEmpty(normalized);
        }
        catch
        {
            return false;
        }
    }

    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed + Path.DirectorySeparatorChar;
        return trimmed.Length == 0 ? path : trimmed;
    }
}
