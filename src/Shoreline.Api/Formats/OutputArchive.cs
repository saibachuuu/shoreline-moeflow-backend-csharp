using System.IO.Compression;
using System.Text;

namespace Shoreline.Api.Formats;

/// <summary>
/// Deterministic, uncompressed zip creation matching Python
/// <c>zipfile.ZipFile(path, "w")</c> (ZIP_STORED) and forward-slash entries.
/// </summary>
public static class OutputArchive
{
    public static async Task CreateAsync(
        string rootDirectory,
        string zipPath,
        IReadOnlyList<string> entryNames,
        CancellationToken ct)
    {
        await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 81920, useAsync: true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var entryName in entryNames)
        {
            ct.ThrowIfCancellationRequested();
            var relative = LabelPlusWriter.SafeEntryPath(entryName);
            var source = Path.Combine(rootDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;

            var entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);
            await using var target = entry.Open();
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true);
            await input.CopyToAsync(target, ct);
        }
    }
}
