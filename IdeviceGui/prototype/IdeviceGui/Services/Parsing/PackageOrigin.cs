using IdeviceGui.Resources;

namespace IdeviceGui.Services.Parsing;

/// <summary>
/// IPA 来源（2026-11-05 用户定，只保留三类）：
/// AppStore 原始包 / 重签名包 / 解密包。
/// 判定证据：`embedded.mobileprovision` 存在 → 重签；否则 `SC_Info/` 存在 → App Store 原始包；
/// 否则 → 解密包（详见 probe/verify/本地IPA来源判定-可行性-2026-11-05.md）。
/// </summary>
internal static class PackageOrigin
{
    public const string Original = "original";
    public const string Resigned = "resigned";
    public const string Decrypted = "decrypted";
    /// <summary>组行专用：成员来源不一致（不是文件来源分类）。</summary>
    public const string Mixed = "mixed";

    public static string FromEntries(bool hasEmbeddedProvision, bool hasScInfo)
        => hasEmbeddedProvision ? Resigned : hasScInfo ? Original : Decrypted;

    /// <summary>语言无关的规范筛选键（与旧版高级筛选 JSON / AdvancedFilterWindow 兼容）。</summary>
    public static string CanonicalText(string? kind) => kind switch
    {
        Original or "appstore" => "原始 App Store",   // appstore 兼容 2026-11-05 旧库
        Resigned => "已重签名",
        Decrypted => "已解密",
        Mixed => "多种",
        _ => "",
    };

    public static string DisplayText(string? kind) => kind switch
    {
        Original or "appstore" => Strings.Filter_Origin_AppStore,   // appstore 兼容 2026-11-05 旧库
        Resigned => Strings.Filter_Origin_Resigned,
        Decrypted => Strings.Filter_Origin_Decrypted,
        Mixed => Strings.Library_Common_Multiple,
        _ => "",
    };
}
