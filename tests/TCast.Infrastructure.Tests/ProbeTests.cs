using System.Buffers.Binary;
using System.Text;
using TCast.Infrastructure.Apk;
using Xunit;

namespace TCast.Infrastructure.Tests;

public sealed class ArscReaderTests
{
    private const uint ResourceId = 0x7F010000;

    [Theory]
    [InlineData(false, 284)]
    [InlineData(false, 288)]
    [InlineData(true, 284)]
    [InlineData(true, 288)]
    public void ResolvesDefaultChineseString(bool utf8, int packageHeader)
    {
        byte[] table = Table(StringPool(["unused", "中文应用"], utf8),
            [TypeChunk([new Entry(0, 3, 1)])], packageHeader);

        Assert.Equal("中文应用", ArscReader.ResolveString(table, ResourceId));
    }

    [Theory]
    [InlineData(43)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(200)]
    public void ResolvesUtf8LengthsAcrossOneByteBoundary(int characters)
    {
        string expected = new('中', characters);
        byte[] table = Table(StringPool([expected]), [TypeChunk([new Entry(0, 3, 0)])]);

        Assert.Equal(expected, ArscReader.ResolveString(table, ResourceId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvesEmptyAndSupplementaryCharacterStrings(bool utf8)
    {
        byte[] table = Table(StringPool(["", "应用\U0001F680"], utf8),
            [TypeChunk([new Entry(0, 3, 0), new Entry(1, 3, 1)])]);

        Assert.Equal("", ArscReader.ResolveString(table, ResourceId));
        Assert.Equal("应用\U0001F680", ArscReader.ResolveString(table, ResourceId + 1));
    }

    [Theory]
    [InlineData(32767, true)]
    [InlineData(32768, true)]
    [InlineData(32769, false)]
    public void HandlesUtf16ExtendedLengthAndCharacterLimit(int length, bool supported)
    {
        string value = new('中', length);
        byte[] table = Table(StringPool([value], false), [TypeChunk([new Entry(0, 3, 0)])]);

        Assert.Equal(supported ? value : null, ArscReader.ResolveString(table, ResourceId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ResolvesDenseSparseAndOffset16EntriesAndMissingSlots(byte flags)
    {
        byte[] table = Table(StringPool(["first", "last"]),
            [TypeChunk([new Entry(1, 3, 0), new Entry(7, 3, 1)], flags)]);

        Assert.Null(ArscReader.ResolveString(table, ResourceId));
        Assert.Equal("first", ArscReader.ResolveString(table, ResourceId + 1));
        Assert.Null(ArscReader.ResolveString(table, ResourceId + 2));
        Assert.Equal("last", ArscReader.ResolveString(table, ResourceId + 7));
        Assert.Null(ArscReader.ResolveString(table, ResourceId + 8));
        Assert.Null(ArscReader.ResolveString(table, ResourceId + ushort.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ResolvesCompactStringAndReferenceEntries(byte flags)
    {
        byte[] table = Table(StringPool(["compact"]),
            [TypeChunk([new Entry(0, 1, ResourceId + 1, true), new Entry(1, 3, 0, true)], flags)]);

        Assert.Equal("compact", ArscReader.ResolveString(table, ResourceId));
        Assert.Equal("compact", ArscReader.ResolveString(table, ResourceId + 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(32)]
    public void ResolvesReferenceChainsWithinDepthLimit(int length)
    {
        byte[] table = ReferenceChain(length);

        Assert.Equal("target", ArscReader.ResolveString(table, ResourceId));
    }

    [Fact]
    public void RejectsReferenceChainsBeyondDepthLimit()
    {
        byte[] table = ReferenceChain(33);

        Assert.Null(ArscReader.ResolveString(table, ResourceId));
        Assert.Equal("target", ArscReader.ResolveString(table, ResourceId + 1));
    }

    [Fact]
    public void RejectsSelfReferencesCyclesAndMissingReferences()
    {
        byte[] table = Table(StringPool(["unused"]),
            [TypeChunk([
                new Entry(0, 1, ResourceId),
                new Entry(1, 1, ResourceId + 2),
                new Entry(2, 1, ResourceId + 1),
                new Entry(3, 1, ResourceId + 20),
                new Entry(4, 1, 0)
            ])]);

        for (uint index = 0; index < 5; index++)
        {
            Assert.Null(ArscReader.ResolveString(table, ResourceId + index));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultConfigurationWinsRegardlessOfChunkOrder(bool localizedFirst)
    {
        byte[] localized = TypeChunk([new Entry(0, 3, 0)], localized: true);
        byte[] defaultType = TypeChunk([new Entry(0, 3, 1)]);
        byte[][] types = localizedFirst ? [localized, defaultType] : [defaultType, localized];
        byte[] table = Table(StringPool(["localized", "default"]), types);

        Assert.Equal("default", ArscReader.ResolveString(table, ResourceId));
    }

    [Fact]
    public void LocalizedOnlyConfigurationIsNotUsedAsFallback()
    {
        byte[] table = Table(StringPool(["localized"]),
            [TypeChunk([new Entry(0, 3, 0)], localized: true)]);

        Assert.Null(ArscReader.ResolveString(table, ResourceId));
    }

    [Theory]
    [InlineData(28)]
    [InlineData(64)]
    public void ReadsEntryOffsetsAfterEntireConfiguration(int configSize)
    {
        byte[] table = Table(StringPool(["configuration"]),
            [TypeChunk([new Entry(0, 3, 0)], configSize: configSize)]);

        Assert.Equal("configuration", ArscReader.ResolveString(table, ResourceId));
    }

    [Fact]
    public void AppliesPackageTypeIdOffsetToValuesAndReferences()
    {
        const uint shiftedId = 0x7F030000;
        byte[] table = Table(StringPool(["shifted"]),
            [TypeChunk([new Entry(0, 1, shiftedId + 1), new Entry(1, 3, 0)])], typeIdOffset: 2);

        Assert.Equal("shifted", ArscReader.ResolveString(table, shiftedId));
        Assert.Null(ArscReader.ResolveString(table, ResourceId));
        Assert.Null(ArscReader.ResolveString(table, 0x80030000));
        Assert.Null(ArscReader.ResolveString(table, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(18)]
    public void NonStringAndNonReferenceValuesAreNotResolved(byte dataType)
    {
        byte[] table = Table(StringPool(["not a string value"]),
            [TypeChunk([new Entry(0, dataType, 0)])]);

        Assert.Null(ArscReader.ResolveString(table, ResourceId));
    }

    [Fact]
    public void EveryTruncatedPrefixReturnsNullWithoutThrowing()
    {
        byte[] table = Table(StringPool(["中文"]), [TypeChunk([new Entry(0, 3, 0)])]);

        for (int length = 0; length < table.Length; length++)
        {
            byte[] truncated = table[..length];
            Assert.Null(ArscReader.ResolveString(truncated, ResourceId));
            if (length >= 12)
            {
                Put32(truncated, 4, (uint)length);
                Assert.Null(ArscReader.ResolveString(truncated, ResourceId));
            }
        }
    }

    [Theory]
    [InlineData("config-size")]
    [InlineData("entry-count")]
    [InlineData("entries-start")]
    [InlineData("dense-offset")]
    [InlineData("unaligned-offset")]
    [InlineData("entry-size")]
    [InlineData("value-size")]
    [InlineData("value-reserved")]
    [InlineData("string-index")]
    public void MalformedTypeStructuresReturnNull(string mutation)
    {
        byte[] type = TypeChunk([new Entry(0, 3, 0)]);
        int header = BinaryPrimitives.ReadUInt16LittleEndian(type.AsSpan(2));
        int entriesStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(type.AsSpan(16));
        switch (mutation)
        {
            case "config-size": Put32(type, 20, uint.MaxValue); break;
            case "entry-count": Put32(type, 12, uint.MaxValue); break;
            case "entries-start": Put32(type, 16, (uint)(header - 4)); break;
            case "dense-offset": Put32(type, header, 0xFFFFFFFC); break;
            case "unaligned-offset": Put32(type, header, 1); break;
            case "entry-size": Put16(type, entriesStart, ushort.MaxValue); break;
            case "value-size": Put16(type, entriesStart + 8, 7); break;
            case "value-reserved": type[entriesStart + 10] = 1; break;
            case "string-index": Put32(type, entriesStart + 12, uint.MaxValue); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Null(ArscReader.ResolveString(Table(StringPool(["value"]), [type]), ResourceId));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("start")]
    [InlineData("offset")]
    [InlineData("style-count")]
    [InlineData("style-start")]
    [InlineData("character-count")]
    [InlineData("byte-count")]
    public void MalformedStringPoolStructuresReturnNull(string mutation)
    {
        byte[] pool = StringPool(["value"]);
        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(20));
        switch (mutation)
        {
            case "count": Put32(pool, 8, uint.MaxValue); break;
            case "start": Put32(pool, 20, 28); break;
            case "offset": Put32(pool, 28, uint.MaxValue); break;
            case "style-count": Put32(pool, 12, uint.MaxValue); break;
            case "style-start": Put32(pool, 24, uint.MaxValue); break;
            case "character-count": pool[start] = 6; break;
            case "byte-count": pool[start + 1] = 127; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Null(ArscReader.ResolveString(Table(pool, [TypeChunk([new Entry(0, 3, 0)])]), ResourceId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingStringTerminatorReturnsNull(bool utf8)
    {
        byte[] pool = StringPool(["A"], utf8);
        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(20));
        pool[start + (utf8 ? 3 : 4)] = 1;

        Assert.Null(ArscReader.ResolveString(Table(pool, [TypeChunk([new Entry(0, 3, 0)])]), ResourceId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidEncodedStringsReturnNullInsteadOfDecoderExceptions(bool utf8)
    {
        byte[] pool = StringPool(["A"], utf8);
        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(20));
        if (utf8)
        {
            pool[start + 2] = 0xFF;
        }
        else
        {
            Put16(pool, start + 2, 0xD800);
        }

        Assert.Null(ArscReader.ResolveString(Table(pool, [TypeChunk([new Entry(0, 3, 0)])]), ResourceId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OutOfRangeScaledOffsetsReturnNull(byte flags)
    {
        byte[] type = TypeChunk([new Entry(0, 3, 0)], flags);
        int header = BinaryPrimitives.ReadUInt16LittleEndian(type.AsSpan(2));
        Put16(type, header + (flags == 1 ? 2 : 0), 0xFFFE);

        Assert.Null(ArscReader.ResolveString(Table(StringPool(["value"]), [type]), ResourceId));
    }

    [Fact]
    public void DuplicateAndUnsortedSparseIndicesReturnNull()
    {
        foreach (ushort secondIndex in new ushort[] { 1, 0 })
        {
            byte[] type = TypeChunk([new Entry(1, 3, 0), new Entry(2, 3, 0)], 1);
            int header = BinaryPrimitives.ReadUInt16LittleEndian(type.AsSpan(2));
            Put16(type, header + 4, secondIndex);

            Assert.Null(ArscReader.ResolveString(Table(StringPool(["value"]), [type]), ResourceId + 1));
        }
    }

    [Fact]
    public void IncorrectPackageCountAndTrailingBytesReturnNull()
    {
        byte[] table = Table(StringPool(["value"]), [TypeChunk([new Entry(0, 3, 0)])]);
        Assert.Null(ArscReader.ResolveString(table.Concat(new byte[] { 0 }).ToArray(), ResourceId));
        Put32(table, 8, 2);
        Assert.Null(ArscReader.ResolveString(table, ResourceId));
    }

    [Fact]
    public void ResolvesReadOnlyMemorySliceWithoutReadingSurroundingBytes()
    {
        byte[] table = Table(StringPool(["slice"]), [TypeChunk([new Entry(0, 3, 0)])]);
        byte[] envelope = new byte[table.Length + 16];
        table.CopyTo(envelope, 8);

        Assert.Equal("slice", ArscReader.ResolveString(envelope.AsMemory(8, table.Length), ResourceId));
    }

    [Fact]
    public void CancellationIsPropagatedForValidAndMalformedInputs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] table = Table(StringPool(["value"]), [TypeChunk([new Entry(0, 3, 0)])]);

        OperationCanceledException exception = Assert.ThrowsAny<OperationCanceledException>(() =>
            ArscReader.ResolveString(table, ResourceId, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ArscReader.ResolveString(ReadOnlyMemory<byte>.Empty, ResourceId, cancellation.Token));
    }

    private static byte[] ReferenceChain(int length)
    {
        Entry[] entries = Enumerable.Range(0, length)
            .Select(index => index == length - 1
                ? new Entry((ushort)index, 3, 0)
                : new Entry((ushort)index, 1, ResourceId + (uint)index + 1))
            .ToArray();
        return Table(StringPool(["target"]), [TypeChunk(entries)]);
    }

    private static byte[] Table(byte[] values, byte[][] types, int packageHeader = 288, uint typeIdOffset = 0)
    {
        byte[] typeNames = StringPool(["string"], false);
        byte[] keyNames = StringPool(["value"], false);
        int entryCount = types.Max(type =>
        {
            int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(type.AsSpan(12)), 65536u);
            int header = BinaryPrimitives.ReadUInt16LittleEndian(type.AsSpan(2));
            return type[9] == 1
                ? BinaryPrimitives.ReadUInt16LittleEndian(type.AsSpan(header + (count - 1) * 4)) + 1
                : Math.Min(count, 65536);
        });
        byte[] specification = Chunk(0x0202, 16, 16 + entryCount * 4);
        specification[8] = 1;
        Put32(specification, 12, (uint)entryCount);
        byte[] package = Chunk(0x0200, packageHeader,
            packageHeader + typeNames.Length + keyNames.Length + specification.Length + types.Sum(type => type.Length));
        Put32(package, 8, 0x7F);
        Encoding.Unicode.GetBytes("com.example.fixture").CopyTo(package, 12);
        Put32(package, 268, (uint)packageHeader);
        Put32(package, 272, 1);
        Put32(package, 276, (uint)(packageHeader + typeNames.Length));
        Put32(package, 280, 1);
        if (packageHeader >= 288)
        {
            Put32(package, 284, typeIdOffset);
        }
        int cursor = packageHeader;
        foreach (byte[] chunk in new[] { typeNames, keyNames, specification }.Concat(types))
        {
            chunk.CopyTo(package, cursor);
            cursor += chunk.Length;
        }
        byte[] table = Chunk(2, 12, 12 + values.Length + package.Length);
        Put32(table, 8, 1);
        values.CopyTo(table, 12);
        package.CopyTo(table, 12 + values.Length);
        return table;
    }

    private static byte[] TypeChunk(Entry[] entries, byte flags = 0, bool localized = false, int configSize = 64)
    {
        int header = 20 + configSize;
        int count = flags == 1 ? entries.Length : entries.Max(entry => entry.Index) + 1;
        int indexWidth = flags == 2 ? 2 : 4;
        int entriesStart = Align4(header + count * indexWidth);
        byte[] chunk = Chunk(0x0201, header, entriesStart + entries.Sum(entry => entry.Compact ? 8 : 16));
        chunk[8] = 1;
        chunk[9] = flags;
        Put32(chunk, 12, (uint)count);
        Put32(chunk, 16, (uint)entriesStart);
        Put32(chunk, 20, (uint)configSize);
        if (localized)
        {
            chunk[28] = (byte)'z';
            chunk[29] = (byte)'h';
        }
        if (flags != 1)
        {
            chunk.AsSpan(header, count * indexWidth).Fill(0xFF);
        }
        int cursor = entriesStart;
        for (int i = 0; i < entries.Length; i++)
        {
            Entry entry = entries[i];
            int offset = cursor - entriesStart;
            if (flags == 1)
            {
                Put16(chunk, header + i * 4, entry.Index);
                Put16(chunk, header + i * 4 + 2, (ushort)(offset / 4));
            }
            else if (flags == 2)
            {
                Put16(chunk, header + entry.Index * 2, (ushort)(offset / 4));
            }
            else
            {
                Put32(chunk, header + entry.Index * 4, (uint)offset);
            }
            if (entry.Compact)
            {
                Put16(chunk, cursor, 0);
                Put16(chunk, cursor + 2, (ushort)(8 | entry.DataType << 8));
                Put32(chunk, cursor + 4, entry.Data);
                cursor += 8;
            }
            else
            {
                Put16(chunk, cursor, 8);
                Put32(chunk, cursor + 4, 0);
                Put16(chunk, cursor + 8, 8);
                chunk[cursor + 11] = entry.DataType;
                Put32(chunk, cursor + 12, entry.Data);
                cursor += 16;
            }
        }
        return chunk;
    }

    private static byte[] StringPool(string[] strings, bool utf8 = true)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload, Encoding.UTF8, true);
        var offsets = new List<uint>();
        foreach (string value in strings)
        {
            offsets.Add((uint)payload.Position);
            if (utf8)
            {
                byte[] encoded = Encoding.UTF8.GetBytes(value);
                WriteLength8(writer, value.Length);
                WriteLength8(writer, encoded.Length);
                writer.Write(encoded);
                writer.Write((byte)0);
            }
            else
            {
                if (value.Length > 0x7FFF)
                {
                    writer.Write((ushort)(0x8000 | value.Length >> 16));
                }
                writer.Write((ushort)value.Length);
                writer.Write(Encoding.Unicode.GetBytes(value));
                writer.Write((ushort)0);
            }
        }
        int start = 28 + strings.Length * 4;
        byte[] chunk = Chunk(1, 28, Align4(start + (int)payload.Length));
        Put32(chunk, 8, (uint)strings.Length);
        Put32(chunk, 16, utf8 ? 0x100u : 0u);
        Put32(chunk, 20, (uint)start);
        for (int i = 0; i < offsets.Count; i++)
        {
            Put32(chunk, 28 + i * 4, offsets[i]);
        }
        payload.ToArray().CopyTo(chunk, start);
        return chunk;
    }

    private static void WriteLength8(BinaryWriter writer, int length)
    {
        if (length > 0x7F)
        {
            writer.Write((byte)(0x80 | length >> 8));
        }
        writer.Write((byte)length);
    }

    private static byte[] Chunk(ushort kind, int header, int size)
    {
        byte[] chunk = new byte[size];
        Put16(chunk, 0, kind);
        Put16(chunk, 2, (ushort)header);
        Put32(chunk, 4, (uint)size);
        return chunk;
    }

    private static int Align4(int size) => (size + 3) & ~3;

    private static void Put16(byte[] data, int at, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);

    private static void Put32(byte[] data, int at, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);

    private readonly record struct Entry(ushort Index, byte DataType, uint Data, bool Compact = false);
}
