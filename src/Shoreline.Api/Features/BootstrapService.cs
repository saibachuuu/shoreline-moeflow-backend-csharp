using System.Text.Json;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Api.Features;

/// <summary>
/// Port of Python <c>init_db</c> (app/factory.py): system roles, the single site
/// setting document, the configured default administrator and, on a database
/// with no teams, the bootstrap "Default Team".
///
/// This runs only when <c>Bootstrap:Enabled</c> is true. Program.cs defaults it
/// to true outside the test environment; tests opt in explicitly. Every step is
/// idempotent so a restart after a partial failure converges instead of
/// duplicating rows.
/// </summary>
public sealed class BootstrapService(MongoStore store, SiteSettings settings, PasswordService passwords,
    LegacyTypes types, TeamMemberService members, IdentityNameGate names, IConfiguration configuration,
    TimeProvider clock, ILogger<BootstrapService> logger)
{
    // Python's create_default_team() defaults. The name intentionally contains a
    // space: Team.create() bypasses verify_new_name just like the Python path.
    public const string DefaultTeamName = "Default Team";
    public const string DefaultTeamIntro =
        "All new users will automatically join this team. This can be disabled by site administrator.\n" +
        "        所有新用户会自动加入此团队，如不需要，站点管理员可以在“站点管理-自动加入的团队 ID”中删除此团队 ID。";

    public async Task RunAsync(CancellationToken ct)
    {
        // Roles must exist before the default role is resolved; the language and
        // public type catalogs are initialized by Program before this call.
        await types.InitializeAsync(ct);
        var setting = await settings.EnsureAsync(ct);
        var admin = await EnsureAdminAsync(ct);
        if (admin is null) return;
        await EnsureDefaultTeamAsync(admin, setting, ct);
    }

    private async Task<BsonDocument?> EnsureAdminAsync(CancellationToken ct)
    {
        var email = (configuration["Auth:AdminEmail"] ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0)
            throw new InvalidOperationException("Auth:AdminEmail must be set before bootstrap");

        var users = store.Collection("user");
        var existing = await users.Find(new BsonDocument("e", email)).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (!existing.Flag("admin"))
            {
                await users.UpdateOneAsync(new BsonDocument("_id", existing["_id"]),
                    new BsonDocument("$set", new BsonDocument("admin", true)), cancellationToken: ct);
                existing["admin"] = true;
                logger.LogInformation("Granted administrator to existing account {Email}", email);
            }
            return existing;
        }

        var initial = (configuration["Auth:AdminInitialPassword"] ?? "").Trim();
        if (initial.Length == 0)
            throw new InvalidOperationException(
                "Auth:AdminInitialPassword must be set before creating the default admin");

        var hash = await passwords.CreateAsync(initial, ct);
        using (await names.EnterAsync("user", "Admin", ct))
        {
            // Re-check under the shared name gate: another instance may have
            // committed the account after the lookup above.
            existing = await users.Find(new BsonDocument("e", email)).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                if (!existing.Flag("admin"))
                {
                    await users.UpdateOneAsync(new BsonDocument("_id", existing["_id"]),
                        new BsonDocument("$set", new BsonDocument("admin", true)), cancellationToken: ct);
                    existing["admin"] = true;
                }
                return existing;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var user = new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["e"] = email, ["n"] = "Admin",
                ["as"] = new BsonArray(), ["ns"] = SearchText.Normalize("Admin"), ["asrch"] = new BsonArray(),
                ["ddn"] = "", ["s"] = "", ["l"] = "auto", ["t"] = "", ["a"] = "",
                ["b"] = false, ["p"] = hash, ["admin"] = true, ["c"] = now
            };
            try
            {
                await users.InsertOneAsync(user, cancellationToken: ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                var byEmail = await users.Find(new BsonDocument("e", email)).FirstOrDefaultAsync(ct);
                if (byEmail is null) throw;
                if (!byEmail.Flag("admin"))
                {
                    await users.UpdateOneAsync(new BsonDocument("_id", byEmail["_id"]),
                        new BsonDocument("$set", new BsonDocument("admin", true)), cancellationToken: ct);
                    byEmail["admin"] = true;
                }
                return byEmail;
            }
            logger.LogInformation("Created bootstrap administrator {Email}", email);
            return user;
        }
    }

    private async Task EnsureDefaultTeamAsync(BsonDocument admin, BsonDocument setting, CancellationToken ct)
    {
        if (await store.Collection("team").Find(FilterDefinition<BsonDocument>.Empty).Limit(1).AnyAsync(ct))
            return;
        // Python also only creates the default team when it is the only role
        // seed step? No: it checks Team.objects().count() == 0, exactly as above.
        var memberRole = await types.SystemRoleAsync("team", "member", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var teamId = ObjectId.GenerateNewId();
        var team = new BsonDocument
        {
            ["_id"] = teamId,
            ["n"] = DefaultTeamName,
            ["a"] = "",
            ["i"] = DefaultTeamIntro,
            ["dr"] = memberRole["_id"],
            ["u"] = 100000,
            ["m_u"] = 100000, // GroupMixin.max_user
            ["m_uc"] = 0,
            ["m_a"] = 2, // AllowApplyType.ALL
            ["m_t"] = 2, // ApplicationCheckType.ADMIN_CHECK
            ["m_c"] = now,
            ["m_e"] = now,
            ["wqm"] = "qualified",
            ["slp"] = BsonNull.Value,
            ["pme"] = true,
            ["pmo"] = 92,
            ["om"] = 0,
            ["ou"] = 0,
            ["og"] = 0,
            ["ot"] = 0,
            ["aak"] = new BsonArray(),
            ["aau"] = ""
        };
        var teams = store.Collection("team");
        team = await teams.FindOneAndUpdateAsync(new BsonDocument("_id", teamId),
            new BsonDocument("$setOnInsert", team),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);

        var sets = store.Collection("project_set");
        if (!await sets.Find(new BsonDocument { ["t"] = teamId, ["d"] = true }).AnyAsync(ct))
        {
            await sets.UpdateOneAsync(new BsonDocument("_id", ObjectId.GenerateNewId()),
                new BsonDocument("$setOnInsert", new BsonDocument
                {
                    ["n"] = "default", ["i"] = "", ["t"] = teamId, ["d"] = true, ["ct"] = now, ["et"] = now
                }), new UpdateOptions { IsUpsert = true }, ct);
        }

        await members.JoinTrustedAsync(team, admin, "creator", "bootstrap-default-team", ct);

        var autoJoin = new BsonArray { new BsonObjectId(teamId) };
        await store.Collection("site_setting").UpdateOneAsync(new BsonDocument("n", "site"),
            new BsonDocument("$set", new BsonDocument("ajt", autoJoin)), cancellationToken: ct);
        logger.LogInformation("Created bootstrap Default Team {TeamId}", teamId);
    }
}

/// <summary>
/// Legacy index/diagnostic routes that the Python app registers at the root.
/// They are not business endpoints but are part of the public route contract.
/// </summary>
public static class LegacyDiagnosticsEndpoints
{
    public static void MapLegacyDiagnostics(this WebApplication app)
    {
        app.MapGet("/error", () =>
        {
            // Python raises ValueError("invalid literal for int() ...").
            _ = int.Parse("1.2");
            return Results.Ok();
        });

        app.MapGet("/warning", () =>
        {
            throw new System.ComponentModel.WarningException("test warning");
        });

        app.MapGet("/urls", (EndpointDataSource endpoints) =>
        {
            var output = new System.Text.StringBuilder("<table>");
            foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
            {
                var methods = string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>());
                var route = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
                var html = $"<tr><td>{System.Net.WebUtility.HtmlEncode(endpoint.DisplayName)}</td><td>{System.Net.WebUtility.HtmlEncode(methods)}</td><td>{System.Net.WebUtility.HtmlEncode(route)}</td></tr>";
                output.Append(html);
            }
            output.Append("</table>");
            return Results.Content(output.ToString(), "text/html; charset=utf-8");
        });

        app.MapGet("/docs/{**path}", (string? path, IConfiguration configuration) =>
        {
            var root = configuration["LegacyDocs:Root"];
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return Results.NotFound();
            var rootFull = Path.GetFullPath(root);
            var requested = Path.GetFullPath(Path.Combine(rootFull, path));
            if (!requested.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return Results.NotFound();
            if (!File.Exists(requested)) return Results.NotFound();
            var contentType = Path.GetExtension(requested).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            return Results.File(requested, contentType);
        });
    }
}
