using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using TCast.Infrastructure.Apk;
using Xunit;

namespace TCast.Infrastructure.Tests;

public sealed class ApkInputSafetyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("TCast-apk-tests-").FullName;

    [Fact]
    public async Task SnapshotIsIndependentLockedAndCleanedIdempotently()
    {
        string original = Path.Combine(_directory, "中文 应用.APK");
        await File.WriteAllBytesAsync(original, [1, 2, 3]);
        var factory = new ApkSnapshotFactory(Path.Combine(_directory, "work"));
        ApkSnapshot snapshot = await factory.CreateAsync(original);
        Assert.Equal(original, snapshot.OriginalPath);
        Assert.Equal(3, snapshot.Bytes);
        Assert.NotEqual(original, snapshot.SnapshotPath);
        Assert.True(File.GetAttributes(snapshot.SnapshotPath).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(snapshot.SnapshotPath));
        await File.WriteAllBytesAsync(original, [4]);
        Assert.Equal(3, new FileInfo(snapshot.SnapshotPath).Length);
        File.SetAttributes(snapshot.SnapshotPath, FileAttributes.Normal);
        Assert.Throws<IOException>(() => File.Open(snapshot.SnapshotPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.Throws<IOException>(() => File.Delete(snapshot.SnapshotPath));
        await snapshot.DisposeAsync();
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot.SnapshotPath)));
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.SnapshotPath)!);
        await File.WriteAllTextAsync(snapshot.SnapshotPath, "replacement");
        await snapshot.DisposeAsync();
        Assert.Equal("replacement", await File.ReadAllTextAsync(snapshot.SnapshotPath));
        Assert.True(File.Exists(original));
    }

    [Theory]
    [InlineData("empty.apk", 0, 10)]
    [InlineData("large.apk", 11, 10)]
    [InlineData("wrong.zip", 1, 10)]
    public async Task SnapshotRejectsInvalidInput(string name, int bytes, long maximum)
    {
        string path = Path.Combine(_directory, name);
        await File.WriteAllBytesAsync(path, new byte[bytes]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkSnapshotFactory(Path.Combine(_directory, "work"), maximum).CreateAsync(path));
        Assert.False(Directory.Exists(Path.Combine(_directory, "work")));
    }

    [Fact]
    public async Task SnapshotRejectsDirectoryMissingFileAndCancellation()
    {
        string path = Path.Combine(_directory, "directory.apk");
        Directory.CreateDirectory(path);
        var factory = new ApkSnapshotFactory(Path.Combine(_directory, "work"));
        await Assert.ThrowsAsync<InvalidDataException>(() => factory.CreateAsync(path));
        await Assert.ThrowsAsync<FileNotFoundException>(() => factory.CreateAsync(Path.Combine(_directory, "missing.apk")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CreateAsync(path, new CancellationToken(true)));
    }

    [Fact]
    public async Task CancellationDuringCopyRemovesPartialSnapshot()
    {
        string source = Path.Combine(_directory, "large.apk");
        using (var file = File.Create(source))
        {
            file.SetLength(128 * 1024 * 1024);
        }

        string work = Path.Combine(_directory, "work");
        Directory.CreateDirectory(work);
        using var cts = new CancellationTokenSource();
        Task<ApkSnapshot> copying = new ApkSnapshotFactory(work).CreateAsync(source, cts.Token);
        cts.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying);
        }
        finally
        {
            if (copying.IsCompletedSuccessfully)
            {
                await (await copying).DisposeAsync();
            }
        }

        Assert.Empty(Directory.GetFileSystemEntries(work));
    }

    [Fact]
    public async Task SnapshotCleanupRetriesAfterConsumerReleasesReadLock()
    {
        string path = Path.Combine(_directory, "source.apk");
        await File.WriteAllBytesAsync(path, [1]);
        var snapshot = await new ApkSnapshotFactory(Path.Combine(_directory, "work")).CreateAsync(path);
        using (var reader = File.Open(snapshot.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await snapshot.DisposeAsync();
            Assert.True(File.Exists(snapshot.SnapshotPath));
        }

        await snapshot.DisposeAsync();
        Assert.False(File.Exists(snapshot.SnapshotPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainerAcceptsBinaryManifestAndChecksAllEntryData(bool utf8)
    {
        string path = CreateApk(BinaryManifest(utf8: utf8), ("classes.dex", new byte[] { 1, 2, 3 }));
        await new ApkContainerValidator().ValidateStandaloneAsync(path);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("/absolute")]
    [InlineData("a\\evil")]
    [InlineData("C:/evil")]
    [InlineData("a/./evil")]
    [InlineData("a//evil")]
    [InlineData("AndroidManifest.xml")]
    public async Task ContainerRejectsUnsafeOrDuplicatePaths(string name)
    {
        string path = CreateApk(BinaryManifest(), (name, new byte[] { 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(path));
    }

    [Theory]
    [InlineData("split", "config.en", 3)]
    [InlineData("configForSplit", "base", 3)]
    [InlineData("isFeatureSplit", null, 18)]
    [InlineData("isSplitRequired", null, 18)]
    [InlineData("requiredSplitTypes", "config.abi", 3)]
    [InlineData("splitTypes", "config.abi", 3)]
    [InlineData("isSplitRequired", null, 1)]
    public async Task ContainerRejectsBinarySplitAttributesOnManifest(string name, string? text, byte type)
    {
        string path = CreateApk(BinaryManifest(name, text, type, 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(path));
    }

    /// <summary>同一属性若只出现在非 manifest/application 元素上，不构成 split 声明。</summary>
    [Theory]
    [InlineData("split", "config.en", 3)]
    [InlineData("isSplitRequired", null, 18)]
    public async Task ContainerIgnoresSplitAttributesOnUnrelatedElements(string name, string? text, byte type)
    {
        string path = CreateApk(BinaryManifest(name, text, type, 1, child: "activity"));
        await new ApkContainerValidator().ValidateStandaloneAsync(path);
    }

    [Fact]
    public async Task ContainerRejectsSplitDependenciesAndResourceIdAliases()
    {
        foreach (byte[] manifest in new[]
        {
            BinaryManifest(child: "uses-split"),
            BinaryManifest("requiredSplitTypes", "config.abi", child: "application"),
            BinaryManifest("alias", null, 18, 1, resourceId: 0x01010591),
            BinaryManifest("alias", "config.abi", 3, resourceId: 0x0101064E),
            BinaryManifest("name", "com.android.vending.splits.required", child: "meta-data"),
        })
        {
            string path = CreateApk(manifest);
            await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(path));
        }
    }

    [Fact]
    public async Task ContainerAllowsExplicitFalseSplitRequirement()
    {
        string path = CreateApk(BinaryManifest("isSplitRequired", null, 18, 0));
        await new ApkContainerValidator().ValidateStandaloneAsync(path);
    }

    [Fact]
    public async Task ContainerRejectsEmptyMissingTextAndTruncatedManifest()
    {
        foreach (byte[] manifest in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("<manifest package='com.example.app'/>"), BinaryManifest()[..^1] })
        {
            string path = CreateApk(manifest);
            await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(path));
        }

        string missing = Path.Combine(_directory, "missing-manifest.apk");
        using (var archive = ZipFile.Open(missing, ZipArchiveMode.Create))
        {
            archive.CreateEntry("classes.dex");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(missing));
        string empty = Path.Combine(_directory, "empty.apk");
        await File.WriteAllBytesAsync(empty, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(empty));
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("crc")]
    [InlineData("large")]
    [InlineData("local-name")]
    [InlineData("truncated")]
    public async Task ContainerRejectsCorruptZip(string mutation)
    {
        string path = CreateApk(BinaryManifest());
        byte[] bytes = await File.ReadAllBytesAsync(path);
        int central = FindSignature(bytes, 0x02014B50);
        switch (mutation)
        {
            case "encrypted":
                bytes[central + 8] |= 1;
                bytes[6] |= 1;
                break;
            case "crc":
                bytes[14] ^= 1;
                bytes[central + 16] ^= 1;
                break;
            case "large":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), 600 * 1024 * 1024);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), 600 * 1024 * 1024);
                break;
            case "local-name":
                bytes[30] ^= 1;
                break;
            case "truncated":
                bytes = bytes[..^8];
                break;
        }

        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(path));
    }

    [Fact]
    public async Task ContainerRejectsExpansionBombAndOversizedManifest()
    {
        string bomb = Path.Combine(_directory, "bomb.apk");
        using (var archive = ZipFile.Open(bomb, ZipArchiveMode.Create))
        {
            using (Stream manifest = archive.CreateEntry("AndroidManifest.xml").Open())
            {
                manifest.Write(BinaryManifest());
            }

            using Stream resource = archive.CreateEntry("resources.arsc", CompressionLevel.SmallestSize).Open();
            resource.Write(new byte[4 * 1024 * 1024]);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(bomb));
        string hugeManifest = CreateApk(new byte[4 * 1024 * 1024 + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ApkContainerValidator().ValidateStandaloneAsync(hugeManifest));
    }

    [Fact]
    public async Task UnsupportedFormatsFailExplicitly()
    {
        string path = CreateApk(BinaryManifest());
        byte[] bytes = await File.ReadAllBytesAsync(path);
        int end = FindSignature(bytes, 0x06054B50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), uint.MaxValue);
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<NotSupportedException>(() => new ApkContainerValidator().ValidateStandaloneAsync(path));
        string unresolved = CreateApk(BinaryManifest("name", null, 1, 0x7F010001, child: "meta-data"));
        await Assert.ThrowsAsync<NotSupportedException>(() => new ApkContainerValidator().ValidateStandaloneAsync(unresolved));
    }

    [Fact]
    public async Task ContainerHonorsCancellation()
    {
        string path = CreateApk(BinaryManifest());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApkContainerValidator().ValidateStandaloneAsync(path, new CancellationToken(true)));
    }

    [Fact]
    public async Task ReaderBindsIdentityAndReadsManifestFields()
    {
        string path = CreateApk(BinaryManifest(versionName: "1.2.3", versionCode: 123));
        var validator = new InspectingValidator(() =>
            Assert.True(File.Exists(path)));
        var reader = new AxmlApkMetadataReader(validator);
        var metadata = await reader.ReadAsync(path);
        Assert.NotNull(metadata.FileIdentity);
        Assert.Equal(path, metadata.FileIdentity.SnapshotPath);
        Assert.Equal(new FileInfo(path).Length, metadata.FileIdentity.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path), metadata.FileIdentity.LastWriteTimeUtc.UtcDateTime);
        Assert.Equal("com.example.app", metadata.PackageName);
        Assert.Equal("1.2.3", metadata.VersionName);
    }

    [Fact]
    public async Task ReaderResolvesResourceLabelIncludingChinese()
    {
        string path = CreateApk(BinaryManifest("label", null, 0x01, 0x7F010001, "application"),
            ("resources.arsc", BuildResources((3u << 24) | 0x7F010001, "中文应用")));
        var metadata = await new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(path);
        Assert.Equal("中文应用", metadata.DisplayName);
        Assert.False(metadata.NameUsesPackage);
    }

    [Fact]
    public async Task ReaderFallsBackToPackageNameWhenLabelUnusable()
    {
        foreach (string label in new[] { "", "a>>b", "a\0b", new string('中', 400) })
        {
            string path = CreateApk(BinaryManifest("label", label, 3, 0, "application"));
            var metadata = await new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(path);
            Assert.Equal("com.example.app", metadata.DisplayName);
            Assert.True(metadata.NameUsesPackage);
        }

        // 资源引用在资源表里找不到时也回退包名。
        string missing = CreateApk(BinaryManifest("label", null, 0x01, 0x7F010001, "application"));
        var fallback = await new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(missing);
        Assert.Equal("com.example.app", fallback.DisplayName);
        Assert.True(fallback.NameUsesPackage);
    }

    [Fact]
    public async Task ReaderRejectsInvalidPackageNameAndMissingManifest()
    {
        string invalid = CreateApk(BinaryManifest());
        byte[] bytes = await File.ReadAllBytesAsync(invalid);
        int at = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("com.example.app"));
        Assert.True(at > 0);
        Encoding.UTF8.GetBytes("com..example").CopyTo(bytes, at);
        await File.WriteAllBytesAsync(invalid, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(invalid));

        string noManifest = Path.Combine(_directory, $"no-manifest-{Guid.NewGuid():N}.apk");
        using (ZipFile.Open(noManifest, ZipArchiveMode.Create))
        {
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(noManifest));
    }

    [Fact]
    public async Task ReaderHonorsCancellation()
    {
        string path = CreateApk(BinaryManifest());
        var reader = new AxmlApkMetadataReader(new ApkContainerValidator());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(path, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public async Task OptionalMalformedResourcesFallBackWithoutRejectingManifest(int length)
    {
        string path = CreateApk(BinaryManifest("label", null, 1, 0x7F010001, "application"),
            ("resources.arsc", new byte[length]));
        var result = await new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(path);
        Assert.True(result.NameUsesPackage);
        Assert.Equal("com.example.app", result.DisplayName);
    }

    [Fact]
    public async Task ResourceVersionNameIsResolved()
    {
        string path = CreateApk(BinaryManifest("versionName", null, 1, 0x7F010001),
            ("resources.arsc", BuildResources(0x7F010001, "2.3 中文")));
        var result = await new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(path);
        Assert.Equal("2.3 中文", result.VersionName);
    }

    [Fact]
    public void HexVersionAndMajorAreCombined()
    {
        var result = AxmlManifest.Read(BinaryManifest("versionCode", null, 0x11, 123));
        Assert.Equal("123", result.VersionCode);
        result = AxmlManifest.Read(BinaryManifest("alias", null, 0x10, 2,
            resourceId: 0x01010576, versionCode: 123));
        Assert.Equal("8589934715", result.VersionCode);
    }

    [Fact]
    public void ForeignNamespaceAndNestedApplicationCannotOverrideMetadata()
    {
        Assert.Empty(AxmlManifest.Read(BinaryManifest("label", "wrong", child: "application",
            attributeNamespace: "urn:other", resourceId: 0x01010001)).Label);
        Assert.Empty(AxmlManifest.Read(BinaryManifest("label", "wrong", child: "application", nested: true)).Label);
        Assert.Equal("right", AxmlManifest.Read(BinaryManifest("alias", "right", child: "application",
            resourceId: 0x01010001)).Label);
    }

    [Fact]
    public async Task ReaderTimeoutReleasesFileAndParseSlot()
    {
        string path = CreateApk(BinaryManifest());
        var blocking = new BlockingValidator();
        var reader = new AxmlApkMetadataReader(blocking, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(path));
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var result = await new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(path);
        Assert.Equal("com.example.app", result.PackageName);
    }

    [Fact]
    public async Task ReaderCancellationDuringValidationReleasesBothSlots()
    {
        string path = CreateApk(BinaryManifest());
        var blocking = new BlockingValidator();
        var reader = new AxmlApkMetadataReader(blocking);
        using var cancellation = new CancellationTokenSource();
        Task first = reader.ReadAsync(path, cancellation.Token);
        Task second = reader.ReadAsync(path, cancellation.Token);
        try
        {
            await blocking.TwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal("com.example.app", (await new AxmlApkMetadataReader(new ApkContainerValidator()).ReadAsync(path)).PackageName);
    }

    private string CreateApk(byte[] manifest, params (string Name, byte[] Data)[] extra)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + " 中文.apk");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in new[] { (Name: "AndroidManifest.xml", Data: manifest) }.Concat(extra))
        {
            using Stream output = archive.CreateEntry(entry.Name, CompressionLevel.NoCompression).Open();
            output.Write(entry.Data);
        }

        return path;
    }

    private static byte[] BinaryManifest(string? attribute = null, string? text = null, byte type = 3, uint value = 0,
        string? child = null, uint resourceId = 0, bool utf8 = true, uint versionCode = 0, string? versionName = null,
        string? attributeNamespace = ApkContainerValidator.AndroidNamespace, bool nested = false)
    {
        string childElement = child ?? "application";
        string[] strings = ["manifest", "package", "com.example.app", childElement, attribute ?? "unused", text ?? "",
            "application", "http://schemas.android.com/apk/res/android", attributeNamespace ?? "", "versionName", "versionCode", versionName ?? "", "activity"];
        bool rootAttribute = attribute is not null && child is null;
        uint testNamespace = attribute is "split" or "configForSplit" || attributeNamespace is null ? uint.MaxValue : 8u;
        uint attributeNameIndex = Array.IndexOf(strings, attribute ?? "unused") is var found and >= 0 ? (uint)found : 4u;
        uint childIndex = Array.IndexOf(strings, childElement) is var childFound and >= 0 ? (uint)childFound : 3u;
        var rootAttributes = new List<(uint Name, uint Namespace, byte Type, uint Data)>
        {
            (1, uint.MaxValue, 3, 2),
        };
        if (versionName is not null)
        {
            rootAttributes.Add((9, 7, 3, 11));
        }

        if (versionCode != 0)
        {
            rootAttributes.Add((10, 7, 0x10, versionCode));
        }

        if (rootAttribute)
        {
            rootAttributes.Add((attributeNameIndex, testNamespace, type, type == 3 ? 5u : value));
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)3);
        writer.Write((ushort)8);
        writer.Write(0u);
        WriteStringPool(writer, strings, utf8);
        writer.Write((ushort)0x0180);
        writer.Write((ushort)8);
        writer.Write((uint)(8 + strings.Length * 4));
        for (uint i = 0; i < strings.Length; i++)
        {
            writer.Write(i switch
            {
                4 => resourceId,
                9 => 0x0101021Cu,
                10 => 0x0101021Bu,
                _ => 0u
            });
        }
        writer.Write((ushort)0x0102);
        writer.Write((ushort)16);
        writer.Write((uint)(36 + rootAttributes.Count * 20));
        writer.Write(1u);
        writer.Write(uint.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write(0u);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)rootAttributes.Count);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        foreach ((uint name, uint ns, byte valueType, uint data) in rootAttributes)
        {
            WriteAttribute(name, ns, valueType, data);
        }

        if (child is not null)
        {
            if (child == "meta-data")
            {
                WriteElement(6, uint.MaxValue, true, false);
            }
            if (nested)
            {
                WriteElement(12, uint.MaxValue, true, false);
            }
            WriteElement(childIndex, uint.MaxValue, true, attribute is not null);
            WriteElement(childIndex, uint.MaxValue, false, false);
            if (nested)
            {
                WriteElement(12, uint.MaxValue, false, false);
            }
            if (child == "meta-data")
            {
                WriteElement(6, uint.MaxValue, false, false);
            }
        }

        WriteElement(0, uint.MaxValue, false, false);
        writer.Flush();
        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        return result;

        void WriteElement(uint name, uint ns, bool start, bool includeAttribute)
        {
            int count = includeAttribute ? 1 : 0;
            writer.Write((ushort)(start ? 0x0102 : 0x0103));
            writer.Write((ushort)16);
            writer.Write((uint)(start ? 36 + count * 20 : 24));
            writer.Write(1u);
            writer.Write(uint.MaxValue);
            writer.Write(ns);
            writer.Write(name);
            if (!start)
            {
                return;
            }

            writer.Write((ushort)20);
            writer.Write((ushort)20);
            writer.Write((ushort)count);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            if (includeAttribute)
            {
                WriteAttribute(attributeNameIndex, testNamespace, type, type == 3 ? 5u : value);
            }
        }

        void WriteAttribute(uint name, uint ns, byte valueType, uint data)
        {
            writer.Write(ns);
            writer.Write(name);
            writer.Write(uint.MaxValue);
            writer.Write((ushort)8);
            writer.Write((byte)0);
            writer.Write(valueType);
            writer.Write(data);
        }
    }

    private static void WriteStringPool(BinaryWriter writer, string[] strings, bool utf8)
    {
        using var data = new MemoryStream();
        var offsets = new uint[strings.Length];
        for (int i = 0; i < strings.Length; i++)
        {
            offsets[i] = (uint)data.Position;
            if (utf8)
            {
                byte[] encoded = Encoding.UTF8.GetBytes(strings[i]);
                WriteUtf8Length(data, strings[i].Length);
                WriteUtf8Length(data, encoded.Length);
                data.Write(encoded);
                data.WriteByte(0);
            }
            else
            {
                byte[] encoded = Encoding.Unicode.GetBytes(strings[i]);
                int length = strings[i].Length;
                if (length > 0x7FFF)
                {
                    WriteUInt16(data, (ushort)(0x8000 | (length >> 16)));
                    WriteUInt16(data, (ushort)length);
                }
                else
                {
                    WriteUInt16(data, (ushort)length);
                }
                data.Write(encoded);
                WriteUInt16(data, 0);
            }
        }

        while (data.Length % 4 != 0)
        {
            data.WriteByte(0);
        }

        int header = 28;
        int stringsStart = header + strings.Length * 4;
        writer.Write((ushort)1);
        writer.Write((ushort)header);
        writer.Write((uint)(stringsStart + data.Length));
        writer.Write((uint)strings.Length);
        writer.Write(0u);
        writer.Write(utf8 ? 0x100u : 0u);
        writer.Write((uint)stringsStart);
        writer.Write(0u);
        foreach (uint offset in offsets)
        {
            writer.Write(offset);
        }
        writer.Write(data.ToArray());
    }

    private static void WriteUtf8Length(Stream output, int length)
    {
        if (length > 0x7F)
        {
            output.WriteByte((byte)(0x80 | (length >> 8)));
        }
        output.WriteByte((byte)length);
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        output.WriteByte((byte)value);
        output.WriteByte((byte)(value >> 8));
    }

    private static byte[] BuildResources(uint resourceId, string value)
    {
        using var pool = new MemoryStream();
        using (var writer = new BinaryWriter(pool, Encoding.UTF8, true))
        {
            WriteStringPool(writer, [value], true);
        }
        int count = (int)(resourceId & 0xFFFF) + 1;
        using var type = new MemoryStream();
        using (var writer = new BinaryWriter(type, Encoding.UTF8, true))
        {
            writer.Write((ushort)0x0201);
            writer.Write((ushort)48);
            writer.Write((uint)(48 + count * 4 + 16));
            writer.Write((byte)(resourceId >> 16));
            writer.Write((byte)0);
            writer.Write((ushort)0);
            writer.Write((uint)count);
            writer.Write((uint)(48 + count * 4));
            writer.Write(28u);
            writer.Write(new byte[24]);
            for (int i = 0; i < count; i++)
            {
                writer.Write(i == count - 1 ? 0u : uint.MaxValue);
            }
            writer.Write((ushort)8);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write((ushort)8);
            writer.Write((byte)0);
            writer.Write((byte)3);
            writer.Write(0u);
        }
        using var package = new MemoryStream();
        using (var writer = new BinaryWriter(package, Encoding.UTF8, true))
        {
            writer.Write((ushort)0x0200);
            writer.Write((ushort)288);
            writer.Write((uint)(288 + type.Length));
            writer.Write(resourceId >> 24);
            writer.Write(new byte[276]);
            writer.Write(type.ToArray());
        }
        using var table = new MemoryStream();
        using (var writer = new BinaryWriter(table, Encoding.UTF8, true))
        {
            writer.Write((ushort)2);
            writer.Write((ushort)12);
            writer.Write((uint)(12 + pool.Length + package.Length));
            writer.Write(1u);
            writer.Write(pool.ToArray());
            writer.Write(package.ToArray());
        }
        return table.ToArray();
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == signature)
            {
                return i;
            }
        }

        throw new InvalidDataException();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class BlockingValidator : IApkContainerValidator
    {
        public int Entered;
        public TaskCompletionSource TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ValidateStandaloneAsync(string snapshotPath, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Entered) == 2)
            {
                TwoEntered.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class InspectingValidator(Action inspect) : IApkContainerValidator
    {
        public Task ValidateStandaloneAsync(string snapshotPath, CancellationToken cancellationToken = default)
        {
            inspect();
            return Task.CompletedTask;
        }
    }
}
