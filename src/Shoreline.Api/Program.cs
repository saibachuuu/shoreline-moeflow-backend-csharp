using System.Text.Json;
using System.Threading.RateLimiting;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Jobs;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MongoStore>();
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
builder.Services.AddSingleton<IdentityNameGate>();
builder.Services.AddSingleton<ArchiveSecrets>();
builder.Services.AddSingleton<IdentityAuditWriter>();
builder.Services.AddScoped<TeamMemberService>();
builder.Services.AddScoped<TeamService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ProjectMemberService>();
builder.Services.AddScoped<ProjectLifecycleService>();
builder.Services.AddScoped<IdentityTagPolicyService>();
builder.Services.AddSingleton<ObjectStorage>();
builder.Services.AddSingleton<JobQueue>();
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
app.UseCors();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ApiError error)
    {
        context.Response.StatusCode = error.Status;
        var body = new Dictionary<string, object?> { ["error"] = error.Error, ["code"] = error.Code, ["message"] = error.ResponseMessage };
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
        app.Logger.LogError("Request failed ({ErrorType})", e.GetType().Name);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "InternalServerError", code = 1, message = "Internal server error" });
    }
});
app.MapGet("/ping", () => Results.Text("pong"));
app.MapUsers();
app.MapEmail();
app.MapSite();
app.MapAdminUsers();
app.MapLanguages();
app.MapTypes();
app.MapTeams();
app.MapTeamIdentity();
app.MapProjects();
app.MapProjectMembers();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (MongoStore store, CancellationToken ct) =>
{
    await store.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1), cancellationToken: ct);
    return Results.Ok(new { status = "ok" });
});
await app.Services.GetRequiredService<MongoStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<LanguageCatalog>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<LegacyTypes>().InitializeAsync(CancellationToken.None);
app.Run();

public partial class Program;
