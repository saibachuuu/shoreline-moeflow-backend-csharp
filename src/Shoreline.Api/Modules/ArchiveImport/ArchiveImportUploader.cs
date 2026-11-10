using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Shoreline.Api.Features;

namespace Shoreline.Api.Modules.ArchiveImport;

/// <summary>Uploads one decoded archive image through the existing project file pipeline.</summary>
public interface IArchiveProjectUploader
{
    Task UploadAsync(BsonDocument project, string filename, Stream content, long length, CancellationToken ct);
}

/// <summary>
/// Resolves FileService from a fresh scope and calls its public UploadAsync.
/// </summary>
public sealed class FileServiceArchiveUploader(IServiceScopeFactory scopes) : IArchiveProjectUploader
{
    public async Task UploadAsync(BsonDocument project, string filename, Stream content, long length,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetService<FileService>()
            ?? throw new ArchiveUploaderUnavailableException();
        var upload = new StreamFormFile(filename, content, length);
        await service.UploadAsync(project, filename, upload, parentId: null, ct).ConfigureAwait(false);
    }

    private sealed class StreamFormFile(string name, Stream stream, long length) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{name}\"";
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => length;
        public string Name => "file";
        public string FileName => name;
        public void CopyTo(Stream target) => stream.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            stream.CopyToAsync(target, cancellationToken);
        public Stream OpenReadStream() => stream;
    }
}

/// <summary>
/// Validates archive member filenames using FileService.ParseFilename.
/// </summary>
public static class ArchiveFilenamePolicy
{
    public static bool IsValid(string name)
    {
        try
        {
            FileService.ParseFilename(name, folder: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
