using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace IdeviceGui;

/// <summary>
/// 极简 plist 解析器，支持 XML 与 binary（bplist00）两种格式。
/// 返回 object 图：Dictionary&lt;string, object?&gt; / List&lt;object?&gt; / string / long / double / bool / DateTime / byte[]。
/// 只解析读取 Info.plist 所需的类型，不做写入。
/// </summary>
internal static class Plist
{
    public static object? Parse(byte[] data)
    {
        if (data.Length >= 8 && data[0] == 'b' && data[1] == 'p' && data[2] == 'l' && data[3] == 'i'
            && data[4] == 's' && data[5] == 't')
        {
            return ParseBinary(data);
        }
        return ParseXml(Encoding.UTF8.GetString(data));
    }

    // ---------------- XML ----------------

    private static object? ParseXml(string text)
    {
        var doc = XDocument.Parse(text);
        var root = doc.Root;
        if (root is null) return null;
        var value = root.Elements().FirstOrDefault();
        return value is null ? null : XmlValue(value);
    }

    private static object? XmlValue(XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "dict":
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                var children = el.Elements().ToList();
                for (var i = 0; i + 1 < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName != "key") continue;
                    dict[children[i].Value] = XmlValue(children[i + 1]);
                }
                return dict;
            case "array":
                return el.Elements().Select(XmlValue).ToList();
            case "string":
            case "key":
                return el.Value;
            case "integer":
                return long.TryParse(el.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0L;
            case "real":
                return double.TryParse(el.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
            case "true":
                return true;
            case "false":
                return false;
            case "data":
                try { return Convert.FromBase64String(el.Value.Trim()); } catch { return Array.Empty<byte>(); }
            case "date":
                return DateTime.TryParse(el.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt)
                    ? dt : DateTime.MinValue;
            default:
                return el.Value;
        }
    }

    // ---------------- binary ----------------

    private static object? ParseBinary(byte[] data)
    {
        var len = data.Length;
        if (len < 32) return null;

        // trailer: 6 字节保留 + 1 字节 offset 宽度 + 1 字节 ref 宽度 + 8 numObjects + 8 topObject + 8 offsetTable
        var offsetIntSize = data[len - 26];
        var objectRefSize = data[len - 25];
        if (offsetIntSize is not (1 or 2 or 4 or 8)) return null;
        if (objectRefSize is not (1 or 2 or 4 or 8)) return null;

        var numObjectsU = ReadUInt(data, len - 24, 8);
        var topObjectU = ReadUInt(data, len - 16, 8);
        var offsetTableOffsetU = ReadUInt(data, len - 8, 8);

        var bodyLimit = len - 32;
        if (bodyLimit <= 8) return null;
        if (numObjectsU == 0 || numObjectsU > (ulong)(bodyLimit / offsetIntSize)) return null;
        if (topObjectU >= numObjectsU) return null;
        if (offsetTableOffsetU < 8 || offsetTableOffsetU >= (ulong)bodyLimit) return null;

        var offsetTableBytes = numObjectsU * (ulong)offsetIntSize;
        if (offsetTableOffsetU + offsetTableBytes > (ulong)bodyLimit) return null;

        var numObjects = (int)numObjectsU;
        var topObject = (int)topObjectU;
        var offsetTableOffset = (int)offsetTableOffsetU;

        int OffsetOf(int index)
        {
            var pos = offsetTableOffset + index * offsetIntSize;
            var off = ReadUInt(data, pos, offsetIntSize);
            if (off < 8 || off >= (ulong)offsetTableOffset) return -1;
            return (int)off;
        }

        object? ParseObject(int index, int depth)
        {
            if (depth > 64 || index < 0 || index >= numObjects) return null;
            var offset = OffsetOf(index);
            if (offset < 8 || offset >= offsetTableOffset) return null;

            var marker = data[offset];
            var type = marker >> 4;
            var info = marker & 0x0F;
            var cursor = offset + 1;

            int ReadCount()
            {
                if (info != 0x0F) return info;
                if (cursor >= offsetTableOffset) return -1;
                // 长度另存为一个 int 对象（marker 高半字节必须为 0x1）
                var m = data[cursor];
                if ((m >> 4) != 0x1) return -1;
                var sizeShift = m & 0x0F;
                if (sizeShift > 3) return -1;
                var size = 1 << sizeShift;
                if (size > offsetTableOffset - cursor - 1) return -1;
                var value = ReadUInt(data, cursor + 1, size);
                if (value > int.MaxValue) return -1;
                cursor += 1 + size;
                return (int)value;
            }

            int ReadRef(int i)
            {
                var pos = cursor + i * objectRefSize;
                if (pos < cursor || pos > offsetTableOffset - objectRefSize) return -1;
                var rawRef = ReadUInt(data, pos, objectRefSize);
                return rawRef < (ulong)numObjects ? (int)rawRef : -1;
            }

            switch (type)
            {
                case 0x0:
                    return info switch
                    {
                        0x08 => false,
                        0x09 => true,
                        _ => null,
                    };

                case 0x1:
                {
                    if (info > 3) return null;
                    var size = 1 << info;
                    if (size > offsetTableOffset - cursor) return null;
                    return (long)ReadUInt(data, cursor, size);
                }

                case 0x2:
                {
                    if (info is not (2 or 3)) return null;
                    var size = 1 << info;
                    if (size > offsetTableOffset - cursor) return null;
                    if (size == 4)
                    {
                        var bits = (uint)ReadUInt(data, cursor, 4);
                        return (double)BitConverter.Int32BitsToSingle((int)bits);
                    }
                    var raw = ReadUInt(data, cursor, 8);
                    return BitConverter.Int64BitsToDouble((long)raw);
                }

                case 0x3:
                {
                    if (info != 3 || 8 > offsetTableOffset - cursor) return null;
                    var raw = ReadUInt(data, cursor, 8);
                    var seconds = BitConverter.Int64BitsToDouble((long)raw);
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
                }

                case 0x4:
                {
                    var count = ReadCount();
                    if (count < 0 || count > offsetTableOffset - cursor) return null;
                    var bytes = new byte[count];
                    Array.Copy(data, cursor, bytes, 0, count);
                    return bytes;
                }

                case 0x5:
                {
                    var count = ReadCount();
                    if (count < 0 || count > offsetTableOffset - cursor) return null;
                    return Encoding.UTF8.GetString(data, cursor, count);
                }

                case 0x6:
                {
                    var count = ReadCount();
                    if (count < 0 || count > (offsetTableOffset - cursor) / 2) return null;
                    var byteCount = count * 2;
                    return Encoding.BigEndianUnicode.GetString(data, cursor, byteCount);
                }

                case 0x8:
                {
                    var size = info + 1;
                    if (size > 8 || size > offsetTableOffset - cursor) return null;
                    return (long)ReadUInt(data, cursor, size);
                }

                case 0xA:
                case 0xC:
                {
                    var count = ReadCount();
                    if (count < 0 || count > (offsetTableOffset - cursor) / objectRefSize) return null;
                    var list = new List<object?>(Math.Min(count, 4096));
                    for (var i = 0; i < count; i++) list.Add(ParseObject(ReadRef(i), depth + 1));
                    return list;
                }

                case 0xD:
                {
                    var count = ReadCount();
                    if (count < 0 || count > (offsetTableOffset - cursor) / (2 * objectRefSize)) return null;
                    var dict = new Dictionary<string, object?>(Math.Min(count, 4096), StringComparer.Ordinal);
                    for (var i = 0; i < count; i++)
                    {
                        var keyObj = ParseObject(ReadRef(i), depth + 1);
                        var valObj = ParseObject(ReadRef(count + i), depth + 1);
                        if (keyObj is string key) dict[key] = valObj;
                    }
                    return dict;
                }

                default:
                    return null;
            }
        }

        return ParseObject(topObject, 0);
    }

    private static ulong ReadUInt(byte[] data, int offset, int size)
    {
        ulong value = 0;
        for (var i = 0; i < size; i++)
        {
            if (offset + i < 0 || offset + i >= data.Length) break;
            value = (value << 8) | data[offset + i];
        }
        return value;
    }
}
