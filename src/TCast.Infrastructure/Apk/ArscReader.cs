using System.Buffers.Binary;
using System.Text;

namespace TCast.Infrastructure.Apk;

internal static class ArscReader
{
    private const int MaxValueCharacters = 32768;

    internal static string? ResolveString(ReadOnlyMemory<byte> table, uint resourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ReadOnlySpan<byte> data = table.Span;
            ReadOnlySpan<byte> root = Chunk(data, 0);
            if (root.Length != data.Length || U16(root, 0) != 2 || U16(root, 2) < 12)
            {
                return null;
            }

            var visited = new HashSet<uint>();
            int budget = 100_000;
            for (int depth = 0; depth < 32 && resourceId != 0 && visited.Add(resourceId); depth++)
            {
                ReadOnlySpan<byte> strings = default;
                (byte Type, uint Data)? value = null;
                uint packages = 0;
                for (int at = U16(root, 2); at < root.Length;)
                {
                    CheckWork(ref budget, cancellationToken);
                    ReadOnlySpan<byte> chunk = Chunk(root, at);
                    switch (U16(chunk, 0))
                    {
                        case 1:
                            if (!strings.IsEmpty)
                            {
                                return null;
                            }
                            strings = chunk;
                            break;
                        case 0x0200:
                            packages++;
                            if (U16(chunk, 2) < 284)
                            {
                                return null;
                            }
                            if (U32(chunk, 8) == resourceId >> 24)
                            {
                                var candidate = FindValue(chunk, resourceId, ref budget, cancellationToken);
                                if (candidate is not null)
                                {
                                    if (value is not null)
                                    {
                                        return null;
                                    }
                                    value = candidate;
                                }
                            }
                            break;
                    }
                    at += chunk.Length;
                }

                if (packages != U32(root, 8) || value is null)
                {
                    return null;
                }
                if (value.Value.Type == 3)
                {
                    return strings.IsEmpty ? null : ReadString(strings, value.Value.Data);
                }
                if (value.Value.Type != 1)
                {
                    return null;
                }
                resourceId = value.Value.Data;
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or
                                   OverflowException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static (byte Type, uint Data)? FindValue(ReadOnlySpan<byte> package, uint resourceId,
        ref int budget, CancellationToken cancellationToken)
    {
        int header = U16(package, 2);
        uint typeOffset = header >= 288 ? U32(package, 284) : 0;
        (byte Type, uint Data)? result = null;
        for (int at = header; at < package.Length;)
        {
            CheckWork(ref budget, cancellationToken);
            ReadOnlySpan<byte> chunk = Chunk(package, at);
            if (U16(chunk, 0) == 0x0201)
            {
                if (U16(chunk, 2) < 24)
                {
                    throw new InvalidDataException();
                }
                int configSize = checked((int)U32(chunk, 20));
                if (configSize < 4 || configSize > U16(chunk, 2) - 20 || chunk[8] == 0)
                {
                    throw new InvalidDataException();
                }
                if ((ulong)chunk[8] + typeOffset == ((resourceId >> 16) & 255) &&
                    chunk.Slice(24, configSize - 4).IndexOfAnyExcept((byte)0) < 0)
                {
                    var candidate = ReadValue(chunk, (int)(resourceId & 0xFFFF), ref budget, cancellationToken);
                    if (candidate is not null)
                    {
                        if (result is not null)
                        {
                            throw new InvalidDataException();
                        }
                        result = candidate;
                    }
                }
            }
            at += chunk.Length;
        }
        return result;
    }

    private static (byte Type, uint Data)? ReadValue(ReadOnlySpan<byte> chunk, int entry,
        ref int budget, CancellationToken cancellationToken)
    {
        int header = U16(chunk, 2);
        int count = checked((int)U32(chunk, 12));
        int entriesStart = checked((int)U32(chunk, 16));
        byte flags = chunk[9];
        if (count > 65536 || flags > 2 || entriesStart < header || entriesStart > chunk.Length ||
            (long)header + count * (flags == 2 ? 2L : 4L) > entriesStart)
        {
            throw new InvalidDataException();
        }
        uint offset = uint.MaxValue;
        if (flags == 1)
        {
            int previous = -1;
            for (int i = 0; i < count; i++)
            {
                CheckWork(ref budget, cancellationToken);
                int index = U16(chunk, header + i * 4);
                if (index <= previous)
                {
                    throw new InvalidDataException();
                }
                previous = index;
                if (index == entry)
                {
                    offset = (uint)U16(chunk, header + i * 4 + 2) * 4;
                }
            }
        }
        else if (entry < count)
        {
            if (flags == 2)
            {
                ushort encoded = U16(chunk, header + entry * 2);
                offset = encoded == ushort.MaxValue ? uint.MaxValue : (uint)encoded * 4;
            }
            else
            {
                offset = U32(chunk, header + entry * 4);
            }
        }
        if (offset == uint.MaxValue)
        {
            return null;
        }
        long position = entriesStart + (long)offset;
        if (offset % 4 != 0 || position > chunk.Length - 8)
        {
            throw new InvalidDataException();
        }
        int at = (int)position;
        int entryFlags = U16(chunk, at + 2);
        if ((entryFlags & 1) != 0)
        {
            return null;
        }
        if ((entryFlags & 8) != 0)
        {
            return ((byte)(entryFlags >> 8), U32(chunk, at + 4));
        }
        int size = U16(chunk, at);
        if (size < 8 || size > chunk.Length - at - 8)
        {
            throw new InvalidDataException();
        }
        at += size;
        if (U16(chunk, at) != 8 || chunk[at + 2] != 0)
        {
            throw new InvalidDataException();
        }
        return (chunk[at + 3], U32(chunk, at + 4));
    }

    private static string? ReadString(ReadOnlySpan<byte> pool, uint index)
    {
        int header = U16(pool, 2);
        if (header < 28)
        {
            return null;
        }
        uint count = U32(pool, 8);
        uint styles = U32(pool, 12);
        uint start = U32(pool, 20);
        uint styleStart = U32(pool, 24);
        if (index >= count || start < header + 4L * (count + (long)styles) || start > pool.Length ||
            (styleStart != 0 && (styleStart < start || styleStart > pool.Length)))
        {
            return null;
        }
        int end = styleStart == 0 ? pool.Length : (int)styleStart;
        long position = start + (long)U32(pool, checked(header + (int)index * 4));
        if (position >= end)
        {
            return null;
        }
        int at = (int)position;
        pool = pool[..end];
        if ((U32(pool, 16) & 0x100) != 0)
        {
            int characters = Length8(pool, ref at);
            int bytes = Length8(pool, ref at);
            if (characters > MaxValueCharacters || bytes >= pool.Length - at || pool[at + bytes] != 0)
            {
                return null;
            }
            string value = new UTF8Encoding(false, true).GetString(pool.Slice(at, bytes));
            return value.Length == characters ? value : null;
        }
        int length = U16(pool, at);
        at += 2;
        if ((length & 0x8000) != 0)
        {
            length = ((length & 0x7FFF) << 16) | U16(pool, at);
            at += 2;
        }
        if (length > MaxValueCharacters || (long)length * 2 + 2 > pool.Length - at || U16(pool, at + length * 2) != 0)
        {
            return null;
        }
        return new UnicodeEncoding(false, false, true).GetString(pool.Slice(at, length * 2));
    }

    private static int Length8(ReadOnlySpan<byte> data, ref int at)
    {
        if (at >= data.Length)
        {
            throw new InvalidDataException();
        }
        int length = data[at++];
        if ((length & 0x80) == 0)
        {
            return length;
        }
        if (at >= data.Length)
        {
            throw new InvalidDataException();
        }
        return ((length & 0x7F) << 8) | data[at++];
    }

    private static ReadOnlySpan<byte> Chunk(ReadOnlySpan<byte> data, int at)
    {
        if (at < 0 || at > data.Length - 8)
        {
            throw new InvalidDataException();
        }
        int header = U16(data, at + 2);
        uint size = U32(data, at + 4);
        if (header < 8 || size < header || size > data.Length - at)
        {
            throw new InvalidDataException();
        }
        return data.Slice(at, (int)size);
    }

    private static void CheckWork(ref int budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (--budget < 0)
        {
            throw new InvalidDataException();
        }
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
}
