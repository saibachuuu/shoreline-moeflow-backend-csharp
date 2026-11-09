using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Modules.PartnerSearch;

/// <summary>
/// Port of the Python partner_search module. The service owns only the module
/// fields of the shared site document (pse/pst/psq/psm) and the throttle
/// collection. It never rewrites the whole site document.
/// </summary>
public sealed class PartnerSearchService
{
    internal const string SettingCollection = "site_setting";
    internal const string ThrottleCollection = "partner_search_throttle";
    internal const string ProjectCollection = "project";
    internal const string TeamCollection = "team";
    internal const string FileCollection = "file";

    internal const int DefaultRateLimitSeconds = 10;
    internal const int DefaultMaxLimit = 20;
    internal const int ImageType = 2;
    internal const int ThumbnailSucceeded = 3;
    internal const int ProjectStatusWorking = 0;
    internal const int MaxThrottleKeyLength = 200;

    private static readonly SemaphoreSlim SettingWriteGate = new(1, 1);

    private readonly MongoStore store;
    private readonly PartnerSearchCoverUrl covers;
    private readonly TimeProvider clock;

    public PartnerSearchService(MongoStore store, PartnerSearchCoverUrl covers, TimeProvider clock)
    {
        this.store = store;
        this.covers = covers;
        this.clock = clock;
    }

    private IMongoCollection<BsonDocument> Settings => store.Collection(SettingCollection);
    private IMongoCollection<BsonDocument> Throttle => store.Collection(ThrottleCollection);
    private IMongoCollection<BsonDocument> Projects => store.Collection(ProjectCollection);
    private IMongoCollection<BsonDocument> Teams => store.Collection(TeamCollection);
    private IMongoCollection<BsonDocument> Files => store.Collection(FileCollection);

    public async Task<BsonDocument> GetSettingsAsync(CancellationToken ct)
    {
        var snapshot = await ReadSettingsAsync(ct).ConfigureAwait(false);
        return ToApi(snapshot);
    }

    /// <summary>
    /// Partial update of the four module fields. Unknown keys are ignored, like
    /// the Python DefaultSchema. Validation happens before any write.
    /// </summary>
    public async Task<BsonDocument> UpdateSettingsAsync(JsonElement data, CancellationToken ct)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw ApiError.Invalid("Invalid request body");

        bool? enabled = null;
        BsonArray? teamIds = null;
        int? rateLimit = null;
        int? maxLimit = null;

        if (Has(data, "partner_search_enabled"))
            enabled = data.RequiredBoolean("partner_search_enabled");

        if (Has(data, "partner_search_team_ids"))
            teamIds = ParseTeamIds(data.GetProperty("partner_search_team_ids"));

        if (Has(data, "partner_search_rate_limit_seconds"))
        {
            var value = data.RequiredInteger("partner_search_rate_limit_seconds");
            if (value < 0)
                throw ApiError.Field("partner_search_rate_limit_seconds",
                    "Must be greater than or equal to 0.");
            if (value > int.MaxValue)
                throw ApiError.Field("partner_search_rate_limit_seconds", "整数超出范围");
            rateLimit = (int)value;
        }

        if (Has(data, "partner_search_max_limit"))
        {
            var value = data.RequiredInteger("partner_search_max_limit");
            if (value < 1)
                throw ApiError.Field("partner_search_max_limit",
                    "Must be greater than or equal to 1.");
            if (value > int.MaxValue)
                throw ApiError.Field("partner_search_max_limit", "整数超出范围");
            maxLimit = (int)value;
        }

        var set = new BsonDocument();
        if (enabled.HasValue) set["pse"] = enabled.Value;
        if (teamIds is not null) set["pst"] = teamIds;
        if (rateLimit.HasValue) set["psq"] = rateLimit.Value;
        if (maxLimit.HasValue) set["psm"] = maxLimit.Value;

        await WriteSettingsAsync(set, ct).ConfigureAwait(false);
        return await GetSettingsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /v1/partner-search-query-entry. Order matters: settings/enabled,
    /// body parsing, then throttling (empty keyword and empty teams still count
    /// as one request), then the search itself.
    /// </summary>
    public async Task<BsonDocument> QueryAsync(JsonElement data, string? forwardedFor,
        string? remoteAddress, CancellationToken ct)
    {
        var settings = await ReadSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.Enabled)
            throw new ApiError("PartnerSearchDisabledError", 1, "partner search is disabled", 403);

        if (data.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw ApiError.Invalid("Invalid request body");
        var body = data.ValueKind == JsonValueKind.Object ? data : default;

        var rawKeyword = ReadKeyword(body);
        var limit = ResolveLimit(body, settings.MaxLimit);
        var page = ResolvePage(body);
        var clientKey = ReadClientKey(body);

        var sourceIp = ResolveSourceIp(forwardedFor, remoteAddress);
        var throttleKey = ThrottleKey(sourceIp, clientKey);
        if (!await CheckThrottleAsync(throttleKey, settings.RateLimitSeconds, ct).ConfigureAwait(false))
            throw new ApiError("PartnerSearchRateLimitedError", 2, "rate limited, please retry later", 429);

        var keyword = rawKeyword.Trim();
        if (keyword.Length == 0 || settings.TeamIds.Count == 0)
            return EmptyResult(page, limit);

        var normalized = SearchText.Normalize(keyword);
        var filter = new BsonDocument
        {
            ["t"] = new BsonDocument("$in", new BsonArray(settings.TeamIds.Select(value => (BsonValue)value))),
            ["st"] = ProjectStatusWorking,
            ["ns"] = new BsonRegularExpression(Regex.Escape(normalized), "i"),
        };

        var total = await Projects.CountDocumentsAsync(filter, cancellationToken: ct).ConfigureAwait(false);
        var skip = SafeSkip(page, limit);
        var rows = skip > int.MaxValue
            ? []
            : await Projects.Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Descending("et").Descending("_id"))
                .Skip((int)skip)
                .Limit(limit)
                .ToListAsync(ct).ConfigureAwait(false);

        var projectIds = rows
            .Where(row => row.TryGetValue("_id", out var id) && id.IsObjectId)
            .Select(row => row["_id"].AsObjectId)
            .ToList();
        var firstImages = await FirstImagesAsync(projectIds, ct).ConfigureAwait(false);
        var thumbnails = await Task.WhenAll(projectIds.Select(async id =>
            (Id: id, Url: firstImages.TryGetValue(id, out var file)
                ? await covers.TrySignAsync(file, ct).ConfigureAwait(false)
                : null))).ConfigureAwait(false);
        var thumbnailById = thumbnails.ToDictionary(item => item.Id, item => item.Url);

        var teams = await LoadTeamsAsync(rows, ct).ConfigureAwait(false);
        var projects = new BsonArray();
        foreach (var row in rows)
        {
            var id = row.TryGetValue("_id", out var rawId) && rawId.IsObjectId
                ? rawId.AsObjectId
                : ObjectId.Empty;
            var teamId = row.TryGetValue("t", out var rawTeam) && rawTeam.IsObjectId
                ? rawTeam.AsObjectId
                : (ObjectId?)null;
            var team = teamId is not null && teams.TryGetValue(teamId.Value, out var found) ? found : null;
            var thumbnail = thumbnailById.TryGetValue(id, out var url) ? url : null;

            projects.Add(new BsonDocument
            {
                ["id"] = id.ToString(),
                ["name"] = Text(row, "n"),
                ["source_name"] = NullableText(row, "sn"),
                ["target_name"] = NullableText(row, "tn"),
                ["status"] = Integer(row, "st"),
                ["intro"] = Text(row, "i"),
                ["team_id"] = teamId is null ? BsonNull.Value : teamId.Value.ToString(),
                ["team_name"] = team is null ? BsonNull.Value : NullableText(team, "n"),
                ["thumbnail_url"] = thumbnail is null ? BsonNull.Value : thumbnail,
            });
        }

        return new BsonDocument
        {
            ["total"] = total,
            ["page"] = page,
            ["limit"] = limit,
            ["projects"] = projects,
        };
    }

    /// <summary>
    /// Atomic throttle check. An expired timestamp is refreshed in one
    /// conditional update; a missing key is inserted and the unique _id makes
    /// exactly one concurrent request succeed.
    /// </summary>
    public async Task<bool> CheckThrottleAsync(string key, int seconds, CancellationToken ct)
    {
        if (seconds <= 0) return true;

        var boundedKey = BoundThrottleKey(key);
        var now = clock.GetUtcNow().UtcDateTime;
        var windowStart = now.AddSeconds(-seconds);
        var filter = new BsonDocument
        {
            ["_id"] = boundedKey,
            ["lt"] = new BsonDocument("$lte", windowStart),
        };
        var update = new BsonDocument("$set", new BsonDocument("lt", now));
        var result = await Throttle.UpdateOneAsync(filter, update, cancellationToken: ct).ConfigureAwait(false);
        if (result.MatchedCount > 0) return true;

        try
        {
            await Throttle.InsertOneAsync(new BsonDocument
            {
                ["_id"] = boundedKey,
                ["lt"] = now,
            }, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private async Task<SettingsSnapshot> ReadSettingsAsync(CancellationToken ct)
    {
        var row = await Settings.Find(new BsonDocument("n", "site"))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
            return new SettingsSnapshot(false, [], DefaultRateLimitSeconds, DefaultMaxLimit);

        var teamIds = new List<ObjectId>();
        if (row.TryGetValue("pst", out var rawTeams) && rawTeams.BsonType == BsonType.Array)
        {
            foreach (var item in rawTeams.AsBsonArray)
            {
                if (item.IsObjectId)
                {
                    teamIds.Add(item.AsObjectId);
                }
                else if (item.BsonType == BsonType.String &&
                         ObjectId.TryParse(item.AsString, out var parsed))
                {
                    teamIds.Add(parsed);
                }
            }
        }

        var enabled = row.TryGetValue("pse", out var rawEnabled) &&
                      rawEnabled.BsonType == BsonType.Boolean && rawEnabled.AsBoolean;
        // Legacy values are returned as stored: Python reads the raw int fields
        // and lets <=0 rate limits disable throttling and <=0 max limits fall
        // back to 1 at query time. Only missing/non-numeric fields default.
        var rateLimit = Integer(row, "psq", DefaultRateLimitSeconds);
        var maxLimit = Integer(row, "psm", DefaultMaxLimit);
        return new SettingsSnapshot(enabled, teamIds, rateLimit, maxLimit);
    }

    private async Task WriteSettingsAsync(BsonDocument set, CancellationToken ct)
    {
        await SettingWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var existing = await Settings.Find(new BsonDocument("n", "site"))
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (set.ElementCount == 0) return;
                    var result = await Settings.UpdateOneAsync(
                        new BsonDocument("_id", existing["_id"]),
                        new BsonDocument("$set", set),
                        cancellationToken: ct).ConfigureAwait(false);
                    if (result.MatchedCount > 0) return;
                    continue;
                }

                var created = new BsonDocument
                {
                    ["n"] = "site",
                    ["pse"] = false,
                    ["pst"] = new BsonArray(),
                    ["psq"] = DefaultRateLimitSeconds,
                    ["psm"] = DefaultMaxLimit,
                };
                foreach (var element in set)
                    created[element.Name] = element.Value;
                try
                {
                    await Settings.InsertOneAsync(created, cancellationToken: ct).ConfigureAwait(false);
                    return;
                }
                catch (MongoWriteException error) when (error.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    // Another host inserted the shared site document first.
                }
            }

            throw new InvalidOperationException("Unable to persist partner search settings.");
        }
        finally
        {
            SettingWriteGate.Release();
        }
    }

    private async Task<Dictionary<ObjectId, BsonDocument>> FirstImagesAsync(
        IReadOnlyList<ObjectId> projectIds, CancellationToken ct)
    {
        var result = new Dictionary<ObjectId, BsonDocument>();
        if (projectIds.Count == 0) return result;

        var ids = new BsonArray(projectIds.Select(value => (BsonValue)value));
        var match = new BsonDocument
        {
            ["p"] = new BsonDocument("$in", ids),
            ["ac"] = true,
            ["t"] = ImageType,
        };
        var manualMatch = match.DeepClone().AsBsonDocument;
        manualMatch["mo"] = new BsonDocument("$ne", BsonNull.Value);

        var manualRows = await Files.Aggregate<BsonDocument>(new BsonDocument[]
        {
            new("$match", manualMatch),
            new("$group", new BsonDocument("_id", "$p")),
        }).ToListAsync(ct).ConfigureAwait(false);
        var manualIds = manualRows
            .Where(row => row.TryGetValue("_id", out var id) && id.IsObjectId)
            .Select(row => row["_id"].AsObjectId)
            .ToHashSet();

        var plainIds = projectIds.Where(id => !manualIds.Contains(id)).ToList();
        var manualIdList = projectIds.Where(manualIds.Contains).ToList();

        await CollectFirstAsync(plainIds, new BsonDocument
        {
            ["dn"] = 1,
            ["t"] = 1,
            ["sn"] = 1,
            ["_id"] = 1,
        }, result, ct).ConfigureAwait(false);
        await CollectFirstAsync(manualIdList, new BsonDocument
        {
            ["dn"] = 1,
            ["t"] = 1,
            ["mo"] = 1,
            ["sn"] = 1,
            ["_id"] = 1,
        }, result, ct).ConfigureAwait(false);
        return result;
    }

    private async Task CollectFirstAsync(IReadOnlyList<ObjectId> ids, BsonDocument sort,
        Dictionary<ObjectId, BsonDocument> result, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var rows = await Files.Aggregate<BsonDocument>(new BsonDocument[]
        {
            new("$match", new BsonDocument
            {
                ["p"] = new BsonDocument("$in", new BsonArray(ids.Select(value => (BsonValue)value))),
                ["ac"] = true,
                ["t"] = ImageType,
            }),
            new("$sort", sort),
            new("$group", new BsonDocument
            {
                ["_id"] = "$p",
                ["file"] = new BsonDocument("$first", "$$ROOT"),
            }),
        }).ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (row.TryGetValue("_id", out var id) && id.IsObjectId &&
                row.TryGetValue("file", out var file) && file.IsBsonDocument)
                result[id.AsObjectId] = file.AsBsonDocument;
        }
    }

    private async Task<Dictionary<ObjectId, BsonDocument>> LoadTeamsAsync(
        IReadOnlyList<BsonDocument> projects, CancellationToken ct)
    {
        var ids = projects
            .Where(row => row.TryGetValue("t", out var team) && team.IsObjectId)
            .Select(row => row["t"].AsObjectId)
            .Distinct()
            .ToList();
        var result = new Dictionary<ObjectId, BsonDocument>();
        if (ids.Count == 0) return result;

        var rows = await Teams.Find(new BsonDocument("_id",
                new BsonDocument("$in", new BsonArray(ids.Select(value => (BsonValue)value)))))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (row.TryGetValue("_id", out var id) && id.IsObjectId)
                result[id.AsObjectId] = row;
        }
        return result;
    }

    private static BsonDocument ToApi(SettingsSnapshot settings) => new()
    {
        ["partner_search_enabled"] = settings.Enabled,
        ["partner_search_team_ids"] = new BsonArray(settings.TeamIds.Select(id => (BsonValue)id.ToString())),
        ["partner_search_rate_limit_seconds"] = settings.RateLimitSeconds,
        ["partner_search_max_limit"] = settings.MaxLimit,
    };

    private static BsonDocument EmptyResult(long page, int limit) => new()
    {
        ["total"] = 0L,
        ["page"] = page,
        ["limit"] = limit,
        ["projects"] = new BsonArray(),
    };

    private static bool Has(JsonElement data, string field) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out _);

    private static BsonArray ParseTeamIds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw ApiError.Field("partner_search_team_ids", "Not a valid list.");
        var ids = new BsonArray();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw ApiError.Field("partner_search_team_ids", "Must be a string.");
            if (!ObjectId.TryParse(item.GetString(), out var id))
                throw ApiError.Field("partner_search_team_ids", "Invalid ObjectId.");
            ids.Add(id);
        }
        return ids;
    }

    private static string ReadKeyword(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("keyword", out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.Null => "",
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.False => "",
            JsonValueKind.Number when value.TryGetDouble(out var number) && number == 0 => "",
            JsonValueKind.Array when value.GetArrayLength() == 0 => "",
            JsonValueKind.Object when !value.EnumerateObject().Any() => "",
            _ => throw ApiError.Invalid("keyword must be a string"),
        };
    }

    private static string ReadClientKey(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("client_key", out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.Null => "",
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.False => "",
            JsonValueKind.Number when value.TryGetDouble(out var number) && number == 0 => "",
            JsonValueKind.Array when value.GetArrayLength() == 0 => "",
            JsonValueKind.Object when !value.EnumerateObject().Any() => "",
            _ => value.GetRawText(),
        };
    }

    private static int ResolveLimit(JsonElement data, int configuredMax)
    {
        var fallback = Math.Max(1, configuredMax);
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("limit", out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return fallback;

        if (!TryCoerceInt(value, out var parsed))
            return fallback;
        if (parsed <= 0)
            return fallback;
        return Math.Max(1, Math.Min(parsed, fallback));
    }

    private static long ResolvePage(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("page", out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return 1;
        if (!TryCoerceLong(value, out var parsed))
            return 1;
        return Math.Max(1, parsed);
    }

    private static bool TryCoerceInt(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.True) { result = 1; return true; }
        if (value.ValueKind == JsonValueKind.False) { result = 0; return true; }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer))
            {
                result = integer > int.MaxValue ? int.MaxValue
                    : integer < int.MinValue ? int.MinValue
                    : (int)integer;
                return true;
            }
            if (value.TryGetDecimal(out var decimalNumber))
            {
                if (decimalNumber >= int.MaxValue) result = int.MaxValue;
                else if (decimalNumber <= int.MinValue) result = int.MinValue;
                else result = (int)decimal.Truncate(decimalNumber);
                return true;
            }
            if (value.TryGetDouble(out var doubleNumber) && !double.IsNaN(doubleNumber))
            {
                result = doubleNumber >= int.MaxValue ? int.MaxValue
                    : doubleNumber <= int.MinValue ? int.MinValue
                    : (int)Math.Truncate(doubleNumber);
                return true;
            }
            return false;
        }
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var textNumber))
        {
            result = textNumber > int.MaxValue ? int.MaxValue
                : textNumber < int.MinValue ? int.MinValue
                : (int)textNumber;
            return true;
        }
        return false;
    }

    private static bool TryCoerceLong(JsonElement value, out long result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.True) { result = 1; return true; }
        if (value.ValueKind == JsonValueKind.False) { result = 0; return true; }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out result)) return true;
            if (value.TryGetDouble(out var number) && !double.IsNaN(number))
            {
                result = number >= long.MaxValue ? long.MaxValue
                    : number <= long.MinValue ? long.MinValue
                    : (long)Math.Truncate(number);
                return true;
            }
            return false;
        }
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out result))
            return true;
        return false;
    }

    private static long SafeSkip(long page, int limit)
    {
        if (page <= 1) return 0;
        if (limit <= 0) return 0;
        return page - 1 > long.MaxValue / limit ? long.MaxValue : (page - 1) * limit;
    }

    private static string ResolveSourceIp(string? forwardedFor, string? remoteAddress)
    {
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }
        var remote = remoteAddress?.Trim();
        return string.IsNullOrEmpty(remote) ? "unknown" : remote;
    }

    private static string ThrottleKey(string ip, string clientKey) =>
        clientKey.Length == 0 ? $"ip:{ip}" : $"ip:{ip}:key:{clientKey}";

    private static string BoundThrottleKey(string key)
    {
        if (key.Length <= MaxThrottleKeyLength) return key;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return key[..150] + "#" + hash[..32];
    }

    private static string Text(BsonDocument document, string field, string fallback = "") =>
        document.TryGetValue(field, out var value) && value.BsonType == BsonType.String
            ? value.AsString
            : fallback;

    private static string? NullableText(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.BsonType == BsonType.String
            ? value.AsString
            : null;

    private static int Integer(BsonDocument document, string field, int fallback = 0)
    {
        if (!document.TryGetValue(field, out var value)) return fallback;
        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 when value.AsInt64 is >= int.MinValue and <= int.MaxValue => (int)value.AsInt64,
            BsonType.Double when value.AsDouble is >= int.MinValue and <= int.MaxValue => (int)value.AsDouble,
            BsonType.Decimal128 when value.ToDecimal() is >= int.MinValue and <= int.MaxValue =>
                (int)value.ToDecimal(),
            _ => fallback,
        };
    }

    private sealed record SettingsSnapshot(
        bool Enabled, IReadOnlyList<ObjectId> TeamIds, int RateLimitSeconds, int MaxLimit);
}

/// <summary>
/// Minimal cover URL adapter for this module. It always uses the default object
/// storage (matching Python's cover_url) and only signs an object that exists.
/// </summary>
public sealed class PartnerSearchCoverUrl
{
    internal const string FilePrefix = "files/";
    internal const string CoverProcessName = "cover";
    private static readonly TimeSpan Validity = TimeSpan.FromHours(1);

    private readonly ObjectStorage storage;
    private readonly ILogger<PartnerSearchCoverUrl> logger;

    public PartnerSearchCoverUrl(ObjectStorage storage, ILogger<PartnerSearchCoverUrl> logger)
    {
        this.storage = storage;
        this.logger = logger;
    }

    public async Task<string?> TrySignAsync(BsonDocument file, CancellationToken ct)
    {
        if (!file.TryGetValue("sa", out var saveNameValue) ||
            saveNameValue.BsonType != BsonType.String ||
            saveNameValue.AsString.Length == 0)
            return null;
        if (!file.TryGetValue("th", out var thumbnailValue) ||
            !IsSucceeded(thumbnailValue))
            return null;

        var saveName = saveNameValue.AsString;
        var dot = saveName.LastIndexOf('.');
        var stem = dot >= 0 ? saveName[..dot] : saveName;
        if (stem.Length == 0) return null;

        var key = FilePrefix + CoverProcessName + "-" + stem + ".webp";
        try
        {
            var info = await storage.HeadAsync(key, ct).ConfigureAwait(false);
            if (info is null) return null;
            return await storage.SignGetAsync(key, Validity).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Partner search cover probe failed for {Key}", key);
            return null;
        }
    }

    private static bool IsSucceeded(BsonValue value) => value.BsonType switch
    {
        BsonType.Int32 => value.AsInt32 == PartnerSearchService.ThumbnailSucceeded,
        BsonType.Int64 => value.AsInt64 == PartnerSearchService.ThumbnailSucceeded,
        BsonType.Double => Math.Abs(value.AsDouble - PartnerSearchService.ThumbnailSucceeded) < double.Epsilon,
        _ => false,
    };
}
