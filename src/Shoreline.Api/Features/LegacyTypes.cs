using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Features;

public sealed class LegacyTypes(MongoStore store, TimeProvider clock)
{
    private static readonly Catalog Definitions = ReadDefinitions();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        foreach (var scope in new[] { "team", "project" })
        {
            var rows = Roles(scope);
            var seeds = store.Collection("csharp_seed");
            var seedId = scope + "-roles-v1";
            var marker = await seeds.Find(new BsonDocument("_id", seedId)).FirstOrDefaultAsync(ct);
            if (marker is null && await rows.Find(FilterDefinition<BsonDocument>.Empty).Limit(1).AnyAsync(ct))
            {
                marker = await seeds.Find(new BsonDocument("_id", seedId)).FirstOrDefaultAsync(ct);
                if (marker is null) continue; // Existing databases keep their role IDs and customization.
            }
            if (marker?.Flag("complete") == true) continue;
            await seeds.UpdateOneAsync(new BsonDocument("_id", seedId),
                new BsonDocument("$setOnInsert", new BsonDocument("complete", false)), new UpdateOptions { IsUpsert = true }, ct);
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("m_o", 1),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "csharp_system_role_code",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("m_s", true)
                }), cancellationToken: ct);
            foreach (var definition in Group(scope).Roles)
            {
                var filter = new BsonDocument { ["m_o"] = definition.SystemCode, ["m_s"] = true };
                var role = new BsonDocument
                {
                    ["m_n"] = definition.Name,
                    ["m_l"] = definition.Level,
                    ["m_i"] = "",
                    ["m_p"] = new BsonArray(definition.Permissions),
                    ["m_s"] = true,
                    ["m_o"] = definition.SystemCode,
                    ["m_c"] = clock.GetUtcNow().UtcDateTime
                };
                try
                {
                    await rows.UpdateOneAsync(filter, new BsonDocument("$setOnInsert", role), new UpdateOptions { IsUpsert = true }, ct);
                }
                catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    if (!await rows.Find(filter).AnyAsync(ct)) throw;
                }
            }
            await seeds.UpdateOneAsync(new BsonDocument("_id", seedId), new BsonDocument("$set", new BsonDocument("complete", true)), cancellationToken: ct);
        }
    }

    public async Task<object[]> SystemRolesAsync(string scope, bool withCreator, bool chinese, CancellationToken ct)
    {
        var filter = new BsonDocument("m_s", true);
        if (!withCreator) filter["m_o"] = new BsonDocument("$ne", "creator");
        var roles = await Roles(scope).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        return roles.Select(role => RoleView(scope, role, chinese)).ToArray();
    }

    public object RoleView(string scope, BsonDocument role, bool chinese)
    {
        var definition = Group(scope).Roles.FirstOrDefault(row => row.Name == role.Text("m_n"));
        var ids = role.GetValue("m_p", new BsonArray()).AsBsonArray.Select(value => value.ToInt32()).ToHashSet();
        return new
        {
            id = role["_id"].ToString(),
            name = !chinese && role.Flag("m_s") && definition is not null ? definition.LocalizedEn : role.Text("m_n"),
            level = role.Integer("m_l"),
            system = role.Flag("m_s"),
            system_code = role.GetValue("m_o", BsonNull.Value).IsString ? role.Text("m_o") : null,
            permissions = Group(scope).Permissions.Where(row => ids.Contains(row.Id.GetInt32())).Select(row => TypeView(row, chinese)).ToArray(),
            create_time = role.Timestamp("m_c")
        };
    }

    public object[] Types(string type, string scope, bool chinese)
    {
        var rows = type switch
        {
            "locale" => Definitions.Locales,
            "permission" => Group(scope).Permissions,
            "allow-apply-type" => Group(scope).AllowApplyTypes,
            "application-check-type" => Group(scope).ApplicationCheckTypes,
            _ => []
        };
        return rows.Select(row => TypeView(row, chinese)).ToArray();
    }

    public async Task<BsonDocument> SystemRoleAsync(string scope, string code, CancellationToken ct) =>
        await Roles(scope).Find(new BsonDocument { ["m_s"] = true, ["m_o"] = code }).FirstOrDefaultAsync(ct)
        ?? throw new ApiError("RoleNotExistError", 102, "角色不存在");

    public async Task<BsonDocument> DefaultRoleAsync(string scope, string id, ObjectId? group, CancellationToken ct)
    {
        var role = await Roles(scope).Find(new BsonDocument("_id", BsonFields.Id(id))).FirstOrDefaultAsync(ct)
            ?? throw ApiError.Field("default_role", "角色不存在");
        if (role.Text("m_o") == "creator" || (!role.Flag("m_s") && (group is null || role.GetValue("g", BsonNull.Value) != new BsonObjectId(group.Value))))
            throw ApiError.Field("default_role", "默认角色必须是该团体的非创建人角色");
        return role;
    }

    private IMongoCollection<BsonDocument> Roles(string scope) => store.Collection(scope switch
    {
        "team" => "team_role",
        "project" => "project_role",
        _ => throw InvalidGroup()
    });
    private static GroupDefinitions Group(string scope) => scope switch { "team" => Definitions.Team, "project" => Definitions.Project, _ => throw InvalidGroup() };
    private static ApiError InvalidGroup() => new("RequestDataWrongError", 111,
        "Request parameter error.: 'group_type' not in ['team', 'project']");
    private static object TypeView(TypeDefinition row, bool chinese) => new
    {
        id = row.Id,
        name = chinese ? row.Name : row.LocalizedEn,
        intro = chinese ? row.Intro : row.IntroEn
    };
    private static Catalog ReadDefinitions()
    {
        using var stream = typeof(LegacyTypes).Assembly.GetManifestResourceStream("Shoreline.SystemTypes")
            ?? throw new InvalidOperationException("Missing system types resource.");
        return JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
    }
    private sealed record Catalog(GroupDefinitions Team, GroupDefinitions Project, TypeDefinition[] Locales);
    private sealed record GroupDefinitions(TypeDefinition[] Permissions, RoleDefinition[] Roles, TypeDefinition[] AllowApplyTypes, TypeDefinition[] ApplicationCheckTypes);
    private sealed record TypeDefinition(string Code, JsonElement Id, string Name, string Intro, string LocalizedEn, string IntroEn);
    private sealed record RoleDefinition(string Name, string LocalizedEn, string SystemCode, int Level, int[] Permissions);
}

public static class TypeEndpoints
{
    public static void MapTypes(this WebApplication app)
    {
        app.MapGet("/v1/types/{typeName}", async (string typeName, HttpContext ctx, LegacyTypes types, CancellationToken ct) =>
        {
            var chinese = LanguageCatalog.ChinesePreferred(ctx.Request.GetTypedHeaders().AcceptLanguage);
            if (typeName == "locale") return Results.Json(types.Types(typeName, "", chinese));
            var scope = ctx.Request.Query["group_type"].ToString();
            if (scope is not ("team" or "project"))
                throw new ApiError("RequestDataWrongError", 111,
                    (chinese ? "请求参数错误: " : "Request parameter error.: ") + "'group_type' not in ['team', 'project']");
            if (typeName is not ("permission" or "allow-apply-type" or "application-check-type" or "system-role")) return Results.NoContent();
            var raw = ctx.Request.Query["with_creator"].ToString();
            var withCreator = raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            return Results.Json(typeName == "system-role" ? await types.SystemRolesAsync(scope, withCreator, chinese, ct) : types.Types(typeName, scope, chinese));
        });
    }
}
