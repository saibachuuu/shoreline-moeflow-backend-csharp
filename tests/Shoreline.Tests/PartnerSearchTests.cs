using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Modules.PartnerSearch;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class PartnerSearchTests(LocalServices services)
{
    [Fact]
    public async Task Settings_defaults_and_partial_update_preserve_core_fields()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.Store.Collection("site_setting").InsertOneAsync(new BsonDocument
        {
            ["n"] = "site",
            ["title"] = "Shoreline",
            ["mail"] = new BsonDocument("enabled", true),
        });

        var view = await runtime.Service.GetSettingsAsync(default);
        Assert.Equal(4, view.ElementCount);
        Assert.False(view["partner_search_enabled"].AsBoolean);
        Assert.Empty(view["partner_search_team_ids"].AsBsonArray);
        Assert.Equal(10, view["partner_search_rate_limit_seconds"].AsInt32);
        Assert.Equal(20, view["partner_search_max_limit"].AsInt32);

        var updated = await runtime.Service.UpdateSettingsAsync(Body(
            """{"partner_search_enabled":true,"partner_search_rate_limit_seconds":30,"unknown_key":"ignored"}"""),
            default);
        Assert.True(updated["partner_search_enabled"].AsBoolean);
        Assert.Equal(30, updated["partner_search_rate_limit_seconds"].AsInt32);
        Assert.Equal(20, updated["partner_search_max_limit"].AsInt32);

        var document = await runtime.Store.Collection("site_setting")
            .Find(new BsonDocument("n", "site")).FirstAsync();
        Assert.Equal("Shoreline", document["title"].AsString);
        Assert.True(document["mail"].AsBsonDocument["enabled"].AsBoolean);
        Assert.True(document["pse"].AsBoolean);
        Assert.Equal(30, document["psq"].AsInt32);
        Assert.False(document.Contains("partner_search_rate_limit_seconds"));
        Assert.Equal(1, await runtime.Store.Collection("site_setting")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Settings_partial_update_validates_only_known_fields()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var invalid = new[]
        {
            """{"partner_search_rate_limit_seconds":-1}""",
            """{"partner_search_max_limit":0}""",
            """{"partner_search_max_limit":-5}""",
            """{"partner_search_team_ids":"not-a-list"}""",
            """{"partner_search_team_ids":["not-an-object-id"]}""",
            """{"partner_search_team_ids":[5]}""",
            """{"partner_search_enabled":null}""",
        };
        foreach (var json in invalid)
        {
            var error = await Assert.ThrowsAsync<ApiError>(() =>
                runtime.Service.UpdateSettingsAsync(Body(json), default));
            Assert.Equal(400, error.Status);
            Assert.Equal(2, error.Code);
            Assert.Equal("ValidateError", error.Error);
        }

        var view = await runtime.Service.UpdateSettingsAsync(Body("""{"unknown_field":123}"""), default);
        Assert.False(view["partner_search_enabled"].AsBoolean);
        Assert.Empty(view["partner_search_team_ids"].AsBsonArray);
        Assert.Equal(10, view["partner_search_rate_limit_seconds"].AsInt32);
        Assert.Equal(20, view["partner_search_max_limit"].AsInt32);
    }

    [Fact]
    public async Task Settings_legacy_document_is_read_without_migration()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var team = ObjectId.GenerateNewId();
        await runtime.Store.Collection("site_setting").InsertOneAsync(new BsonDocument
        {
            ["n"] = "site",
            ["pse"] = "true",
            ["pst"] = new BsonArray { team.ToString(), team, 3, "bad" },
            ["legacy"] = new BsonDocument("keep", true),
        });

        var view = await runtime.Service.GetSettingsAsync(default);
        Assert.False(view["partner_search_enabled"].AsBoolean);
        Assert.Equal(2, view["partner_search_team_ids"].AsBsonArray.Count);
        Assert.All(view["partner_search_team_ids"].AsBsonArray,
            value => Assert.Equal(team.ToString(), value.AsString));
        Assert.Equal(10, view["partner_search_rate_limit_seconds"].AsInt32);
        Assert.Equal(20, view["partner_search_max_limit"].AsInt32);

        var document = await runtime.Store.Collection("site_setting")
            .Find(new BsonDocument("n", "site")).FirstAsync();
        Assert.Equal("true", document["pse"].AsString);
        Assert.Equal(4, document["pst"].AsBsonArray.Count);
        Assert.True(document["legacy"].AsBsonDocument["keep"].AsBoolean);
    }

    [Fact]
    public async Task Settings_legacy_numeric_values_are_preserved_and_bounded()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("legacy numeric");
        runtime.InsertProject(team, "legacy match");
        await runtime.Store.Collection("site_setting").InsertOneAsync(new BsonDocument
        {
            ["n"] = "site",
            ["pse"] = true,
            ["pst"] = new BsonArray { team },
            ["psq"] = -5,
            ["psm"] = 0,
        });

        var view = await runtime.Service.GetSettingsAsync(default);
        Assert.Equal(-5, view["partner_search_rate_limit_seconds"].AsInt32);
        Assert.Equal(0, view["partner_search_max_limit"].AsInt32);

        var result = await runtime.Service.QueryAsync(Body("""{"keyword":"legacy"}"""),
            null, "127.0.0.1", default);
        Assert.Equal(1L, result["total"].ToInt64());
        Assert.Equal(1, result["limit"].AsInt32);
        Assert.Equal(0L, await runtime.Store.Collection("partner_search_throttle")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Disabled_query_returns_403_before_body_parsing()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.SetSettingsAsync(enabled: false);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Service.QueryAsync(Body("[1,2,3]"), "1.2.3.4", "127.0.0.1", default));
        Assert.Equal(403, error.Status);
        Assert.Equal(1, error.Code);
        Assert.Equal("PartnerSearchDisabledError", error.Error);
        Assert.Equal("partner search is disabled", error.Message);
    }

    [Fact]
    public async Task Empty_keyword_and_empty_teams_still_consume_one_throttle_slot()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.SetSettingsAsync(enabled: true, teams: [], rateLimitSeconds: 10);

        var first = await runtime.Service.QueryAsync(default, null, "127.0.0.1", default);
        Assert.Equal(0, first["total"].ToInt64());
        Assert.Equal(1, first["page"].ToInt64());
        Assert.Equal(20, first["limit"].AsInt32);
        Assert.Empty(first["projects"].AsBsonArray);

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Service.QueryAsync(default, null, "127.0.0.1", default));
        Assert.Equal(429, error.Status);
        Assert.Equal(2, error.Code);
        Assert.Equal("PartnerSearchRateLimitedError", error.Error);
        Assert.Equal("rate limited, please retry later", error.Message);
    }

    [Fact]
    public async Task Limit_and_page_follow_python_quirks_without_overflow()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("limits");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);

        var cases = new (string Json, int Limit, long Page)[]
        {
            ("{}", 20, 1),
            ("""{"limit":0}""", 20, 1),
            ("""{"limit":-3}""", 20, 1),
            ("""{"limit":"abc"}""", 20, 1),
            ("""{"limit":"7"}""", 7, 1),
            ("""{"limit":50}""", 20, 1),
            ("""{"limit":true}""", 1, 1),
            ("""{"limit":2.9}""", 2, 1),
            ("""{"page":0}""", 20, 1),
            ("""{"page":"3"}""", 20, 3),
            ("""{"page":2.8}""", 20, 2),
            ("""{"page":"x"}""", 20, 1),
            ("""{"page":true}""", 20, 1),
            ("""{"page":9223372036854775807,"limit":20}""", 20, long.MaxValue),
        };
        foreach (var item in cases)
        {
            var result = await runtime.Service.QueryAsync(Body(item.Json), null, "127.0.0.1", default);
            Assert.Equal(item.Limit, result["limit"].AsInt32);
            Assert.Equal(item.Page, result["page"].ToInt64());
            Assert.Empty(result["projects"].AsBsonArray);
        }
    }

    [Fact]
    public async Task Search_uses_nfc_casefold_and_literal_regex()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("unicode");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);
        runtime.InsertProject(team, "Straße");
        runtime.InsertProject(team, "ς", normalizedName: "σ");
        runtime.InsertProject(team, "axb");

        var german = await runtime.Service.QueryAsync(Body("""{"keyword":"STRASSE"}"""),
            null, "127.0.0.1", default);
        Assert.Equal(1, german["total"].ToInt64());
        Assert.Equal("Straße", german["projects"].AsBsonArray[0].AsBsonDocument["name"].AsString);

        var greek = await runtime.Service.QueryAsync(Body("""{"keyword":"Σ"}"""),
            null, "127.0.0.1", default);
        Assert.Equal(1, greek["total"].ToInt64());
        Assert.Equal("ς", greek["projects"].AsBsonArray[0].AsBsonDocument["name"].AsString);

        var literal = await runtime.Service.QueryAsync(Body("""{"keyword":"a.b"}"""),
            null, "127.0.0.1", default);
        Assert.Equal(0, literal["total"].ToInt64());
    }

    [Fact]
    public async Task Search_filters_to_configured_teams_and_working_status()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var allowed = runtime.InsertTeam("Allowed Team");
        var other = runtime.InsertTeam("Other Team");
        await runtime.SetSettingsAsync(enabled: true, teams: [allowed], rateLimitSeconds: 0);

        var visible = runtime.InsertProject(allowed, "match working");
        runtime.InsertProject(allowed, "match completed", status: 5);
        runtime.InsertProject(allowed, "match cleared", status: 1);
        runtime.InsertProject(other, "match other team");

        var result = await runtime.Service.QueryAsync(Body("""{"keyword":"match"}"""),
            null, "127.0.0.1", default);
        Assert.Equal(1, result["total"].ToInt64());
        var project = result["projects"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal(visible.ToString(), project["id"].AsString);
        Assert.Equal(allowed.ToString(), project["team_id"].AsString);
        Assert.Equal("Allowed Team", project["team_name"].AsString);
        Assert.Equal("source", project["source_name"].AsString);
        Assert.Equal("target", project["target_name"].AsString);
        Assert.Equal("intro", project["intro"].AsString);
        Assert.Equal(0, project["status"].AsInt32);
    }

    [Fact]
    public async Task Search_orders_by_edit_time_then_id_descending()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("order");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);
        var now = DateTime.UtcNow;
        var first = runtime.InsertProject(team, "order keyword", editTime: now.AddSeconds(-20));
        var second = runtime.InsertProject(team, "order keyword", editTime: now.AddSeconds(-10));
        var third = runtime.InsertProject(team, "order keyword", editTime: now.AddSeconds(-10));

        var result = await runtime.Service.QueryAsync(Body("""{"keyword":"order"}"""),
            null, "127.0.0.1", default);
        var ids = result["projects"].AsBsonArray
            .Select(value => value.AsBsonDocument["id"].AsString).ToArray();
        Assert.Equal(new[] { third.ToString(), second.ToString(), first.ToString() }, ids);
    }

    [Fact]
    public async Task Search_sorts_by_m_e_then_id_when_id_order_conflicts_with_time()
    {
        // Python: Project.objects.order_by("-edit_time", "-id"). Project.edit_time
        // is GroupMixin.edit_time with db_field="m_e" (app/core/rbac.py:259), not
        // the project-set field "et". Explicit ids make the two sort keys disagree:
        // low id has the newest edit time, so time must win; the tie is then broken
        // by _id descending.
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("order m_e");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);
        var now = DateTime.UtcNow;
        var newestLowId = ObjectId.Parse("000000000000000000000010");
        var olderHighId = ObjectId.Parse("000000000000000000000020");
        var tieHigherId = ObjectId.Parse("000000000000000000000030");
        runtime.InsertProject(team, "order keyword", editTime: now, id: newestLowId);
        runtime.InsertProject(team, "order keyword", editTime: now.AddSeconds(-10), id: olderHighId);
        runtime.InsertProject(team, "order keyword", editTime: now.AddSeconds(-10), id: tieHigherId);

        var result = await runtime.Service.QueryAsync(Body("""{"keyword":"order"}"""),
            null, "127.0.0.1", default);
        var ids = result["projects"].AsBsonArray
            .Select(value => value.AsBsonDocument["id"].AsString).ToArray();
        Assert.Equal(new[] { newestLowId.ToString(), tieHigherId.ToString(), olderHighId.ToString() }, ids);
    }

    [Fact]
    public async Task Search_ignores_project_set_et_timestamp()
    {
        // Regression for the old bug: the query used the project-set key "et"
        // (ProjectSet.edit_time, db_field="et") instead of the project key "m_e".
        // The synthetic "et" values below are reversed against m_e; sorting by
        // "et" would move the older project to the front.
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("order et");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);
        var now = DateTime.UtcNow;
        var newer = runtime.InsertProject(team, "order keyword", editTime: now,
            setEditTime: now.AddSeconds(-100));
        var older = runtime.InsertProject(team, "order keyword", editTime: now.AddSeconds(-10),
            setEditTime: now.AddSeconds(100));

        var result = await runtime.Service.QueryAsync(Body("""{"keyword":"order"}"""),
            null, "127.0.0.1", default);
        var ids = result["projects"].AsBsonArray
            .Select(value => value.AsBsonDocument["id"].AsString).ToArray();
        Assert.Equal(new[] { newer.ToString(), older.ToString() }, ids);
    }

    [Fact]
    public async Task Thumbnail_requires_succeeded_cover_and_first_page_images()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("thumbs");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);

        var good = runtime.InsertProject(team, "thumb good", editTime: DateTime.UtcNow.AddSeconds(-5));
        runtime.InsertImage(good, "good.png");
        runtime.InsertImage(good, "inactive.png", active: false, sortName: "0");
        await runtime.PutCoverAsync("good");

        var generating = runtime.InsertProject(team, "thumb generating", editTime: DateTime.UtcNow.AddSeconds(-4));
        runtime.InsertImage(generating, "generating.png", thumbnailStatus: 2);
        await runtime.PutCoverAsync("generating");

        var missing = runtime.InsertProject(team, "thumb missing", editTime: DateTime.UtcNow.AddSeconds(-3));
        runtime.InsertImage(missing, "missing.png");

        var empty = runtime.InsertProject(team, "thumb empty", editTime: DateTime.UtcNow.AddSeconds(-2));

        var pooled = runtime.InsertProject(team, "thumb pooled", editTime: DateTime.UtcNow.AddSeconds(-1),
            csid: ObjectId.GenerateNewId());
        runtime.InsertImage(pooled, "pooled.png");
        await runtime.PutCoverAsync("pooled");

        var result = await runtime.Service.QueryAsync(Body("""{"keyword":"thumb"}"""),
            null, "127.0.0.1", default);
        var projects = result["projects"].AsBsonArray
            .Select(value => value.AsBsonDocument)
            .ToDictionary(value => value["name"].AsString, value => value["thumbnail_url"]);
        Assert.Contains("cover-good.webp", projects["thumb good"].AsString);
        Assert.True(projects["thumb generating"].IsBsonNull);
        Assert.True(projects["thumb missing"].IsBsonNull);
        Assert.True(projects["thumb empty"].IsBsonNull);
        Assert.Contains("cover-pooled.webp", projects["thumb pooled"].AsString);
    }

    [Fact]
    public async Task Thumbnail_manual_order_overrides_sort_name()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var team = runtime.InsertTeam("manual");
        await runtime.SetSettingsAsync(enabled: true, teams: [team], rateLimitSeconds: 0);
        var project = runtime.InsertProject(team, "manual thumb");
        runtime.InsertImage(project, "alpha.png", sortName: "a", manualOrder: 2);
        runtime.InsertImage(project, "bravo.png", sortName: "b", manualOrder: 1);
        await runtime.PutCoverAsync("alpha");
        await runtime.PutCoverAsync("bravo");

        var result = await runtime.Service.QueryAsync(Body("""{"keyword":"manual"}"""),
            null, "127.0.0.1", default);
        var url = result["projects"].AsBsonArray[0].AsBsonDocument["thumbnail_url"].AsString;
        Assert.Contains("cover-bravo.webp", url);
    }

    [Fact]
    public async Task Throttle_is_disabled_and_writes_nothing_for_non_positive_window()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.SetSettingsAsync(enabled: true, teams: [], rateLimitSeconds: 0);

        await runtime.Service.QueryAsync(default, null, "127.0.0.1", default);
        await runtime.Service.QueryAsync(default, null, "127.0.0.1", default);
        Assert.Equal(0, await runtime.Store.Collection("partner_search_throttle")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Throttle_expires_only_at_the_window_boundary()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.SetSettingsAsync(enabled: true, teams: [], rateLimitSeconds: 10);

        await runtime.Service.QueryAsync(default, null, "127.0.0.1", default);
        await Assert429(runtime, default);
        runtime.Clock.Advance(TimeSpan.FromSeconds(9));
        await Assert429(runtime, default);
        runtime.Clock.Advance(TimeSpan.FromSeconds(1));
        await runtime.Service.QueryAsync(default, null, "127.0.0.1", default);
        await Assert429(runtime, default);

        Assert.Equal(1, await runtime.Store.Collection("partner_search_throttle")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Throttle_concurrent_requests_allow_exactly_one()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.SetSettingsAsync(enabled: true, teams: [], rateLimitSeconds: 10);

        var tasks = Enumerable.Range(0, 24).Select(async _ =>
        {
            try
            {
                await runtime.Service.QueryAsync(default, null, "127.0.0.1", default);
                return true;
            }
            catch (ApiError error) when (error.Status == 429)
            {
                return false;
            }
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(allowed => allowed));
        Assert.Equal(1, await runtime.Store.Collection("partner_search_throttle")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Throttle_key_uses_forwarded_ip_and_client_key()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.SetSettingsAsync(enabled: true, teams: [], rateLimitSeconds: 10);

        await runtime.Service.QueryAsync(Body("""{"client_key":"abc"}"""),
            "1.2.3.4, 9.9.9.9", "10.0.0.1", default);
        var throttles = runtime.Store.Collection("partner_search_throttle");
        var row = await throttles.Find(new BsonDocument("_id", "ip:1.2.3.4:key:abc")).FirstOrDefaultAsync();
        Assert.NotNull(row);
        await Assert429(runtime, Body("""{"client_key":"abc"}"""), "1.2.3.4, 9.9.9.9");

        await runtime.Service.QueryAsync(Body("""{"client_key":"abc"}"""), "5.6.7.8", null, default);
        await runtime.Service.QueryAsync(Body("""{"client_key":"xyz"}"""), "1.2.3.4", null, default);
        await runtime.Service.QueryAsync(default, "", "10.0.0.2", default);
        Assert.NotNull(await throttles.Find(new BsonDocument("_id", "ip:5.6.7.8:key:abc")).FirstOrDefaultAsync());
        Assert.NotNull(await throttles.Find(new BsonDocument("_id", "ip:1.2.3.4:key:xyz")).FirstOrDefaultAsync());
        Assert.NotNull(await throttles.Find(new BsonDocument("_id", "ip:10.0.0.2")).FirstOrDefaultAsync());
    }

    [Fact]
    public async Task Settings_first_insert_is_single_and_concurrent_safe()
    {
        using var runtime = new PartnerSearchRuntime(services);
        await runtime.Store.Collection("site_setting").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("n"),
                new CreateIndexOptions { Unique = true }));

        var updates = Enumerable.Range(0, 10).Select(index =>
            runtime.Service.UpdateSettingsAsync(Body(index % 2 == 0
                ? """{"partner_search_rate_limit_seconds":30}"""
                : """{"partner_search_max_limit":25}"""), default));
        await Task.WhenAll(updates);

        Assert.Equal(1, await runtime.Store.Collection("site_setting")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var document = await runtime.Store.Collection("site_setting")
            .Find(new BsonDocument("n", "site")).FirstAsync();
        Assert.True(document.Contains("psq"));
        Assert.True(document.Contains("psm"));
        Assert.True(document.Contains("pse"));
        Assert.True(document.Contains("pst"));
    }

    [Fact]
    public async Task Settings_api_exposes_exactly_the_four_long_names()
    {
        using var runtime = new PartnerSearchRuntime(services);
        var view = await runtime.Service.GetSettingsAsync(default);
        Assert.Equal(
            new[] { "partner_search_enabled", "partner_search_max_limit",
             "partner_search_rate_limit_seconds", "partner_search_team_ids" },
            view.Names.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private static JsonElement Body(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task Assert429(PartnerSearchRuntime runtime, JsonElement body,
        string? forwardedFor = null, string? remote = "127.0.0.1")
    {
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            runtime.Service.QueryAsync(body, forwardedFor, remote, default));
        Assert.Equal(429, error.Status);
        Assert.Equal(2, error.Code);
        Assert.Equal("PartnerSearchRateLimitedError", error.Error);
        Assert.Equal("rate limited, please retry later", error.Message);
    }
}

internal sealed class PartnerSearchClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;

    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan delta) => current += delta;
}

internal sealed class PartnerSearchRuntime : IDisposable
{
    public MongoStore Store { get; }
    public ObjectStorage Storage { get; }
    public PartnerSearchClock Clock { get; }
    public PartnerSearchService Service { get; }

    public PartnerSearchRuntime(LocalServices services)
    {
        var baseUri = services.Configuration["Mongo:Uri"]
            ?? throw new InvalidOperationException("Mongo:Uri is required for partner-search tests");
        var databaseName = "shoreline_partner_search_test_" + Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:Uri"] = new MongoUrlBuilder(baseUri) { DatabaseName = databaseName }
                    .ToMongoUrl().ToString(),
            })
            .Build();
        Store = new MongoStore(configuration);
        Storage = new ObjectStorage(services.Configuration);
        Clock = new PartnerSearchClock(DateTimeOffset.UtcNow);
        Service = new PartnerSearchService(Store,
            new PartnerSearchCoverUrl(Storage, NullLogger<PartnerSearchCoverUrl>.Instance), Clock);
    }

    public ObjectId InsertTeam(string name)
    {
        var id = ObjectId.GenerateNewId();
        Store.Collection("team").InsertOne(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["i"] = "",
        });
        return id;
    }

    public ObjectId InsertProject(ObjectId team, string name, int status = 0,
        DateTime? editTime = null, string? normalizedName = null, ObjectId? csid = null,
        ObjectId? id = null, DateTime? setEditTime = null)
    {
        var projectId = id ?? ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = name,
            ["ns"] = normalizedName ?? SearchText.Normalize(name),
            ["i"] = "intro",
            ["sn"] = "source",
            ["tn"] = "target",
            ["t"] = team,
            ["st"] = status,
            // Project edit time is m_e (GroupMixin), matching ProjectService.cs
            // create (:227) and update (:395). The old fixture wrote "et", which
            // masked the service sorting by the project-set key by accident.
            ["m_e"] = editTime ?? DateTime.UtcNow,
        };
        // Synthetic project-set timestamp, only used by the regression test that
        // proves the query ignores "et". Real project documents do not have it.
        if (setEditTime is not null) document["et"] = setEditTime.Value;
        if (csid is not null) document["csid"] = csid.Value;
        Store.Collection("project").InsertOne(document);
        return projectId;
    }

    public ObjectId InsertImage(ObjectId project, string saveName, int thumbnailStatus = 3,
        bool active = true, string sortName = "a", string dirSortName = "a",
        int? manualOrder = null)
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["p"] = project,
            ["n"] = saveName,
            ["t"] = 2,
            ["dn"] = dirSortName,
            ["sn"] = sortName,
            ["sa"] = saveName,
            ["th"] = thumbnailStatus,
            ["ac"] = active,
        };
        if (manualOrder is not null) document["mo"] = manualOrder.Value;
        Store.Collection("file").InsertOne(document);
        return id;
    }

    public async Task PutCoverAsync(string stem)
    {
        await Storage.PutAsync("files/cover-" + stem + ".webp",
            new MemoryStream(Encoding.UTF8.GetBytes("cover")), "image/webp", default);
    }

    public Task SetSettingsAsync(bool enabled, ObjectId[]? teams = null,
        int rateLimitSeconds = 10, int maxLimit = 20) =>
        Store.Collection("site_setting").UpdateOneAsync(
            new BsonDocument("n", "site"),
            new BsonDocument("$set", new BsonDocument
            {
                ["n"] = "site",
                ["pse"] = enabled,
                ["pst"] = new BsonArray((teams ?? []).Select(id => (BsonValue)id)),
                ["psq"] = rateLimitSeconds,
                ["psm"] = maxLimit,
            }),
            new UpdateOptions { IsUpsert = true });

    public void Dispose()
    {
        Storage.Dispose();
        Store.Database.Client.DropDatabase(Store.Database.DatabaseNamespace.DatabaseName);
    }
}

