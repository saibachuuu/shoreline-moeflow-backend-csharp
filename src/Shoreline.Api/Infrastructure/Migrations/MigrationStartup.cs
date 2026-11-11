using Shoreline.Api.Infrastructure.Migrations;

namespace Shoreline.Api.Infrastructure;

/// <summary>
/// Startup migration policy. Mirrors the <c>manage.py migrate</c> flags:
/// <c>--check</c> (default, read-only deploy gate), <c>--dry-run</c> and
/// applying, plus an <c>Enabled</c> kill switch. The default never modifies
/// the database; an operator explicitly opts into <c>apply</c>.
/// </summary>
public sealed class MigrationOptions
{
    public const string SectionName = "Migrations";

    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "check";

    public MigrationMode ParsedMode
    {
        get
        {
            var mode = (Mode ?? string.Empty).Trim().ToLowerInvariant();
            return mode switch
            {
                "" or "check" => MigrationMode.Check,
                "apply" => MigrationMode.Apply,
                "dry-run" or "dryrun" => MigrationMode.DryRun,
                _ => throw new MigrationException(
                    $"Unknown Migrations:Mode '{Mode}'; expected 'check', 'apply' or 'dry-run'"),
            };
        }
    }
}

public enum MigrationMode
{
    Check,
    Apply,
    DryRun,
}

/// <summary>
/// Records what the startup migration pass saw. When <c>check</c> mode finds
/// pending migrations, <c>/health/ready</c> reports 503 with these ids; the
/// process stays up so the operator can inspect it, but it never serves
/// traffic as ready. Populated once at startup (a snapshot, mirroring the
/// Python <c>--check</c> deploy gate); applying later requires a restart.
/// </summary>
public sealed class MigrationStartupState
{
    public bool Enabled { get; set; } = true;
    public MigrationMode Mode { get; set; } = MigrationMode.Check;
    public IReadOnlyList<string> PendingIds { get; set; } = [];
    public IReadOnlyList<string> AppliedIds { get; set; } = [];
    public DateTime? CheckedAtUtc { get; set; }
    public string? Error { get; set; }
}

/// <summary>Testable readiness evaluation for <c>/health/ready</c>.</summary>
public static class MigrationReadiness
{
    public static bool IsReady(MigrationStartupState state, out IReadOnlyList<string> pendingIds)
    {
        pendingIds = state.PendingIds;
        if (!state.Enabled) return true;
        if (state.Mode != MigrationMode.Check) return true;
        return state.PendingIds.Count == 0;
    }
}

public static class MigrationStartup
{
    /// <summary>
    /// Runs the configured startup migration pass and records the outcome in
    /// the shared <see cref="MigrationStartupState"/>. Check and dry-run are
    /// strictly read-only; only <c>apply</c> writes.
    /// </summary>
    public static async Task<MigrationStartupState> RunLegacyMigrationsAsync(
        this IServiceProvider services, ILogger log, CancellationToken ct)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var options = configuration.GetSection(MigrationOptions.SectionName).Get<MigrationOptions>()
            ?? new MigrationOptions();
        var state = services.GetRequiredService<MigrationStartupState>();
        state.Enabled = options.Enabled;
        state.Mode = options.ParsedMode;

        if (!options.Enabled)
        {
            log.LogInformation("Legacy migrations disabled (Migrations:Enabled=false); skipping");
            state.CheckedAtUtc = DateTime.UtcNow;
            return state;
        }

        var database = services.GetRequiredService<MongoStore>().Database;
        var runner = new LegacyMigrationRunner(database, LegacyMigrationRunner.DefaultMigrations(), log);
        try
        {
            switch (options.ParsedMode)
            {
                case MigrationMode.Check:
                {
                    var pending = await runner.CheckAsync(ct);
                    state.PendingIds = pending.Select(m => m.Version).ToArray();
                    state.CheckedAtUtc = DateTime.UtcNow;
                    if (pending.Count > 0)
                    {
                        log.LogWarning(
                            "Migration check found {Count} pending migration(s): {Ids}. " +
                            "/health/ready will report 503 until Migrations:Mode=apply runs them.",
                            pending.Count,
                            string.Join(", ", pending.Select(m => $"{m.Version} {m.Name}")));
                    }
                    else
                    {
                        log.LogInformation("Migration check clean: no pending migrations");
                    }
                    break;
                }
                case MigrationMode.DryRun:
                {
                    var pending = await runner.DryRunAsync("AUTO", ct);
                    state.PendingIds = pending.Select(m => m.Version).ToArray();
                    state.CheckedAtUtc = DateTime.UtcNow;
                    foreach (var migration in pending)
                        log.LogInformation("PENDING {Version} {Level} {Name}", migration.Version, migration.Level, migration.Name);
                    break;
                }
                case MigrationMode.Apply:
                {
                    var applied = await runner.ApplyAsync("AUTO", ct);
                    state.AppliedIds = applied.Select(m => m.Version).ToArray();
                    state.PendingIds = (await runner.PendingAsync("ALL", ct)).Select(m => m.Version).ToArray();
                    state.CheckedAtUtc = DateTime.UtcNow;
                    foreach (var migration in applied)
                        log.LogInformation("APPLIED {Version} {Name}", migration.Version, migration.Name);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.CheckedAtUtc = DateTime.UtcNow;
            throw;
        }
        return state;
    }
}
