using System.Text.Json;
using System.Threading.RateLimiting;
using Shoreline.Api;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Infrastructure.Migrations;
using Shoreline.Api.Jobs;
using Shoreline.Api.Modules.ArchiveImport;
using Shoreline.Api.Modules.Mit;
using Shoreline.Api.Modules.PartnerSearch;
using Shoreline.Api.Modules.StoragePool;
using Shoreline.Api.Modules.ZitengPartner;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
LegacyEnvironmentConfiguration.Apply(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
// Without this, a handler that binds a body parameter to an empty or malformed
// payload is rejected by the framework BEFORE the handler runs, and the default
// response is a bare 400 with Content-Length: 0. Clients then get an "empty
// response" they cannot explain. Throwing instead routes the failure through the
// ApiError middleware below, which answers with {error, code, message}.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MongoStore>();
builder.Services.AddSingleton<MigrationStartupState>();
builder.Services.AddSingleton<LegacyToken>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<CaptchaService>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<UserView>();
builder.Services.AddScoped<UserProfileService>();
builder.Services.AddScoped<UserRegistrationService>();
builder.Services.AddSingleton<SiteSettings>();
builder.Services.AddSingleton<LanguageCatalog>();
builder.Services.AddSingleton<LegacyTypes>();
builder.Services.AddScoped<IdentityPermissions>();
builder.Services.AddSingleton<TeamMutationGate>();
builder.Services.AddSingleton<InvitationMutationGate>();
builder.Services.AddSingleton<IdentityNameGate>();
builder.Services.AddSingleton<ArchiveSecrets>();
builder.Services.AddSingleton<IdentityAuditWriter>();
builder.Services.AddScoped<TeamMemberService>();
builder.Services.AddScoped<TeamService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddFileServices();
builder.Services.AddFileParseServices();
builder.Services.AddOutputServices();
builder.Services.AddNotificationManagementServices();
builder.Services.AddTeamDissolutionServices();
builder.Services.AddTeamInsightServices();
builder.Services.AddProjectImports();
builder.Services.AddProofreadFeedbackServices();
builder.Services.AddMagickProofreadImageRenderer();
builder.Services.AddThumbnailServices();
builder.Services.AddArchiveImportServices();
builder.Services.AddPartnerSearchServices();
// The in-process StoragePool worker is enabled outside tests, matching Python's
// always-registered beat entry. The database switches (se/re) remain the
// runtime gate; StoragePool:WorkerEnabled is only an operator kill switch.
if (builder.Configuration["StoragePool:WorkerEnabled"] is null)
    builder.Configuration["StoragePool:WorkerEnabled"] = builder.Environment.IsEnvironment("Testing") ? "false" : "true";
builder.Services.AddStoragePoolServices();
builder.Services.AddZitengPartner();
builder.Services.AddAdditionalListServices();
builder.Services.AddTermServices();
builder.Services.AddPresenceServices();
builder.Services.AddLabelServices();
builder.Services.AddLabelSnapshots();
builder.Services.AddAvatarServices();
builder.Services.AddAdminStorageOverviewServices();
builder.Services.AddNotificationServices();
builder.Services.AddNotificationPublishServices();
builder.Services.AddMitServices();
builder.Services.AddMitBridge();
builder.Services.AddScoped<ProjectMemberService>();
builder.Services.AddScoped<ProjectLifecycleService>();
builder.Services.AddScoped<ProjectInvitationService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<ApplicationService>();
builder.Services.AddScoped<IdentityTagPolicyService>();
builder.Services.AddScoped<BootstrapService>();
builder.Services.AddSingleton<ObjectStorage>();
builder.Services.AddSingleton(sp => new JobQueue(
    sp.GetRequiredService<MongoStore>(),
    sp.GetRequiredService<TimeProvider>(),
    maxAttempts: sp.GetRequiredService<IConfiguration>().GetValue("Jobs:MaxAttempts", JobQueue.DefaultMaxAttempts)));
builder.Services.AddSingleton<EmailCodeService>();
builder.Services.AddSingleton<IJobHandler, SecurityEmailHandler>();
builder.Services.AddSingleton<IJobHandler, TeamCreationHandler>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetSlidingWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 4,
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost"])
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
    .WithHeaders("Authorization", "Content-Type", "X-Label-Revision", "X-Request-ID", "Idempotency-Key")
    .WithExposedHeaders("X-Pagination-Page", "X-Pagination-Limit", "X-Pagination-Count", "X-Pagination-Page-Count", "X-Label-Revision", "X-Label-Project")));
var app = builder.Build();
// Trusted proxy support is opt-in and fail closed: with no explicit proxy list
// the forwarded headers are not interpreted at all, so a client cannot choose
// its own rate-limit partition by sending X-Forwarded-For. Operators behind the
// bundled nginx must list the proxy address (Security:TrustedProxies:0=...).
var trustedProxyOptions = TrustedProxyConfiguration.Create(
    app.Configuration.GetSection(TrustedProxyConfiguration.SectionName).Get<string[]>());
if (trustedProxyOptions is not null) app.UseForwardedHeaders(trustedProxyOptions);
// Python's init_db always prepares the site setting, administrator and default
// team. Tests run against disposable databases with their own fixtures, so the
// bootstrap is opt-out there unless explicitly requested. Read the merged
// configuration after Build so WebApplicationFactory overrides are visible.
var bootstrapEnabled = bool.TryParse(app.Configuration["Bootstrap:Enabled"], out var configuredBootstrap)
    ? configuredBootstrap
    : !app.Environment.IsEnvironment("Testing");
app.UseCors();
// Endpoint-scoped rate limiting policies are resolved from routing metadata, so
// routing must run first. With the previous order (UseRateLimiter before
// UseRouting) the "auth" policy silently never applied and login/email-code
// endpoints were unthrottled.
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ApiError error)
    {
        context.Response.StatusCode = error.Status;
        var chinese = LegacyMessages.Chinese(context);
        var message = error.ResponseMessage is string text
            ? LegacyMessages.Translate(text, chinese)
            : error.ResponseMessage;
        var body = new Dictionary<string, object?> { ["error"] = error.Error, ["code"] = error.Code, ["message"] = message };
        if (error.IdentityCode is not null) body["identity_code"] = error.IdentityCode;
        await context.Response.WriteAsJsonAsync(body);
    }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "ValidateError", code = 2, message = "Invalid request" });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception e)
    {
        app.Logger.LogError(e, "Request failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "InternalServerError", code = 1, message = "Internal server error" });
    }
});
app.UseLabelRequestGuard();
app.MapGet("/ping", () => Results.Text("pong"));
app.MapLegacyDiagnostics();
app.MapUsers();
app.MapEmail();
app.MapSite();
app.MapAdminUsers();
app.MapAdditionalLists();
app.MapLanguages();
app.MapTypes();
app.MapTeams();
app.MapTeamIdentity();
app.MapProjects();
app.MapLabels();
app.MapLabelSnapshots();
app.MapTerms();
app.MapPresence();
app.MapAvatarEndpoints();
app.MapAdminStorageOverviewEndpoints();
app.MapNotificationEndpoints();
app.MapNotificationPublishEndpoints();
app.MapProjectMembers();
app.MapInvitations();
app.MapApplications();
app.MapFileEndpoints();
app.MapOcr();
OutputEndpoints.MapOutputs(app);
NotificationManagementEndpoints.MapNotificationManagementEndpoints(app);
app.MapTeamDissolution();
TeamInsightEndpoints.MapTeamInsights(app);
app.MapProjectImports();
app.MapProofreadFeedbackEndpoints();
app.MapThumbnailEndpoints();
app.MapArchiveImports();
app.MapPartnerSearch();
app.MapStoragePool();
app.MapZitengPartner();
var mitStorageRoot = builder.Configuration["Mit:StorageRoot"] ?? Environment.GetEnvironmentVariable("MIT_STORAGE_ROOT");
if (!string.IsNullOrWhiteSpace(mitStorageRoot) && mitStorageRoot != "/MIT_STORAGE_ROOT_UNDEFINED")
{
    // MIT is fail closed. The routes are mapped when queued work can actually
    // run: either every job kind has a registered in-process consumer (the
    // legacy path), or the Celery bridge is enabled (RabbitMq:Enabled=true
    // with a non-empty RabbitMq:Url, 方案 A in docs/mit-bridge-spike.md §4).
    // Otherwise the insertion would be silently accepted and never executed.
    var missingMitConsumers = MitEndpoints.FindMissingConsumers(app.Services);
    var mitBridgeSettings = MitBridgeSettings.FromConfiguration(builder.Configuration);
    if (missingMitConsumers.Length == 0 || mitBridgeSettings.IsBridgeEnabled)
    {
        if (mitBridgeSettings.IsBridgeEnabled && missingMitConsumers.Length != 0)
        {
            app.Logger.LogInformation(
                "Mapping /v1/mit via the RabbitMQ Celery bridge ({Settings}); " +
                "in-process consumers missing for {Kinds}, results polled from celery_taskmeta.",
                mitBridgeSettings.Describe(),
                string.Join(", ", missingMitConsumers));
        }
        app.MapMitEndpoints();
    }
    else
    {
        app.Logger.LogWarning(
            "MIT_STORAGE_ROOT is configured but no job consumer is registered for {Kinds}; /v1/mit routes stay unmapped. " +
            "Actual MIT parity requires an in-process handler or a supported external worker bridge.",
            string.Join(", ", missingMitConsumers));
    }
}
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (MongoStore store, MigrationStartupState migrations, CancellationToken ct) =>
{
    if (!MigrationReadiness.IsReady(migrations, out var pendingIds))
    {
        // check mode with pending migrations: stay up for inspection but
        // never report ready, mirroring the Python --check deploy gate.
        return Results.Json(new { status = "migrations-pending", pending = pendingIds }, statusCode: 503);
    }
    await store.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: ct);
    return Results.Ok(new { status = "ok" });
});
// Versioned schema migrations run before the startup index initializers so a
// fresh database converges through the same chain Python applies; check mode
// (the default) is strictly read-only and never modifies the database.
await app.Services.RunLegacyMigrationsAsync(app.Logger, CancellationToken.None);
await app.Services.GetRequiredService<MongoStore>().InitializeAsync(CancellationToken.None);
await app.Services.InitializeFileIndexesAsync();
await app.Services.InitializeTeamDissolutionAsync();
await ProofreadFeedbackRegistration.InitializeAsync(
    app.Services.GetRequiredService<MongoStore>(), CancellationToken.None);
await app.Services.InitializeArchiveImportIndexesAsync();
await app.Services.InitializeStoragePoolAsync();
await app.Services.InitializeZitengPartnerAsync();
await app.Services.InitializeLabelIndexesAsync();
await app.Services.InitializeLabelSnapshotIndexesAsync();
await NotificationRegistration.InitializeAsync(
    app.Services.GetRequiredService<MongoStore>(), CancellationToken.None);
await NotificationPublishRegistration.InitializeAsync(
    app.Services.GetRequiredService<MongoStore>(), CancellationToken.None);
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<PresenceService>().InitializeAsync(CancellationToken.None);
}
await app.Services.GetRequiredService<LanguageCatalog>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<LegacyTypes>().InitializeAsync(CancellationToken.None);
// A request killed mid-flight leaves its project_label_lock row behind and
// every later request for that project answers 409 LabelBusyError. The row has
// no TTL (Python behaves the same), so drop whatever a previous process owned
// before accepting traffic. Single-writer deployment only; replicas would need
// a heartbeat instead.
await app.Services.GetRequiredService<ProjectLabelLock>().ClearStaleAsync(
    System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
    CancellationToken.None);
if (bootstrapEnabled)
{
    using var bootstrapScope = app.Services.CreateScope();
    await bootstrapScope.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(CancellationToken.None);
}
app.Run();

public partial class Program;
