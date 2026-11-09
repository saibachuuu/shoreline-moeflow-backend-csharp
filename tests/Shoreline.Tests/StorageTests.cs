using System.Security.Cryptography;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class StorageTests(LocalServices services)
{
    [Fact]
    public async Task StreamsCopiesAndDeletesObjects()
    {
        using var storage = new ObjectStorage(services.Configuration);
        var key = "test/" + Guid.NewGuid().ToString("N");
        var copy = key + "-copy";
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024);
        using var input = new MemoryStream(bytes);
        try
        {
            Assert.Null(await storage.SizeAsync(key, default));
            await storage.PutAsync(key, input, "application/octet-stream", default);
            Assert.True(input.CanRead);
            Assert.Equal(bytes.Length, await storage.SizeAsync(key, default));
            await storage.CopyAsync(key, copy, default);
            using (var download = await storage.GetAsync(copy, default))
            {
                Assert.Equal(bytes.Length, download.Length);
                Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(download.Content));
            }
            var signed = await storage.SignGetAsync(copy, TimeSpan.FromMinutes(1));
            Assert.Contains("X-Amz-Signature=", signed);
        }
        finally
        {
            await storage.DeleteAsync(key, default);
            await storage.DeleteAsync(copy, default);
        }
        Assert.Null(await storage.SizeAsync(key, default));
    }
}
