using System.Diagnostics;
using System.IO;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

internal sealed record ExternalLinkLaunchResult(
    bool Ok,
    ProcessStartInfo? StartInfo,
    string? SafeUrl,
    string? ErrorMessage);

/// <summary>
/// Safe external http/https URL launcher supporting either system default shell
/// or a user-selected .exe via UseShellExecute=false + ArgumentList (never string-concatenated commands).
/// </summary>
internal static class ExternalLinkOpener
{
    public static bool TryCreateSafeHttpUri(string? raw, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        uri = parsed;
        return true;
    }

    public static ExternalLinkLaunchResult TryBuildStartInfo(
        string? rawUrl,
        string? customProgramPath,
        Func<string, bool>? fileExists = null)
    {
        if (!TryCreateSafeHttpUri(rawUrl, out var uri) || uri is null)
            return new ExternalLinkLaunchResult(false, null, null, Strings.Status_SourceLinkInvalid);

        var trimmedProg = (customProgramPath ?? "").Trim();
        if (trimmedProg.Length == 0)
        {
            return new ExternalLinkLaunchResult(
                true,
                new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true },
                uri.AbsoluteUri,
                null);
        }

        string fullProg;
        try
        {
            fullProg = Path.GetFullPath(trimmedProg);
        }
        catch
        {
            return new ExternalLinkLaunchResult(
                false, null, uri.AbsoluteUri,
                Strings.Format(nameof(Strings.Links_Error_ProgramMissingFormat), trimmedProg));
        }

        if (!string.Equals(Path.GetExtension(fullProg), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !(fileExists ?? File.Exists)(fullProg))
        {
            return new ExternalLinkLaunchResult(
                false, null, uri.AbsoluteUri,
                Strings.Format(nameof(Strings.Links_Error_ProgramMissingFormat), fullProg));
        }

        var psi = new ProcessStartInfo(fullProg)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(uri.AbsoluteUri);
        return new ExternalLinkLaunchResult(true, psi, uri.AbsoluteUri, null);
    }
}
