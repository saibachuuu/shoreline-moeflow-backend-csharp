using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Storage;

namespace Shoreline.Tests;

[CollectionDefinition("local-services-parse")]
public sealed class ParseServicesCollection : ICollectionFixture<LocalServicesWithoutWorker>;

[Collection("local-services-parse")]
public sealed class FileParseTests(LocalServicesWithoutWorker services)
{
    private readonly CancellationToken ct = CancellationToken.None;

    [Fact]
    public async Task ParseText_RunSync_CreatesRankedBlankAwareSourcesAndCounters()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var file = await FileAsync(project, "lines.txt", ParseText("one\n\nthree\r\nfour"));
        var parse = Service(clock);

        await parse.ParseTextAsync(file, null, runSync: true, ct);

        var sources = await SourcesAsync(file);
        Assert.Equal(new[] { "one", "", "three", "four" }, sources.Select(row => row.Text("c")));
        Assert.Equal(new[] { 0L, 1L, 2L, 3L }, sources.Select(row => row.Integer("r")));
        Assert.Equal(new[] { false, true, false, false }, sources.Select(row => row.Flag("b")));
        Assert.All(sources, row => Assert.True(row.Flag("cm")));

        var row = await LoadFileAsync(file);
        Assert.Equal(FileParseService.ParseSucceeded, row.Integer("ps"));
        Assert.False(row.Contains("pt"));
        Assert.False(row.Contains("pti"));
        Assert.False(row.Contains("pst"));
        Assert.Equal(3, row.Integer("sc"));
        Assert.Equal(3, (await LoadProjectAsync(project)).Integer("sc"));
        Assert.Equal(0, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = FileParseService.ParseTextJobKind,
                ["payload.file_id"] = file.ToString()
            }));
    }

    [Fact]
    public async Task ParseText_OldRevision_CopiesTranslationsAndTipsWithTargetCounters()
    {
        var clock = new TestClock();
        var project = await ProjectAsync(withTarget: true);
        var old = await FileAsync(project, "old.txt", ParseText("hello\nworld"));
        var file = await FileAsync(project, "new.txt", ParseText("hello\nworld"));
        var target = (await services.Store.Collection("target")
            .Find(new BsonDocument("t", project)).FirstAsync(ct))["_id"].AsObjectId;

        var oldSource = await InsertSourceAsync(old, 0, "hello", sc: true);
        var translationId = ObjectId.GenerateNewId();
        await services.Store.Collection("translation").InsertOneAsync(new BsonDocument
        {
            ["_id"] = translationId,
            ["o"] = oldSource,
            ["t"] = target,
            ["c"] = "你好",
            ["p"] = "校对",
            ["u"] = ObjectId.GenerateNewId(),
            ["s"] = true,
            ["mt"] = false,
            ["ct"] = clock.Now.UtcDateTime,
            ["e"] = clock.Now.UtcDateTime
        }, cancellationToken: ct);
        await services.Store.Collection("tip").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = oldSource,
            ["t"] = target,
            ["c"] = "tip",
            ["u"] = ObjectId.GenerateNewId(),
            ["ct"] = clock.Now.UtcDateTime,
            ["et"] = clock.Now.UtcDateTime
        }, cancellationToken: ct);

        await Service(clock).ParseTextAsync(file, old, runSync: true, ct);

        var newSources = await SourcesAsync(file);
        Assert.Equal(2, newSources.Count);
        var copiedSource = newSources.Single(row => row.Text("c") == "hello");
        Assert.Equal("hello", copiedSource.Text("c"));

        var copied = await services.Store.Collection("translation")
            .Find(new BsonDocument("o", copiedSource["_id"])).FirstAsync(ct);
        Assert.Equal(target, copied["t"].AsObjectId);
        Assert.Equal("你好", copied.Text("c"));
        Assert.Equal("校对", copied.Text("p"));
        Assert.True(copied.Flag("s"));
        Assert.NotEqual(translationId, copied["_id"].AsObjectId);

        Assert.Equal(1, await services.Store.Collection("tip")
            .CountDocumentsAsync(new BsonDocument("o", copiedSource["_id"])));

        var fileRow = await LoadFileAsync(file);
        Assert.Equal(2, fileRow.Integer("sc"));
        Assert.Equal(1, fileRow.Integer("tsc"));
        Assert.Equal(1, fileRow.Integer("csc"));
        var projectRow = await LoadProjectAsync(project);
        Assert.Equal(1, projectRow.Integer("tsc"));
        Assert.Equal(1, projectRow.Integer("csc"));
        Assert.Equal(1, (await LoadTargetAsync(target)).Integer("tsc"));
        Assert.Equal(1, (await LoadCacheAsync(file, target)).Integer("ts"));
        Assert.Equal(1, (await LoadCacheAsync(file, target)).Integer("cs"));
    }

    [Fact]
    public async Task ParseText_MissingSource_MarksFailedAndIncrementsParseTimes()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var file = await FileAsync(project, "missing.txt", Array.Empty<byte>(),
            saveName: "does-not-exist.txt");

        await Service(clock).ParseTextAsync(file, null, runSync: true, ct);

        var row = await LoadFileAsync(file);
        Assert.Equal(FileParseService.ParseFailed, row.Integer("ps"));
        Assert.Equal(FileParseService.ParseErrorFileCannotRead, row.Integer("pe"));
        Assert.Equal(1, row.Integer("pt"));
        Assert.Empty(await SourcesAsync(file));
    }

    [Fact]
    public async Task ParseText_InvalidUtf32_MarksUnknownCharset()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var file = await FileAsync(project, "charset.txt", [0xFF, 0xFE, 0x00, 0x00, 0x41]);

        await Service(clock).ParseTextAsync(file, null, runSync: true, ct);

        var row = await LoadFileAsync(file);
        Assert.Equal(FileParseService.ParseFailed, row.Integer("ps"));
        Assert.Equal(FileParseService.ParseErrorUnknownCharset, row.Integer("pe"));
        Assert.Equal(1, row.Integer("pt"));
    }

    [Fact]
    public async Task ParseText_LeaseRecovery_SameRunClearsPartialSourcesAndCompletes()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var file = await FileAsync(project, "recover.txt", ParseText("alpha\nbeta"));
        var queue = new JobQueue(services.Store, clock);
        var parse = Service(clock, queue);

        var jobId = await parse.ParseTextAsync(file, null, runSync: false, ct);
        Assert.NotNull(jobId);
        var claimed = await queue.ClaimAsync("first", [FileParseService.ParseTextJobKind], ct);
        Assert.NotNull(claimed);
        var payload = claimed!["payload"].AsBsonDocument;

        // Simulate a crash after the task marked PARSING and inserted one
        // source, but before it finished. The lease is then abandoned.
        await services.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", file),
            new BsonDocument("$set", new BsonDocument
            {
                ["ps"] = FileParseService.ParseParsing,
                ["parse_run"] = payload.Text("run_id"),
                ["sc"] = 1
            }), cancellationToken: ct);
        await InsertSourceAsync(file, 0, "partial", sc: false);

        clock.Now = clock.Now.AddMinutes(3);
        var reclaimed = await queue.ClaimAsync("replacement", [FileParseService.ParseTextJobKind], ct);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed!["attempts"].AsInt32);

        await parse.RunParseTextAsync(reclaimed["payload"].AsBsonDocument, ct);
        await queue.CompleteAsync(jobId.Value, "replacement", ct);

        var sources = await SourcesAsync(file);
        Assert.Equal(new[] { "alpha", "beta" }, sources.Select(row => row.Text("c")));
        var row = await LoadFileAsync(file);
        Assert.Equal(FileParseService.ParseSucceeded, row.Integer("ps"));
        Assert.Equal(2, row.Integer("sc"));
        Assert.False(row.Contains("parse_run"));
        Assert.Equal("done", (await LoadJobAsync(jobId.Value)).Text("state"));
    }

    [Fact]
    public async Task ParseText_DifferentRunWhileParsing_IsSkipped()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var file = await FileAsync(project, "busy.txt", ParseText("alpha"));
        await services.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", file),
            new BsonDocument("$set", new BsonDocument
            {
                ["ps"] = FileParseService.ParseParsing,
                ["parse_run"] = "someone-else"
            }), cancellationToken: ct);

        await Service(clock).RunParseTextAsync(new BsonDocument
        {
            ["file_id"] = file.ToString(),
            ["old_revision_id"] = BsonNull.Value,
            ["run_id"] = "our-run"
        }, ct);

        Assert.Empty(await SourcesAsync(file));
        var row = await LoadFileAsync(file);
        Assert.Equal(FileParseService.ParseParsing, row.Integer("ps"));
        Assert.Equal("someone-else", row.Text("parse_run"));
    }

    [Fact]
    public async Task FindTerms_QueuesThenAppliesOnlyMatchingTermBanks()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var bank = ObjectId.GenerateNewId();
        var otherBank = ObjectId.GenerateNewId();
        await services.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", project),
            new BsonDocument("$set", new BsonDocument("tb", new BsonArray { bank })),
            cancellationToken: ct);
        var term = await TermAsync(bank, "hello");
        await TermAsync(otherBank, "hello");
        var world = await TermAsync(bank, "world");
        var file = await FileAsync(project, "terms.txt", ParseText("hello world\nnothing"));
        await InsertSourceAsync(file, 0, "hello world", sc: true);
        await InsertSourceAsync(file, 1, "nothing", sc: false);

        var jobId = await Service(clock).FindTermsAsync(file, runSync: false, ct);
        Assert.NotNull(jobId);
        var queued = await LoadFileAsync(file);
        Assert.Equal(FileParseService.FindTermsQueuing, queued.Integer("ft"));
        Assert.Equal(jobId.Value.ToString(), queued.Text("ftt"));

        var payload = (await LoadJobAsync(jobId.Value))["payload"].AsBsonDocument;
        await Service(clock).RunFindTermsAsync(payload, ct);

        var sources = await SourcesAsync(file);
        var matched = sources[0]["pt"].AsBsonArray.Select(value => value.AsObjectId).ToList();
        Assert.Equal(new[] { term, world }, matched);
        Assert.Empty(sources[1]["pt"].AsBsonArray);
        var row = await LoadFileAsync(file);
        Assert.Equal(FileParseService.FindTermsFinished, row.Integer("ft"));
        Assert.False(row.Contains("ftt"));
        Assert.False(row.Contains("fts"));
    }

    [Fact]
    public async Task FindTerms_SameRunRecoveryContinuesAndDifferentRunSkips()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var bank = ObjectId.GenerateNewId();
        await services.Store.Collection("project").UpdateOneAsync(
            new BsonDocument("_id", project),
            new BsonDocument("$set", new BsonDocument("tb", new BsonArray { bank })),
            cancellationToken: ct);
        var term = await TermAsync(bank, "hello");
        var file = await FileAsync(project, "recover-terms.txt", ParseText("hello"));
        await InsertSourceAsync(file, 0, "hello", sc: false);
        await services.Store.Collection("file").UpdateOneAsync(
            new BsonDocument("_id", file),
            new BsonDocument("$set", new BsonDocument
            {
                ["ft"] = FileParseService.FindTermsFinding,
                ["find_terms_run"] = "run-1"
            }), cancellationToken: ct);

        await Service(clock).RunFindTermsAsync(new BsonDocument
        {
            ["file_id"] = file.ToString(),
            ["run_id"] = "run-2"
        }, ct);
        var skipped = await SourcesAsync(file);
        Assert.Empty(skipped[0]["pt"].AsBsonArray);

        await Service(clock).RunFindTermsAsync(new BsonDocument
        {
            ["file_id"] = file.ToString(),
            ["run_id"] = "run-1"
        }, ct);
        var recovered = await SourcesAsync(file);
        Assert.Equal(new[] { term }, recovered[0]["pt"].AsBsonArray.Select(value => value.AsObjectId));
        Assert.Equal(FileParseService.FindTermsFinished, (await LoadFileAsync(file)).Integer("ft"));
    }

    [Fact]
    public async Task ParseText_ReplayAfterSuccess_IsNoOp()
    {
        var clock = new TestClock();
        var project = await ProjectAsync();
        var file = await FileAsync(project, "replay.txt", ParseText("one\ntwo"));
        var queue = new JobQueue(services.Store, clock);
        var parse = Service(clock, queue);

        var jobId = await parse.ParseTextAsync(file, null, runSync: false, ct);
        Assert.NotNull(jobId);
        var claimed = await queue.ClaimAsync("first", [FileParseService.ParseTextJobKind], ct);
        if (claimed is null || claimed["payload"].AsBsonDocument.Text("file_id") != file.ToString())
        {
            // Other tests in this collection schedule parse jobs they never
            // claim; drain until this test's own job is delivered.
            claimed = null;
            for (var i = 0; i < 100 && claimed is null; i++)
            {
                var candidate = await queue.ClaimAsync("first", [FileParseService.ParseTextJobKind], ct);
                if (candidate is null) break;
                if (candidate["payload"].AsBsonDocument.Text("file_id") == file.ToString()) claimed = candidate;
            }
        }
        Assert.NotNull(claimed);
        var payload = claimed!["payload"].AsBsonDocument;
        await parse.RunParseTextAsync(payload, ct);
        await queue.CompleteAsync(jobId.Value, "first", ct);

        var sources = await SourcesAsync(file);
        Assert.Equal(2, sources.Count);
        var ids = sources.Select(row => row["_id"].AsObjectId).ToList();

        // Redelivery of the same job after success must not create duplicate
        // business documents or schedule follow-up work.
        await parse.RunParseTextAsync(payload, ct);

        var replayed = await SourcesAsync(file);
        Assert.Equal(ids, replayed.Select(row => row["_id"].AsObjectId).ToList());
        Assert.Equal(FileParseService.ParseSucceeded, (await LoadFileAsync(file)).Integer("ps"));
        Assert.Equal(1, await services.Store.Collection("csharp_job").CountDocumentsAsync(
            new BsonDocument
            {
                ["kind"] = FileParseService.ParseTextJobKind,
                ["payload.file_id"] = file.ToString()
            }));
        Assert.Equal("done", (await LoadJobAsync(jobId.Value)).Text("state"));
    }

    // ---------------------------------------------------------------- fixtures

    private FileParseService Service(TestClock clock, JobQueue? queue = null) => new(
        services.Store,
        Storage(),
        queue ?? new JobQueue(services.Store, clock),
        new ProjectLabelLock(services.Store, clock),
        new LabelRequestContext(),
        clock);

    private ObjectStorage Storage() => services.App.Services.GetRequiredService<ObjectStorage>();

    private async Task<ObjectId> ProjectAsync(bool withTarget = false)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "parse-project-" + id,
            ["st"] = 0,
            ["label_revision"] = 0L,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["tb"] = new BsonArray()
        }, cancellationToken: ct);
        if (withTarget)
        {
            var target = ObjectId.GenerateNewId();
            await services.Store.Collection("target").InsertOneAsync(new BsonDocument
            {
                ["_id"] = target,
                ["t"] = id,
                ["l"] = ObjectId.GenerateNewId(),
                ["tsc"] = 0,
                ["csc"] = 0,
                ["label_epoch"] = 0L
            }, cancellationToken: ct);
        }
        return id;
    }

    private async Task<ObjectId> FileAsync(ObjectId project, string name, byte[] content,
        string? saveName = null)
    {
        var id = ObjectId.GenerateNewId();
        saveName ??= id + ".txt";
        await services.Store.Collection("file").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["t"] = FileParseService.FileTypeText,
            ["p"] = project,
            ["a"] = new BsonArray(),
            ["ac"] = true,
            ["sa"] = saveName,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            ["ps"] = FileParseService.ParseNotStart,
            ["ft"] = FileParseService.FindTermsQueuing,
            ["label_epoch"] = 0L
        }, cancellationToken: ct);
        var targets = await services.Store.Collection("target")
            .Find(new BsonDocument("t", project)).ToListAsync(ct);
        foreach (var target in targets)
        {
            await services.Store.Collection("file_target_cache").InsertOneAsync(new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["f"] = id,
                ["t"] = target["_id"],
                ["ts"] = 0,
                ["cs"] = 0,
                ["e"] = DateTime.UtcNow,
                ["label_epoch"] = 0L
            }, cancellationToken: ct);
        }
        if (saveName == "does-not-exist.txt") return id;
        await using var stream = new MemoryStream(content);
        await Storage().PutAsync(FileService.FilePrefix + saveName, stream,
            "text/plain; charset=utf-8", ct);
        return id;
    }

    private async Task<ObjectId> InsertSourceAsync(ObjectId file, int rank, string content, bool sc)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["f"] = file,
            ["r"] = rank,
            ["c"] = content,
            ["pt"] = new BsonArray(),
            ["b"] = content.Trim().Length == 0,
            ["p"] = 0,
            ["x"] = 0.0,
            ["y"] = 0.0,
            ["v"] = new BsonArray(),
            ["lf"] = true,
            ["cm"] = true
        }, cancellationToken: ct);
        if (sc)
            await services.Store.Collection("file").UpdateOneAsync(
                new BsonDocument("_id", file),
                new BsonDocument("$inc", new BsonDocument("sc", 1)), cancellationToken: ct);
        return id;
    }

    private async Task<ObjectId> TermAsync(ObjectId bank, string source)
    {
        var id = ObjectId.GenerateNewId();
        await services.Store.Collection("term").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["tb"] = bank,
            ["o"] = source,
            ["t"] = "目标",
            ["ti"] = ""
        }, cancellationToken: ct);
        return id;
    }

    private async Task<List<BsonDocument>> SourcesAsync(ObjectId file) =>
        await services.Store.Collection("source").Find(new BsonDocument("f", file))
            .Sort(new BsonDocument("r", 1)).ToListAsync(ct);

    private async Task<BsonDocument> LoadFileAsync(ObjectId id) =>
        await services.Store.Collection("file").Find(new BsonDocument("_id", id)).FirstAsync(ct);

    private async Task<BsonDocument> LoadProjectAsync(ObjectId id) =>
        await services.Store.Collection("project").Find(new BsonDocument("_id", id)).FirstAsync(ct);

    private async Task<BsonDocument> LoadTargetAsync(ObjectId id) =>
        await services.Store.Collection("target").Find(new BsonDocument("_id", id)).FirstAsync(ct);

    private async Task<BsonDocument> LoadCacheAsync(ObjectId file, ObjectId target) =>
        await services.Store.Collection("file_target_cache")
            .Find(new BsonDocument { ["f"] = file, ["t"] = target }).FirstAsync(ct);

    private async Task<BsonDocument> LoadJobAsync(ObjectId id) =>
        await services.Store.Collection("csharp_job").Find(new BsonDocument("_id", id)).FirstAsync(ct);

    private static byte[] ParseText(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
