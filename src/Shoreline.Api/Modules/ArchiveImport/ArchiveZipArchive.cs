using System.IO.Compression;

namespace Shoreline.Api.Modules.ArchiveImport;

public sealed record ArchiveZipEntry(string Name, ZipArchiveEntry Entry, long Length);

/// <summary>
/// Read-only ZIP session. Every entry is streamed once for structural/CRC/size validation, then only selected
/// image entries are streamed again during import. Nothing is extracted as a directory tree, so zip-slip cannot
/// escape the owned temporary directory.
/// </summary>
public sealed class ArchiveZipArchive : IDisposable
{
    private static readonly HashSet<string> ImageSuffixes =
        new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "bmp", "gif", "webp" };

    private readonly FileStream _file;
    private readonly ZipArchive _zip;

    public ArchiveZipArchive(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            _zip = new ZipArchive(_file, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<ArchiveZipEntry>> ValidateAndListAsync(ArchiveImportOptions options,
        CancellationToken ct)
    {
        try
        {
            return await ValidateAsync(options, ct);
        }
        catch (ArchiveImportFailure)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException
                                          or ArgumentException)
        {
            throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
        }
    }

    public async Task ExtractEntryAsync(ArchiveZipEntry entry, string destinationPath, long maxEntryBytes,
        CancellationToken ct)
    {
        await using var input = entry.Entry.Open();
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);
        var buffer = new byte[256 * 1024];
        long total = 0;
        var crc = 0xffffffffu;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > maxEntryBytes)
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage,
                    $"归档单条目大小超过上限 {maxEntryBytes} bytes");
            crc = ArchiveCrc32.Append(crc, buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await output.FlushAsync(ct);
        if ((crc ^ 0xffffffffu) != entry.Entry.Crc32)
            throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
        if (total != entry.Length)
            throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
    }

    public void Dispose()
    {
        _zip.Dispose();
        _file.Dispose();
    }

    private async Task<IReadOnlyList<ArchiveZipEntry>> ValidateAsync(ArchiveImportOptions options,
        CancellationToken ct)
    {
        if (_zip.Entries.Count > options.MaxEntries)
            throw new ArchiveImportFailure(ArchiveImportText.FailedStage,
                $"归档条目数量超过上限 {options.MaxEntries}");

        long declaredTotal = 0;
        foreach (var entry in _zip.Entries)
        {
            if (IsDirectory(entry)) continue;
            if (entry.Length > options.MaxEntryBytes)
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage,
                    $"归档单条目大小超过上限 {options.MaxEntryBytes} bytes");
            declaredTotal += entry.Length;
            if (declaredTotal > options.MaxUncompressedBytes)
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage,
                    $"归档解压后大小超过上限 {options.MaxUncompressedBytes} bytes");
        }

        long actualTotal = 0;
        foreach (var entry in _zip.Entries)
        {
            if (IsDirectory(entry)) continue;
            ct.ThrowIfCancellationRequested();
            await using var stream = entry.Open();
            var buffer = new byte[256 * 1024];
            long actual = 0;
            var crc = 0xffffffffu;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                actual += read;
                actualTotal += read;
                if (actual > options.MaxEntryBytes)
                    throw new ArchiveImportFailure(ArchiveImportText.FailedStage,
                        $"归档单条目大小超过上限 {options.MaxEntryBytes} bytes");
                if (actualTotal > options.MaxUncompressedBytes)
                    throw new ArchiveImportFailure(ArchiveImportText.FailedStage,
                        $"归档解压后大小超过上限 {options.MaxUncompressedBytes} bytes");
                crc = ArchiveCrc32.Append(crc, buffer.AsSpan(0, read));
            }
            if ((crc ^ 0xffffffffu) != entry.Crc32)
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
            if (actual != entry.Length)
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage, "归档不是有效的 zip 或已损坏");
        }

        var images = new List<ArchiveZipEntry>();
        foreach (var entry in _zip.Entries)
        {
            if (IsDirectory(entry)) continue;
            var name = ImageName(entry.FullName);
            if (name.Length == 0 || name.StartsWith('.')) continue;
            var dot = name.LastIndexOf('.');
            if (dot < 0) continue;
            if (!ImageSuffixes.Contains(name[(dot + 1)..])) continue;
            images.Add(new ArchiveZipEntry(name, entry, entry.Length));
            if (images.Count > options.MaxEntries)
                throw new ArchiveImportFailure(ArchiveImportText.FailedStage,
                    $"归档图片数量超过上限 {options.MaxEntries}");
        }
        return images;
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0;

    private static string ImageName(string fullName)
    {
        if (fullName.StartsWith("images/", StringComparison.Ordinal)) fullName = fullName[7..];
        var slash = fullName.LastIndexOf('/');
        if (slash >= 0) fullName = fullName[(slash + 1)..];
        var backslash = fullName.LastIndexOf('\\');
        if (backslash >= 0) fullName = fullName[(backslash + 1)..];
        return fullName.Trim();
    }
}
