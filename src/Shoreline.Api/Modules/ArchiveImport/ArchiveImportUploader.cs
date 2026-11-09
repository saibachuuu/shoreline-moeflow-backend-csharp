using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace Shoreline.Api.Modules.ArchiveImport;

/// <summary>Uploads one decoded archive image through the existing project file pipeline.</summary>
public interface IArchiveProjectUploader
{
    Task UploadAsync(BsonDocument project, string filename, Stream content, long length, CancellationToken ct);
}

/// <summary>
/// Resolves D1 FileService from a fresh scope and calls its public UploadAsync. Reflection keeps this module
/// compile-neutral while D1 is still uncommitted; once FileService is in the shared baseline the same code keeps
/// working (the type and method signature are stable).
/// </summary>
public sealed class FileServiceArchiveUploader(IServiceScopeFactory scopes) : IArchiveProjectUploader
{
    private static readonly Type? FileServiceType =
        Type.GetType("Shoreline.Api.Features.FileService, Shoreline.Api", throwOnError: false);
    private static readonly MethodInfo? UploadMethod = FileServiceType?.GetMethod("UploadAsync",
        BindingFlags.Public | BindingFlags.Instance, binder: null,
        types: [typeof(BsonDocument), typeof(string), typeof(IFormFile), typeof(string), typeof(CancellationToken)],
        modifiers: null);

    public async Task UploadAsync(BsonDocument project, string filename, Stream content, long length,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (FileServiceType is null || UploadMethod is null)
            throw new ArchiveUploaderUnavailableException();
        var service = scope.ServiceProvider.GetService(FileServiceType);
        if (service is null) throw new ArchiveUploaderUnavailableException();
        var upload = new StreamFormFile(filename, content, length);
        try
        {
            var task = (Task)UploadMethod.Invoke(service, [project, filename, upload, null, ct])!;
            await task.ConfigureAwait(false);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
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
/// Uses the public D1 Filename parser when present, with a contract-identical fallback so this module can be
/// unit-tested on a baseline that does not yet contain FileService.cs.
/// </summary>
public static class ArchiveFilenamePolicy
{
    private static readonly Type? FileServiceType =
        Type.GetType("Shoreline.Api.Features.FileService, Shoreline.Api", throwOnError: false);

    private static readonly MethodInfo? ParseMethod = FileServiceType?.GetMethod("ParseFilename",
        BindingFlags.Public | BindingFlags.Static, binder: null,
        types: [typeof(string), typeof(bool)], modifiers: null);

    private static readonly HashSet<string> ImageSuffixes =
        new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "bmp", "gif", "webp" };

    public static bool IsValid(string name)
    {
        if (ParseMethod is not null)
        {
            try
            {
                ParseMethod.Invoke(null, [name, false]);
                return true;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }
        return Fallback(name);
    }

    private static bool Fallback(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 127) return false;
        if (name is "." or ".." || name.EndsWith('.')) return false;
        if (name.Any(value => value is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')) return false;
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 && ImageSuffixes.Contains(name[(dot + 1)..]);
    }
}
