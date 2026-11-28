using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Titan.Application.Abstractions;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.FunctionCode;

namespace Titan.Tests.Infrastructure.FunctionCode;

// The zip checks of titan-ops features/ovc-function/design.md#code-limits and the deterministic code layer of
// titan-ops features/ovc-function/design.md#code-source, on real zips written to a temp directory.
public sealed class ZipFunctionCodeInspectorTests : IDisposable
{
    private const int Executable = 0b111_101_101;
    private const int Regular = 0b110_100_100;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "titan-zip-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _workDir;
    private readonly ZipFunctionCodeInspector _inspector = new();

    public ZipFunctionCodeInspectorTests()
    {
        Directory.CreateDirectory(_root);
        _workDir = Path.Combine(_root, "work");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed record Item(string Name, byte[] Content, int? UnixMode = null);

    private string Zip(params Item[] files)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = CreateZip(path))
        {
            foreach (Item file in files)
            {
                ZipArchiveEntry entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                if (file.UnixMode is int mode)
                {
                    entry.ExternalAttributes = mode << 16;
                }
                using Stream s = entry.Open();
                s.Write(file.Content);
            }
        }
        return path;
    }

    private static ZipArchive CreateZip(string path) => new(File.Create(path), ZipArchiveMode.Create);

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    // The first 20 bytes of an ELF header: 64-bit, little-endian, e_machine at 18.
    private static byte[] Elf(ushort machine = 0x3E, byte elfClass = 2, byte endianness = 1)
    {
        var header = new byte[64];
        header[0] = 0x7F;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = elfClass;
        header[5] = endianness;
        header[18] = (byte)(machine & 0xFF);
        header[19] = (byte)(machine >> 8);
        return header;
    }

    private Task<FunctionCodeLayer> Pack(string zip, FunctionCodeLanguage language = FunctionCodeLanguage.Python, string? handler = "main.handler") =>
        _inspector.PackAsync(zip, language, handler, _workDir, CancellationToken.None);

    private async Task<string> Rejected(string zip, FunctionCodeLanguage language = FunctionCodeLanguage.Python, string? handler = "main.handler")
    {
        string workDir = Path.Combine(_root, "rejected-" + Guid.NewGuid().ToString("N"));
        var e = await Assert.ThrowsAsync<BusinessException>(
            () => _inspector.PackAsync(zip, language, handler, workDir, CancellationToken.None));
        Assert.Equal("FUNCTION_CODE_INVALID", e.Code);
        Assert.False(Directory.Exists(workDir) && Directory.EnumerateFileSystemEntries(workDir).Any(),
            "a rejected zip leaves no file in the work directory");
        return e.Message;
    }

    private static List<(string Name, TarEntryType Type, UnixFileMode Mode, DateTimeOffset MTime, int Uid, int Gid, string User, string Group, byte[] Data)> ReadLayer(string gzipPath)
    {
        var entries = new List<(string, TarEntryType, UnixFileMode, DateTimeOffset, int, int, string, string, byte[])>();
        using var gzip = new GZipStream(File.OpenRead(gzipPath), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data);
            var posix = (PosixTarEntry)entry;
            entries.Add((entry.Name, entry.EntryType, entry.Mode, entry.ModificationTime, entry.Uid, entry.Gid, posix.UserName, posix.GroupName, data.ToArray()));
        }
        return entries;
    }

    [Fact]
    public async Task APythonZip_BecomesALayerUnderFunction_WithParentsSortedModesPinnedAndTimesZero()
    {
        string zip = Zip(
            new Item("main.py", Text("def handler(e, c): return 'hi'")),
            new Item("lib/util.py", Text("X = 1"), Regular | 0x8000),
            new Item("bin/tool", Text("#!/bin/sh"), Executable | 0x8000));

        FunctionCodeLayer layer = await Pack(zip);

        var entries = ReadLayer(layer.Path);
        Assert.Equal(
            ["function/", "function/bin/", "function/bin/tool", "function/lib/", "function/lib/util.py", "function/main.py"],
            entries.Select(e => e.Name));
        Assert.All(entries, e =>
        {
            Assert.Equal(DateTimeOffset.UnixEpoch, e.MTime);
            Assert.Equal(0, e.Uid);
            Assert.Equal(0, e.Gid);
            Assert.Equal("", e.User);
            Assert.Equal("", e.Group);
        });
        Assert.All(entries.Where(e => e.Name.EndsWith('/')), e =>
        {
            Assert.Equal(TarEntryType.Directory, e.Type);
            Assert.Equal((UnixFileMode)Executable, e.Mode);
        });
        Assert.Equal((UnixFileMode)Executable, entries.Single(e => e.Name == "function/bin/tool").Mode);
        Assert.Equal((UnixFileMode)Regular, entries.Single(e => e.Name == "function/lib/util.py").Mode);
        Assert.Equal((UnixFileMode)Regular, entries.Single(e => e.Name == "function/main.py").Mode);
        Assert.Equal(Text("X = 1"), entries.Single(e => e.Name == "function/lib/util.py").Data);

        Assert.Equal(3, layer.FileCount);
        Assert.Equal(Text("def handler(e, c): return 'hi'").Length + 5 + 9, layer.UnpackedBytes);
        Assert.Equal(new FileInfo(zip).Length, layer.ZipBytes);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip))), layer.ZipSha256);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(layer.Path))), layer.Digest);
        Assert.Equal(new FileInfo(layer.Path).Length, layer.Bytes);
        Assert.StartsWith(_workDir, layer.Path);
        Assert.Equal([layer.Path], Directory.GetFiles(_workDir));
    }

    [Fact]
    public async Task TheDiffId_IsTheSha256OfTheUncompressedTar()
    {
        FunctionCodeLayer layer = await Pack(Zip(new Item("main.py", Text("x"))));

        using var gzip = new GZipStream(File.OpenRead(layer.Path), CompressionMode.Decompress);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(gzip)), layer.DiffId);
    }

    [Fact]
    public async Task PackingTheSameZipTwice_GivesTheSameDiffIdAndDigest()
    {
        string zip = Zip(new Item("main.py", Text("print(1)")), new Item("pkg/a.py", Text("a"), Executable | 0x8000));

        FunctionCodeLayer first = await Pack(zip);
        await Task.Delay(1100); // a writer that stamps the current time would differ now
        FunctionCodeLayer second = await Pack(zip);

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(first.DiffId, second.DiffId);
        Assert.Equal(first.Digest, second.Digest);
    }

    [Fact]
    public async Task AZipListingItsEntriesInAnotherOrder_GivesTheSameDiffId()
    {
        FunctionCodeLayer one = await Pack(Zip(new Item("main.py", Text("m")), new Item("a/b.py", Text("b"))));
        FunctionCodeLayer other = await Pack(Zip(new Item("a/", []), new Item("a/b.py", Text("b")), new Item("main.py", Text("m"))));

        Assert.Equal(one.DiffId, other.DiffId);
    }

    [Fact]
    public async Task APathLongerThanUstarAllows_IsKeptWhole()
    {
        string deep = string.Join("/", Enumerable.Repeat("node_modules_with_a_long_name", 8)) + "/index.js";
        FunctionCodeLayer layer = await Pack(Zip(new Item("main.py", Text("m")), new Item(deep, Text("d"))));

        Assert.Contains(ReadLayer(layer.Path), e => e.Name == "function/" + deep && e.Data.SequenceEqual(Text("d")));
    }

    [Theory]
    [InlineData("handler.main", "handler.py")]
    [InlineData("pkg.mod.run", "pkg/mod.py")]
    public async Task APythonHandler_NeedsItsModuleFile(string handler, string file)
    {
        FunctionCodeLayer layer = await Pack(Zip(new Item(file, Text("def f(): pass"))), handler: handler);

        Assert.Equal(1, layer.FileCount);
        string message = await Rejected(Zip(new Item("other.py", Text("x"))), handler: handler);
        Assert.Contains($"'{file}'", message);
    }

    [Theory]
    [InlineData("index.js")]
    [InlineData("index.mjs")]
    [InlineData("index.cjs")]
    public async Task ANodeHandler_NeedsAJsMjsOrCjsFile(string file)
    {
        FunctionCodeLayer layer = await Pack(Zip(new Item(file, Text("export const handler = 1"))), FunctionCodeLanguage.NodeJs, "index.handler");

        Assert.Contains(ReadLayer(layer.Path), e => e.Name == "function/" + file);
    }

    [Fact]
    public async Task ANestedNodeHandler_NeedsItsFileInTheDirectory()
    {
        await Pack(Zip(new Item("src/app.js", Text("x"))), FunctionCodeLanguage.NodeJs, "src.app.handler");

        string message = await Rejected(Zip(new Item("app.js", Text("x"))), FunctionCodeLanguage.NodeJs, "src.app.handler");
        Assert.Contains("'src/app.js' or 'src/app.mjs' or 'src/app.cjs'", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("1main.handler")]
    [InlineData("main.")]
    [InlineData("main..handler")]
    [InlineData("src/app.handler")]
    public async Task AHandlerNotModuleDotFunction_IsRejected(string? handler)
    {
        string message = await Rejected(Zip(new Item("main.py", Text("x"))), handler: handler);

        Assert.Contains("<module>.<function>", message);
    }

    [Fact]
    public async Task AGoZip_HasItsBootstrapExecutable_EvenWithoutAModeInTheZip()
    {
        // A zip made on Windows: no unix mode at all.
        FunctionCodeLayer layer = await Pack(Zip(new Item("bootstrap", Elf()), new Item("config.json", Text("{}"))),
            FunctionCodeLanguage.Go, handler: null);

        var entries = ReadLayer(layer.Path);
        Assert.Equal((UnixFileMode)Executable, entries.Single(e => e.Name == "function/bootstrap").Mode);
        Assert.Equal((UnixFileMode)Regular, entries.Single(e => e.Name == "function/config.json").Mode);
    }

    [Fact]
    public async Task AGoZip_WithAHandler_IsRejected()
    {
        string message = await Rejected(Zip(new Item("bootstrap", Elf())), FunctionCodeLanguage.Go, "main.handler");

        Assert.Contains("no handler", message);
    }

    [Fact]
    public async Task AGoZip_WithoutBootstrapAtTheRoot_IsRejected()
    {
        string message = await Rejected(Zip(new Item("bin/bootstrap", Elf())), FunctionCodeLanguage.Go, null);

        Assert.Contains("'bootstrap'", message);
    }

    public static TheoryData<byte[]> NotX8664Elf => new()
    {
        Text("#!/bin/sh\necho hi\n not an elf at all"),
        Elf(machine: 0xB7),
        Elf(elfClass: 1),
        Elf(endianness: 2),
        Elf()[..19],
    };

    [Theory]
    [MemberData(nameof(NotX8664Elf))]
    public async Task AGoBootstrap_ThatIsNotAnX8664Elf_IsRejected(byte[] bootstrap)
    {
        string message = await Rejected(Zip(new Item("bootstrap", bootstrap)), FunctionCodeLanguage.Go, null);

        Assert.Contains("x86-64", message);
    }

    [Theory]
    [InlineData("a\\main.py", "backslash")]
    [InlineData("/etc/main.py", "absolute")]
    [InlineData("C:/main.py", "absolute")]
    [InlineData("../main.py", "'..'")]
    [InlineData("a/../../main.py", "'..'")]
    [InlineData("a//main.py", "empty")]
    [InlineData("./main.py", "'.'")]
    public async Task AnEntryWithAnUnsafePath_IsRejected_NamingIt(string name, string rule)
    {
        string message = await Rejected(Zip(new Item("main.py", Text("x")), new Item(name, Text("y"))));

        Assert.Contains(name, message);
        Assert.Contains(rule, message);
    }

    [Fact]
    public async Task ASymbolicLink_IsRejected()
    {
        string message = await Rejected(Zip(new Item("main.py", Text("x")), new Item("link", Text("/etc/passwd"), 0xA000 | 0x1FF)));

        Assert.Contains("'link' is a symbolic link", message);
    }

    [Fact]
    public async Task APathGivenTwice_IsRejected()
    {
        string message = await Rejected(Zip(new Item("main.py", Text("x")), new Item("main.py", Text("y"))));

        Assert.Contains("twice", message);
    }

    [Fact]
    public async Task AFileThatIsAlsoADirectory_IsRejected()
    {
        string message = await Rejected(Zip(new Item("main.py", Text("x")), new Item("lib", Text("y")), new Item("lib/a.py", Text("z"))));

        Assert.Contains("'lib'", message);
    }

    [Fact]
    public async Task MoreThan20000Files_IsRejected_ButDirectoriesDoNotCount()
    {
        var atTheLimit = Enumerable.Range(0, 19_999).Select(i => new Item($"d{i % 50}/f{i}", []))
            .Append(new Item("main.py", Text("x")))
            .Concat(Enumerable.Range(0, 50).Select(i => new Item($"d{i}/", [])))
            .ToArray();
        FunctionCodeLayer layer = await Pack(Zip(atTheLimit));
        Assert.Equal(20_000, layer.FileCount);
        File.Delete(layer.Path);

        string message = await Rejected(Zip(atTheLimit.Append(new Item("one-more", [])).ToArray()));
        Assert.Contains("20000 files", message);
    }

    [Fact]
    public async Task AZipDeclaringMoreEntriesThanFilesAndDirectoriesCouldNeed_IsRejectedBeforeItsDirectoryIsRead()
    {
        var entries = Enumerable.Range(0, ZipFunctionCodeInspector.MaxEntries + 1).Select(i => new Item($"e{i}/", [])).Append(new Item("main.py", Text("x")));

        string message = await Rejected(Zip(entries.ToArray()));

        Assert.Contains("entries", message);
    }

    [Fact]
    public async Task AZipBomb_IsRejected_WhileUnpacking_WithoutLeavingItsTar()
    {
        string zip = Path.Combine(_root, "bomb.zip");
        using (var archive = CreateZip(zip))
        {
            archive.CreateEntry("main.py").Open().Dispose();
            using Stream s = archive.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open();
            var chunk = new byte[1 << 20];
            for (int i = 0; i < 300; i++)
            {
                s.Write(chunk);
            }
        }
        Assert.True(new FileInfo(zip).Length < 2 << 20, "the bomb is small on the wire");

        string message = await Rejected(zip);

        Assert.Contains("250 MiB", message);
    }

    [Fact]
    public async Task AZipLargerThan50MiB_IsRejectedBeforeItIsOpened()
    {
        string zip = Path.Combine(_root, "big.zip");
        using (FileStream f = File.Create(zip))
        {
            f.SetLength(ZipFunctionCodeInspector.MaxZipBytes + 1);
        }

        string message = await Rejected(zip);

        Assert.Contains("50 MiB", message);
    }

    [Fact]
    public async Task AFileThatIsNotAZip_IsRejected()
    {
        string notAZip = Path.Combine(_root, "random.zip");
        File.WriteAllBytes(notAZip, RandomNumberGenerator.GetBytes(4096));

        string message = await Rejected(notAZip);

        Assert.Contains("not a readable zip", message);
    }

    [Fact]
    public async Task ACorruptEntry_IsRejected()
    {
        string zip = Zip(new Item("main.py", Text(string.Concat(Enumerable.Repeat("print('hello')\n", 5000)))));
        byte[] bytes = File.ReadAllBytes(zip);
        // The first deflate block of the only entry gets the reserved block type: the directory still reads, the data does not.
        int dataStart = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28);
        bytes[dataStart] |= 0b110;
        File.WriteAllBytes(zip, bytes);

        string message = await Rejected(zip);

        Assert.Contains("not a readable zip", message);
    }
}
