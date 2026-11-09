using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Notifications;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;
using Shoreline.Imaging;
using Xunit;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class ProofreadFeedbackTests : IDisposable
{
    private readonly LocalServices _services;
    private readonly NotificationClock _clock = new(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
    private readonly FakeMailTransport _mail = new();
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "g3-proofread-tests", Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly MongoStore _jobs;

    public ProofreadFeedbackTests(LocalServices services)
    {
        _services = services;
        Directory.CreateDirectory(_storageRoot);
        // Keep direct-service jobs away from the shared host's JobWorker: the
        // host connects to the collection database and would otherwise bake the
        // note concurrently with the explicit BakeAsync calls under test.
        var jobsUri = new MongoUrlBuilder(services.Configuration["Mongo:Uri"])
        {
            DatabaseName = "shoreline_csharp_proofread_jobs_" + _suffix,
        }.ToMongoUrl().ToString();
        _jobs = new MongoStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Mongo:Uri"] = jobsUri }).Build());
    }

    public void Dispose()
    {
        try { _jobs.Database.Client.DropDatabase(_jobs.Database.DatabaseNamespace.DatabaseName); }
        catch { /* disposable local test database only */ }
        try { Directory.Delete(_storageRoot, true); }
        catch { /* best-effort temp cleanup */ }
    }

    private sealed class NotificationClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Shoreline.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeMailTransport : INotificationMailTransport
    {
        public List<NotificationMailMessage> Messages { get; } = [];
        public NotificationMailResult? Result { get; set; }

        public Task<NotificationMailResult> SendAsync(NotificationMailMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.FromResult(Result ?? new NotificationMailResult("accepted", ""));
        }
    }

    private sealed class FakeRenderer : IProofreadImageRenderer
    {
        public int Renders { get; private set; }
        public string Fingerprint(IReadOnlyList<ProofreadMarkerLabel> labels, int opacity) =>
            "fp-" + opacity + "-" + labels.Count;
        public byte[]? Render(byte[] source, IReadOnlyList<ProofreadMarkerLabel> labels, int opacity)
        {
            Renders++;
            return [1, 2, 3, 4];
        }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------
    private static ObjectId Oid(BsonValue value) => value.AsObjectId;

    private IConfiguration Configuration(bool notifications, bool trust = true, params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mongo:Uri"] = _services.Configuration["Mongo:Uri"],
            ["Notifications:Enabled"] = notifications ? "true" : "false",
            ["Notifications:SiteOrigin"] = "https://example.test",
            ["Notifications:MaxAudience"] = "20000",
            ["Notifications:UserEmailEnabled"] = "true",
            ["Notifications:TrustExistingEmails"] = trust ? "true" : "false",
            ["Notifications:WorkerEnabled"] = "false",
            ["Site:Name"] = "萌翻",
            ["Site:Origin"] = "https://example.test",
            ["Storage:Type"] = "Local",
            ["Storage:Root"] = _storageRoot,
            ["Storage:Domain"] = "https://storage.example.test",
            ["Mail:Enabled"] = "true",
            ["Mail:Host"] = "127.0.0.1",
            ["Mail:Port"] = "21128",
            ["Mail:From"] = "noreply@example.test",
        };
        for (var i = 0; i < extra.Length; i++)
            settings[$"Notifications:EmailAllowlist:{i}"] = extra[i].Value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private async Task<BsonDocument> InsertUserAsync(string name, string email, bool banned = false)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = $"{name}-{_suffix}",
            ["e"] = email,
            ["admin"] = false,
            ["b"] = banned,
            ["p"] = new BsonDocument(),
        };
        await _services.Store.Collection("user").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertTeamAsync(string name)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = $"{name}-{_suffix}",
        };
        await _services.Store.Collection("team").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertProjectAsync(ObjectId teamId, ObjectId ownerId,
        long labelRevision = 0, bool pooledStorage = false)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "项目" + Guid.NewGuid().ToString("N")[..6],
            ["t"] = teamId,
            ["ps"] = ObjectId.GenerateNewId(),
            ["st"] = 0,
            ["ou"] = ownerId,
            ["label_revision"] = labelRevision,
        };
        if (pooledStorage) document["csid"] = "pool-1";
        await _services.Store.Collection("project").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertLanguageAsync(string name)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["c"] = name,
            ["co"] = "test-" + Guid.NewGuid().ToString("N")[..8],
        };
        await _services.Store.Collection("language").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertTargetAsync(ObjectId projectId, ObjectId languageId)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = projectId,
            ["l"] = languageId,
        };
        await _services.Store.Collection("target").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertFileAsync(ObjectId projectId, string name, string saveName,
        string sortName = "a", string dirSortName = "a")
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = projectId,
            ["t"] = 2,
            ["ac"] = true,
            ["n"] = name,
            ["sa"] = saveName,
            ["sn"] = sortName,
            ["dir_sort_name"] = dirSortName,
            ["th"] = 3,
        };
        await _services.Store.Collection("file").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertSourceAsync(ObjectId fileId, long rank, string content,
        double x = 0.5, double y = 0.5, int positionType = 1)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["f"] = fileId,
            ["r"] = rank,
            ["c"] = content,
            ["x"] = x,
            ["y"] = y,
            ["p"] = positionType,
        };
        await _services.Store.Collection("source").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertTranslationAsync(ObjectId sourceId, ObjectId targetId,
        ObjectId userId, string content, string proofread = "", bool selected = false, DateTime? editTime = null)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = sourceId,
            ["t"] = targetId,
            ["u"] = userId,
            ["c"] = content,
            ["p"] = proofread,
            ["s"] = selected,
            ["e"] = editTime ?? _clock.Now.UtcDateTime,
        };
        await _services.Store.Collection("translation").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertTipAsync(ObjectId sourceId, ObjectId targetId, ObjectId? userId,
        string content, DateTime? createTime = null)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["o"] = sourceId,
            ["t"] = targetId,
            ["u"] = userId is { } id ? id : BsonNull.Value,
            ["c"] = content,
            ["et"] = createTime ?? _clock.Now.UtcDateTime,
            ["ct"] = createTime ?? _clock.Now.UtcDateTime,
        };
        await _services.Store.Collection("tip").InsertOneAsync(document);
        return document;
    }

    private async Task<BsonDocument> InsertMemberAsync(ObjectId projectId, ObjectId userId,
        params string[] tags)
    {
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = projectId,
            ["user"] = userId,
            ["status"] = "active",
            ["tags"] = new BsonArray(tags),
        };
        await _services.Store.Collection("project_member").InsertOneAsync(document);
        return document;
    }

    private ProofreadFeedbackService Service(IConfiguration configuration,
        IProofreadImageRenderer? renderer = null, FakeMailTransport? mail = null)
    {
        var permissions = new IdentityPermissions(_services.Store, _clock);
        var content = new NotificationContent(_services.Store, permissions, configuration);
        var read = new NotificationReadService(_services.Store, permissions, content, configuration, _clock);
        var publish = new NotificationPublishService(_services.Store, content, read, configuration, mail ?? _mail);
        return new ProofreadFeedbackService(
            _services.Store,
            permissions,
            publish,
            read,
            new ProjectLabelLock(_services.Store, _clock),
            new LabelRequestContext(),
            new JobQueue(_jobs, _clock),
            new ObjectStorage(configuration),
            configuration,
            new TestEnvironment(),
            _clock,
            renderer ?? new NullProofreadImageRenderer(),
            mail ?? _mail,
            NullLogger<ProofreadFeedbackService>.Instance);
    }

    private sealed record Fixture(BsonDocument Actor, BsonDocument Project, BsonDocument Target,
        BsonDocument File, BsonDocument Source, string ActorEmail, string TranslatorEmail);

    /// <summary>Keeps fixed, human-readable addresses while isolating every test.</summary>
    private string UniqueEmail(string email)
    {
        var at = email.IndexOf('@');
        return at < 0 ? $"{email}+{_suffix}" : $"{email[..at]}+{_suffix}{email[at..]}";
    }

    private async Task<Fixture> CreateChangedPageAsync(string translatorEmail = "translator@example.test")
    {
        var actorEmail = UniqueEmail("owner@example.test");
        var actualTranslatorEmail = UniqueEmail(translatorEmail);
        var actor = await InsertUserAsync("校对者", actorEmail);
        var translator = await InsertUserAsync("译者", actualTranslatorEmail);
        var team = await InsertTeamAsync("team");
        var project = await InsertProjectAsync(Oid(team["_id"]), Oid(actor["_id"]));
        var language = await InsertLanguageAsync("日语");
        var target = await InsertTargetAsync(Oid(project["_id"]), Oid(language["_id"]));
        await InsertMemberAsync(Oid(project["_id"]), Oid(actor["_id"]), "proofreader");
        await InsertMemberAsync(Oid(project["_id"]), Oid(translator["_id"]), "translator");
        var file = await InsertFileAsync(Oid(project["_id"]), "page.png", "page.png");
        var source = await InsertSourceAsync(Oid(file["_id"]), 0, "原文");
        await InsertTranslationAsync(Oid(source["_id"]), Oid(target["_id"]), Oid(translator["_id"]),
            "旧译", "新译");
        return new Fixture(actor, project, target, file, source, actorEmail, actualTranslatorEmail);
    }

    private static JsonElement Body(object? value = null) =>
        JsonSerializer.SerializeToElement(value ?? new { });

    private async Task<BsonDocument?> NoteAsync(ObjectId noteId) =>
        await _services.Store.Collection("notification")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", noteId)).FirstOrDefaultAsync();

    private async Task<BsonDocument> JobAsync(ObjectId noteId) =>
        await _jobs.Collection("csharp_job")
            .Find(Builders<BsonDocument>.Filter.Eq("payload.notification_id", noteId)).FirstOrDefaultAsync();

    private static BsonDocument JobPayload(BsonDocument note, BsonDocument project) => new()
    {
        ["notification_id"] = note["_id"],
        ["project_id"] = project["_id"],
        ["label_revision"] = project.Integer("label_revision"),
    };

    // ------------------------------------------------------------------
    // direct service: validation / permission
    // ------------------------------------------------------------------
    [Fact]
    public async Task Send_RequiresProofreadPermission()
    {
        var page = await CreateChangedPageAsync();
        var stranger = await InsertUserAsync("外人", UniqueEmail("stranger@example.test"));
        var service = Service(Configuration(false, true, ("a", page.TranslatorEmail)));

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(stranger, page.Project["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(), "key-0001", default));
        Assert.Equal(403, error.Status);
        Assert.Equal("您没有校对权限，无法寄送校对稿", error.Message);
    }

    [Fact]
    public async Task Send_RejectsForeignTarget()
    {
        var page = await CreateChangedPageAsync();
        var otherProject = await InsertProjectAsync(Oid(page.Project["t"]), Oid(page.Actor["_id"]));
        var service = Service(Configuration(false));

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(page.Actor, otherProject["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(), "key-0002", default));
        Assert.Equal(4024, error.Code);
    }

    [Fact]
    public async Task Send_RejectsForeignFile()
    {
        var page = await CreateChangedPageAsync();
        var otherProject = await InsertProjectAsync(Oid(page.Project["t"]), Oid(page.Actor["_id"]));
        var foreignFile = await InsertFileAsync(Oid(otherProject["_id"]), "other.png", "other.png");
        await InsertMemberAsync(Oid(otherProject["_id"]), Oid(page.Actor["_id"]), "proofreader");
        var service = Service(Configuration(false));

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(new { file_id = foreignFile["_id"].AsObjectId.ToString() }),
                "key-0003", default));
        Assert.Equal(8001, error.Code);
    }

    [Fact]
    public async Task Send_RejectsUnknownAndNonBooleanFields()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(false));

        var unknown = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(new { nope = 1 }), "key-0004", default));
        Assert.Equal(2, unknown.Code);

        var nonBoolean = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = "yes" }), "key-0005", default));
        Assert.Equal(2, nonBoolean.Code);
    }

    [Fact]
    public async Task Send_NoChangesThrows()
    {
        var actor = await InsertUserAsync("校对者", UniqueEmail("owner@example.test"));
        var team = await InsertTeamAsync("team");
        var project = await InsertProjectAsync(Oid(team["_id"]), Oid(actor["_id"]));
        var language = await InsertLanguageAsync("日语");
        var target = await InsertTargetAsync(Oid(project["_id"]), Oid(language["_id"]));
        await InsertMemberAsync(Oid(project["_id"]), Oid(actor["_id"]), "proofreader");
        var file = await InsertFileAsync(Oid(project["_id"]), "page.png", "page.png");
        var source = await InsertSourceAsync(Oid(file["_id"]), 0, "原文");
        await InsertTranslationAsync(Oid(source["_id"]), Oid(target["_id"]), Oid(actor["_id"]), "旧译", "旧译");
        var service = Service(Configuration(false));

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(actor, project["_id"].AsObjectId.ToString(),
                target["_id"].AsObjectId.ToString(), Body(), "key-0006", default));
        Assert.Equal(4025, error.Code);
    }

    // ------------------------------------------------------------------
    // direct service: page construction
    // ------------------------------------------------------------------
    [Fact]
    public async Task Send_FileFilterKeepsOnlyRequestedPageButUsesGlobalPageNumber()
    {
        var page = await CreateChangedPageAsync();
        var second = await InsertFileAsync(Oid(page.Project["_id"]), "second.png", "second.png", "b", "b");
        var secondSource = await InsertSourceAsync(Oid(second["_id"]), 0, "second");
        await InsertTranslationAsync(Oid(secondSource["_id"]), Oid(page.Target["_id"]),
            Oid(page.Actor["_id"]), "旧", "新");
        var service = Service(Configuration(false, true, ("a", page.TranslatorEmail)));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(),
            Body(new { file_id = second["_id"].AsObjectId.ToString(), cc_myself = false }), "key-0007", default);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, result.Body["changed_pages_count"]);
        Assert.False(result.Body.ContainsKey("notification_id"));
    }

    [Fact]
    public async Task Send_TipsOnlyPageIsIncludedWithAllLabels()
    {
        var page = await CreateChangedPageAsync();
        var second = await InsertFileAsync(Oid(page.Project["_id"]), "tips.png", "tips.png", "b", "b");
        var source = await InsertSourceAsync(Oid(second["_id"]), 0, "tip source");
        await InsertTranslationAsync(Oid(source["_id"]), Oid(page.Target["_id"]),
            Oid(page.Actor["_id"]), "same", "same");
        await InsertTipAsync(Oid(source["_id"]), Oid(page.Target["_id"]), Oid(page.Actor["_id"]), "批注");
        var service = Service(Configuration(true, true, ("a", page.TranslatorEmail)));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0008", default);

        Assert.Equal(202, result.StatusCode);
        var note = await NoteAsync(ObjectId.Parse((string)result.Body["notification_id"]!));
        var pages = note!["payload"]["pending"]["changed_pages"].AsBsonArray;
        Assert.Equal(2, pages.Count);
        Assert.Equal("tips.png", pages[1]["file_name"].AsString);
        Assert.Equal(1, pages[1]["tip_count"].AsInt32);
        Assert.Single(pages[1]["labels"].AsBsonArray);
    }

    [Fact]
    public async Task Send_SelectedTranslationWinsAndKeepsUnchangedLabels()
    {
        var page = await CreateChangedPageAsync();
        var secondSource = await InsertSourceAsync(Oid(page.File["_id"]), 1, "原文2");
        await InsertTranslationAsync(Oid(secondSource["_id"]), Oid(page.Target["_id"]),
            Oid(page.Actor["_id"]), "未改", "未改", selected: true);
        var service = Service(Configuration(true, true, ("a", page.TranslatorEmail)));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0009", default);
        var note = await NoteAsync(ObjectId.Parse((string)result.Body["notification_id"]!));
        var labels = note!["payload"]["pending"]["changed_pages"][0]["labels"].AsBsonArray;
        Assert.Equal(2, labels.Count);
        Assert.True(labels[0]["is_changed"].AsBoolean);
        Assert.False(labels[1]["is_changed"].AsBoolean);
        Assert.Equal(1, note["payload"]["pending"]["changed_pages"][0]["changed_count"].AsInt32);
    }

    [Fact]
    public async Task Send_DiffHtmlEscapesUserContentButKeepsMarkup()
    {
        var page = await CreateChangedPageAsync();
        await _services.Store.Collection("translation").UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("o", page.Source["_id"]),
            Builders<BsonDocument>.Update.Set("c", "<b>old</b>").Set("p", "<b>new</b>"));
        var service = Service(Configuration(true, true, ("a", page.TranslatorEmail)));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0010", default);
        var note = await NoteAsync(ObjectId.Parse((string)result.Body["notification_id"]!));
        var html = note!["payload"]["pending"]["changed_pages"][0]["labels"][0]["diff_html"].AsString;
        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("<del", html);
        Assert.Contains("<ins", html);
        Assert.DoesNotContain("<b>old</b>", html);
    }

    // ------------------------------------------------------------------
    // direct service: notifications off (protected mail, fake transport)
    // ------------------------------------------------------------------
    [Fact]
    public async Task Send_DisabledNotificationsSendsProtectedMail()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(false, true,
            ("a", page.ActorEmail), ("b", page.TranslatorEmail)));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = false }), "key-0011", default);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("校对稿已成功发送至翻译邮箱", result.Body["message"]);
        Assert.Single(_mail.Messages);
        Assert.Equal(page.TranslatorEmail, _mail.Messages[0].To);
        Assert.Contains("校对稿修改反馈", _mail.Messages[0].Subject);
        Assert.Contains("page.png", _mail.Messages[0].Text);
        Assert.NotNull(_mail.Messages[0].Html);
    }

    [Fact]
    public async Task Send_DisabledCcMyselfAndDeduplicatesRecipients()
    {
        var page = await CreateChangedPageAsync();
        await _services.Store.Collection("project_member").UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("project", page.Project["_id"]),
                Builders<BsonDocument>.Filter.Eq("user", page.Actor["_id"])),
            Builders<BsonDocument>.Update.Set("tags", new BsonArray { "translator", "proofreader" }));
        var service = Service(Configuration(false, true,
            ("a", page.ActorEmail), ("b", page.TranslatorEmail)));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = true }), "key-0012", default);

        Assert.Equal(200, result.StatusCode);
        var recipients = (List<string>)result.Body["recipients"]!;
        Assert.Equal(2, recipients.Count);
        Assert.Equal(1, recipients.Count(email => email == page.ActorEmail));
        Assert.Equal(2, _mail.Messages.Count);
    }

    [Fact]
    public async Task Send_DisabledNoTranslatorWithoutCcThrows()
    {
        var page = await CreateChangedPageAsync();
        await _services.Store.Collection("user").UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("e", page.TranslatorEmail),
            Builders<BsonDocument>.Update.Set("b", true));
        var service = Service(Configuration(false, true,
            ("a", page.ActorEmail), ("b", page.TranslatorEmail)));

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = false }), "key-0013", default));
        Assert.Equal("NoTranslatorMemberError", error.Error);
    }

    [Fact]
    public async Task Send_DisabledNoTranslatorFallsBackToCcSelf()
    {
        var page = await CreateChangedPageAsync();
        await _services.Store.Collection("user").UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("e", page.TranslatorEmail),
            Builders<BsonDocument>.Update.Set("b", true));
        var service = Service(Configuration(false, true,
            ("a", page.ActorEmail)));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = true }), "key-0014", default);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(_mail.Messages);
        Assert.Equal(page.ActorEmail, _mail.Messages[0].To);
    }

    [Fact]
    public async Task Send_BannedTranslatorIsExcludedFromRecipients()
    {
        var page = await CreateChangedPageAsync("banned-translator@example.test");
        await _services.Store.Collection("user").UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("e", page.TranslatorEmail),
            Builders<BsonDocument>.Update.Set("b", true));
        var service = Service(Configuration(false, true, ("a", page.ActorEmail)));

        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = false }), "key-0015", default));
        Assert.Equal("NoTranslatorMemberError", error.Error);
    }

    // ------------------------------------------------------------------
    // direct service: notifications on (draft + job)
    // ------------------------------------------------------------------
    [Fact]
    public async Task Send_EnabledCreatesDraftAndDurableJob()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));

        var result = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0016", default);

        Assert.Equal(202, result.StatusCode);
        var noteId = ObjectId.Parse((string)result.Body["notification_id"]!);
        var note = await NoteAsync(noteId);
        Assert.Equal("draft", note!["state"].AsString);
        Assert.Equal("proofread_feedback", note["event_type"].AsString);
        Assert.Equal("project", note["category"].AsString);
        Assert.True(note["requested_email"].AsBoolean);
        Assert.Equal("「" + page.Project["n"].AsString + "」校对稿 · 正在生成标注图（0/1）", note["title"].AsString);
        var job = await JobAsync(noteId);
        Assert.NotNull(job);
        Assert.Equal("proofread_draft_bake", job!["kind"].AsString);
    }

    [Fact]
    public async Task Send_EnabledSameKeyReturnsExistingNote()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));

        var first = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "idem-0017", default);
        var second = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "idem-0017", default);

        Assert.Equal(202, second.StatusCode);
        Assert.Equal(first.Body["notification_id"], second.Body["notification_id"]);
        Assert.Equal(1, await _services.Store.Collection("notification")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("event_type", "proofread_feedback"),
                Builders<BsonDocument>.Filter.Eq("project_id", page.Project["_id"]))));
    }

    [Fact]
    public async Task Send_EnabledSameKeyDifferentOperationConflicts()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));

        await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "idem-0018", default);
        var error = await Assert.ThrowsAsync<ApiError>(() =>
            service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
                page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = false }), "idem-0018", default));
        Assert.Equal(409, error.Status);
        Assert.Equal("相同操作键不能用于不同内容", error.Message);
    }

    [Fact]
    public async Task Bake_RendersMarkedPageAndPreparesNotification()
    {
        var page = await CreateChangedPageAsync();
        var renderer = new FakeRenderer();
        var service = Service(Configuration(true), renderer);
        await new ObjectStorage(Configuration(true)).PutAsync("files/page.png",
            new MemoryStream([9, 9, 9]), "image/png", default);

        var send = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0019", default);
        var noteId = ObjectId.Parse((string)send.Body["notification_id"]!);
        var note = await NoteAsync(noteId);
        await service.BakeAsync(JobPayload(note!, page.Project), default);

        var baked = await NoteAsync(noteId);
        Assert.Equal("published", baked!["state"].AsString);
        Assert.Equal(1, renderer.Renders);
        var finalPayload = baked["payload"].AsBsonDocument;
        Assert.Contains("<img", finalPayload["html"].AsString);
        Assert.Contains("页面图片", finalPayload["text"].AsString);
        Assert.Equal(page.ActorEmail, finalPayload["reply_to"].AsString);
        var pageDoc = finalPayload["html"].AsString;
        Assert.Contains("proofread-fp-92-1-page.webp", pageDoc);
    }

    [Fact]
    public async Task Bake_IsIdempotentAndDoesNotDoubleReceipt()
    {
        var page = await CreateChangedPageAsync();
        var renderer = new FakeRenderer();
        var service = Service(Configuration(true), renderer);
        await new ObjectStorage(Configuration(true)).PutAsync("files/page.png",
            new MemoryStream([9, 9, 9]), "image/png", default);
        var send = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0020", default);
        var noteId = ObjectId.Parse((string)send.Body["notification_id"]!);
        var note = await NoteAsync(noteId);

        await service.BakeAsync(JobPayload(note!, page.Project), default);
        var receiptsAfterFirstBake = await _services.Store.Collection("notification_receipt")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("notification_id", noteId));
        await service.BakeAsync(JobPayload(note!, page.Project), default);

        // The baker flips draft -> preparing and dispatches inline (Python
        // behavior), so the second call must be a no-op rather than adding
        // another receipt.
        Assert.Equal(receiptsAfterFirstBake, await _services.Store.Collection("notification_receipt")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("notification_id", noteId)));
        Assert.Equal(1, renderer.Renders);
    }

    [Fact]
    public async Task Bake_RevokedWhileBakingLeavesDraftAndSendsNothing()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));
        var send = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0021", default);
        var noteId = ObjectId.Parse((string)send.Body["notification_id"]!);
        var note = await NoteAsync(noteId);
        await _services.Store.Collection("notification").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", noteId),
            Builders<BsonDocument>.Update.Set("revoked_at", _clock.Now.UtcDateTime).Set("state", "cancelled"));

        await service.BakeAsync(JobPayload(note!, page.Project), default);

        var after = await NoteAsync(noteId);
        Assert.Equal("cancelled", after!["state"].AsString);
        Assert.Empty(_mail.Messages);
    }

    [Fact]
    public async Task Bake_StaleRevisionCancelsDraft()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));
        var send = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0022", default);
        var noteId = ObjectId.Parse((string)send.Body["notification_id"]!);
        var note = await NoteAsync(noteId);
        await _services.Store.Collection("project").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", page.Project["_id"]),
            Builders<BsonDocument>.Update.Inc("label_revision", 1));

        await service.BakeAsync(JobPayload(note!, page.Project), default);

        var after = await NoteAsync(noteId);
        Assert.Equal("cancelled", after!["state"].AsString);
        Assert.Equal("label_revision_stale", after["error"].AsString);
    }

    [Fact]
    public async Task Bake_CancellationLeavesRecoverableDraft()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));
        var send = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0023", default);
        var noteId = ObjectId.Parse((string)send.Body["notification_id"]!);
        var note = await NoteAsync(noteId);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.BakeAsync(JobPayload(note!, page.Project), cancel.Token));

        var after = await NoteAsync(noteId);
        Assert.Equal("draft", after!["state"].AsString);
    }

    [Fact]
    public async Task Bake_RecipientRemovedBeforeDispatchDropsRecipient()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));
        var send = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(new { cc_myself = false }), "key-0024", default);
        var noteId = ObjectId.Parse((string)send.Body["notification_id"]!);
        var note = await NoteAsync(noteId);
        await _services.Store.Collection("project_member").DeleteManyAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("project", page.Project["_id"]),
                Builders<BsonDocument>.Filter.Eq("tags", "translator")));

        await service.BakeAsync(JobPayload(note!, page.Project), default);

        var after = await NoteAsync(noteId);
        // Python's dispatch seals an empty audience as published rather than
        // cancelling; the important guarantee is that no mail is sent.
        Assert.Equal("published", after!["state"].AsString);
        Assert.Empty(_mail.Messages);
    }

    [Fact]
    public async Task Bake_AuthorPermissionLostCancels()
    {
        var page = await CreateChangedPageAsync();
        var service = Service(Configuration(true));
        var send = await service.SendAsync(page.Actor, page.Project["_id"].AsObjectId.ToString(),
            page.Target["_id"].AsObjectId.ToString(), Body(), "key-0025", default);
        var noteId = ObjectId.Parse((string)send.Body["notification_id"]!);
        var note = await NoteAsync(noteId);
        await _services.Store.Collection("project_member").DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("user", page.Actor["_id"]));
        await _services.Store.Collection("project").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", page.Project["_id"]),
            Builders<BsonDocument>.Update.Set("ou", ObjectId.GenerateNewId()));

        await service.BakeAsync(JobPayload(note!, page.Project), default);

        var after = await NoteAsync(noteId);
        Assert.Equal("cancelled", after!["state"].AsString);
        Assert.Equal("sender_permission_lost", after["error"].AsString);
    }

    [Fact]
    public async Task Bake_PythonJsonSizeUsesLegacyEightMiBCap()
    {
        var huge = new BsonDocument
        {
            ["html"] = new string('x', 8 * 1024 * 1024 + 1000),
            ["text"] = "",
            ["reply_to"] = "owner@example.test",
        };
        Assert.True(ProofreadFeedbackService.PythonJsonSize(huge) > ProofreadFeedbackService.MaxPayloadBytes);
        Assert.True(ProofreadFeedbackService.PythonJsonSize(new BsonDocument { ["a"] = 1 }) < 1024);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MarkerFingerprint_MatchesPythonOracle()
    {
        var first = ProofreadImageProcessor.Fingerprint(
            [new ProofreadMarker(0.5, 0.25, 1, 1), new ProofreadMarker(0.125, -0.0, 2, 23)], 92);
        var second = ProofreadImageProcessor.Fingerprint(
            [new ProofreadMarker(1e-05, 0.3333333333333333, 2, 7), new ProofreadMarker(0.0, 0.0, 1, 2)], 0);
        var third = ProofreadImageProcessor.Fingerprint(
            [new ProofreadMarker(0.123456789, 0.987654321, 1, 3)], 100);

        Assert.Equal("1bc2823a", first);
        Assert.Equal("d7ad7162", second);
        Assert.Equal("a361217a", third);
    }

    [Fact]
    public void MarkerRender_ResizesAndPaintsColors()
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 800, 400);
        image.Format = ImageMagick.MagickFormat.Png;
        var source = image.ToByteArray();

        var inside = ProofreadImageProcessor.Render(source,
            [new ProofreadMarker(0.5, 0.5, 1, 1)], 92);
        var outside = ProofreadImageProcessor.Render(source,
            [new ProofreadMarker(0.25, 0.25, 2, 2)], 92);

        Assert.NotNull(inside);
        Assert.NotNull(outside);
        using var renderedInside = new ImageMagick.MagickImage(inside!);
        Assert.Equal(640u, renderedInside.Width);
        Assert.Equal(320u, renderedInside.Height);
        var pixel = renderedInside.GetPixels().GetPixel(310, 160).ToColor()!;
        Assert.InRange(pixel.R, 250, 255);
        Assert.InRange(pixel.G, 95, 110);
        Assert.InRange(pixel.B, 118, 130);
        Assert.Equal(235, pixel.A);

        using var renderedOutside = new ImageMagick.MagickImage(outside!);
        var outPixel = renderedOutside.GetPixels().GetPixel(150, 80).ToColor()!;
        Assert.InRange(outPixel.R, 250, 255);
        Assert.InRange(outPixel.G, 208, 218);
        Assert.InRange(outPixel.B, 126, 136);
    }

    [Fact]
    public void MarkerRender_ClampsOpacityAndRejectsCorruptBase()
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 100, 100);
        image.Format = ImageMagick.MagickFormat.Png;
        var source = image.ToByteArray();

        using var zero = new ImageMagick.MagickImage(ProofreadImageProcessor.Render(source,
            [new ProofreadMarker(0.5, 0.5, 1, 1)], -5)!);
        Assert.Equal(0, zero.GetPixels().GetPixel(44, 50).ToColor()!.A);
        using var hundred = new ImageMagick.MagickImage(ProofreadImageProcessor.Render(source,
            [new ProofreadMarker(0.5, 0.5, 1, 1)], 150)!);
        Assert.Equal(255, hundred.GetPixels().GetPixel(44, 50).ToColor()!.A);
        Assert.Null(ProofreadImageProcessor.Render([1, 2, 3], [new ProofreadMarker(0.5, 0.5, 1, 1)], 92));
        var copy = source.ToArray();
        ProofreadImageProcessor.Render(copy, [new ProofreadMarker(0.5, 0.5, 1, 1)], 92);
        Assert.Equal(source, copy);
    }

    [Fact]
    public void MarkerRender_SkipsZeroPositionAndDisabledLabels()
    {
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.White, 100, 100);
        image.Format = ImageMagick.MagickFormat.Png;
        var source = image.ToByteArray();
        var rendered = ProofreadImageProcessor.Render(source,
            [new ProofreadMarker(0.0, 0.0, 1, 1)], 92);
        Assert.NotNull(rendered);
        using var output = new ImageMagick.MagickImage(rendered!);
        Assert.Equal(255, output.GetPixels().GetPixel(50, 50).ToColor()!.A);
    }

    [Fact]
    public void DiffHtml_UsesPythonMarkupAndEscapes()
    {
        var html = ProofreadFeedbackService.GenerateDiffHtml("a<b", "a<B");
        Assert.Contains("&lt;", html);
        Assert.Contains("<del style=", html);
        Assert.Contains("<ins style=", html);
        Assert.Contains("#ffdce0", html);
        Assert.Contains("#dcffe4", html);
    }

    // ------------------------------------------------------------------
    // HTTP
    // ------------------------------------------------------------------
    private WebApplicationFactory<Program> HttpApp(bool enabled, params string[] allowlist)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Notifications:Enabled"] = enabled ? "true" : "false",
            ["Notifications:SiteOrigin"] = "https://example.test",
            ["Notifications:WorkerEnabled"] = "false",
            ["Notifications:UserEmailEnabled"] = "true",
            ["Notifications:TrustExistingEmails"] = "true",
            ["Site:Name"] = "萌翻",
            ["Site:Origin"] = "https://example.test",
            ["Storage:Type"] = "Local",
            ["Storage:Root"] = _storageRoot,
            ["Storage:Domain"] = "https://storage.example.test",
            ["Mail:Enabled"] = "true",
            ["Mail:Host"] = "127.0.0.1",
            ["Mail:Port"] = "21128",
            ["Mail:From"] = "noreply@example.test",
        };
        for (var i = 0; i < allowlist.Length; i++)
            settings[$"Notifications:EmailAllowlist:{i}"] = allowlist[i];
        return _services.App.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<INotificationMailTransport>(_mail));
        });
    }

    private static HttpClient Authenticated(WebApplicationFactory<Program> app, BsonDocument user)
    {
        var client = app.CreateClient();
        var token = app.Services.GetRequiredService<LegacyToken>()
            .Issue(user["_id"].AsObjectId.ToString(), user.Text("p"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(Fixture Fixture, HttpClient Client, WebApplicationFactory<Program> App)> HttpFixture(
        bool enabled, params string[] allowlist)
    {
        var page = await CreateChangedPageAsync();
        var app = HttpApp(enabled, allowlist);
        var client = Authenticated(app, page.Actor);
        return (page, client, app);
    }

    private static string SendPath(Fixture page) =>
        $"/v1/projects/{page.Project["_id"].AsObjectId}/targets/{page.Target["_id"].AsObjectId}/send-proofread-draft";

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Http_RequiresToken()
    {
        var page = await CreateChangedPageAsync();
        using var app = HttpApp(false);
        var client = app.CreateClient();
        var response = await Post(client, SendPath(page), new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NeedTokenError", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Http_RejectsNonObjectAndUnknownFields()
    {
        var (page, client, app) = await HttpFixture(false);
        using (app)
        {
            var array = await Post(client, SendPath(page), new[] { 1, 2 }, "http-0001");
            Assert.Equal(HttpStatusCode.BadRequest, array.StatusCode);
            var arrayError = await array.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("请求必须是 JSON 对象", arrayError.GetProperty("message").GetString());

            var unknown = await Post(client, SendPath(page), new { nope = true }, "http-0002");
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            var unknownError = await unknown.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("请求包含不支持的字段", unknownError.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task Http_NoPermissionReturns403()
    {
        var page = await CreateChangedPageAsync();
        var stranger = await InsertUserAsync("外人", UniqueEmail("stranger@example.test"));
        using var app = HttpApp(false);
        var client = Authenticated(app, stranger);
        var response = await Post(client, SendPath(page), new { }, "http-0003");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_ForeignTargetReturns4024()
    {
        var page = await CreateChangedPageAsync();
        var other = await InsertProjectAsync(Oid(page.Project["t"]), Oid(page.Actor["_id"]));
        await InsertMemberAsync(Oid(other["_id"]), Oid(page.Actor["_id"]), "proofreader");
        using var app = HttpApp(false);
        var client = Authenticated(app, page.Actor);
        var path = $"/v1/projects/{other["_id"].AsObjectId}/targets/{page.Target["_id"].AsObjectId}/send-proofread-draft";
        var response = await Post(client, path, new { }, "http-0004");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4024, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Http_DisabledReturns200AndUsesFakeTransport()
    {
        var page = await CreateChangedPageAsync();
        using var app = HttpApp(false, page.ActorEmail, page.TranslatorEmail);
        var client = Authenticated(app, page.Actor);
        var response = await Post(client, SendPath(page), new { cc_myself = true }, "http-0005");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("校对稿已成功发送至翻译邮箱", body.GetProperty("message").GetString());
        Assert.True(body.GetProperty("changed_pages_count").GetInt32() >= 1);
        // cc_myself defaults to true, so the translator plus the actor receive mail.
        Assert.Equal(2, _mail.Messages.Count);
        Assert.Contains(_mail.Messages, message => message.To == page.TranslatorEmail);
        Assert.Contains(_mail.Messages, message => message.To == page.ActorEmail);
    }

    [Fact]
    public async Task Http_EnabledReturns202AndIsIdempotent()
    {
        var (page, client, app) = await HttpFixture(true);
        using (app)
        {
            var first = await Post(client, SendPath(page), new { }, "http-idem-0001");
            Assert.Equal((HttpStatusCode)202, first.StatusCode);
            var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
            var noteId = firstBody.GetProperty("notification_id").GetString();
            Assert.NotNull(noteId);

            var second = await Post(client, SendPath(page), new { }, "http-idem-0001");
            Assert.Equal((HttpStatusCode)202, second.StatusCode);
            var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(noteId, secondBody.GetProperty("notification_id").GetString());

            var conflict = await Post(client, SendPath(page), new { cc_myself = false }, "http-idem-0001");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
    }
}
