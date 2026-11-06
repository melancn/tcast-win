using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TCast.Infrastructure.Apk;

public sealed class ApkContainerValidator : IApkContainerValidator
{
    private const int MaxEntries = 10_000;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly uint[] CrcTable = CreateCrcTable();

    public async Task ValidateStandaloneAsync(string snapshotPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApkSnapshotFactory.EnsureOrdinaryPath(Path.GetFullPath(snapshotPath));
        await using var file = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (file.Length <= 0 || file.Length > ApkSnapshotFactory.DefaultMaxFileBytes)
        {
            throw new InvalidDataException("APK 文件大小超出限制");
        }

        uint[] checksums = ReadDirectory(file, cancellationToken);
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != checksums.Length)
        {
            throw new InvalidDataException("ZIP 目录不一致");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] buffer = new byte[81920];
        byte[]? manifest = null;
        long expanded = 0;
        for (int index = 0; index < archive.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.Entries[index];
            string name = entry.FullName;
            string[] segments = name.TrimEnd('/').Split('/');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
                name.Any(char.IsControl) || segments.Any(part => part is "" or "." or "..") || !names.Add(name))
            {
                throw new InvalidDataException("ZIP 包含不安全或重复路径");
            }

            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000))
            {
                throw new InvalidDataException("ZIP 包含非普通文件");
            }

            expanded += entry.Length;
            if (entry.Length > MaxEntryBytes || expanded > MaxExpandedBytes ||
                (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 1000))
            {
                throw new InvalidDataException("ZIP 解压资源超过限制");
            }

            bool isManifest = name == "AndroidManifest.xml";
            if (isManifest && (entry.Length == 0 || entry.Length > MaxManifestBytes))
            {
                throw new InvalidDataException("Android 清单大小无效");
            }

            using var manifestOutput = isManifest ? new MemoryStream((int)entry.Length) : null;
            await using Stream input = entry.Open();
            long actual = 0;
            uint crc = uint.MaxValue;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                actual += count;
                if (actual > entry.Length || actual > MaxEntryBytes)
                {
                    throw new InvalidDataException("ZIP 条目解压长度无效");
                }

                for (int i = 0; i < count; i++)
                {
                    crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                }

                manifestOutput?.Write(buffer, 0, count);
            }

            if (actual != entry.Length || ~crc != checksums[index])
            {
                throw new InvalidDataException("ZIP 条目损坏");
            }

            if (isManifest)
            {
                manifest = manifestOutput!.ToArray();
            }
        }

        if (manifest is null)
        {
            throw new InvalidDataException("APK 缺少 AndroidManifest.xml");
        }

        ValidateBinaryManifest(manifest, cancellationToken);
    }

    private static uint[] ReadDirectory(FileStream file, CancellationToken cancellationToken)
    {
        int tailLength = (int)Math.Min(file.Length, 65557);
        byte[] tail = new byte[tailLength];
        file.Position = file.Length - tailLength;
        file.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (U32(tail, i) == 0x06054B50 && i + 22 + U16(tail, i + 20) == tail.Length)
            {
                end = i;
                break;
            }
        }

        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 ||
            U16(tail, end + 8) != U16(tail, end + 10))
        {
            throw new InvalidDataException("ZIP 目录损坏或为多卷归档");
        }

        int count = U16(tail, end + 10);
        long directorySize = U32(tail, end + 12);
        long directoryOffset = U32(tail, end + 16);
        if (count == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            throw new NotSupportedException("尚不支持 ZIP64 APK");
        }

        if (count == 0 || count > MaxEntries || directoryOffset + directorySize != file.Length - tailLength + end)
        {
            throw new InvalidDataException("ZIP 目录大小无效");
        }

        uint[] checksums = new uint[count];
        byte[] header = new byte[46];
        byte[] local = new byte[30];
        long position = directoryOffset;
        var ranges = new List<(long Start, long End)>();
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position + header.Length > directoryOffset + directorySize)
            {
                throw new InvalidDataException("ZIP 目录截断");
            }

            file.Position = position;
            file.ReadExactly(header);
            ushort flags = U16(header, 8);
            ushort method = U16(header, 10);
            if (U32(header, 0) != 0x02014B50 || (flags & 0x2041) != 0 || U16(header, 34) != 0 || method is not (0 or 8))
            {
                throw new InvalidDataException("ZIP 损坏、加密或压缩方式不支持");
            }

            checksums[i] = U32(header, 16);
            uint compressed = U32(header, 20);
            uint uncompressed = U32(header, 24);
            long localOffset = U32(header, 42);
            if (compressed == uint.MaxValue || uncompressed == uint.MaxValue || localOffset == uint.MaxValue)
            {
                throw new NotSupportedException("尚不支持 ZIP64 APK");
            }

            position += 46 + U16(header, 28) + U16(header, 30) + U16(header, 32);
            if (position > directoryOffset + directorySize || localOffset + 30 > directoryOffset)
            {
                throw new InvalidDataException("ZIP 条目偏移无效");
            }

            file.Position = localOffset;
            file.ReadExactly(local);
            long dataEnd = localOffset + 30 + U16(local, 26) + U16(local, 28) + compressed;
            if (U32(local, 0) != 0x04034B50 || U16(local, 6) != flags || U16(local, 8) != method ||
                U16(local, 26) != U16(header, 28) || dataEnd > directoryOffset)
            {
                throw new InvalidDataException("ZIP 本地头无效");
            }

            byte[] localName = new byte[U16(local, 26)];
            file.ReadExactly(localName);
            file.Position = position - U16(header, 32) - U16(header, 30) - U16(header, 28);
            byte[] centralName = new byte[localName.Length];
            file.ReadExactly(centralName);
            if (!localName.AsSpan().SequenceEqual(centralName) ||
                ((flags & 8) == 0 && (U32(local, 14) != checksums[i] || U32(local, 18) != compressed || U32(local, 22) != uncompressed)))
            {
                throw new InvalidDataException("ZIP 本地头与目录不一致");
            }

            ranges.Add((localOffset, dataEnd));
        }

        if (position != directoryOffset + directorySize)
        {
            throw new InvalidDataException("ZIP 目录长度不一致");
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (int i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].Start < ranges[i - 1].End)
            {
                throw new InvalidDataException("ZIP 条目重叠");
            }
        }

        return checksums;
    }

    internal const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    internal static void ValidateBinaryManifest(byte[] data, CancellationToken cancellationToken = default) =>
        ParseBinaryManifest(data, cancellationToken);

    internal static void ParseBinaryManifest(byte[] data, CancellationToken cancellationToken = default,
        AxmlManifest.ManifestValues? values = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > MaxManifestBytes || data.Length < 8 || U16(data, 0) != 3 || U16(data, 2) != 8 || U32(data, 4) != data.Length)
        {
            throw new InvalidDataException("仅支持有效的 Android 二进制 AXML 清单");
        }

        string[]? strings = null;
        uint[]? resourceIds = null;
        var elements = new Stack<(string Namespace, string Name)>();
        var namespaces = new Stack<(uint Prefix, uint Uri, int Depth)>();
        bool resourceMapSeen = false;
        bool applicationSeen = false;
        bool rootSeen = false;
        for (int offset = 8; offset < data.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length - offset < 8)
            {
                throw new InvalidDataException("AXML 块截断");
            }

            int type = U16(data, offset);
            int headerSize = U16(data, offset + 2);
            uint sizeValue = U32(data, offset + 4);
            if (headerSize < 8 || sizeValue < headerSize || sizeValue > data.Length - offset)
            {
                throw new InvalidDataException("AXML 块大小无效");
            }

            int size = (int)sizeValue;
            ReadOnlySpan<byte> chunk = data.AsSpan(offset, size);
            if (type == 1)
            {
                if (strings is not null || rootSeen)
                {
                    throw new InvalidDataException("AXML 字符串池重复");
                }

                strings = ReadStringPool(chunk, cancellationToken);
            }
            else if (type == 0x0102)
            {
                if (strings is null || headerSize != 16 || size < 36)
                {
                    throw new InvalidDataException("AXML 元素无效");
                }

                string elementNamespace = NamespaceAt(strings, U32(chunk, 16));
                string element = StringAt(strings, U32(chunk, 20));
                bool root = elements.Count == 0;
                bool application = elements.Count == 1 && elementNamespace.Length == 0 && element == "application";
                bool splitScope = root || application;
                bool metadata = elementNamespace.Length == 0 && element == "meta-data" &&
                    elements.Count == 2 && elements.Peek() == (string.Empty, "application");
                if (elementNamespace.Length == 0 && element == "uses-split" && elements.Count == 1)
                {
                    throw new InvalidDataException("不支持依赖 split 的 APK");
                }

                if (elements.Count == 0)
                {
                    if (rootSeen || element != "manifest" || elementNamespace.Length != 0)
                    {
                        throw new InvalidDataException("AXML 根元素无效");
                    }

                    rootSeen = true;
                }

                if (application)
                {
                    if (applicationSeen)
                    {
                        throw new InvalidDataException("AXML application 元素重复");
                    }

                    applicationSeen = true;
                }

                int attributeStart = U16(chunk, 24) + 16;
                int attributeSize = U16(chunk, 26);
                int attributeCount = U16(chunk, 28);
                if (attributeStart < 36 || attributeSize != 20 || (long)attributeStart + (long)attributeSize * attributeCount > size)
                {
                    throw new InvalidDataException("AXML 属性无效");
                }

                var attributes = new Dictionary<(string Namespace, string Name), (string? Text, byte Type, uint Value)>();
                for (int i = 0; i < attributeCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int at = attributeStart + i * attributeSize;
                    string ns = NamespaceAt(strings, U32(chunk, at));
                    uint nameIndex = U32(chunk, at + 4);
                    string rawName = StringAt(strings, nameIndex);
                    uint attributeId = resourceIds is not null && nameIndex < resourceIds.Length
                        ? resourceIds[nameIndex]
                        : 0u;
                    string name = ns == AndroidNamespace ? AndroidAttributeNames(attributeId) ?? rawName : rawName;

                    uint raw = U32(chunk, at + 8);
                    byte valueType = chunk[at + 15];
                    uint value = U32(chunk, at + 16);
                    if (U16(chunk, at + 12) != 8 || chunk[at + 14] != 0)
                    {
                        throw new InvalidDataException("AXML 属性类型无效");
                    }

                    string? rawText = raw != uint.MaxValue ? StringAt(strings, raw) : null;
                    string? text = valueType == 3 ? StringAt(strings, value) : rawText;
                    if (!attributes.TryAdd((ns, name), (text, valueType, value)))
                    {
                        throw new InvalidDataException("AXML 属性重复或存在歧义");
                    }

                    if (splitScope)
                    {
                        values?.ReadAttribute(element, ns, name, valueType, value, text);
                    }
                }

                if (splitScope)
                {
                    foreach (var pair in attributes)
                    {
                        bool splitName = pair.Key.Namespace.Length == 0 && pair.Key.Name is "split" or "configForSplit";
                        bool splitTypes = pair.Key.Namespace == AndroidNamespace && pair.Key.Name is "requiredSplitTypes" or "splitTypes";
                        bool splitFlag = pair.Key.Namespace == AndroidNamespace && pair.Key.Name is "isFeatureSplit" or "isSplitRequired";
                        if (((splitName || splitTypes) && !(pair.Value.Type == 3 && string.IsNullOrEmpty(pair.Value.Text))) ||
                            (splitFlag && !IsFalse(pair.Value.Type, pair.Value.Value)))
                        {
                            throw new InvalidDataException("不支持 split APK");
                        }
                    }
                }

                if (metadata && attributes.TryGetValue((AndroidNamespace, "name"), out var metadataName))
                {
                    if (metadataName.Type != 3)
                    {
                        throw new NotSupportedException("尚不支持资源引用形式的 meta-data 名称");
                    }

                    if (metadataName.Text == "com.android.vending.splits.required" &&
                        (!attributes.TryGetValue((AndroidNamespace, "value"), out var metadataValue) ||
                            !IsFalse(metadataValue.Type, metadataValue.Value)))
                    {
                        throw new InvalidDataException("不支持要求 split 的 APK");
                    }
                }

                elements.Push((elementNamespace, element));
                if (elements.Count > 128)
                {
                    throw new InvalidDataException("AXML 嵌套过深");
                }
            }
            else if (type == 0x0103)
            {
                if (strings is null || headerSize != 16 || size != 24 || elements.Count == 0 ||
                    elements.Pop() != (NamespaceAt(strings, U32(chunk, 16)), StringAt(strings, U32(chunk, 20))))
                {
                    throw new InvalidDataException("AXML 元素未正确闭合");
                }
            }
            else if (type is 0x0100 or 0x0101)
            {
                if (strings is null || headerSize != 16 || size != 24)
                {
                    throw new InvalidDataException("AXML 命名空间无效");
                }

                uint prefix = U32(chunk, 16);
                uint uri = U32(chunk, 20);
                _ = NamespaceAt(strings, prefix);
                _ = StringAt(strings, uri);
                if (type == 0x0100)
                {
                    if (namespaces.Count >= 128)
                    {
                        throw new InvalidDataException("AXML 命名空间嵌套过深");
                    }

                    namespaces.Push((prefix, uri, elements.Count));
                }
                else if (namespaces.Count == 0 || namespaces.Pop() != (prefix, uri, elements.Count))
                {
                    throw new InvalidDataException("AXML 命名空间未正确闭合");
                }
            }
            else if (type == 0x0180)
            {
                if (headerSize != 8 || (size - 8) % 4 != 0 || rootSeen || resourceMapSeen ||
                    strings is null || (size - 8) / 4 > strings.Length)
                {
                    throw new InvalidDataException("AXML 资源映射无效");
                }

                resourceMapSeen = true;
                resourceIds = new uint[(size - 8) / 4];
                for (int i = 0; i < resourceIds.Length; i++)
                {
                    resourceIds[i] = U32(chunk, 8 + i * 4);
                }
            }
            else
            {
                throw new NotSupportedException("AXML 包含尚不支持的块类型");
            }

            offset += size;
        }

        if (!rootSeen || elements.Count != 0 || namespaces.Count != 0)
        {
            throw new InvalidDataException("AXML 清单不完整");
        }
    }

    /// <summary>
    /// Android 属性资源 id → 属性名。只列出本工程用到的属性；
    /// 属性是否“带 android 命名空间但不常用”并不影响这里，因为只做名字归一化。
    /// </summary>
    internal static string? AndroidAttributeNames(uint attributeId) => attributeId switch
    {
        0x01010001 => "label",
        0x01010003 => "name",
        0x01010024 => "value",
        0x0101021B => "versionCode",
        0x0101021C => "versionName",
        0x01010576 => "versionCodeMajor",
        0x0101055B => "isFeatureSplit",
        0x01010591 => "isSplitRequired",
        0x0101064E => "requiredSplitTypes",
        0x0101064F => "splitTypes",
        _ => null,
    };

    private static string NamespaceAt(string[] strings, uint index) =>
        index == uint.MaxValue ? string.Empty : StringAt(strings, index);

    private static bool IsFalse(byte type, uint value) => type is 0x10 or 0x11 or 0x12 && value == 0;

    /// <summary>解析 AXML 字符串池（UTF-8 或 UTF-16），供清单字段读取复用。</summary>
    internal static string[] ReadStringPool(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (chunk.Length < 28 || U16(chunk, 0) != 1 || U16(chunk, 2) != 28 || U32(chunk, 4) != chunk.Length)
        {
            throw new InvalidDataException("AXML 字符串池无效");
        }

        uint count = U32(chunk, 8);
        uint styles = U32(chunk, 12);
        uint start = U32(chunk, 20);
        uint styleStart = U32(chunk, 24);
        if (count > 100_000 || styles > 100_000 || start < 28L + 4L * (count + styles) || start > chunk.Length ||
            (styleStart != 0 && (styleStart < start || styleStart > chunk.Length)))
        {
            throw new InvalidDataException("AXML 字符串池偏移无效");
        }

        int end = styleStart == 0 ? chunk.Length : (int)styleStart;
        bool utf8 = (U32(chunk, 16) & 0x100) != 0;
        string[] strings = new string[count];
        long decodedCharacters = 0;
        for (int i = 0; i < strings.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long position = start + (long)U32(chunk, 28 + i * 4);
            if (position >= end)
            {
                throw new InvalidDataException("AXML 字符串偏移无效");
            }

            int at = (int)position;
            try
            {
                if (utf8)
                {
                    int characters = ReadLength8(chunk[..end], ref at);
                    int length = ReadLength8(chunk[..end], ref at);
                    decodedCharacters += characters;
                    if (decodedCharacters > MaxManifestBytes)
                    {
                        throw new InvalidDataException("AXML 字符串解码资源超过限制");
                    }

                    if (at + length >= end || chunk[at + length] != 0)
                    {
                        throw new InvalidDataException("AXML UTF-8 字符串截断");
                    }

                    strings[i] = new UTF8Encoding(false, true).GetString(chunk.Slice(at, length));
                    if (strings[i].Length != characters)
                    {
                        throw new InvalidDataException("AXML 字符串长度无效");
                    }
                }
                else
                {
                    int length = U16(chunk[..end], at);
                    at += 2;
                    if ((length & 0x8000) != 0)
                    {
                        length = ((length & 0x7FFF) << 16) | U16(chunk[..end], at);
                        at += 2;
                    }

                    decodedCharacters += length;
                    if (decodedCharacters > MaxManifestBytes)
                    {
                        throw new InvalidDataException("AXML 字符串解码资源超过限制");
                    }

                    if (length > (end - at - 2) / 2 || U16(chunk, at + length * 2) != 0)
                    {
                        throw new InvalidDataException("AXML UTF-16 字符串截断");
                    }

                    strings[i] = new UnicodeEncoding(false, false, true).GetString(chunk.Slice(at, length * 2));
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("AXML 字符串编码无效", exception);
            }
        }

        return strings;
    }

    private static int ReadLength8(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length)
        {
            throw new InvalidDataException("AXML 字符串截断");
        }

        int value = data[offset++];
        if ((value & 0x80) != 0)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException("AXML 字符串截断");
            }

            value = ((value & 0x7F) << 8) | data[offset++];
        }

        return value;
    }

    /// <summary>按索引取字符串，越界视为无效引用。</summary>
    internal static string StringAt(string[] strings, uint index) => index < strings.Length
        ? strings[index]
        : throw new InvalidDataException("AXML 字符串引用无效");

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => offset >= 0 && offset <= data.Length - 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..])
        : throw new InvalidDataException("数据截断");

    private static uint U32(ReadOnlySpan<byte> data, int offset) => offset >= 0 && offset <= data.Length - 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])
        : throw new InvalidDataException("数据截断");

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : 0xEDB88320 ^ (value >> 1);
            }

            table[i] = value;
        }

        return table;
    }
}
