namespace Shoreline.Api.Storage;

/// <summary>
/// Filesystem backend for local development and tests. Object keys are mapped below
/// one owned root; reparse points are never followed, writes replace atomically, and
/// listing uses bounded memory instead of materializing every file at once.
/// </summary>
internal sealed class LocalStorageBackend : IStorageBackend
{
    private readonly string root;
    private readonly string rootWithSeparator;
    private readonly string? domain;

    public LocalStorageBackend(string root, string? domain)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Storage:Root is required for Local storage.");
        }

        if (!Path.IsPathRooted(root))
        {
            throw new InvalidOperationException("Storage:Root must be an absolute path.");
        }

        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        CheckReparsePoint(fullRoot);
        this.root = Path.TrimEndingDirectorySeparator(fullRoot);
        rootWithSeparator = Path.EndsInDirectorySeparator(this.root)
            ? this.root
            : this.root + Path.DirectorySeparatorChar;
        this.domain = domain;
    }

    public async Task PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken,
        long? contentLength = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        StorageKey.ValidateLocalObjectKey(key);

        var fullPath = ResolvePath(key);
        EnsureNoReparsePoint(fullPath);

        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Object key does not have a parent directory.", nameof(key));
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoint(parent);

        var temporaryPath = Path.Combine(
            parent,
            "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The temporary file may never have been created, or cleanup may
                    // race with another process. The original object is untouched.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    public Task<ObjectDownload> GetAsync(string key, CancellationToken cancellationToken)
    {
        StorageKey.ValidateLocalObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = ResolvePath(key);
        EnsureNoReparsePoint(fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Object not found.", key);
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult(new ObjectDownload(stream, stream.Length, "application/octet-stream"));
    }

    public Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        StorageKey.ValidateLocalObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = ResolvePath(key);
        EnsureNoReparsePoint(fullPath);

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<ObjectInfo?>(null);
        }

        var info = new FileInfo(fullPath);
        return Task.FromResult<ObjectInfo?>(new ObjectInfo(
            key,
            info.Length,
            ETag: null,
            ContentType: null,
            LastModified: info.LastWriteTimeUtc));
    }

    public async Task<long?> SizeAsync(string key, CancellationToken cancellationToken)
    {
        var info = await HeadAsync(key, cancellationToken).ConfigureAwait(false);
        return info?.Size;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        StorageKey.ValidateLocalObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = ResolvePath(key);
        if (Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("Object key refers to a directory; refusing to delete it.");
        }

        EnsureNoReparsePoint(fullPath);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    public async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        StorageKey.ValidateLocalObjectKey(source, nameof(source));
        StorageKey.ValidateLocalObjectKey(destination, nameof(destination));

        using var sourceStream = OpenRead(source, cancellationToken);
        await PutAsync(destination, sourceStream, "application/octet-stream", cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> SignGetAsync(string key, TimeSpan validity) =>
        SignGetAsync(key, validity, null);

    public Task<string> SignGetAsync(string key, TimeSpan validity, string? contentDisposition)
    {
        StorageKey.ValidateLocalObjectKey(key);

        if (validity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), "Signed URL validity must be positive.");
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new InvalidOperationException(
                "Storage:Domain is required to create Local storage download URLs.");
        }

        // No filesystem access here: the URL is a pure string operation. A later
        // server/static-file wiring step is responsible for actually serving it.
        return Task.FromResult(domain.TrimEnd('/') + "/" + StorageKey.EncodePath(key));
    }

    public Task<ObjectPage> ListAsync(
        string prefix,
        string? startAfter,
        int maxKeys,
        CancellationToken cancellationToken)
    {
        if (maxKeys is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxKeys), "maxKeys must be between 1 and 1000.");
        }

        prefix ??= string.Empty;
        StorageKey.ValidatePrefix(prefix, nameof(prefix));

        if (!string.IsNullOrEmpty(startAfter))
        {
            StorageKey.ValidatePrefix(startAfter, nameof(startAfter));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var limit = maxKeys + 1;
        var selected = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var relativeKey in EnumerateRelativeFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!relativeKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(startAfter)
                && StringComparer.Ordinal.Compare(relativeKey, startAfter) <= 0)
            {
                continue;
            }

            if (selected.Count < limit)
            {
                selected.Add(relativeKey);
                continue;
            }

            var largest = selected.Max!;
            if (StringComparer.Ordinal.Compare(relativeKey, largest) < 0)
            {
                selected.Remove(largest);
                selected.Add(relativeKey);
            }
        }

        var ordered = selected.Take(maxKeys).ToList();
        var objects = new List<ObjectInfo>(ordered.Count);
        foreach (var relativeKey in ordered)
        {
            var fullPath = Path.Combine(
                root,
                relativeKey.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    continue;
                }

                objects.Add(new ObjectInfo(
                    relativeKey,
                    info.Length,
                    ETag: null,
                    ContentType: null,
                    LastModified: info.LastWriteTimeUtc));
            }
            catch (IOException)
            {
                // A file can disappear between enumeration and metadata lookup.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip entries the process cannot read.
            }
        }

        var next = selected.Count > maxKeys && ordered.Count > 0
            ? ordered[^1]
            : null;

        return Task.FromResult(new ObjectPage(objects, next));
    }

    public void Dispose()
    {
    }

    private FileStream OpenRead(string key, CancellationToken cancellationToken)
    {
        StorageKey.ValidateLocalObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = ResolvePath(key);
        EnsureNoReparsePoint(fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Object not found.", key);
        }

        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private string ResolvePath(string key)
    {
        var relativePath = key.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.Equals(root, comparison)
            && !fullPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new ArgumentException("Object key escapes the Local storage root.", nameof(key));
        }

        return fullPath;
    }

    private void EnsureNoReparsePoint(string fullPath)
    {
        CheckReparsePoint(root);

        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath is "." or "")
        {
            return;
        }

        var parts = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            CheckReparsePoint(current);
        }
    }

    private static void CheckReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Refusing to follow a reparse point or symbolic link in the Local storage root.");
        }
    }

    private static IEnumerator<string>? TryEnumerateDirectory(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IEnumerable<string> EnumerateRelativeFiles()
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        CheckReparsePoint(root);

        // Keep one lazy enumerator per directory on the current DFS path. This
        // bounds memory by depth rather than by the total number of directories.
        var pending = new Stack<IEnumerator<string>>();
        var rootEntries = TryEnumerateDirectory(root);
        if (rootEntries is null)
        {
            yield break;
        }

        pending.Push(rootEntries);

        try
        {
            while (pending.Count > 0)
            {
                var entries = pending.Peek();

                bool hasNext;
                try
                {
                    hasNext = entries.MoveNext();
                }
                catch (IOException)
                {
                    pending.Pop().Dispose();
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    pending.Pop().Dispose();
                    continue;
                }

                if (!hasNext)
                {
                    pending.Pop().Dispose();
                    continue;
                }

                var entry = entries.Current;

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var childEntries = TryEnumerateDirectory(entry);
                    if (childEntries is not null)
                    {
                        pending.Push(childEntries);
                    }

                    continue;
                }

                var relativePath = Path.GetRelativePath(root, entry);
                if (Path.IsPathRooted(relativePath)
                    || relativePath == ".."
                    || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return relativePath
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                pending.Pop().Dispose();
            }
        }
    }
}
