using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Modules.ZitengPartner;
using Shoreline.Api.Security;
using Xunit;

namespace Shoreline.Tests;

/// <summary>
/// Direct and HTTP coverage for the Ziteng partner port. Golden title/matching
/// vectors are produced by executing the Python source through AST; the fixtures
/// must never be edited to fit the C# implementation.
/// </summary>
public sealed class ZitengPartnerTests
{
    private static string MongoUri => "mongodb://127.0.0.1:" + TestEnvironment.MongoPort + "/";
    private static readonly string Password = PasswordHash.Create("test-password");

    [Fact]
    public void Title_MatchesPythonGoldenVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        foreach (var item in document.RootElement.GetProperty("title_cases").EnumerateArray())
        {
            var input = item.GetProperty("input").GetString()!;
            Assert.Equal(item.GetProperty("expected").GetString(), ZitengPartnerTitle.Extract(input));
            Assert.Equal(item.GetProperty("searchable").GetBoolean(), ZitengPartnerText.IsSearchable(input));
        }
    }

    [Fact]
    public void Matching_MatchesPythonGoldenVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        foreach (var item in document.RootElement.GetProperty("score_cases").EnumerateArray())
        {
            var work = WorkFrom(item.GetProperty("candidate"));
            var (level, score) = ZitengPartnerMatching.Score(
                item.GetProperty("project_title").GetString(), work);
            Assert.Equal(item.GetProperty("level").GetString(), level);
            Assert.Equal(item.GetProperty("score").GetDouble(), score, 12);
            Assert.Equal(item.GetProperty("round3").GetDouble(),
                ZitengPartnerMatching.RoundScore(score), 12);
        }

        foreach (var item in document.RootElement.GetProperty("find_suspects_cases").EnumerateArray())
        {
            var candidates = item.GetProperty("candidates").EnumerateArray()
                .Select(WorkFrom).ToList();
            var actual = ZitengPartnerMatching.FindSuspects(
                item.GetProperty("project_title").GetString(),
                candidates,
                item.GetProperty("limit").GetInt32());
            var expected = item.GetProperty("expected").EnumerateArray().ToList();

            Assert.Equal(expected.Count, actual.Count);
            for (var index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expected[index].GetProperty("id").GetString(), actual[index].Work.Id);
                Assert.Equal(expected[index].GetProperty("level").GetString(), actual[index].Level);
                Assert.Equal(expected[index].GetProperty("round3").GetDouble(),
                    ZitengPartnerMatching.RoundScore(actual[index].Score), 12);
                Assert.Equal(expected[index].GetProperty("state_rank").GetInt32(), actual[index].StateRank);
            }
        }
    }

    [Fact]
    public void Settings_ReadLegacyOverridesAndNeverExposeKey()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ZitengPartner:ApiKey"] = "section-key",
            ["ZitengPartner:MaxResults"] = "3",
            ["ZITENG_PARTNER_API_KEY"] = "  legacy-key  ",
            ["ZITENG_PARTNER_MAX_PAGES"] = "7",
            ["ZITENG_PARTNER_INCLUDE_INACTIVE"] = "true",
            ["ZITENG_PARTNER_TIMEOUT"] = "9.5",
        }).Build();

        var settings = ZitengPartnerSettings.FromConfiguration(configuration);
        Assert.Equal("legacy-key", settings.ApiKey);
        Assert.Equal(7, settings.MaxPages);
        Assert.Equal(3, settings.MaxResults);
        Assert.True(settings.IncludeInactive);
        Assert.Equal(9.5, settings.TimeoutSeconds, 12);
        Assert.True(settings.Enabled);
        Assert.DoesNotContain("legacy-key", settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-key",
            settings.Redact("Authorization: Bearer legacy-key"), StringComparison.Ordinal);

        var disabled = ZitengPartnerSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ZitengPartner:ApiKey"] = "section-key",
                ["ZITENG_PARTNER_API_KEY"] = "",
            }).Build());
        Assert.Equal("", disabled.ApiKey);
        Assert.False(disabled.Enabled);
        Assert.Equal(ZitengPartnerSettings.DefaultApiUrl, disabled.ApiUrl);
    }

    [Fact]
    public async Task Store_CreatesExpectedIndexesAndUniqueKeys()
    {
        await using var world = await DirectWorld.CreateAsync();
        var workIndexes = await ListDocumentsAsync(
            await world.Store.Collection(ZitengPartnerStore.WorkCollection).Indexes.ListAsync());
        Assert.Contains(workIndexes, index => index["name"].AsString == "work_id_1");
        Assert.Contains(workIndexes, index => index["name"].AsString == "state_1");

        var queryIndexes = await ListDocumentsAsync(
            await world.Store.Collection(ZitengPartnerStore.QueryCollection).Indexes.ListAsync());
        Assert.Contains(queryIndexes, index => index["name"].AsString == "keyword_1");
        Assert.All(queryIndexes, index => Assert.False(index.Contains("expireAfterSeconds")));

        var checkIndexes = await ListDocumentsAsync(
            await world.Store.Collection(ZitengPartnerStore.CheckCollection).Indexes.ListAsync());
        Assert.Contains(checkIndexes, index => index["name"].AsString == "project_id_1");
        Assert.Contains(checkIndexes, index => index["name"].AsString == "status_1");

        await world.Store.Collection(ZitengPartnerStore.WorkCollection)
            .InsertOneAsync(new BsonDocument("work_id", "duplicate"));
        await Assert.ThrowsAsync<MongoWriteException>(() =>
            world.Store.Collection(ZitengPartnerStore.WorkCollection)
                .InsertOneAsync(new BsonDocument("work_id", "duplicate")));
    }

    [Fact]
    public async Task Cache_PreservesOrderSkipsLostWorksAndExpires()
    {
        await using var world = await DirectWorld.CreateAsync();
        var works = new[] { Work("a"), Work("b"), Work("c") };
        await world.PartnerStore.StoreAsync("作品名", works, 9, true);

        var cached = await world.PartnerStore.GetCachedAsync("作品名", 604800);
        Assert.True(cached.HasValue);
        Assert.Equal(new[] { "a", "b", "c" }, cached.Value.Works.Select(item => item.Id));
        Assert.True(cached.Value.Truncated);

        await world.Store.Collection(ZitengPartnerStore.WorkCollection)
            .DeleteOneAsync(new BsonDocument("work_id", "b"));
        var partial = await world.PartnerStore.GetCachedAsync("作品名", 604800);
        Assert.True(partial.HasValue);
        Assert.Equal(new[] { "a", "c" }, partial.Value.Works.Select(item => item.Id));

        world.Clock.Now = world.Clock.Now.AddSeconds(604799);
        Assert.True((await world.PartnerStore.GetCachedAsync("作品名", 604800)).HasValue);
        world.Clock.Now = world.Clock.Now.AddSeconds(2);
        Assert.False((await world.PartnerStore.GetCachedAsync("作品名", 604800)).HasValue);
    }

    [Fact]
    public async Task ApiGate_SpacesRequestsAcrossInstancesWithoutSleeping()
    {
        await using var world = await DirectWorld.CreateAsync();
        var start = world.Clock.Now;
        var delay = new RecordingDelay();
        var first = new ZitengPartnerApiGate(world.Store, world.Settings, world.Clock, delay);
        var second = new ZitengPartnerApiGate(world.Store, world.Settings, world.Clock, delay);

        await first.WaitAsync(CancellationToken.None);
        Assert.Empty(delay.Delays);

        await second.WaitAsync(CancellationToken.None);
        var wait = Assert.Single(delay.Delays);
        Assert.InRange(wait.TotalSeconds, 1.099, 1.101);

        world.Clock.Now = start.AddSeconds(2.2);
        await first.WaitAsync(CancellationToken.None);
        Assert.Single(delay.Delays);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(cancelled.Token));
    }

    [Fact]
    public async Task Client_PagesCarryRevisionWarnAboutTruncationAndSetHeaders()
    {
        await using var world = await DirectWorld.CreateAsync(maxPages: 2);
        var handler = new FakeHandler((request, call, _) => Task.FromResult(call == 1
            ? JsonResponse("""
                {"revision":"rev-1","total":"7","next_page":true,"results":[
                  {"id":1,"reference":null,"display_title":"D1","original_title":"O1",
                   "author":null,"circle":"C","state":"published","stage":"final"}]}
                """)
            : JsonResponse("""
                {"total":7,"next_page":true,"results":[
                  {"id":"2","reference":"R2","display_title":"D2","original_title":"O2",
                   "author":"A","circle":"C","state":"in_progress","stage":"final"}]}
                """)));
        var client = world.Client(handler);

        var result = await client.SearchAsync("作品");

        Assert.Equal("作品", result.Query);
        Assert.Equal(7, result.Total);
        Assert.True(result.Truncated);
        Assert.Equal(new[] { "1", "2" }, result.Works.Select(item => item.Id));
        Assert.Equal("", result.Works[0].Reference);
        Assert.Equal("A", result.Works[1].Author);
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain("revision=", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("revision=rev-1", handler.Requests[1].RequestUri!.Query);
        Assert.Equal("Bearer test-key", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Contains(handler.Requests[0].Headers.Accept, header => header.MediaType == "application/json");
        Assert.Equal("MoeFlow/ZitengPartnerSearch", handler.Requests[0].Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Client_Retries429OnceAndCapsRetryAfter()
    {
        await using var world = await DirectWorld.CreateAsync();
        var delay = new RecordingDelay();
        var handler = new FakeHandler((_, call, _) => Task.FromResult(call == 1
            ? JsonResponse("{}", (HttpStatusCode)429, "Infinity")
            : JsonResponse(EmptyPayload)));
        var client = world.Client(handler, delay);

        await client.SearchAsync("作品");

        Assert.Equal(2, handler.Calls);
        var wait = Assert.Single(delay.Delays);
        Assert.True(wait > TimeSpan.Zero);
        Assert.True(wait <= TimeSpan.FromSeconds(180));

        var limited = new FakeHandler((_, _, _) =>
            Task.FromResult(JsonResponse("{}", (HttpStatusCode)429)));
        var error = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(limited).SearchAsync("作品"));
        Assert.Contains("被限流", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, limited.Calls);
    }

    [Fact]
    public async Task Client_RestartsWholeRoundOn409AndFailsWhenItPersists()
    {
        await using var world = await DirectWorld.CreateAsync();
        var handler = new FakeHandler((_, call, _) => Task.FromResult(call == 1
            ? JsonResponse("{}", (HttpStatusCode)409)
            : JsonResponse("""
                {"results":[{"id":"after","original_title":"作品"}],"total":1,"next_page":false}
                """)));
        var result = await world.Client(handler).SearchAsync("作 品");
        Assert.Equal(2, handler.Calls);
        Assert.Contains("page=1", handler.Requests[1].RequestUri!.Query);
        Assert.Single(result.Works);

        var persistent = new FakeHandler((_, _, _) =>
            Task.FromResult(JsonResponse("{}", (HttpStatusCode)409)));
        var error = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(persistent).SearchAsync("作品"));
        Assert.Contains("索引持续变化", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, persistent.Calls);
    }

    [Fact]
    public async Task Client_RejectsInvalidInputResponsesAndMissingKey()
    {
        await using var world = await DirectWorld.CreateAsync();
        var emptyQuery = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(EmptyPayload))))
                .SearchAsync("   "));
        Assert.Equal("检索词为空", emptyQuery.Message);

        var tooLong = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(EmptyPayload))))
                .SearchAsync(new string('あ', 241)));
        Assert.Contains("240", tooLong.Message, StringComparison.Ordinal);

        var server = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(new FakeHandler((_, _, _) =>
                Task.FromResult(JsonResponse("{}", HttpStatusCode.InternalServerError))))
                .SearchAsync("作品"));
        Assert.Equal("对方服务不可用: HTTP 500", server.Message);

        var rejected = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(new FakeHandler((_, _, _) =>
                Task.FromResult(JsonResponse("secret provider body", HttpStatusCode.Forbidden))))
                .SearchAsync("作品"));
        Assert.Equal("请求被拒绝: HTTP 403", rejected.Message);
        Assert.DoesNotContain("secret provider body", rejected.Message, StringComparison.Ordinal);

        var invalidJson = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(new FakeHandler((_, _, _) => Task.FromResult(JsonResponse("{oops"))))
                .SearchAsync("作品"));
        Assert.Equal("返回内容不是合法 JSON", invalidJson.Message);

        var invalidShape = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
            world.Client(new FakeHandler((_, _, _) =>
                Task.FromResult(JsonResponse("""{"results":{"a":1}}"""))))
                .SearchAsync("作品"));
        Assert.Equal("返回内容格式不合法", invalidShape.Message);

        var noKey = await DirectWorld.CreateAsync(apiKey: "");
        try
        {
            var missing = await Assert.ThrowsAsync<ZitengPartnerApiException>(() =>
                noKey.Client(new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(EmptyPayload))))
                    .SearchAsync("作品"));
            Assert.Equal("未配置 API 密钥", missing.Message);
        }
        finally
        {
            await noKey.DisposeAsync();
        }
    }

    [Fact]
    public async Task Store_ClaimIsExclusiveUntilLeaseExpiresAndGenerationFencesWrites()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId().ToString();
        await world.PartnerStore.EnsureCheckAsync(projectId, "作品名");
        var generation = (await world.PartnerStore.ResetForRetryAsync(projectId))!.Value;
        var lease = world.Clock.GetUtcNow().UtcDateTime.AddMinutes(1);

        Assert.True(await world.PartnerStore.ClaimAsync(projectId, generation, "owner-a", lease));
        Assert.False(await world.PartnerStore.ClaimAsync(projectId, generation, "owner-b", lease));
        Assert.False(await world.PartnerStore.CompleteAsync(projectId, generation, "owner-b",
            ZitengPartnerStore.VerdictClear, [], CancellationToken.None));
        Assert.True(await world.PartnerStore.CompleteAsync(projectId, generation, "owner-a",
            ZitengPartnerStore.VerdictClear, [], CancellationToken.None));

        var next = (await world.PartnerStore.ResetForRetryAsync(projectId))!.Value;
        Assert.False(await world.PartnerStore.ClaimAsync(projectId, generation, "owner-a", lease));
        Assert.True(await world.PartnerStore.ClaimAsync(projectId, next, "owner-a", lease));
        Assert.False(await world.PartnerStore.CompleteAsync(projectId, generation, "owner-a",
            ZitengPartnerStore.VerdictClear, [], CancellationToken.None));
    }

    [Fact]
    public async Task Request_EnqueuesWithoutNetworkAndAdvancesGeneration()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId().ToString();
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("HTTP must not run"));
        var service = world.Service(handler);

        var first = await service.RequestCheckAsync(projectId,
            "【12】[社团] 作品名 (シリーズ) [DL版]");
        Assert.NotNull(first);
        Assert.Equal("作品名 (シリーズ)", first.Keyword);
        Assert.Equal(1, first.Generation);
        Assert.Equal(ZitengPartnerStore.StatusQueued, first.Status);
        Assert.Equal(0, handler.Calls);

        var job = await world.Store.Collection("csharp_job")
            .Find(new BsonDocument("kind", ZitengPartnerService.JobKind))
            .FirstAsync();
        Assert.Equal(projectId, job["payload"]["projectId"].AsString);
        Assert.Equal(1L, job["payload"]["generation"].ToInt64());

        var second = await service.RequestCheckAsync(projectId, "作品名 (シリーズ)");
        Assert.Equal(2, second!.Generation);
        Assert.False(await world.PartnerStore.ClaimAsync(projectId, 1, "old-worker",
            world.Clock.GetUtcNow().UtcDateTime.AddMinutes(1)));
    }

    [Fact]
    public async Task Run_MarksApiFailureAsFailedNeverClear()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId().ToString();
        await world.PartnerStore.EnsureCheckAsync(projectId, "作品名");
        var generation = (await world.PartnerStore.ResetForRetryAsync(projectId))!.Value;
        var handler = new FakeHandler((_, _, _) =>
            Task.FromResult(JsonResponse("{}", HttpStatusCode.BadGateway)));
        var service = world.Service(handler);

        await service.RunAsync(projectId, generation, "worker");

        var check = await world.PartnerStore.GetCheckAsync(projectId);
        Assert.NotNull(check);
        Assert.Equal(ZitengPartnerStore.StatusFailed, check.Status);
        Assert.Equal(ZitengPartnerStore.VerdictFailed, check.Verdict);
        Assert.Equal("对方服务不可用: HTTP 502", check.Error);
    }

    [Fact]
    public async Task Run_EmptyResultIsClearAndNotSearchableTitleSkipsNetwork()
    {
        await using var world = await DirectWorld.CreateAsync();
        var emptyProject = ObjectId.GenerateNewId().ToString();
        await world.PartnerStore.EnsureCheckAsync(emptyProject, "作品名");
        var emptyGeneration = (await world.PartnerStore.ResetForRetryAsync(emptyProject))!.Value;
        var emptyHandler = new FakeHandler((_, _, _) =>
            Task.FromResult(JsonResponse(EmptyPayload)));
        await world.Service(emptyHandler).RunAsync(emptyProject, emptyGeneration, "worker");
        var empty = await world.PartnerStore.GetCheckAsync(emptyProject);
        Assert.Equal(ZitengPartnerStore.StatusSucceeded, empty!.Status);
        Assert.Equal(ZitengPartnerStore.VerdictClear, empty.Verdict);
        Assert.Empty(empty.Suspects);

        var punctuationProject = ObjectId.GenerateNewId().ToString();
        await world.PartnerStore.EnsureCheckAsync(punctuationProject, "！！！");
        var punctuationGeneration = (await world.PartnerStore.ResetForRetryAsync(punctuationProject))!.Value;
        var punctuationHandler = new FakeHandler((_, _, _) => throw new InvalidOperationException("must not call"));
        await world.Service(punctuationHandler)
            .RunAsync(punctuationProject, punctuationGeneration, "worker");
        var punctuation = await world.PartnerStore.GetCheckAsync(punctuationProject);
        Assert.Equal(ZitengPartnerStore.VerdictClear, punctuation!.Verdict);
        Assert.Equal(0, punctuationHandler.Calls);
    }

    [Fact]
    public async Task Run_UsesFreshCacheWithoutCallingPartner()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId().ToString();
        await world.PartnerStore.EnsureCheckAsync(projectId, "作品名 (シリーズ)");
        var generation = (await world.PartnerStore.ResetForRetryAsync(projectId))!.Value;
        await world.PartnerStore.StoreAsync("作品名 (シリーズ)", [Work("cached", "作品名")], 1, false);
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("must use cache"));

        await world.Service(handler).RunAsync(projectId, generation, "worker");

        var check = await world.PartnerStore.GetCheckAsync(projectId);
        Assert.Equal(ZitengPartnerStore.StatusSucceeded, check!.Status);
        Assert.Equal(ZitengPartnerStore.VerdictSuspected, check.Verdict);
        Assert.Equal("cached", Assert.Single(check.Suspects)["id"].AsString);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Run_RereadsRenamedProjectForMatchingButKeepsQueryKeyword()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId();
        await world.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "新しい作品名",
        });
        await world.PartnerStore.EnsureCheckAsync(projectId.ToString(), "古い作品名");
        var generation = (await world.PartnerStore.ResetForRetryAsync(projectId.ToString()))!.Value;
        var handler = new FakeHandler((request, _, _) =>
        {
            Assert.Contains("q=" + Uri.EscapeDataString("古い作品名"),
                request.RequestUri!.Query, StringComparison.Ordinal);
            return Task.FromResult(JsonResponse("""
                {"results":[{"id":"renamed","original_title":"新しい作品名"}],"total":1,"next_page":false}
                """));
        });

        await world.Service(handler).RunAsync(projectId.ToString(), generation, "worker");

        var check = await world.PartnerStore.GetCheckAsync(projectId.ToString());
        Assert.Equal(ZitengPartnerStore.VerdictSuspected, check!.Verdict);
        Assert.Equal("新しい作品名", Assert.Single(check.Suspects)["original_title"].AsString);
    }

    [Fact]
    public async Task Run_MissingOrStaleGenerationIsIgnored()
    {
        await using var world = await DirectWorld.CreateAsync();
        var service = world.Service(new FakeHandler((_, _, _) =>
            Task.FromResult(JsonResponse(EmptyPayload))));

        await service.RunAsync(ObjectId.GenerateNewId().ToString(), 0, "worker");

        var projectId = ObjectId.GenerateNewId().ToString();
        await world.PartnerStore.EnsureCheckAsync(projectId, "作品名");
        var generation = (await world.PartnerStore.ResetForRetryAsync(projectId))!.Value;
        await service.RunAsync(projectId, generation + 1, "worker");
        var check = await world.PartnerStore.GetCheckAsync(projectId);
        Assert.Equal(ZitengPartnerStore.StatusQueued, check!.Status);
    }

    [Fact]
    public async Task ProjectCreatedNotifier_EnqueuesCurrentGenerationWithoutNetwork()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId();
        await world.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "【7】[社团] 作品名 (シリーズ)",
        });
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("must not call"));
        var service = world.Service(handler);
        var notifier = new ZitengPartnerProjectCreatedNotifier(service,
            NullLogger<ZitengPartnerProjectCreatedNotifier>.Instance);

        await notifier.TryNotifyAsync(projectId.ToString());

        var check = await world.PartnerStore.GetCheckAsync(projectId.ToString());
        Assert.NotNull(check);
        Assert.Equal("作品名 (シリーズ)", check.Keyword);
        var job = await world.Store.Collection("csharp_job")
            .Find(new BsonDocument("kind", ZitengPartnerService.JobKind))
            .FirstAsync();
        Assert.Equal(check.Generation, job["payload"]["generation"].ToInt64());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ProjectCreatedHook_InvokesNotifierAndEnqueuesCheck()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId();
        await world.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "【7】[社团] 作品名 (シリーズ)",
        });
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("must not call"));
        var service = world.Service(handler);
        var notifier = new ZitengPartnerProjectCreatedNotifier(service,
            NullLogger<ZitengPartnerProjectCreatedNotifier>.Instance);
        var hook = new ZitengPartnerProjectCreatedHook(notifier);

        await hook.OnProjectCreatedAsync(projectId, CancellationToken.None);

        var check = await world.PartnerStore.GetCheckAsync(projectId.ToString());
        Assert.NotNull(check);
        Assert.Equal("作品名 (シリーズ)", check.Keyword);
        var job = await world.Store.Collection("csharp_job")
            .Find(new BsonDocument("kind", ZitengPartnerService.JobKind))
            .FirstAsync();
        Assert.Equal(check.Generation, job["payload"]["generation"].ToInt64());
    }

    [Fact]
    public async Task Store_ReadProjectNameReturnsEmptyWhenDeleted()
    {
        await using var world = await DirectWorld.CreateAsync();
        var projectId = ObjectId.GenerateNewId();
        await world.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = projectId,
            ["n"] = "作品名",
        });
        Assert.Equal("作品名", await world.PartnerStore.ReadProjectNameAsync(projectId.ToString()));
        await world.Store.Collection("project").DeleteOneAsync(new BsonDocument("_id", projectId));
        Assert.Equal("", await world.PartnerStore.ReadProjectNameAsync(projectId.ToString()));
        Assert.Equal("", await world.PartnerStore.ReadProjectNameAsync("not-an-object-id"));
    }

    [Fact]
    public async Task Http_ConfigRequiresTokenAndReportsEnabledState()
    {
        await using var world = await HttpWorld.CreateAsync(enabled: true, maxResults: 3);
        var anonymous = await world.App.CreateClient()
            .GetAsync("/v1/ziteng-partner/config");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var response = await world.Http.GetAsync("/v1/ziteng-partner/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, body.RootElement.GetProperty("max_results").GetInt32());

        var alias = await world.Http.GetAsync("/v1/config/ziteng-partner");
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
    }

    [Fact]
    public async Task Http_DisabledKeyStillServesRoutesAndFailsCheckExplicitly()
    {
        await using var world = await HttpWorld.CreateAsync(enabled: false);
        var config = await world.Http.GetAsync("/v1/ziteng-partner/config");
        using var configBody = JsonDocument.Parse(await config.Content.ReadAsStringAsync());
        Assert.False(configBody.RootElement.GetProperty("enabled").GetBoolean());

        var before = await world.Http.GetAsync($"/v1/projects/{world.ProjectId}/ziteng-check");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var beforeBody = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, beforeBody.RootElement.GetProperty("check").ValueKind);

        var post = await world.Http.PostAsync(
            $"/v1/projects/{world.ProjectId}/ziteng-check",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var check = await WaitForCheckAsync(world, status: ZitengPartnerStore.StatusFailed);
        Assert.Equal(ZitengPartnerStore.VerdictFailed, check.Text("verdict"));
        Assert.Contains("API 密钥", check.Text("error"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_ForeignUserCannotReadOrTriggerProjectCheck()
    {
        await using var world = await HttpWorld.CreateAsync();
        var response = await world.ForeignHttp.GetAsync(
            $"/v1/projects/{world.ProjectId}/ziteng-check");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(103, body.RootElement.GetProperty("code").GetInt32());

        var post = await world.ForeignHttp.PostAsync(
            $"/v1/projects/{world.ProjectId}/ziteng-check",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Http_PostReturnsPublicCheckShapeWithoutPrivateFields()
    {
        await using var world = await HttpWorld.CreateAsync();
        var post = await world.Http.PostAsync(
            $"/v1/projects/{world.ProjectId}/ziteng-check",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        var check = body.RootElement.GetProperty("check");
        Assert.NotEqual(JsonValueKind.Null, check.ValueKind);
        Assert.Equal(world.ProjectId.ToString(), check.GetProperty("project_id").GetString());
        Assert.Equal("作品名 (シリーズ)", check.GetProperty("keyword").GetString());
        Assert.InRange(check.GetProperty("status").GetInt32(), 0, 3);
        foreach (var publicField in new[]
                 {
                     "project_id", "keyword", "status", "verdict", "suspects",
                     "suspect_count", "error", "updated_at",
                 })
            Assert.True(check.TryGetProperty(publicField, out _), publicField);
        foreach (var privateField in new[]
                 {
                     "generation", "lease_owner", "lease_until", "created_at", "document",
                 })
            Assert.False(check.TryGetProperty(privateField, out _), privateField);

        var read = await world.Http.GetAsync($"/v1/projects/{world.ProjectId}/ziteng-check");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Http_MissingProjectUsesLegacyProjectNotExistCode()
    {
        await using var world = await HttpWorld.CreateAsync();
        var response = await world.Http.GetAsync(
            $"/v1/projects/{ObjectId.GenerateNewId()}/ziteng-check");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(4001, body.RootElement.GetProperty("code").GetInt32());
    }

    private static string FixturePath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "ZitengPartnerGolden.json");

    private static async Task<List<BsonDocument>> ListDocumentsAsync(
        IAsyncCursor<BsonDocument> cursor, CancellationToken ct = default)
    {
        using (cursor)
            return await cursor.ToListAsync(ct);
    }

    private static ZitengPartnerWork WorkFrom(JsonElement candidate) => new(
        candidate.GetProperty("id").GetString()!,
        candidate.GetProperty("reference").GetString()!,
        candidate.GetProperty("display_title").GetString()!,
        candidate.GetProperty("original_title").GetString()!,
        candidate.GetProperty("author").GetString()!,
        candidate.GetProperty("circle").GetString()!,
        candidate.GetProperty("state").GetString()!,
        candidate.GetProperty("stage").GetString()!);

    private static ZitengPartnerWork Work(string id, string title = "作品名", string state = "published") =>
        new(id, "REF-" + id, title, title, "作者", "社团", state, "final");

    private const string EmptyPayload =
        """{"results":[],"total":0,"revision":"r","next_page":false}""";

    private static HttpResponseMessage JsonResponse(string body,
        HttpStatusCode status = HttpStatusCode.OK, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static async Task<BsonDocument> WaitForCheckAsync(HttpWorld world, int status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var document = await world.Store.Collection(ZitengPartnerStore.CheckCollection)
                .Find(new BsonDocument("project_id", world.ProjectId.ToString()))
                .FirstOrDefaultAsync();
            if (document is not null && document.Integer("status") == status) return document;
            await Task.Delay(50);
        }
        throw new TimeoutException("check did not reach status " + status.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1794182400);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingDelay : IZitengPartnerDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateGate : IZitengPartnerApiGate
    {
        public Task WaitAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            return handler(request, Calls, cancellationToken);
        }
    }

    private sealed class DirectWorld : IAsyncDisposable
    {
        public required string DatabaseName { get; init; }
        public required MongoStore Store { get; init; }
        public required ZitengPartnerStore PartnerStore { get; init; }
        public required ZitengPartnerSettings Settings { get; init; }
        public required TestClock Clock { get; init; }
        public required JobQueue Jobs { get; init; }

        public static async Task<DirectWorld> CreateAsync(string apiKey = "test-key",
            int maxPages = 3, int maxResults = 5)
        {
            var database = "shoreline_csharp_ziteng_" + Guid.NewGuid().ToString("N");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Mongo:Uri"] = MongoUri + database,
                }).Build();
            var store = new MongoStore(configuration);
            await store.InitializeAsync(CancellationToken.None);
            var clock = new TestClock();
            var settings = new ZitengPartnerSettings
            {
                ApiKey = apiKey,
                MaxPages = maxPages,
                MaxResults = maxResults,
                TimeoutSeconds = 5,
                TaskTimeoutSeconds = 180,
                MinIntervalSeconds = 1.1,
            };
            var partnerStore = new ZitengPartnerStore(store, clock);
            await partnerStore.EnsureIndexesAsync();
            return new DirectWorld
            {
                DatabaseName = database,
                Store = store,
                PartnerStore = partnerStore,
                Settings = settings,
                Clock = clock,
                Jobs = new JobQueue(store, clock),
            };
        }

        public ZitengPartnerClient Client(FakeHandler handler, RecordingDelay? delay = null) =>
            new(Settings, new HttpClient(handler), new ImmediateGate(), Clock,
                delay ?? new RecordingDelay());

        public ZitengPartnerService Service(FakeHandler handler) =>
            new(PartnerStore, Client(handler), Settings, Jobs, Clock,
                NullLogger<ZitengPartnerService>.Instance);

        public async ValueTask DisposeAsync()
        {
            var client = new MongoClient(MongoUri);
            await client.DropDatabaseAsync(DatabaseName);
        }
    }

    private sealed class HttpWorld : IAsyncDisposable
    {
        public required string DatabaseName { get; init; }
        public required MongoStore Store { get; init; }
        public required WebApplicationFactory<Program> App { get; init; }
        public required HttpClient Http { get; init; }
        public required HttpClient ForeignHttp { get; init; }
        public required ObjectId ProjectId { get; init; }
        public required string ProjectName { get; init; }

        public static async Task<HttpWorld> CreateAsync(bool enabled = true, int maxResults = 5)
        {
            var database = "shoreline_csharp_ziteng_http_" + Guid.NewGuid().ToString("N");
            var settings = Settings(database);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var store = new MongoStore(configuration);
            await store.InitializeAsync(CancellationToken.None);

            var zoneSettings = new ZitengPartnerSettings
            {
                ApiKey = enabled ? "http-test-key" : "",
                MaxResults = maxResults,
                TimeoutSeconds = 5,
                TaskTimeoutSeconds = 180,
            };
            var handler = new FakeHandler((_, _, _) => Task.FromResult(JsonResponse(EmptyPayload)));
            var fakeClient = new ZitengPartnerClient(zoneSettings, new HttpClient(handler),
                new ImmediateGate(), new TestClock(), new RecordingDelay());

            var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ZitengPartnerSettings>();
                    services.AddSingleton(zoneSettings);
                    services.RemoveAll<ZitengPartnerClient>();
                    services.AddSingleton(fakeClient);
                });
            });

            var ownerHttp = app.CreateClient();
            var projectId = ObjectId.GenerateNewId();
            var ownerId = ObjectId.GenerateNewId();
            var foreignId = ObjectId.GenerateNewId();
            var now = DateTime.UtcNow;
            var owner = new BsonDocument
            {
                ["_id"] = ownerId,
                ["n"] = "ziteng-owner-" + Guid.NewGuid().ToString("N"),
                ["ns"] = "ziteng-owner-" + Guid.NewGuid().ToString("N"),
                ["as"] = new BsonArray(),
                ["l"] = "auto",
                ["p"] = Password,
                ["e"] = "ziteng-owner-" + Guid.NewGuid().ToString("N") + "@example.test",
                ["admin"] = false,
                ["b"] = false,
                ["c"] = now,
            };
            var foreign = new BsonDocument
            {
                ["_id"] = foreignId,
                ["n"] = "ziteng-foreign-" + Guid.NewGuid().ToString("N"),
                ["ns"] = "ziteng-foreign-" + Guid.NewGuid().ToString("N"),
                ["as"] = new BsonArray(),
                ["l"] = "auto",
                ["p"] = Password,
                ["e"] = "ziteng-foreign-" + Guid.NewGuid().ToString("N") + "@example.test",
                ["admin"] = false,
                ["b"] = false,
                ["c"] = now,
            };
            await store.Collection("user").InsertManyAsync([owner, foreign]);
            var projectName = "【12】[社团] 作品名 (シリーズ) [DL版]";
            await store.Collection("project").InsertOneAsync(new BsonDocument
            {
                ["_id"] = projectId,
                ["n"] = projectName,
                ["st"] = BsonInt32.Create(0),
                ["ou"] = ownerId,
                ["label_revision"] = 0L,
                ["m_e"] = now,
            });
            await store.Collection("project_member").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["project"] = projectId,
                ["user"] = ownerId,
                ["status"] = "active",
                ["tags"] = new BsonArray { "creator" },
            });

            var token = app.Services.GetRequiredService<LegacyToken>()
                .Issue(ownerId.ToString(), Password);
            var foreignToken = app.Services.GetRequiredService<LegacyToken>()
                .Issue(foreignId.ToString(), Password);
            ownerHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var foreignHttp = app.CreateClient();
            foreignHttp.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", foreignToken);

            return new HttpWorld
            {
                DatabaseName = database,
                Store = store,
                App = app,
                Http = ownerHttp,
                ForeignHttp = foreignHttp,
                ProjectId = projectId,
                ProjectName = projectName,
            };
        }

        private static Dictionary<string, string?> Settings(string database) => new()
        {
            ["Mongo:Uri"] = MongoUri + database,
            ["Auth:Secret"] = "ziteng-http-test-secret-0123456789abcdef",
            ["Auth:AdminEmail"] = "owner@example.test",
            ["Notifications:WorkerEnabled"] = "false",
            ["Storage:Endpoint"] = "http://127.0.0.1:" + TestEnvironment.S3Port,
            ["Storage:Bucket"] = "shoreline-test",
            ["Storage:AccessKey"] = "local-test-only",
            ["Storage:SecretKey"] = "local-disposable-test-secret",
            ["Storage:ForcePathStyle"] = "true",
            ["Mail:Enabled"] = "true",
            ["Mail:Host"] = "127.0.0.1",
            ["Mail:Port"] = TestEnvironment.SmtpPort.ToString(),
            ["Mail:StartTls"] = "false",
            ["Mail:From"] = "noreply@example.test",
        };

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            ForeignHttp.Dispose();
            await App.DisposeAsync();
            var client = new MongoClient(MongoUri);
            await client.DropDatabaseAsync(DatabaseName);
        }
    }
}

