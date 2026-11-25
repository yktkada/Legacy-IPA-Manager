using System.Globalization;
using System.Threading;

#if IDEVICEINDEX
namespace IdeviceIndex;
#else
namespace IdeviceGui.ViewModels.Library;
#endif

/// <summary>
/// 名称首字符分桶与汉语拼音排序辅助（0-9、A-Z、#）：
/// 主程序资料库、主程序设备应用与 IdeviceIndex 共用，保证零第三方依赖且汉字按拼音首字母归入 A-Z。
/// </summary>
internal static class AlphabetBuckets
{
    public const int BucketCount = 37;
    public const int HashBucketIndex = 36;

    public static readonly string[] Labels =
    {
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J",
        "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T",
        "U", "V", "W", "X", "Y", "Z", "#",
    };

    // Windows ICU zh-CN 拼音序下，各首字母最小边界汉字（无 I/U/V；已实测 0x4E00..0x9FFF 区间无字符小于 A 锚点）。
    private static readonly (byte LetterOffset, string Anchor)[] ZhCnAnchors =
    {
        (0,  "\u963f"), // A: 阿
        (1,  "\u516b"), // B: 八
        (2,  "\u5693"), // C: 嚓
        (3,  "\u642d"), // D: 搭
        (4,  "\u59b8"), // E: 妸
        (5,  "\u53d1"), // F: 发
        (6,  "\u65ee"), // G: 旮
        (7,  "\u54c8"), // H: 哈
        (9,  "\u8ba5"), // J: 讥
        (10, "\u5496"), // K: 咖
        (11, "\u5783"), // L: 垃
        (12, "\u5988"), // M: 妈
        (13, "\u62ff"), // N: 拿
        (14, "\u5594"), // O: 喔
        (15, "\u8db4"), // P: 趴
        (16, "\u4e03"), // Q: 七
        (17, "\u5465"), // R: 呥
        (18, "\u4ee8"), // S: 仨
        (19, "\u4ed6"), // T: 他
        (22, "\u6316"), // W: 挖
        (23, "\u5915"), // X: 夕
        (24, "\u4e2b"), // Y: 丫
        (25, "\u531d"), // Z: 匝
    };

    private static readonly CompareInfo ZhCnCompare = CreateZhCnCompareInfo();
    private static readonly byte[] CjkBmpCache = new byte[0xA000 - 0x4E00]; // 0 = 未算，1..26 = A..Z + 1

    private static CompareInfo CreateZhCnCompareInfo()
    {
        try
        {
            return CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture.CompareInfo;
        }
    }

    public static int ClassifyIndex(string? text)
    {
        if (string.IsNullOrEmpty(text)) return HashBucketIndex;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\u200B' || c == '\uFEFF')
                continue;

            if (c is >= '0' and <= '9') return c - '0';
            if (c is >= '\uFF10' and <= '\uFF19') return c - '\uFF10';
            if (c is >= 'A' and <= 'Z') return 10 + (c - 'A');
            if (c is >= 'a' and <= 'z') return 10 + (c - 'a');
            if (c is >= '\uFF21' and <= '\uFF3A') return 10 + (c - '\uFF21');
            if (c is >= '\uFF41' and <= '\uFF5A') return 10 + (c - '\uFF41');

            if (c is >= '\u4E00' and <= '\u9FFF')
            {
                var slot = c - '\u4E00';
                var cached = Volatile.Read(ref CjkBmpCache[slot]);
                if (cached == 0)
                {
                    var letter = ComputeCjkLetterIndex(c);
                    cached = (byte)(letter + 1);
                    Volatile.Write(ref CjkBmpCache[slot], cached);
                }

                var offset = cached - 1;
                return offset is >= 0 and < 26 ? 10 + offset : HashBucketIndex;
            }

            if (c is >= '\u3400' and <= '\u4DBF' || c is >= '\uF900' and <= '\uFAFF')
            {
                var offset = ComputeCjkLetterIndex(c);
                return offset is >= 0 and < 26 ? 10 + offset : HashBucketIndex;
            }

            return HashBucketIndex;
        }

        return HashBucketIndex;
    }

    public static string ClassifyLabel(string? text) => Labels[ClassifyIndex(text)];

    private static int ComputeCjkLetterIndex(char c)
    {
        var chStr = c.ToString();
        var lo = 0;
        var hi = ZhCnAnchors.Length - 1;
        var hit = 0;

        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var cmp = ZhCnCompare.Compare(chStr, ZhCnAnchors[mid].Anchor, CompareOptions.IgnoreCase);
            if (cmp >= 0)
            {
                hit = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ZhCnAnchors[hit].LetterOffset;
    }

    /// <summary>
    /// 统一名称比较器：先按 0-9 / A-Z（汉字拼音归入对应字母）/ # 分桶序比较，同桶内按 zh-CN 忽略大小写排序。
    /// 使英文与汉语拼音同字母混排在同一区块内。
    /// </summary>
    public static int CompareNames(string? left, string? right)
    {
        var lb = ClassifyIndex(left);
        var rb = ClassifyIndex(right);
        var byBucket = lb.CompareTo(rb);
        if (byBucket != 0) return byBucket;

        return ZhCnCompare.Compare(left ?? "", right ?? "", CompareOptions.IgnoreCase);
    }

    /// <summary>
    /// 一趟线性扫描生成 37 个桶的计数与首个可见行下标（-1 表示无匹配）。
    /// </summary>
    public static void BuildIndex<T>(
        IReadOnlyList<T> items,
        Func<T, string?> nameSelector,
        Func<T, bool>? includePredicate,
        int[] counts,
        int[] firstRowIndices)
    {
        Array.Clear(counts, 0, counts.Length);
        Array.Fill(firstRowIndices, -1);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (includePredicate is not null && !includePredicate(item)) continue;

            var bucket = ClassifyIndex(nameSelector(item));
            counts[bucket]++;
            if (firstRowIndices[bucket] < 0) firstRowIndices[bucket] = i;
        }
    }
}
