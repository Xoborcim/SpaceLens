using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace SpaceLens.Mac;

/// <summary>
/// Reads Apple property lists (Info.plist and friends) in XML or binary ("bplist00") form.
/// Values become <see cref="Dictionary{TKey,TValue}"/> (string keys), <see cref="List{T}"/>, string, long,
/// double, bool, byte[] or <see cref="DateTime"/> (UTC). Only reading is supported.
/// </summary>
public static class PropertyList
{
    private const int MaxObjects = 1_000_000;

    public static object? Read(byte[] data)
    {
        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "bplist00")
        {
            return new BinaryReader(data).ReadTop();
        }

        return ReadXml(XDocument.Parse(Encoding.UTF8.GetString(data)).Root?.Elements().FirstOrDefault());
    }

    /// <summary>Reads a file whose top-level value is a dictionary (like Info.plist); null when unreadable.</summary>
    public static Dictionary<string, object?>? ReadDictionary(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path)) as Dictionary<string, object?>;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    public static string? GetString(this Dictionary<string, object?> dictionary, string key) =>
        dictionary.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static object? ReadXml(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        switch (element.Name.LocalName)
        {
            case "dict":
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? key = null;
                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName == "key")
                    {
                        key = child.Value;
                    }
                    else if (key is not null)
                    {
                        dict[key] = ReadXml(child);
                        key = null;
                    }
                }

                return dict;
            case "array":
                return element.Elements().Select(ReadXml).ToList();
            case "string":
                return element.Value;
            case "integer":
                return long.Parse(element.Value.Trim(), CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(element.Value.Trim(), CultureInfo.InvariantCulture);
            case "true":
                return true;
            case "false":
                return false;
            case "date":
                return DateTime.Parse(element.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            case "data":
                return Convert.FromBase64String(string.Concat(element.Value.Where(c => !char.IsWhiteSpace(c))));
            default:
                return null;
        }
    }

    /// <summary>The binary format: objects referenced through an offset table described by a 32-byte trailer.</summary>
    private sealed class BinaryReader(byte[] data)
    {
        private int _offsetSize;
        private int _refSize;
        private long[] _offsets = [];
        private int _depth;

        public object? ReadTop()
        {
            if (data.Length < 8 + 32)
            {
                throw new InvalidDataException("Binary property list too short.");
            }

            var trailer = data.AsSpan(data.Length - 32);
            _offsetSize = trailer[6];
            _refSize = trailer[7];
            long count = BinaryPrimitives.ReadInt64BigEndian(trailer[8..]);
            long top = BinaryPrimitives.ReadInt64BigEndian(trailer[16..]);
            long tableOffset = BinaryPrimitives.ReadInt64BigEndian(trailer[24..]);
            if (_offsetSize is < 1 or > 8 || _refSize is < 1 or > 8 || count is <= 0 or > MaxObjects || top < 0 || top >= count ||
                tableOffset < 8 || tableOffset + count * _offsetSize > data.Length - 32)
            {
                throw new InvalidDataException("Invalid binary property list trailer.");
            }

            _offsets = new long[count];
            for (int i = 0; i < count; i++)
            {
                _offsets[i] = (long)ReadUInt((int)tableOffset + i * _offsetSize, _offsetSize);
            }

            return ReadObject(top);
        }

        private object? ReadObject(long reference)
        {
            if (reference < 0 || reference >= _offsets.Length || ++_depth > 512)
            {
                throw new InvalidDataException("Invalid object reference.");
            }

            try
            {
                int offset = checked((int)_offsets[reference]);
                CheckRange(offset, 1);
                byte marker = data[offset];
                int type = marker >> 4;
                int info = marker & 0x0F;
                switch (type)
                {
                    case 0x0:
                        return info switch { 0x8 => false, 0x9 => true, _ => null };
                    case 0x1:
                    {
                        int size = 1 << info;
                        CheckRange(offset + 1, size);
                        return size == 8 ? BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset + 1)) : (long)ReadUInt(offset + 1, size);
                    }

                    case 0x2:
                    {
                        int size = 1 << info;
                        CheckRange(offset + 1, size);
                        return size == 4 ? BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset + 1)) : BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(offset + 1));
                    }

                    case 0x3:
                        CheckRange(offset + 1, 8);
                        return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(offset + 1)));
                    case 0x4:
                    {
                        var (length, start) = Length(offset, info);
                        CheckRange(start, length);
                        return data.AsSpan(start, length).ToArray();
                    }

                    case 0x5:
                    {
                        var (length, start) = Length(offset, info);
                        CheckRange(start, length);
                        return Encoding.ASCII.GetString(data, start, length);
                    }

                    case 0x6:
                    {
                        var (length, start) = Length(offset, info);
                        CheckRange(start, length * 2);
                        return Encoding.BigEndianUnicode.GetString(data, start, length * 2);
                    }

                    case 0x8:
                        CheckRange(offset + 1, info + 1);
                        return (long)ReadUInt(offset + 1, info + 1);
                    case 0xA:
                    {
                        var (length, start) = Length(offset, info);
                        CheckRange(start, length * _refSize);
                        var list = new List<object?>(length);
                        for (int i = 0; i < length; i++)
                        {
                            list.Add(ReadObject((long)ReadUInt(start + i * _refSize, _refSize)));
                        }

                        return list;
                    }

                    case 0xD:
                    {
                        var (length, start) = Length(offset, info);
                        CheckRange(start, length * 2 * _refSize);
                        var dict = new Dictionary<string, object?>(length, StringComparer.Ordinal);
                        for (int i = 0; i < length; i++)
                        {
                            if (ReadObject((long)ReadUInt(start + i * _refSize, _refSize)) is string key)
                            {
                                dict[key] = ReadObject((long)ReadUInt(start + (length + i) * _refSize, _refSize));
                            }
                        }

                        return dict;
                    }

                    default:
                        return null;
                }
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>A length in the marker's low nibble, or (0xF) as a following integer object.</summary>
        private (int Length, int Start) Length(int offset, int info)
        {
            if (info != 0xF)
            {
                return (info, offset + 1);
            }

            CheckRange(offset + 1, 1);
            byte intMarker = data[offset + 1];
            if (intMarker >> 4 != 0x1)
            {
                throw new InvalidDataException("Invalid length.");
            }

            int size = 1 << (intMarker & 0x0F);
            CheckRange(offset + 2, size);
            long length = (long)ReadUInt(offset + 2, size);
            if (length < 0 || length > data.Length)
            {
                throw new InvalidDataException("Invalid length.");
            }

            return ((int)length, offset + 2 + size);
        }

        private ulong ReadUInt(int offset, int size)
        {
            CheckRange(offset, size);
            ulong value = 0;
            for (int i = 0; i < size; i++)
            {
                value = (value << 8) | data[offset + i];
            }

            return value;
        }

        private void CheckRange(int offset, long length)
        {
            if (offset < 0 || length < 0 || offset + length > data.Length)
            {
                throw new InvalidDataException("Property list data out of range.");
            }
        }
    }
}
