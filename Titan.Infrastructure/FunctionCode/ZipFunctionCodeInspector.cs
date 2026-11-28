using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Titan.Application.Abstractions;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.FunctionCode;

/// The zip's checks and its code layer (titan-ops features/ovc-function/design.md#code-limits,
/// titan-ops features/ovc-function/design.md#code-source). Everything goes through files under workDir: the pod's memory is
/// smaller than what one zip may unpack to.
public sealed partial class ZipFunctionCodeInspector : IFunctionCodeInspector
{
    public const string InvalidCode = "FUNCTION_CODE_INVALID";
    public const long MaxZipBytes = 52_428_800;
    public const long MaxUnpackedBytes = 262_144_000;
    public const int MaxFiles = 20_000;
    public const int MaxEntries = 2 * MaxFiles;

    private const string LayerRoot = "function/";
    private const string GoBootstrap = "bootstrap";
    private const int MaxNameShown = 200;
    private const UnixFileMode DirectoryMode = (UnixFileMode)0b111_101_101;
    private const UnixFileMode ExecutableMode = (UnixFileMode)0b111_101_101;
    private const UnixFileMode RegularFileMode = (UnixFileMode)0b110_100_100;

    public async Task<FunctionCodeLayer> PackAsync(string zipPath, FunctionCodeLanguage language, string? handler, string workDir,
        CancellationToken ct)
    {
        string[] handlerFiles = HandlerFiles(language, handler);
        long zipBytes = new FileInfo(zipPath).Length;
        if (zipBytes > MaxZipBytes)
        {
            throw Invalid("The zip is larger than 50 MiB.");
        }

        Directory.CreateDirectory(workDir);
        string stem = Path.Combine(workDir, "code-" + Guid.NewGuid().ToString("N"));
        string tarPath = stem + ".tar";
        string gzipPath = stem + ".tar.gz";
        try
        {
            string zipSha256 = await Sha256OfFileAsync(zipPath, ct);
            (long unpacked, int files) = await WriteTarAsync(zipPath, language, handlerFiles, tarPath, ct);
            string diffId = await Sha256OfFileAsync(tarPath, ct);
            await GzipAsync(tarPath, gzipPath, ct);
            File.Delete(tarPath);
            string digest = await Sha256OfFileAsync(gzipPath, ct);
            return new FunctionCodeLayer(zipSha256, zipBytes, unpacked, files, diffId, digest, new FileInfo(gzipPath).Length, gzipPath);
        }
        catch
        {
            File.Delete(tarPath);
            File.Delete(gzipPath);
            throw;
        }
    }

    /// The files one of which must be in the zip for the handler to load: "a.b.fn" is module a/b, function fn.
    private static string[] HandlerFiles(FunctionCodeLanguage language, string? handler)
    {
        if (language == FunctionCodeLanguage.Go)
        {
            if (!string.IsNullOrEmpty(handler))
            {
                throw Invalid("A Go function has no handler: the zip's bootstrap file is the function.");
            }
            return [GoBootstrap];
        }
        if (handler is null || !HandlerPattern().IsMatch(handler))
        {
            throw Invalid("The handler must be <module>.<function>, each part letters, digits or underscores, not starting with a digit.");
        }
        string module = handler[..handler.LastIndexOf('.')].Replace('.', '/');
        return language == FunctionCodeLanguage.Python
            ? [module + ".py"]
            : [module + ".js", module + ".mjs", module + ".cjs"];
    }

    private sealed record ZipItem(string Path, ZipArchiveEntry? Entry, bool IsDirectory, int UnixMode);

    private static async Task<(long Unpacked, int Files)> WriteTarAsync(string zipPath, FunctionCodeLanguage language,
        string[] handlerFiles, string tarPath, CancellationToken ct)
    {
        await using FileStream zipStream = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            // ZipArchive reads the whole central directory into objects: a 50 MiB zip of empty entries is a few hundred
            // thousand of them, so the count it declares is checked first.
            long? declared = await DeclaredEntryCountAsync(zipStream, ct);
            if (declared > MaxEntries)
            {
                throw Invalid($"The zip holds more than {MaxEntries} entries (files and directories).");
            }
            zipStream.Position = 0;
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            List<ZipItem> items = Items(zip);
            int files = items.Count(i => !i.IsDirectory);
            RequireHandlerFile(items, language, handlerFiles);
            if (language == FunctionCodeLanguage.Go)
            {
                await RequireElfX8664Async(items.Single(i => i.Path == GoBootstrap).Entry!, ct);
            }

            var budget = new UnpackBudget();
            await using (FileStream tarStream = new(tarPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            await using (var tar = new TarWriter(tarStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                await WriteEntryAsync(tar, new ZipItem("", null, true, 0), language, budget, ct);
                foreach (ZipItem item in items.OrderBy(i => TarName(i), StringComparer.Ordinal))
                {
                    await WriteEntryAsync(tar, item, language, budget, ct);
                }
            }
            return (budget.Used, files);
        }
        catch (InvalidDataException)
        {
            throw Invalid("The file is not a readable zip archive.");
        }
    }

    /// The entry count of the end of central directory record (zip64 too), or null when there is none to read: ZipArchive
    /// then refuses the file itself.
    private static async Task<long?> DeclaredEntryCountAsync(FileStream zip, CancellationToken ct)
    {
        const int EndRecordSize = 22;
        const int Zip64LocatorSize = 20;
        int tailLength = (int)Math.Min(zip.Length, EndRecordSize + ushort.MaxValue);
        var tail = new byte[tailLength];
        zip.Position = zip.Length - tailLength;
        await zip.ReadExactlyAsync(tail, ct);
        for (int at = tailLength - EndRecordSize; at >= 0; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at)) != 0x06054B50)
            {
                continue;
            }
            ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at + 10));
            bool isZip64 = entries == ushort.MaxValue && at >= Zip64LocatorSize
                && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at - Zip64LocatorSize)) == 0x07064B50;
            if (!isZip64)
            {
                return entries;
            }
            long recordOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(at - Zip64LocatorSize + 8));
            if (recordOffset < 0 || recordOffset > zip.Length - 40)
            {
                return null;
            }
            var record = new byte[40];
            zip.Position = recordOffset;
            await zip.ReadExactlyAsync(record, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064B50)
            {
                return null;
            }
            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            return count > long.MaxValue ? long.MaxValue : (long)count;
        }
        return null;
    }

    /// Every file and directory the layer holds, the parents a zip need not list included.
    private static List<ZipItem> Items(ZipArchive zip)
    {
        var items = new Dictionary<string, ZipItem>(StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        int files = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName;
            int unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if ((unixMode & 0xF000) == 0xA000)
            {
                throw Invalid($"The entry '{Shown(name)}' is a symbolic link; a zip may hold only files and directories.");
            }
            CheckName(name);
            bool isDirectory = name.EndsWith('/');
            string path = isDirectory ? name[..^1] : name;
            if (items.ContainsKey(path))
            {
                throw Invalid($"The entry '{Shown(name)}' appears twice in the zip.");
            }
            if (!isDirectory && ++files > MaxFiles)
            {
                throw Invalid($"The zip holds more than {MaxFiles} files.");
            }
            items[path] = new ZipItem(path, entry, isDirectory, unixMode);
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                parents.Add(path[..slash]);
            }
        }
        foreach (string parent in parents)
        {
            if (items.TryGetValue(parent, out ZipItem? item) && !item.IsDirectory)
            {
                throw Invalid($"The entry '{Shown(parent)}' is a file and also the directory of other entries.");
            }
            items.TryAdd(parent, new ZipItem(parent, null, true, 0));
        }
        return items.Values.ToList();
    }

    private static void CheckName(string name)
    {
        if (name.Contains('\\'))
        {
            throw Invalid($"The entry '{Shown(name)}' has a backslash in its path; use '/' between directories.");
        }
        if (name.StartsWith('/') || DriveLetter().IsMatch(name))
        {
            throw Invalid($"The entry '{Shown(name)}' has an absolute path; paths must be relative to the zip's root.");
        }
        string path = name.EndsWith('/') ? name[..^1] : name;
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                throw Invalid($"The entry '{Shown(name)}' has an empty directory name in its path.");
            }
            if (segment is ".." or ".")
            {
                throw Invalid($"The entry '{Shown(name)}' has a '{segment}' segment in its path.");
            }
        }
    }

    private static void RequireHandlerFile(List<ZipItem> items, FunctionCodeLanguage language, string[] handlerFiles)
    {
        bool found = items.Any(i => !i.IsDirectory && handlerFiles.Contains(i.Path, StringComparer.Ordinal));
        if (found)
        {
            return;
        }
        if (language == FunctionCodeLanguage.Go)
        {
            throw Invalid("A Go zip must hold the executable 'bootstrap' at its root.");
        }
        throw Invalid($"The handler's module is not in the zip: expected {string.Join(" or ", handlerFiles.Select(f => $"'{f}'"))}.");
    }

    // The function cluster's machines are x86-64 Linux; anything else fails only when the first pod starts.
    private static async Task RequireElfX8664Async(ZipArchiveEntry bootstrap, CancellationToken ct)
    {
        var header = new byte[20];
        int read = 0;
        await using (Stream stream = bootstrap.Open())
        {
            while (read < header.Length)
            {
                int n = await stream.ReadAsync(header.AsMemory(read), ct);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
        }
        bool isElf64LittleEndianX8664 = read == header.Length
            && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F'
            && header[4] == 2 && header[5] == 1
            && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2)) == 0x3E;
        if (!isElf64LittleEndianX8664)
        {
            throw Invalid("The entry 'bootstrap' is not a Linux x86-64 executable (64-bit little-endian ELF); build it with GOOS=linux GOARCH=amd64.");
        }
    }

    private static string TarName(ZipItem item) =>
        item.Path.Length == 0 ? LayerRoot : LayerRoot + item.Path + (item.IsDirectory ? "/" : "");

    private static async Task WriteEntryAsync(TarWriter tar, ZipItem item, FunctionCodeLanguage language, UnpackBudget budget,
        CancellationToken ct)
    {
        // GNU format with atime and ctime pinned: PAX headers carry the writer's pid and the current time, and ustar refuses
        // a long path. Same zip ⇒ same tar bytes ⇒ same diff id.
        var entry = new GnuTarEntry(item.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, TarName(item))
        {
            ModificationTime = DateTimeOffset.UnixEpoch,
            AccessTime = DateTimeOffset.UnixEpoch,
            ChangeTime = DateTimeOffset.UnixEpoch,
            Uid = 0,
            Gid = 0,
            UserName = "",
            GroupName = "",
            Mode = item.IsDirectory ? DirectoryMode : ModeOf(item, language),
        };
        if (item.IsDirectory)
        {
            await tar.WriteEntryAsync(entry, ct);
            return;
        }
        await using Stream data = item.Entry!.Open();
        // Not seekable, so the writer counts what it copies instead of trusting the length the zip declares.
        entry.DataStream = new BudgetedStream(data, budget);
        await tar.WriteEntryAsync(entry, ct);
    }

    // A zip made on Windows carries no mode, and a bootstrap that is not executable cannot start.
    private static UnixFileMode ModeOf(ZipItem item, FunctionCodeLanguage language)
    {
        if (language == FunctionCodeLanguage.Go && item.Path == GoBootstrap)
        {
            return ExecutableMode;
        }
        return (item.UnixMode & 0b001_001_001) != 0 ? ExecutableMode : RegularFileMode;
    }

    private static async Task GzipAsync(string tarPath, string gzipPath, CancellationToken ct)
    {
        await using FileStream source = new(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using FileStream target = new(gzipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await using var gzip = new GZipStream(target, CompressionLevel.Optimal, leaveOpen: true);
        await source.CopyToAsync(gzip, ct);
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    private static BusinessException Invalid(string reason) => new(InvalidCode, reason);

    private static string Shown(string name) => name.Length <= MaxNameShown ? name : name[..MaxNameShown] + "…";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex HandlerPattern();

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetter();

    private sealed class UnpackBudget
    {
        public long Used;
    }

    /// Counts the bytes really unpacked, across every entry of one zip, and stops at the limit.
    private sealed class BudgetedStream(Stream inner, UnpackBudget budget) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            budget.Used += read;
            if (budget.Used > MaxUnpackedBytes)
            {
                throw Invalid("The zip unpacks to more than 250 MiB.");
            }
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
