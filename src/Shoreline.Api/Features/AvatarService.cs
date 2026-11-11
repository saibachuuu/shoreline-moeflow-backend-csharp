using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

/// <summary>
/// Legacy avatar upload semantics from app/apis/avatar.py. The stored value is
/// still only the object name in the owner's <c>a</c> field; the response exposes
/// a short-lived signed URL.
/// </summary>
public sealed class AvatarService(
    MongoStore store,
    IdentityPermissions permissions,
    ObjectStorage storage,
    IConfiguration configuration,
    ILogger<AvatarService> logger)
{
    public const long DefaultMaxUploadBytes = 20L * 1024 * 1024;
    private static readonly TimeSpan UrlValidity = ObjectStorage.DefaultSignedUrlValidity;

    // A deliberately small seam for the unknown-outcome persistence test. It is
    // not used by production DI and does not mirror the storage/database API.
    public AvatarPersistAsync? PersistAsync { get; init; }

    public async Task<AvatarResult> UploadAsync(
        BsonDocument actor,
        IFormFile? file,
        string? ownerType,
        string? ownerId,
        CancellationToken ct)
    {
        // Flask checks request.files.get("file") before touching the type.
        if (file is null)
        {
            throw new ApiError("UploadFileNotFoundError", 109, "上传未包含文件: 请选择图片");
        }

        BsonDocument owner;
        string collection;
        string prefix;
        string missingCode;
        string missingMessage;

        if (string.Equals(ownerType, "user", StringComparison.Ordinal))
        {
            // The legacy endpoint deliberately ignores the supplied id for the
            // authenticated user's own avatar.
            owner = actor;
            collection = "user";
            prefix = AvatarStoragePrefixes.User(configuration);
            missingCode = "UserNotExistError";
            missingMessage = "用户不存在";
        }
        else if (string.Equals(ownerType, "team", StringComparison.Ordinal))
        {
            owner = await FindTeamAsync(ownerId, ct);
            if (!(await permissions.TeamSnapshotAsync(actor, owner, ct)).Has("team:CHANGE"))
            {
                throw ApiError.Forbidden();
            }

            collection = "team";
            prefix = AvatarStoragePrefixes.Team(configuration);
            missingCode = "TeamNotExistError";
            missingMessage = "团队不存在";
        }
        else
        {
            throw new ApiError("RequestDataWrongError", 111, "请求参数错误: 不支持的头像类型");
        }

        var maxBytes = MaxUploadBytes();
        if (file.Length > maxBytes)
        {
            throw new ApiError("RequestEntityTooLarge", 2, "上传文件超过大小限制", 413);
        }

        var oldAvatar = owner.Text("a");
        var filename = ObjectId.GenerateNewId().ToString() + ".jpg";
        var key = prefix + filename;
        var oldKey = oldAvatar.Length == 0 ? null : prefix + oldAvatar.TrimStart('/');
        var temporaryPath = Path.Combine(Path.GetTempPath(), "shoreline-avatar-" + Guid.NewGuid().ToString("N"));

        try
        {
            await CopyBoundedAsync(file, temporaryPath, maxBytes, ct);

            await using var upload = File.OpenRead(temporaryPath);
            await storage.PutAsync(key, upload, ContentType(file), ct);

            string signedUrl;
            try
            {
                signedUrl = await storage.SignGetAsync(key, UrlValidity);
            }
            catch
            {
                await TryDeleteAsync(key);
                throw;
            }

            UpdateResult result;
            try
            {
                var filter = new BsonDocument("_id", owner["_id"]);
                var update = new BsonDocument("$set", new BsonDocument("a", filename));
                result = PersistAsync is null
                    ? await store.Collection(collection).UpdateOneAsync(filter, update, cancellationToken: ct)
                    : await PersistAsync(store.Collection(collection), filter, update, ct);
            }
            catch (Exception error)
            {
                // A transport timeout/cancellation can lose the acknowledgement
                // after the server committed the new reference. Deleting the new
                // object in that case could remove the avatar we just stored.
                // Prefer an orphan object over a dangling avatar reference.
                logger.LogWarning(
                    "Avatar persistence outcome is unknown; retaining uploaded object ({ErrorType})",
                    error.GetType().Name);
                throw;
            }

            if (result.MatchedCount != 1)
            {
                // A known no-match did not store the new reference, so the new
                // object is safe to compensate. This also covers a concurrently
                // deleted owner.
                await TryDeleteAsync(key);
                throw new ApiError(missingCode, missingCode == "TeamNotExistError" ? 3001 : 1005, missingMessage);
            }

            if (oldKey is not null && !string.Equals(oldKey, key, StringComparison.Ordinal))
            {
                await TryDeleteAsync(oldKey, logFailure: true);
            }

            return new AvatarResult("修改成功", signedUrl);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Best-effort temporary cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temporary cleanup only.
            }
        }
    }

    private async Task<BsonDocument> FindTeamAsync(string? ownerId, CancellationToken ct)
    {
        if (!ObjectId.TryParse(ownerId, out var id))
        {
            throw new ApiError("TeamNotExistError", 3001, "团队不存在");
        }

        return await store.Collection("team").Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("TeamNotExistError", 3001, "团队不存在");
    }

    public long MaxUploadBytes()
    {
        var configured = configuration.GetValue<long?>("Storage:AvatarUploadLimitBytes");
        return configured is > 0 ? configured.Value : DefaultMaxUploadBytes;
    }

    private static string ContentType(IFormFile file) =>
        string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

    private static async Task CopyBoundedAsync(IFormFile file, string path, long maxBytes, CancellationToken ct)
    {
        await using var source = file.OpenReadStream();
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
            {
                throw new ApiError("RequestEntityTooLarge", 2, "上传文件超过大小限制", 413);
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private async Task TryDeleteAsync(string key, bool logFailure = false)
    {
        try
        {
            // Cleanup must not hang a request on an unreachable backend, and must
            // not inherit an already-cancelled request token.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await storage.DeleteAsync(key, timeout.Token);
        }
        catch (Exception error)
        {
            if (logFailure)
            {
                // Do not log object URLs or keys; only the exception type is useful
                // for diagnosing an unreachable backend without leaking storage data.
                logger.LogWarning("Avatar cleanup failed ({ErrorType})", error.GetType().Name);
            }
        }
    }

}

public delegate Task<UpdateResult> AvatarPersistAsync(
    IMongoCollection<BsonDocument> collection,
    BsonDocument filter,
    BsonDocument update,
    CancellationToken cancellationToken);

public sealed record AvatarResult(string Message, string Avatar);

internal static class AvatarStoragePrefixes
{
    public static string User(IConfiguration configuration) =>
        Normalize(configuration["Storage:UserAvatarPrefix"] ?? configuration["Storage:AvatarPrefix"], "user-avatars/");

    public static string Team(IConfiguration configuration) =>
        Normalize(configuration["Storage:TeamAvatarPrefix"], "team-avatars/");

    private static string Normalize(string? configured, string fallback)
    {
        if (configured is null) return fallback;
        var trimmed = configured.Trim();
        if (trimmed.Length == 0) return "";
        return trimmed.TrimEnd('/') + "/";
    }
}
