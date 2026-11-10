namespace Shoreline.Api.Storage;

public interface IStorageBackend : IDisposable
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    Task<ObjectDownload> GetAsync(string key, CancellationToken cancellationToken);

    Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken);

    Task<long?> SizeAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    Task CopyAsync(string source, string destination, CancellationToken cancellationToken);

    Task<string> SignGetAsync(string key, TimeSpan validity) => SignGetAsync(key, validity, null);

    Task<string> SignGetAsync(string key, TimeSpan validity, string? contentDisposition);

    Task<ObjectPage> ListAsync(string prefix, string? startAfter, int maxKeys, CancellationToken cancellationToken);
}
