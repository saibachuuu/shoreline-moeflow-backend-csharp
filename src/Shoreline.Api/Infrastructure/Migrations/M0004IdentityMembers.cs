using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Api.Infrastructure.Migrations;

/// <summary>
/// m0004: migrate legacy roles and workers into the identity-tag collections.
///
/// Faithful port of <c>m0004_identity_members.py</c>: raw BSON documents are
/// used throughout so the pre-identity schema stays readable after the
/// runtime stops loading legacy relations. Two deliberate differences from
/// Python are documented in <c>docs/migrations.md</c>: the filesystem
/// evidence bundle is not written (reports stay in
/// <c>identity_migration_report</c>), and Unicode casefolding uses
/// <c>ToLowerInvariant</c>.
/// </summary>
public sealed class M0004IdentityMembers : LegacyMigration
{
    public const string ReportCollection = "identity_migration_report";
    public const string ReportSummaryId = "identity-v1-summary";
    private const string MappingVersion = "identity-v1";
    private const string SchemaVersion = "0004";

    public override string Version => "0004";
    public override string Name => "identity_members";
    public override string Description => "Migrate legacy roles and workers into identity members";
    protected override string ChecksumSpec =>
        "identity:project_role_tags=creator,admin,proofreader,translator,picture_editor,coordinator,supporter;" +
        "team_role_tags=creator,admin;worker_tags=provider,图源,scan,扫图,scan_retoucher,修图,translator,翻译," +
        "proofreader,校对,picture_editor,嵌字;indexes=project_member_project_user_v1,project_member_project_external_v1," +
        "project_member_identity_key_v1,team_member_team_user_v1,project_member_status_v1,project_member_tags_v1," +
        "project_member_display_name_v1";

    private static readonly IReadOnlyDictionary<string, string> ProjectRoleTags =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["creator"] = "creator",
            ["admin"] = "admin",
            ["proofreader"] = "proofreader",
            ["translator"] = "translator",
            ["picture_editor"] = "typesetter",
            ["coordinator"] = "proofreader",
            ["supporter"] = "translator",
        };

    private static readonly IReadOnlyDictionary<string, string> TeamRoleTags =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["creator"] = "creator",
            ["admin"] = "admin",
        };

    private static readonly IReadOnlyDictionary<string, int> TeamRolePriority =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["member"] = 0,
            ["admin"] = 1,
            ["creator"] = 2,
        };

    private static readonly IReadOnlyDictionary<string, string> WorkerTags =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = "raw_provider",
            ["图源"] = "raw_provider",
            ["scan"] = "scanner",
            ["扫图"] = "scanner",
            ["scan_retoucher"] = "cleaner",
            ["修图"] = "cleaner",
            ["translator"] = "translator",
            ["翻译"] = "translator",
            ["proofreader"] = "proofreader",
            ["校对"] = "proofreader",
            ["picture_editor"] = "typesetter",
            ["嵌字"] = "typesetter",
        };

    private static readonly HashSet<string> ProjectStatusValues = ["0", "1", "5"];
    private static readonly HashSet<string> MemberStatusValues = ["active", "invited", "removed"];
    private static readonly HashSet<string> TeamMemberStatusValues = ["active", "removed"];

    private static readonly DateTime MigrationEpoch =
        new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid ExternalNamespace = Uuid5(
        Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8"), "moeflow:identity-migration:v1");

    private static readonly string[] SummaryCounterKeys =
    [
        "projects", "teams", "project_members", "team_members", "external_members",
        "workers_projects", "worker_values", "workers_issue_count",
        "project_relation_count", "project_relation_duplicate_count",
        "team_relation_count", "team_relation_duplicate_count",
        "legacy_relation_tag_count", "status_plan_count", "status_issue_count",
        "owner_issue_count", "owner_repaired_count", "owner_missing_count",
        "owner_conflict_count", "owner_invalid_count", "custom_role_issue_count",
        "identity_member_duplicate_count", "identity_member_repaired_count",
        "identity_member_reported_invalid_count",
        "invitation_count", "application_count",
        "pending_invitation_count", "allowed_invitation_count", "denied_invitation_count",
        "pending_application_count", "allowed_application_count", "denied_application_count",
        "project_members_from_join_process", "join_process_issue_count",
    ];

    public override async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        var state = new RunState { MigrationTime = DateTime.UtcNow };
        state.BatchId = ConfiguredBatchId(state.MigrationTime);
        foreach (var key in SummaryCounterKeys)
            state.Summary[key] = 0;

        await MergeExistingDocumentsAsync(database, state, "project_member", team: false, ct);
        await MergeExistingDocumentsAsync(database, state, "team_member", team: true, ct);

        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var doc in ListAsync(database, "user",
            Builders<BsonDocument>.Projection.Include("_id").Include("n"), ct))
        {
            users[Key(doc["_id"])] = Text(doc, "n");
        }

        var projectRoles = await LoadRolesAsync(database, "project_role", ct);
        var teamRoles = await LoadRolesAsync(database, "team_role", ct);
        var projects = await MigrationBson.Collection(database, "project")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        var projectById = projects.ToDictionary(p => Key(p["_id"]), StringComparer.Ordinal);
        var teams = await MigrationBson.Collection(database, "team")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        var teamById = teams.ToDictionary(t => Key(t["_id"]), StringComparer.Ordinal);

        var projectMembers = new Dictionary<(string Project, string User), MemberDraft>();
        var ownerCandidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        await foreach (var relation in ListAsync(database, "project_user_relation",
            Builders<BsonDocument>.Projection.Exclude("_cls"), ct))
        {
            state.Summary["project_relation_count"]++;
            RecordLegacyRelation(state, "project", relation, projectRoles);
            if (relation.TryGetValue("m_t", out var tagMarker) && IsTruthy(tagMarker)) state.Summary["legacy_relation_tag_count"]++;
            var projectId = NormKey(relation.TryGetValue("g", out var g) ? g : null);
            var userId = NormKey(relation.TryGetValue("u", out var u) ? u : null);
            var roleCode = RoleCode(relation.TryGetValue("r", out var r) ? r : null, projectRoles);
            if (projectId is null || userId is null ||
                !projectById.ContainsKey(projectId) || !users.ContainsKey(userId))
            {
                AddIssue(state, "project_relation", Key(relation["_id"]), "dangling_reference",
                    new BsonDocument { ["project"] = ToBson(projectId), ["user"] = ToBson(userId) });
                continue;
            }
            if (relation.TryGetValue("r", out var roleRef) is false ||
                NormObjectId(roleRef) is not { } roleId || !projectRoles.ContainsKey(roleId))
                RecordMissingRole(state, "project", projectId, relation, projectRoles);
            string? mappedTag = roleCode is not null && ProjectRoleTags.TryGetValue(roleCode, out var tag) ? tag : null;
            if (mappedTag is null)
            {
                AddIssue(state, "project", ToObjectId(projectId)!, "unmapped_project_role", ToBson(roleCode));
                state.Summary["custom_role_issue_count"]++;
            }
            var key = (projectId, userId);
            if (!projectMembers.TryGetValue(key, out var member))
            {
                member = new MemberDraft
                {
                    Project = projectId,
                    User = userId,
                    DisplayName = users[userId].Length > 0 ? users[userId] : userId,
                    CreateTime = EventTime(relation, state.MigrationTime),
                    Status = "active",
                };
                member.EditTime = ValidDateTime(relation.TryGetValue("m_e", out var me) ? me : null)
                    ?? member.CreateTime;
                projectMembers[key] = member;
            }
            else
            {
                state.Summary["project_relation_duplicate_count"]++;
                AddIssue(state, "project_relation", Key(relation["_id"]), "duplicate_member_relation",
                    new BsonDocument
                    {
                        ["canonical_relation"] = member.RelationIds.Count > 0 ? member.RelationIds[0] : BsonNull.Value,
                        ["member"] = $"{projectId}:{userId}",
                    });
            }
            member.RelationIds.Add(relation["_id"]);
            if (mappedTag is not null) member.Tags.Add(mappedTag);
            var relationReport = Report(state, $"project_relation:{Key(relation["_id"])}");
            relationReport["project_member_key"] = $"{projectId}:u:{userId}";
            relationReport["mapped_tags"] = mappedTag is not null
                ? new BsonArray([(BsonValue)new BsonString(mappedTag)])
                : new BsonArray();
            if (roleCode == "creator")
            {
                if (!ownerCandidates.TryGetValue(projectId, out var set))
                    ownerCandidates[projectId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(userId);
            }
        }

        var joinEvents = new Dictionary<(string Project, string User), List<JoinEvent>>();
        await foreach (var invitation in ListAsync(database, "invitation",
            Builders<BsonDocument>.Projection.Exclude("_cls"), ct))
        {
            state.Summary["invitation_count"]++;
            RecordJoinProcessEvent(state, joinEvents, invitation, "invitation",
                projectById, teamById, users, projectRoles, teamRoles);
        }
        await foreach (var application in ListAsync(database, "application",
            Builders<BsonDocument>.Projection.Exclude("_cls"), ct))
        {
            state.Summary["application_count"]++;
            RecordJoinProcessEvent(state, joinEvents, application, "application",
                projectById, teamById, users, projectRoles, teamRoles);
        }
        ProjectJoinProcessMembers(state, projectMembers, joinEvents, users);

        foreach (var ((projectId, userId), member) in projectMembers)
        {
            await UpsertProjectMemberAsync(database, new BsonDocument
            {
                ["project"] = SubjectRef(projectId),
                ["user"] = SubjectRef(userId),
                ["ik"] = $"{projectId}:u:{userId}",
                ["display_name"] = member.DisplayName,
                ["status"] = member.Status,
                ["create_time"] = member.CreateTime,
                ["edit_time"] = member.EditTime,
                ["removed_time"] = member.RemovedTime is DateTime removed
                    ? (BsonValue)new BsonDateTime(removed) : BsonNull.Value,
                ["version"] = 0,
            }, member.Tags, ct);
            state.Summary["project_members"]++;
        }

        var externalMembers = new Dictionary<(string Project, string Key), ExternalDraft>();
        foreach (var project in projects)
        {
            await MigrateProjectStatusAsync(database, state, project, ct);
            MigrateWorkers(state, project, externalMembers);
        }
        foreach (var ((_, _), member) in externalMembers)
        {
            await UpsertProjectMemberAsync(database, new BsonDocument
            {
                ["project"] = SubjectRef(member.Project),
                ["external_id"] = member.ExternalId,
                ["ik"] = $"{member.Project}:e:{member.ExternalId}",
                ["display_name"] = member.DisplayName,
                ["status"] = "active",
                ["create_time"] = member.CreateTime,
                ["edit_time"] = member.EditTime,
                ["version"] = 0,
            }, member.Tags, ct);
        }

        await RepairOwnersAsync(
            database, state, projectById, teamRoles, users, ownerCandidates, projectMembers, ct);

        foreach (var team in teams)
        {
            state.Summary["teams"]++;
            await MigrationBson.Collection(database, "identity_tag_policy").UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("team", team["_id"]),
                Builders<BsonDocument>.Update
                    .SetOnInsert("team", team["_id"])
                    .SetOnInsert("team_tags", new BsonDocument())
                    .SetOnInsert("project_tags", new BsonDocument())
                    .SetOnInsert("version", 0)
                    .SetOnInsert("create_time", state.MigrationTime)
                    .SetOnInsert("edit_time", state.MigrationTime),
                new UpdateOptions { IsUpsert = true }, ct);
        }

        var teamMembers = new Dictionary<(string Team, string User), TeamDraft>();
        await foreach (var relation in ListAsync(database, "team_user_relation",
            Builders<BsonDocument>.Projection.Exclude("_cls"), ct))
        {
            state.Summary["team_relation_count"]++;
            RecordLegacyRelation(state, "team", relation, teamRoles);
            if (relation.TryGetValue("m_t", out var tagMarker) && IsTruthy(tagMarker)) state.Summary["legacy_relation_tag_count"]++;
            var teamId = NormKey(relation.TryGetValue("g", out var g) ? g : null);
            var userId = NormKey(relation.TryGetValue("u", out var u) ? u : null);
            var roleCode = RoleCode(relation.TryGetValue("r", out var r) ? r : null, teamRoles);
            if (teamId is null || userId is null ||
                !teamById.ContainsKey(teamId) || !users.ContainsKey(userId))
            {
                AddIssue(state, "team_relation", Key(relation["_id"]), "dangling_reference",
                    new BsonDocument { ["team"] = ToBson(teamId), ["user"] = ToBson(userId) });
                continue;
            }
            if (relation.TryGetValue("r", out var roleRef) is false ||
                NormObjectId(roleRef) is not { } roleId || !teamRoles.ContainsKey(roleId))
                RecordMissingRole(state, "team", teamId, relation, teamRoles);
            var baseTag = roleCode is not null && TeamRoleTags.TryGetValue(roleCode, out var mapped)
                ? mapped : "member";
            if (roleCode is not null && !TeamRoleTags.ContainsKey(roleCode) &&
                roleCode is not ("member" or "senior" or "beginner"))
            {
                AddIssue(state, "team", ToObjectId(teamId)!, "unmapped_team_role", new BsonString(roleCode));
                state.Summary["custom_role_issue_count"]++;
            }
            var key = (teamId, userId);
            if (!teamMembers.TryGetValue(key, out var current))
            {
                current = new TeamDraft
                {
                    Team = teamId,
                    User = userId,
                    CreateTime = EventTime(relation, state.MigrationTime),
                };
                current.EditTime = ValidDateTime(relation.TryGetValue("m_e", out var me) ? me : null)
                    ?? current.CreateTime;
                teamMembers[key] = current;
            }
            else
            {
                state.Summary["team_relation_duplicate_count"]++;
                AddIssue(state, "team_relation", Key(relation["_id"]), "duplicate_member_relation",
                    new BsonDocument
                    {
                        ["canonical_relation"] = current.RelationIds.Count > 0 ? current.RelationIds[0] : BsonNull.Value,
                        ["member"] = $"{teamId}:{userId}",
                    });
            }
            current.RelationIds.Add(relation["_id"]);
            var teamReport = Report(state, $"team_relation:{Key(relation["_id"])}");
            teamReport["team_member_key"] = $"{teamId}:u:{userId}";
            teamReport["mapped_base_tag"] = baseTag;
            if (TeamRolePriority[baseTag] > TeamRolePriority[current.BaseTag])
                current.BaseTag = baseTag;
        }

        foreach (var ((_, _), member) in teamMembers)
        {
            await UpsertTeamMemberAsync(database, new BsonDocument
            {
                ["team"] = ToObjectId(member.Team)!,
                ["user"] = ToObjectId(member.User)!,
                ["base_tag"] = member.BaseTag,
                ["tags"] = new BsonArray(),
                ["worker_qualifications"] = new BsonArray(),
                ["aliases"] = new BsonArray(),
                ["version"] = 0,
                ["status"] = "active",
                ["create_time"] = member.CreateTime,
                ["edit_time"] = member.EditTime,
            }, member.BaseTag, ct);
            state.Summary["team_members"]++;
        }

        foreach (var project in projects)
        {
            var activeCount = await MigrationBson.Collection(database, "project_member").CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("project", project["_id"]),
                    Builders<BsonDocument>.Filter.Eq("status", "active")),
                cancellationToken: ct);
            await MigrationBson.Collection(database, "project").UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", project["_id"]),
                Builders<BsonDocument>.Update.Set("m_uc", activeCount).Unset("w"),
                cancellationToken: ct);
        }
        foreach (var team in teams)
        {
            var activeCount = await MigrationBson.Collection(database, "team_member").CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("team", team["_id"]),
                    Builders<BsonDocument>.Filter.Eq("status", "active")),
                cancellationToken: ct);
            await MigrationBson.Collection(database, "team").UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", team["_id"]),
                Builders<BsonDocument>.Update.Set("m_uc", activeCount),
                cancellationToken: ct);
        }

        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            MigrationBson.Keys(("project", 1), ("user", 1)), "project_member_project_user_v1",
            unique: true, new BsonDocument("user", new BsonDocument("$type", "objectId")), ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            MigrationBson.Keys(("project", 1), ("external_id", 1)), "project_member_project_external_v1",
            unique: true, new BsonDocument("external_id", new BsonDocument("$type", "string")), ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            MigrationBson.Keys(("ik", 1)), "project_member_identity_key_v1",
            unique: true, new BsonDocument("ik", new BsonDocument("$type", "string")), ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "team_member",
            MigrationBson.Keys(("team", 1), ("user", 1)), "team_member_team_user_v1",
            unique: true, partialFilter: null, ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            MigrationBson.Keys(("project", 1), ("status", 1)), "project_member_status_v1",
            unique: false, partialFilter: null, ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            MigrationBson.Keys(("project", 1), ("tags", 1)), "project_member_tags_v1",
            unique: false, partialFilter: null, ct);
        await MigrationBson.EnsureNamedIndexAsync(database, "project_member",
            MigrationBson.Keys(("project", 1), ("display_name", 1)), "project_member_display_name_v1",
            unique: false, partialFilter: null, ct);

        await SaveReportsAsync(database, state, ct);
    }

    public override async Task<bool> VerifyAsync(IMongoDatabase database, CancellationToken ct)
    {
        var projects = MigrationBson.Collection(database, "project");
        if (await projects.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Nin("st", ProjectStatusValues.Select(s => (BsonValue)new BsonInt32(int.Parse(s)))),
                cancellationToken: ct) != 0) return false;
        if (await projects.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Type("stv", BsonType.Int32) |
                        Builders<BsonDocument>.Filter.Type("stv", BsonType.Int64)),
                    Builders<BsonDocument>.Filter.Lt("stv", 0)),
                cancellationToken: ct) != 0) return false;
        if (await projects.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Type("ouv", BsonType.Int32) |
                        Builders<BsonDocument>.Filter.Type("ouv", BsonType.Int64)),
                    Builders<BsonDocument>.Filter.Lt("ouv", 0)),
                cancellationToken: ct) != 0) return false;
        foreach (var field in new[] { "w", "pft", "pdt", "ft" })
        {
            if (await projects.CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.Exists(field), cancellationToken: ct) != 0)
                return false;
        }

        var allProjects = await projects.Find(FilterDefinition<BsonDocument>.Empty)
            .Project<BsonDocument>(Builders<BsonDocument>.Projection
                .Include("_id").Include("st").Include("ctm").Include("m_uc"))
            .ToListAsync(ct);
        var projectMembers = MigrationBson.Collection(database, "project_member");
        foreach (var project in allProjects)
        {
            var status = project.TryGetValue("st", out var st) && st.IsNumeric ? st.ToInt32() : -1;
            if (status == 5)
            {
                if (!project.TryGetValue("ctm", out var ctm) || !ctm.IsBsonDateTime) return false;
            }
            else if (project.Contains("ctm"))
            {
                return false;
            }
            if (!project.TryGetValue("m_uc", out var count) || !IsNonNegativeInt(count)) return false;
            var active = await projectMembers.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("project", project["_id"]),
                    Builders<BsonDocument>.Filter.Eq("status", "active")),
                cancellationToken: ct);
            if (count.ToInt64() != active) return false;
        }

        var members = await projectMembers.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        foreach (var member in members)
        {
            var projectId = NormKey(member.TryGetValue("project", out var p) ? p : null);
            var userId = NormKey(member.TryGetValue("user", out var u) ? u : null);
            var externalId = member.TryGetValue("external_id", out var e) && e.IsString ? e.AsString : null;
            var hasUser = userId is not null;
            var hasExternal = !string.IsNullOrEmpty(externalId);
            if (projectId is null || hasUser == hasExternal) return false;
            var expectedKey = hasUser ? $"{projectId}:u:{userId}" : $"{projectId}:e:{externalId}";
            if (!member.TryGetValue("ik", out var ik) || !ik.IsString || ik.AsString != expectedKey)
                return false;
            if (!member.TryGetValue("display_name", out var name) || !name.IsString ||
                string.IsNullOrWhiteSpace(name.AsString) || name.AsString.EnumerateRunes().Count() > 140)
                return false;
            if (!member.TryGetValue("status", out var status) || !status.IsString ||
                !MemberStatusValues.Contains(status.AsString))
                return false;
            if (!IsStringList(member, "tags")) return false;
            if (!IsValidTimes(member)) return false;
            var removed = status.AsString == "removed";
            if (removed)
            {
                if (!member.TryGetValue("removed_time", out var removedTime) || !removedTime.IsBsonDateTime)
                    return false;
            }
            else if (member.TryGetValue("removed_time", out var kept) && !kept.IsBsonNull)
            {
                return false;
            }
        }

        var teams = MigrationBson.Collection(database, "team");
        var teamMembers = MigrationBson.Collection(database, "team_member");
        var allTeams = await teams.Find(FilterDefinition<BsonDocument>.Empty)
            .Project<BsonDocument>(Builders<BsonDocument>.Projection.Include("_id").Include("m_uc"))
            .ToListAsync(ct);
        foreach (var team in allTeams)
        {
            if (!team.TryGetValue("m_uc", out var count) || !IsNonNegativeInt(count)) return false;
            var active = await teamMembers.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("team", team["_id"]),
                    Builders<BsonDocument>.Filter.Eq("status", "active")),
                cancellationToken: ct);
            if (count.ToInt64() != active) return false;
        }
        var teamRows = await teamMembers.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(ct);
        foreach (var member in teamRows)
        {
            if (NormKey(member.TryGetValue("team", out var t) ? t : null) is null) return false;
            if (NormKey(member.TryGetValue("user", out var u) ? u : null) is null) return false;
            if (!member.TryGetValue("base_tag", out var baseTag) || !baseTag.IsString ||
                !TeamRolePriority.ContainsKey(baseTag.AsString))
                return false;
            if (!member.TryGetValue("status", out var status) || !status.IsString ||
                !TeamMemberStatusValues.Contains(status.AsString))
                return false;
            if (!IsStringList(member, "tags")) return false;
            if (!IsStringList(member, "worker_qualifications")) return false;
            if (!IsStringList(member, "aliases")) return false;
            if (member.TryGetValue("worker_qualifications", out var qualifications) &&
                qualifications.IsBsonArray &&
                qualifications.AsBsonArray.Any(q =>
                    !q.IsString || !WorkerTags.Values.Contains(q.AsString)))
                return false;
            if (!IsValidTimes(member)) return false;
            if (status.AsString == "removed")
            {
                if (!member.TryGetValue("removed_time", out var removedTime) || !removedTime.IsBsonDateTime)
                    return false;
            }
            else if (member.TryGetValue("removed_time", out var kept) && !kept.IsBsonNull)
            {
                return false;
            }
        }

        if (!await HasSubjectIndexAsync(database, "project_member",
                MigrationBson.Keys(("project", 1), ("user", 1)), "user", ct))
            return false;
        if (!await HasSubjectIndexAsync(database, "project_member",
                MigrationBson.Keys(("project", 1), ("external_id", 1)), "external_id", ct))
            return false;
        var memberIndexes = await MigrationBson.ListIndexesSafeAsync(projectMembers, ct);
        if (!memberIndexes.Any(i => i.GetValue("name", "").AsString == "project_member_identity_key_v1"))
            return false;
        var teamCatalog = await MigrationBson.ListIndexesSafeAsync(teamMembers, ct);
        if (!teamCatalog.Any(i => i.GetValue("name", "").AsString == "team_member_team_user_v1"))
            return false;

        var reports = MigrationBson.Collection(database, ReportCollection);
        if (await reports.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("_id", ReportSummaryId),
                cancellationToken: ct) != 1)
            return false;
        var summary = await reports.Find(
            Builders<BsonDocument>.Filter.Eq("_id", ReportSummaryId)).FirstOrDefaultAsync(ct);
        var summaryDoc = summary?.TryGetValue("summary", out var inner) == true && inner.IsBsonDocument
            ? inner.AsBsonDocument : new BsonDocument();
        return summaryDoc.TryGetValue("owner_issue_count", out var issues) is false ||
            !issues.IsNumeric || issues.ToInt64() == 0;
    }

    // ---- helpers ----

    private sealed class RunState
    {
        public DateTime MigrationTime;
        public string BatchId = string.Empty;
        public Dictionary<string, BsonDocument> Reports = new(StringComparer.Ordinal);
        public Dictionary<string, long> Summary = new(StringComparer.Ordinal);
    }

    private sealed class MemberDraft
    {
        public string Project = string.Empty;
        public string User = string.Empty;
        public string DisplayName = string.Empty;
        public HashSet<string> Tags = new(StringComparer.Ordinal);
        public DateTime CreateTime;
        public DateTime EditTime;
        public DateTime? RemovedTime;
        public string Status = "active";
        public List<BsonValue> RelationIds = [];
    }

    private sealed class ExternalDraft
    {
        public string Project = string.Empty;
        public string ExternalId = string.Empty;
        public string DisplayName = string.Empty;
        public HashSet<string> Tags = new(StringComparer.Ordinal);
        public DateTime CreateTime;
        public DateTime EditTime;
    }

    private sealed class TeamDraft
    {
        public string Team = string.Empty;
        public string User = string.Empty;
        public string BaseTag = "member";
        public DateTime CreateTime;
        public DateTime EditTime;
        public List<BsonValue> RelationIds = [];
    }

    private static async IAsyncEnumerable<BsonDocument> ListAsync(
        IMongoDatabase database, string collection,
        ProjectionDefinition<BsonDocument>? projection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var cursor = await MigrationBson.Collection(database, collection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Project(projection ?? Builders<BsonDocument>.Projection.Include("_id"))
            .ToCursorAsync(ct);
        using (cursor)
        {
            while (await cursor.MoveNextAsync(ct))
                foreach (var doc in cursor.Current) yield return doc;
        }
    }

    private static string Key(BsonValue value) => value switch
    {
        BsonObjectId oid => oid.Value.ToString(),
        BsonString s => s.AsString,
        _ => value.ToString()!,
    };

    private static BsonValue ToBson(string? value) =>
        value is null ? BsonNull.Value : new BsonString(value);

    private static BsonValue ToBson(ObjectId? value) =>
        value is null ? BsonNull.Value : new BsonObjectId(value.Value);

    /// <summary>
    /// Round-trip a normalized subject key back to its BSON reference form.
    /// </summary>
    private static BsonValue SubjectRef(string key) =>
        ObjectId.TryParse(key, out var id) ? new BsonObjectId(id) : new BsonString(key);

    private static ObjectId? ToObjectId(string? key) =>
        key is not null && ObjectId.TryParse(key, out var id) ? id : null;

    private static string Text(BsonDocument doc, string key) =>
        doc.TryGetValue(key, out var value) && value.IsString ? value.AsString : string.Empty;

    /// <summary>
    /// Normalize a legacy reference (plain ObjectId, GenericReference shape
    /// <c>{_ref: ...}</c>, or exported <c>{$oid}</c>/<c>{$id}</c> forms) to a
    /// comparable string key, mirroring Python's <c>_ref_id</c>.
    /// </summary>
    private static string? NormKey(BsonValue? value)
    {
        switch (value)
        {
            case null or BsonNull: return null;
            case BsonObjectId oid: return oid.Value.ToString();
            case BsonString s: return s.AsString;
            case BsonDocument doc:
                if (doc.TryGetValue("_ref", out var nested))
                {
                    if (nested is BsonDocument nestedDoc)
                    {
                        foreach (var field in new[] { "$id", "$oid", "id" })
                            if (nestedDoc.TryGetValue(field, out var candidate))
                                return NormKey(candidate);
                        return null;
                    }
                    // BSON DBRef payload: use its id.
                    return NormKey(nested);
                }
                foreach (var field in new[] { "$oid", "$id", "id" })
                    if (doc.TryGetValue(field, out var candidate))
                        return NormKey(candidate);
                if (doc.TryGetValue("$ref", out _) && doc.TryGetValue("$id", out var dbRefId))
                    return NormKey(dbRefId);
                return null;
            default: return value.ToString();
        }
    }

    private static string? NormObjectId(BsonValue? value)
    {
        var key = NormKey(value);
        return key is not null && ObjectId.TryParse(key, out _) ? key : null;
    }

    private static async Task<Dictionary<string, BsonDocument>> LoadRolesAsync(
        IMongoDatabase database, string collection, CancellationToken ct)
    {
        var result = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        await foreach (var doc in ListAsync(database, collection,
            Builders<BsonDocument>.Projection.Include("_id").Include("m_o").Include("m_p"), ct))
        {
            result[Key(doc["_id"])] = doc;
        }
        return result;
    }

    private static string? RoleCode(BsonValue? role, Dictionary<string, BsonDocument> roles)
    {
        var id = NormObjectId(role);
        if (id is null || !roles.TryGetValue(id, out var data)) return null;
        return data.TryGetValue("m_o", out var code) && code.IsString ? code.AsString : null;
    }

    private static BsonDocument RoleSnapshot(BsonValue? role, Dictionary<string, BsonDocument> roles)
    {
        var id = NormKey(role);
        var snapshot = new BsonDocument { ["id"] = id is not null && ObjectId.TryParse(id, out var oid)
            ? (BsonValue)new BsonObjectId(oid) : ToBson(id) };
        if (id is not null && roles.TryGetValue(id, out var data))
        {
            if (data.TryGetValue("m_o", out var code) && code.IsString)
                snapshot["system_code"] = code.AsString;
            if (data.TryGetValue("m_p", out var permissions) && permissions.IsBsonArray)
                snapshot["permissions"] = new BsonArray(permissions.AsBsonArray);
        }
        return snapshot;
    }

    // Python truthiness for the legacy `m_t` annotation: missing/None/""/[]/{}/0
    // do not count toward legacy_relation_tag_count.
    private static bool IsTruthy(BsonValue? value) => value switch
    {
        null or BsonNull => false,
        BsonBoolean b => b.Value,
        BsonString s => s.Value.Length > 0,
        BsonArray a => a.Count > 0,
        BsonDocument d => d.ElementCount > 0,
        BsonInt32 i => i.Value != 0,
        BsonInt64 l => l.Value != 0,
        BsonDouble dbl => dbl.Value != 0,
        _ => true,
    };

    // Only string annotations survive normalization (Python _normalized_legacy_tags).
    private static SortedSet<string> NormalizedLegacyTags(BsonValue? value)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        switch (value)
        {
            case BsonArray array:
                foreach (var item in array)
                    if (item.IsString)
                    {
                        var stripped = item.AsString.Trim();
                        if (stripped.Length > 0) result.Add(stripped);
                    }
                break;
            case BsonString s:
                var single = s.Value.Trim();
                if (single.Length > 0) result.Add(single);
                break;
        }
        return result;
    }

    // String-only scalar/array reader for `aliases` (Python _raw_tag_values + str filter).
    private static IEnumerable<string> AliasStrings(BsonValue? value)
    {
        if (value is BsonArray array)
            foreach (var item in array)
            {
                if (item.IsString) yield return item.AsString;
            }
        else if (value is BsonString s) yield return s.Value;
    }

    // Python repr() of a JSON-decoded worker name for the invalid_worker_name issue.
    private static string WorkerNameDetail(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => QuoteRepr(item.GetString()!),
        JsonValueKind.Null or JsonValueKind.Undefined => "None",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => item.GetRawText(),
    };

    private static string QuoteRepr(string value) =>
        value.Contains('\'') && !value.Contains('"') ? $"\"{value}\"" : $"'{value}'";

    private static BsonDocument Report(RunState state, string key)
    {
        if (!state.Reports.TryGetValue(key, out var report))
        {
            var separator = key.IndexOf(':');
            report = new BsonDocument
            {
                ["scope"] = separator >= 0 ? key[..separator] : key,
                ["source_id"] = separator >= 0 ? key[(separator + 1)..] : key,
            };
            state.Reports[key] = report;
        }
        return report;
    }

    private static void AddIssue(
        RunState state, string scope, BsonValue sourceId, string code, BsonValue detail)
    {
        var key = $"{scope}:{Key(sourceId)}";
        var report = Report(state, key);
        if (!report.TryGetValue("issues", out var issues) || !issues.IsBsonArray)
        {
            issues = new BsonArray();
            report["issues"] = issues;
        }
        issues.AsBsonArray.Add(new BsonDocument { ["code"] = code, ["detail"] = detail });
    }

    private static DateTime? ValidDateTime(BsonValue? value) =>
        value is BsonDateTime dt ? dt.ToUniversalTime() : null;

    private static DateTime EventTime(BsonDocument document, DateTime fallback)
    {
        foreach (var field in new[] { "c", "m_c", "create_time", "m_e", "edit_time" })
        {
            if (document.TryGetValue(field, out var value) && ValidDateTime(value) is { } time)
                return time;
        }
        if (document.TryGetValue("_id", out var id) && id is BsonObjectId objectId)
        {
            var oid = objectId.Value;
            if (oid != ObjectId.Empty) return oid.CreationTime;
        }
        return fallback;
    }

    private static long Count(RunState state, string key)
    {
        state.Summary.TryGetValue(key, out var value);
        return value;
    }

    private static void Bump(RunState state, string key, long delta = 1) =>
        state.Summary[key] = Count(state, key) + delta;

    private static void RecordLegacyRelation(
        RunState state, string scope, BsonDocument relation, Dictionary<string, BsonDocument> roles)
    {
        var report = Report(state, $"{scope}_relation:{Key(relation["_id"])}");
        report["mapping_version"] = MappingVersion;
        report["role"] = RoleSnapshot(
            relation.TryGetValue("r", out var role) ? role : null, roles);
        // Preserve the raw annotation values (Python keeps them as-is); only
        // strings survive the tag normalization elsewhere.
        var rawTags = new List<BsonValue>();
        if (relation.TryGetValue("m_t", out var tagValue))
        {
            if (tagValue is BsonArray tagArray) rawTags.AddRange(tagArray);
            else if (tagValue is not BsonNull) rawTags.Add(tagValue);
        }
        if (rawTags.Count > 0)
        {
            report["legacy_tags"] = new BsonArray(rawTags);
            var issues = report.TryGetValue("issues", out var existing) && existing.IsBsonArray
                ? existing.AsBsonArray : new BsonArray();
            issues.Add(new BsonDocument
            {
                ["code"] = "legacy_relation_tags",
                ["detail"] = new BsonArray(rawTags),
            });
            report["issues"] = issues;
        }
    }

    private static void RecordMissingRole(
        RunState state, string scope, string groupId, BsonDocument relation,
        Dictionary<string, BsonDocument> roles)
    {
        var roleId = NormObjectId(relation.TryGetValue("r", out var role) ? role : null);
        if (roleId is not null && roles.ContainsKey(roleId)) return;
        var code = roleId is null ? "missing_role_reference" : "unknown_role_reference";
        AddIssue(state, scope, ToBson(groupId), code, new BsonDocument
        {
            ["relation"] = relation["_id"],
            ["role_id"] = ToBson(roleId),
        });
        Bump(state, "custom_role_issue_count");
    }

    private static string ConfiguredBatchId(DateTime migrationTime)
    {
        var configured = (Environment.GetEnvironmentVariable("IDENTITY_MIGRATION_BATCH_ID") ?? string.Empty).Trim();
        if (configured.Length > 0)
        {
            if (!configured.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
                throw new MigrationException(
                    "IDENTITY_MIGRATION_BATCH_ID may contain only letters, numbers, '.', '_' and '-'.");
            return configured;
        }
        return $"0004-{migrationTime:yyyyMMddTHHmmssffffffZ}";
    }

    private static string NormalizedName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static Guid Uuid5(Guid namespaceId, string name)
    {
        var ns = namespaceId.ToByteArray();
        Swap(ns);
        var hash = SHA1.HashData(ns.Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        var guid = new byte[16];
        Array.Copy(hash, guid, 16);
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        Swap(guid);
        return new Guid(guid);

        static void Swap(byte[] bytes)
        {
            (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
            (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
            (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
            (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
        }
    }

    private static string ExternalId(string projectId, string displayName) =>
        Uuid5(ExternalNamespace, $"{projectId}:{NormalizedName(displayName)}").ToString("D");

    private async Task MergeExistingDocumentsAsync(
        IMongoDatabase database, RunState state, string collectionName, bool team, CancellationToken ct)
    {
        var collection = MigrationBson.Collection(database, collectionName);
        var groups = new Dictionary<string, List<BsonDocument>>(StringComparer.Ordinal);
        var invalid = 0;
        // Full documents: grouping, tag normalization and the canonical update
        // all read fields beyond _id (a null projection would reduce every
        // row to its _id and mark the whole collection invalid).
        await foreach (var document in ListAsync(database, collectionName,
            Builders<BsonDocument>.Projection.Exclude("_cls"), ct))
        {
            string? key = null;
            if (team)
            {
                var teamId = NormKey(document.TryGetValue("team", out var t) ? t : null);
                var userId = NormKey(document.TryGetValue("user", out var u) ? u : null);
                if (teamId is not null && userId is not null) key = $"{teamId}:u:{userId}";
            }
            else
            {
                key = SubjectKey(document);
            }
            if (key is null)
            {
                invalid++;
                AddIssue(state, collectionName, document["_id"], "invalid_identity_member",
                    new BsonDocument
                    {
                        ["team"] = document.TryGetValue("team", out var t) ? t : BsonNull.Value,
                        ["project"] = document.TryGetValue("project", out var p) ? p : BsonNull.Value,
                    });
                continue;
            }
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(document);
        }
        state.Summary["identity_member_reported_invalid_count"] =
            Count(state, "identity_member_reported_invalid_count") + invalid;

        foreach (var (_, documents) in groups)
        {
            documents.Sort((a, b) =>
            {
                var ta = ValidDateTime(a.TryGetValue("create_time", out var ca) ? ca : null) ?? MigrationEpoch;
                var tb = ValidDateTime(b.TryGetValue("create_time", out var cb) ? cb : null) ?? MigrationEpoch;
                var cmp = ta.CompareTo(tb);
                return cmp != 0 ? cmp : string.CompareOrdinal(Key(a["_id"]), Key(b["_id"]));
            });
            var canonical = documents[0];
            var mergedTags = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in documents)
                foreach (var tag in NormalizedLegacyTags(
                    item.TryGetValue("tags", out var tags) ? tags : null))
                    mergedTags.Add(tag);
            var statuses = documents
                .Select(item => MemberStatus(item.TryGetValue("status", out var s) && s.IsString ? s.AsString : null, team))
                .Where(s => s is not null)
                .ToArray();
            var status = statuses.Length > 0
                ? statuses.MaxBy(s => StatusPriority(s, team))!
                : "active";
            var version = documents.Max(item => NonNegativeInt(
                item.TryGetValue("version", out var v) ? v : null));
            var (createTime, editTime, removedTime) = MemberTimes(canonical, status == "removed");

            BsonDocument values;
            if (team)
            {
                var baseTag = documents
                    .Select(item => item.TryGetValue("base_tag", out var b) && b.IsString ? b.AsString : null)
                    .Where(b => b is not null && TeamRolePriority.ContainsKey(b))
                    .MaxBy(b => TeamRolePriority[b!]) ?? "member";
                var qualifications = new SortedSet<string>(StringComparer.Ordinal);
                var aliases = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var item in documents)
                {
                    foreach (var q in NormalizedLegacyTags(
                        item.TryGetValue("worker_qualifications", out var wq) ? wq : null))
                        qualifications.Add(q);
                    foreach (var alias in AliasStrings(
                        item.TryGetValue("aliases", out var al) ? al : null))
                    {
                        var stripped = alias.Trim();
                        if (stripped.Length > 0) aliases.Add(stripped);
                    }
                }
                var invalidQualifications = qualifications
                    .Where(q => !WorkerTags.Values.Contains(q)).ToArray();
                if (invalidQualifications.Length > 0)
                {
                    AddIssue(state, collectionName, canonical["_id"], "invalid_worker_qualification",
                        new BsonArray(invalidQualifications.Select(q => (BsonValue)new BsonString(q))));
                    foreach (var q in invalidQualifications) qualifications.Remove(q);
                }
                values = new BsonDocument
                {
                    ["team"] = canonical.GetValue("team"),
                    ["user"] = canonical.GetValue("user"),
                    ["base_tag"] = baseTag!,
                    ["tags"] = new BsonArray(mergedTags.Select(t => (BsonValue)new BsonString(t))),
                    ["worker_qualifications"] = new BsonArray(qualifications.Select(q => (BsonValue)new BsonString(q))),
                    ["aliases"] = new BsonArray(aliases.Select(a => (BsonValue)new BsonString(a))),
                    ["status"] = status,
                };
            }
            else
            {
                var userId = NormKey(canonical.TryGetValue("user", out var u) ? u : null);
                var externalId = canonical.TryGetValue("external_id", out var e) && e.IsString ? e.AsString : null;
                var displayName = Text(canonical, "display_name");
                if (displayName.Length == 0) displayName = userId ?? externalId ?? string.Empty;
                var projectKey = NormKey(canonical.TryGetValue("project", out var p) ? p : null);
                values = new BsonDocument
                {
                    ["project"] = canonical.GetValue("project"),
                    ["display_name"] = displayName,
                    ["tags"] = new BsonArray(mergedTags.Select(t => (BsonValue)new BsonString(t))),
                    ["status"] = status,
                    ["ik"] = userId is not null ? $"{projectKey}:u:{userId}" : $"{projectKey}:e:{externalId}",
                };
                if (userId is not null) values["user"] = canonical.GetValue("user");
                else if (externalId is not null) values["external_id"] = externalId;
            }
            values["create_time"] = createTime;
            values["edit_time"] = editTime;
            values["version"] = version;
            var unset = new BsonDocument();
            if (status == "removed")
            {
                values["removed_time"] = removedTime!.Value;
            }
            else
            {
                if (!team)
                {
                    if (values.Contains("user")) unset["external_id"] = string.Empty;
                    else unset["user"] = string.Empty;
                }
                unset["removed_time"] = string.Empty;
            }
            var update = new BsonDocument("$set", values);
            if (unset.ElementCount > 0) update["$unset"] = unset;
            await collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", canonical["_id"]), update,
                cancellationToken: ct);
            if (documents.Count > 1)
            {
                foreach (var duplicate in documents.Skip(1))
                    await collection.DeleteOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", duplicate["_id"]), ct);
                Bump(state, "identity_member_duplicate_count", documents.Count - 1);
                AddIssue(state, collectionName, canonical["_id"], "duplicate_identity_member",
                    new BsonDocument { ["count"] = documents.Count });
            }
        }
    }

    private static string? SubjectKey(BsonDocument document)
    {
        var projectId = NormKey(document.TryGetValue("project", out var p) ? p : null);
        var userId = NormKey(document.TryGetValue("user", out var u) ? u : null);
        var externalId = document.TryGetValue("external_id", out var e) && e.IsString && e.AsString.Length > 0
            ? e.AsString : null;
        if (projectId is null) return null;
        if (userId is not null && externalId is not null) return $"{projectId}:u:{userId}";
        if (userId is not null) return $"{projectId}:u:{userId}";
        if (externalId is not null) return $"{projectId}:e:{externalId}";
        return null;
    }

    private static string? MemberStatus(string? value, bool team) =>
        (team ? TeamMemberStatusValues : MemberStatusValues).Contains(value ?? string.Empty) ? value : null;

    private static int StatusPriority(string? value, bool team) => team
        ? value switch { "active" => 1, "removed" => 0, _ => -1 }
        : value switch { "active" => 2, "invited" => 1, "removed" => 0, _ => -1 };

    private static long NonNegativeInt(BsonValue? value, long fallback = 0) =>
        value is BsonInt32 i && i.Value >= 0 ? i.Value
        : value is BsonInt64 l && l.Value >= 0 ? l.Value
        : fallback;

    private static bool IsNonNegativeInt(BsonValue value) =>
        value is BsonInt32 i && i.Value >= 0 || value is BsonInt64 l && l.Value >= 0;

    private static (DateTime Create, DateTime Edit, DateTime? Removed) MemberTimes(
        BsonDocument document, bool removed)
    {
        var create = ValidDateTime(document.TryGetValue("create_time", out var c) ? c : null) ?? MigrationEpoch;
        var edit = ValidDateTime(document.TryGetValue("edit_time", out var e) ? e : null) ?? create;
        if (edit < create) edit = create;
        DateTime? removedTime = ValidDateTime(document.TryGetValue("removed_time", out var r) ? r : null);
        if (removed) removedTime ??= edit;
        else removedTime = null;
        return (create, edit, removedTime);
    }

    private static bool IsStringList(BsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || !value.IsBsonArray) return false;
        return value.AsBsonArray.All(item => item.IsString && item.AsString.Trim().Length > 0);
    }

    private static bool IsValidTimes(BsonDocument member)
    {
        if (ValidDateTime(member.TryGetValue("create_time", out var c) ? c : null) is not { } create)
            return false;
        if (ValidDateTime(member.TryGetValue("edit_time", out var e) ? e : null) is not { } edit)
            return false;
        if (edit < create) return false;
        if (!member.TryGetValue("version", out var version) || !IsNonNegativeInt(version))
            return false;
        return true;
    }

    private static async Task MigrateProjectStatusAsync(
        IMongoDatabase database, RunState state, BsonDocument project, CancellationToken ct)
    {
        var projectId = project["_id"];
        var statusValue = project.TryGetValue("st", out var st) ? st : null;
        int oldStatus;
        bool knownShape;
        if (statusValue is null)
        {
            oldStatus = 0;
            knownShape = true;
        }
        else if (statusValue is BsonInt32 si)
        {
            oldStatus = si.Value;
            knownShape = true;
        }
        else if (statusValue is BsonInt64 sl)
        {
            oldStatus = sl.Value > int.MaxValue || sl.Value < int.MinValue ? -1 : (int)sl.Value;
            knownShape = true;
        }
        else if (statusValue is BsonDouble sd && sd.Value == Math.Truncate(sd.Value) &&
            sd.Value >= int.MinValue && sd.Value <= int.MaxValue)
        {
            oldStatus = (int)sd.Value;
            knownShape = true;
        }
        else
        {
            oldStatus = -1;
            knownShape = false;
        }
        var newStatus = oldStatus switch { 0 => 0, 1 => 1, 5 => 5, 2 => 0, 3 => 0, _ => 0 };
        if (oldStatus == 2)
        {
            AddIssue(state, "project", projectId, "legacy_finish_plan",
                new BsonString("PLAN_FINISH was normalized to NORMAL; review the pending plan separately."));
            Bump(state, "status_plan_count");
        }
        else if (oldStatus == 3)
        {
            AddIssue(state, "project", projectId, "legacy_delete_plan",
                new BsonString("PLAN_DELETE was normalized to NORMAL; no destructive delete was performed."));
            Bump(state, "status_plan_count");
        }
        else if (oldStatus is not (0 or 1 or 5) || !knownShape)
        {
            AddIssue(state, "project", projectId, "unknown_project_status",
                new BsonString(statusValue?.ToString() ?? "null"));
            Bump(state, "status_issue_count");
        }

        var update = Builders<BsonDocument>.Update
            .Set("st", newStatus)
            .Set("stv", NonNegativeInt(project.TryGetValue("stv", out var stv) ? stv : null))
            .Set("ouv", NonNegativeInt(project.TryGetValue("ouv", out var ouv) ? ouv : null));
        var unset = Builders<BsonDocument>.Update.Unset("pft").Unset("pdt").Unset("ft");
        if (newStatus == 5)
        {
            var completed = ValidDateTime(project.TryGetValue("ctm", out var ctm) ? ctm : null)
                ?? ValidDateTime(project.TryGetValue("ft", out var ft) ? ft : null)
                ?? state.MigrationTime;
            update = update.Set("ctm", completed);
        }
        else if (project.Contains("ctm"))
        {
            unset = unset.Unset("ctm");
        }
        await MigrationBson.Collection(database, "project").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", projectId),
            Builders<BsonDocument>.Update.Combine(update, unset),
            cancellationToken: ct);
        Bump(state, "projects");
    }

    private static async Task<BsonDocument?> FindProjectMemberAsync(
        IMongoDatabase database, BsonDocument values, CancellationToken ct)
    {
        var members = MigrationBson.Collection(database, "project_member");
        if (values.TryGetValue("user", out var user) && !user.IsBsonNull)
        {
            var found = await members.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("project", values["project"]),
                Builders<BsonDocument>.Filter.Eq("user", user))).FirstOrDefaultAsync(ct);
            if (found is not null) return found;
        }
        else if (values.TryGetValue("external_id", out var external) && external.IsString)
        {
            var found = await members.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("project", values["project"]),
                Builders<BsonDocument>.Filter.Eq("external_id", external))).FirstOrDefaultAsync(ct);
            if (found is not null) return found;
        }
        return await members.Find(
            Builders<BsonDocument>.Filter.Eq("ik", values["ik"])).FirstOrDefaultAsync();
    }

    private static async Task UpsertProjectMemberAsync(
        IMongoDatabase database, BsonDocument values, IEnumerable<string> tags, CancellationToken ct)
    {
        var members = MigrationBson.Collection(database, "project_member");
        var desiredStatus = values.TryGetValue("status", out var s) && s.IsString &&
            MemberStatusValues.Contains(s.AsString) ? s.AsString : "active";
        var existing = await FindProjectMemberAsync(database, values, ct);
        var normalizedTags = new SortedSet<string>(NormalizedLegacyTags(
            values.TryGetValue("tags", out var t) ? t : null), StringComparer.Ordinal);
        foreach (var tag in tags.Select(t => t.Trim()).Where(t => t.Length > 0))
            normalizedTags.Add(tag);
        if (existing is null)
        {
            var createTime = ValidDateTime(values.TryGetValue("create_time", out var c) ? c : null)
                ?? MigrationEpoch;
            var editTime = ValidDateTime(values.TryGetValue("edit_time", out var e) ? e : null)
                ?? createTime;
            var version = NonNegativeInt(values.TryGetValue("version", out var v) ? v : null);
            var insert = new BsonDocument(values)
            {
                ["tags"] = new BsonArray(normalizedTags.Select(t => (BsonValue)new BsonString(t))),
                ["status"] = desiredStatus,
                ["create_time"] = createTime,
                ["edit_time"] = editTime,
                ["version"] = version,
            };
            if (desiredStatus == "removed")
                insert["removed_time"] = ValidDateTime(
                    values.TryGetValue("removed_time", out var r) ? r : null) ?? editTime;
            else
                insert.Remove("removed_time");
            await members.InsertOneAsync(insert, cancellationToken: ct);
            return;
        }

        var currentStatus = existing.TryGetValue("status", out var cs) && cs.IsString ? cs.AsString : null;
        string finalStatus;
        if (desiredStatus == "active" || currentStatus == "active") finalStatus = "active";
        else if (currentStatus is not null && MemberStatusValues.Contains(currentStatus)) finalStatus = desiredStatus;
        else finalStatus = desiredStatus;
        var existingCreate = ValidDateTime(existing.TryGetValue("create_time", out var ec) ? ec : null)
            ?? ValidDateTime(values.TryGetValue("create_time", out var vc) ? vc : null)
            ?? MigrationEpoch;
        var existingEdit = ValidDateTime(existing.TryGetValue("edit_time", out var ee) ? ee : null)
            ?? ValidDateTime(values.TryGetValue("edit_time", out var ve) ? ve : null)
            ?? existingCreate;
        var existingDisplay = Text(existing, "display_name");
        if (existingDisplay.Length == 0)
            existingDisplay = Text(values, "display_name");
        if (existingDisplay.Length == 0)
            existingDisplay = Key(values.TryGetValue("user", out var vu) ? vu
                : values.TryGetValue("external_id", out var ve2) ? ve2 : BsonNull.Value);
        var mergedTags = new SortedSet<string>(NormalizedLegacyTags(
            existing.TryGetValue("tags", out var et) ? et : null), StringComparer.Ordinal);
        foreach (var tag in normalizedTags) mergedTags.Add(tag);
        var updateValues = new BsonDocument
        {
            ["project"] = values["project"],
            ["display_name"] = existingDisplay,
            ["tags"] = new BsonArray(mergedTags.Select(t => (BsonValue)new BsonString(t))),
            ["status"] = finalStatus,
            ["create_time"] = existingCreate,
            ["edit_time"] = existingEdit,
            ["version"] = NonNegativeInt(existing.TryGetValue("version", out var ev) ? ev : null,
                NonNegativeInt(values.TryGetValue("version", out var vv) ? vv : null)),
            ["ik"] = values["ik"],
        };
        var unset = new BsonDocument();
        if (values.Contains("user"))
        {
            updateValues["user"] = values["user"];
            unset["external_id"] = string.Empty;
        }
        else
        {
            updateValues["external_id"] = values["external_id"];
            unset["user"] = string.Empty;
        }
        if (finalStatus == "removed")
        {
            updateValues["removed_time"] = ValidDateTime(
                    existing.TryGetValue("removed_time", out var er) ? er : null)
                ?? ValidDateTime(values.TryGetValue("removed_time", out var vr) ? vr : null)
                ?? existingEdit;
        }
        else
        {
            unset["removed_time"] = string.Empty;
        }
        var update = new BsonDocument("$set", updateValues);
        if (unset.ElementCount > 0) update["$unset"] = unset;
        await members.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", existing["_id"]), update,
            cancellationToken: ct);
    }

    private static async Task UpsertTeamMemberAsync(
        IMongoDatabase database, BsonDocument values, string baseTag, CancellationToken ct)
    {
        var members = MigrationBson.Collection(database, "team_member");
        var existing = await members.Find(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("team", values["team"]),
            Builders<BsonDocument>.Filter.Eq("user", values["user"]))).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            var insert = new BsonDocument(values)
            {
                ["base_tag"] = TeamRolePriority.ContainsKey(baseTag) ? baseTag : "member",
                ["tags"] = new BsonArray(NormalizedLegacyTags(
                    values.TryGetValue("tags", out var t) ? t : null).Select(t => (BsonValue)new BsonString(t))),
                ["worker_qualifications"] = new BsonArray(NormalizedLegacyTags(
                    values.TryGetValue("worker_qualifications", out var wq) ? wq : null)
                    .Select(q => (BsonValue)new BsonString(q))),
                ["aliases"] = new BsonArray(AliasStrings(
                    values.TryGetValue("aliases", out var al) ? al : null)
                    .Select(a => a.Trim()).Where(a => a.Length > 0)
                    .Select(a => (BsonValue)new BsonString(a))),
                ["status"] = values.TryGetValue("status", out var s) && s.IsString &&
                    TeamMemberStatusValues.Contains(s.AsString) ? s.AsString : "active",
                ["create_time"] = ValidDateTime(values.TryGetValue("create_time", out var c) ? c : null)
                    ?? MigrationEpoch,
                ["version"] = NonNegativeInt(values.TryGetValue("version", out var v) ? v : null),
            };
            insert["edit_time"] = ValidDateTime(values.TryGetValue("edit_time", out var e) ? e : null)
                ?? ValidDateTime(values.TryGetValue("create_time", out var cc) ? cc : null)
                ?? MigrationEpoch;
            if (insert["status"].AsString == "removed")
                insert["removed_time"] = ValidDateTime(
                    values.TryGetValue("removed_time", out var r) ? r : null)
                    ?? insert["edit_time"].ToUniversalTime();
            await members.InsertOneAsync(insert, cancellationToken: ct);
            return;
        }

        var oldTag = existing.TryGetValue("base_tag", out var ob) && ob.IsString ? ob.AsString : "member";
        if (TeamRolePriority.GetValueOrDefault(oldTag, 0) > TeamRolePriority.GetValueOrDefault(baseTag, 0))
            baseTag = oldTag;
        var status = existing.TryGetValue("status", out var es) && es.IsString ? es.AsString : null;
        if (status is null || !TeamMemberStatusValues.Contains(status))
            status = values.TryGetValue("status", out var vs) && vs.IsString &&
                TeamMemberStatusValues.Contains(vs.AsString) ? vs.AsString : "active";
        var createTime = ValidDateTime(existing.TryGetValue("create_time", out var ec) ? ec : null)
            ?? ValidDateTime(values.TryGetValue("create_time", out var vc) ? vc : null)
            ?? MigrationEpoch;
        var editTime = ValidDateTime(existing.TryGetValue("edit_time", out var ee) ? ee : null)
            ?? ValidDateTime(values.TryGetValue("edit_time", out var ve) ? ve : null)
            ?? createTime;
        var mergedTags = new SortedSet<string>(NormalizedLegacyTags(
            values.TryGetValue("tags", out var vt) ? vt : null), StringComparer.Ordinal);
        foreach (var tag in NormalizedLegacyTags(existing.TryGetValue("tags", out var ext) ? ext : null))
            mergedTags.Add(tag);
        var existingQualifications = existing.TryGetValue("worker_qualifications", out var eq)
            ? eq : null;
        var qualifications = NormalizedLegacyTags(
            existingQualifications is not null ? existingQualifications
            : values.TryGetValue("worker_qualifications", out var vq) ? vq : null);
        var existingAliases = existing.TryGetValue("aliases", out var ea) ? ea : null;
        var aliases = AliasStrings(existingAliases is not null ? existingAliases
                : values.TryGetValue("aliases", out var va) ? va : null)
            .Select(a => a.Trim()).Where(a => a.Length > 0)
            .Select(a => (BsonValue)new BsonString(a)).ToArray();
        var updateValues = new BsonDocument
        {
            ["team"] = values["team"],
            ["user"] = values["user"],
            ["base_tag"] = TeamRolePriority.ContainsKey(baseTag) ? baseTag : "member",
            ["tags"] = new BsonArray(mergedTags.Select(t => (BsonValue)new BsonString(t))),
            ["worker_qualifications"] = new BsonArray(qualifications.Select(q => (BsonValue)new BsonString(q))),
            ["aliases"] = new BsonArray(aliases),
            ["status"] = status,
            ["create_time"] = createTime,
            ["edit_time"] = editTime,
            ["version"] = NonNegativeInt(existing.TryGetValue("version", out var ev) ? ev : null,
                NonNegativeInt(values.TryGetValue("version", out var vv) ? vv : null)),
        };
        var unset = new BsonDocument();
        if (status == "removed")
        {
            updateValues["removed_time"] = ValidDateTime(
                    existing.TryGetValue("removed_time", out var er) ? er : null)
                ?? ValidDateTime(values.TryGetValue("removed_time", out var vr) ? vr : null)
                ?? editTime;
        }
        else
        {
            unset["removed_time"] = string.Empty;
        }
        var update = new BsonDocument("$set", updateValues);
        if (unset.ElementCount > 0) update["$unset"] = unset;
        await members.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", existing["_id"]), update,
            cancellationToken: ct);
    }

    private static async Task<bool> HasSubjectIndexAsync(
        IMongoDatabase database, string collection, BsonDocument keys, string field, CancellationToken ct)
    {
        var indexes = await MigrationBson.ListIndexesSafeAsync(
            MigrationBson.Collection(database, collection), ct);
        foreach (var index in indexes)
        {
            if (!index.GetValue("key", BsonNull.Value).Equals(keys)) continue;
            if (!index.GetValue("unique", false).ToBoolean()) continue;
            // m0004 installs $exists filters; m0005 upgrades them to $type.
            // verify accepts both shapes because both are the canonical state
            // of their respective migration stage.
            if (!index.TryGetValue("partialFilterExpression", out var partial) || !partial.IsBsonDocument)
                continue;
            var filter = partial.AsBsonDocument;
            if (!filter.TryGetValue(field, out var clause) || !clause.IsBsonDocument)
                continue;
            var clauseDoc = clause.AsBsonDocument;
            if (clauseDoc.TryGetValue("$exists", out var exists) && exists.IsBoolean && exists.AsBoolean)
                return true;
            if (clauseDoc.TryGetValue("$type", out var type) && type.IsString &&
                ((field == "user" && type.AsString == "objectId") ||
                 (field == "external_id" && type.AsString == "string")))
                return true;
        }
        return false;
    }

    private sealed class JoinEvent
    {
        public string Kind = string.Empty;
        public string Status = string.Empty;
        public DateTime Time;
        public BsonValue SourceId = BsonNull.Value;
    }

    private static string? JoinProcessStatus(BsonValue? value) => value switch
    {
        BsonInt32 i => i.Value switch { 1 => "pending", 2 => "active", 3 => "removed", _ => null },
        BsonInt64 l => l.Value switch { 1 => "pending", 2 => "active", 3 => "removed", _ => null },
        _ => null,
    };

    private static void RecordJoinProcessEvent(
        RunState state,
        Dictionary<(string Project, string User), List<JoinEvent>> joinEvents,
        BsonDocument document,
        string kind,
        Dictionary<string, BsonDocument> projectById,
        Dictionary<string, BsonDocument> teamById,
        Dictionary<string, string> users,
        Dictionary<string, BsonDocument> projectRoles,
        Dictionary<string, BsonDocument> teamRoles)
    {
        var groupId = NormKey(document.TryGetValue("g", out var g) ? g : null);
        var userId = NormKey(document.TryGetValue("u", out var u) ? u : null);
        var statusValue = document.TryGetValue("s", out var s) ? s : null;
        var status = JoinProcessStatus(statusValue);
        var scope = kind == "invitation" ? "invitation" : "application";
        var report = Report(state, $"{scope}:{Key(document["_id"])}");
        report["mapping_version"] = MappingVersion;
        report["group_id"] = groupId is null ? BsonNull.Value : SubjectRef(groupId);
        report["user_id"] = userId is null ? BsonNull.Value : SubjectRef(userId);
        report["legacy_status"] = statusValue ?? BsonNull.Value;
        report["status"] = status is null ? BsonNull.Value : new BsonString(status);
        var codes = groupId is not null && projectById.ContainsKey(groupId) ? projectRoles : teamRoles;
        if (kind == "invitation")
            report["role"] = RoleSnapshot(
                document.TryGetValue("r", out var role) ? role : null, codes);
        if (status is null)
        {
            AddIssue(state, scope, document["_id"], "unknown_join_process_status",
                statusValue ?? BsonNull.Value);
            Bump(state, "join_process_issue_count");
            return;
        }
        Bump(state, status == "active" ? $"allowed_{kind}_count"
            : status == "removed" ? $"denied_{kind}_count" : $"pending_{kind}_count");
        if ((groupId is null || (!projectById.ContainsKey(groupId) && !teamById.ContainsKey(groupId))) ||
            userId is null || !users.ContainsKey(userId))
        {
            AddIssue(state, scope, document["_id"], "dangling_reference", new BsonDocument
            {
                ["project_or_team"] = groupId is null ? BsonNull.Value : SubjectRef(groupId),
                ["user"] = userId is null ? BsonNull.Value : SubjectRef(userId),
            });
            Bump(state, "join_process_issue_count");
            return;
        }
        if (!projectById.ContainsKey(groupId!))
        {
            report["projection"] = "team_scope_no_project_member";
            return;
        }
        joinEvents.TryGetValue((groupId!, userId!), out var events);
        events ??= joinEvents[(groupId!, userId!)] = [];
        events.Add(new JoinEvent
        {
            Kind = kind,
            Status = status,
            Time = EventTime(document, state.MigrationTime),
            SourceId = document["_id"],
        });
    }

    private static void ProjectJoinProcessMembers(
        RunState state,
        Dictionary<(string Project, string User), MemberDraft> projectMembers,
        Dictionary<(string Project, string User), List<JoinEvent>> joinEvents,
        Dictionary<string, string> users)
    {
        foreach (var (key, events) in joinEvents)
        {
            var (projectId, userId) = key;
            if (projectMembers.ContainsKey(key))
            {
                foreach (var evt in events)
                {
                    var report = Report(state, $"{evt.Kind}:{Key(evt.SourceId)}");
                    report["projection"] = "active_relation";
                    report["project_member_key"] = $"{projectId}:u:{userId}";
                }
                continue;
            }
            var ordered = events
                .OrderBy(e => e.Time)
                .ThenBy(e => Key(e.SourceId), StringComparer.Ordinal)
                .ToList();
            var final = ordered[^1];
            var memberKey = $"{projectId}:u:{userId}";
            var userName = users[userId];
            string projection;
            if (final.Status == "pending" && final.Kind == "application")
            {
                projection = "pending_application_no_member";
            }
            else
            {
                var sourceTime = final.Time;
                projectMembers[key] = new MemberDraft
                {
                    Project = projectId,
                    User = userId,
                    DisplayName = userName.Length > 0 ? userName : userId,
                    Tags = [],
                    CreateTime = sourceTime,
                    EditTime = sourceTime,
                    RemovedTime = final.Status == "removed" ? sourceTime : null,
                    Status = final.Status == "pending" ? "invited" : final.Status,
                    RelationIds = [],
                };
                Bump(state, "project_members_from_join_process");
                projection = $"project_member_{projectMembers[key].Status}";
            }
            foreach (var evt in events)
            {
                var report = Report(state, $"{evt.Kind}:{Key(evt.SourceId)}");
                report["projection"] = projection;
                if (projection != "pending_application_no_member")
                    report["project_member_key"] = memberKey;
            }
        }
    }

    private static void MigrateWorkers(
        RunState state,
        BsonDocument project,
        Dictionary<(string Project, string Key), ExternalDraft> externalMembers)
    {
        if (!project.TryGetValue("w", out var raw) || raw.IsBsonNull) return;
        var projectId = project["_id"];
        var projectKey = Key(projectId);
        var report = Report(state, $"project:{projectKey}");
        string rawText;
        if (raw.IsString)
        {
            rawText = raw.AsString;
        }
        else
        {
            AddIssue(state, "project", projectId, "workers_parse_error",
                new BsonString($"expected JSON string, got {raw.BsonType}"));
            Bump(state, "workers_issue_count");
            return;
        }
        report["mapping_version"] = MappingVersion;
        report["raw_workers_sha256"] = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawText))).ToLowerInvariant();
        report["raw_workers_length"] = rawText.EnumerateRunes().Count();
        Bump(state, "workers_projects");
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(string.IsNullOrEmpty(rawText) ? "{}" : rawText);
        }
        catch (JsonException error)
        {
            AddIssue(state, "project", projectId, "workers_parse_error", new BsonString(error.Message));
            Bump(state, "workers_issue_count");
            return;
        }
        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                AddIssue(state, "project", projectId, "workers_root_not_object",
                    new BsonString(JsonTypeName(parsed.RootElement)));
                Bump(state, "workers_issue_count");
                return;
            }
            var workerKeys = parsed.RootElement.EnumerateObject()
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            report["worker_keys"] = new BsonArray(workerKeys.Select(k => (BsonValue)new BsonString(k)));
            var mappedWorkers = new BsonArray();
            report["mapped_workers"] = mappedWorkers;
            var memberKeys = new BsonArray();
            report["project_member_keys"] = memberKeys;
            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                if (!WorkerTags.TryGetValue(property.Name, out var targetTag))
                {
                    AddIssue(state, "project", projectId, "unknown_worker_tag",
                        new BsonString(property.Name));
                    Bump(state, "workers_issue_count");
                    continue;
                }
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    AddIssue(state, "project", projectId, "worker_names_not_array",
                        new BsonString(property.Name));
                    Bump(state, "workers_issue_count");
                    continue;
                }
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        AddIssue(state, "project", projectId, "invalid_worker_name",
                            new BsonString(WorkerNameDetail(item)));
                        Bump(state, "workers_issue_count");
                        continue;
                    }
                    var displayName = item.GetString()!.Trim();
                    var memberKey = (projectKey, NormalizedName(displayName));
                    if (!externalMembers.TryGetValue(memberKey, out var member))
                    {
                        member = externalMembers[memberKey] = new ExternalDraft
                        {
                            Project = projectKey,
                            ExternalId = ExternalId(projectKey, displayName),
                            DisplayName = displayName,
                            CreateTime = ValidDateTime(
                                project.TryGetValue("m_c", out var mc) ? mc : null)
                                ?? state.MigrationTime,
                            EditTime = ValidDateTime(
                                project.TryGetValue("m_e", out var me) ? me : null)
                                ?? ValidDateTime(
                                    project.TryGetValue("m_c", out var mc2) ? mc2 : null)
                                ?? state.MigrationTime,
                        };
                    }
                    if (member.Tags.Count == 0) Bump(state, "external_members");
                    member.Tags.Add(targetTag);
                    var identityKey = $"{projectKey}:e:{member.ExternalId}";
                    mappedWorkers.Add(new BsonDocument
                    {
                        ["source_tag"] = property.Name,
                        ["tag"] = targetTag,
                        ["display_name"] = displayName,
                        ["project_member_key"] = identityKey,
                    });
                    memberKeys.Add(identityKey);
                    Bump(state, "worker_values");
                }
            }
        }
    }

    private static string JsonTypeName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "list",
        JsonValueKind.Object => "dict",
        JsonValueKind.String => "str",
        JsonValueKind.Number => element.TryGetInt64(out _) ? "int" : "float",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "NoneType",
    };

    private static async Task RepairOwnersAsync(
        IMongoDatabase database,
        RunState state,
        Dictionary<string, BsonDocument> projectById,
        Dictionary<string, BsonDocument> teamRoles,
        Dictionary<string, string> users,
        Dictionary<string, HashSet<string>> ownerCandidates,
        Dictionary<(string Project, string User), MemberDraft> projectMembers,
        CancellationToken ct)
    {
        var teamCreatorByTeam = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var relation in ListAsync(database, "team_user_relation",
            Builders<BsonDocument>.Projection.Include("g").Include("u").Include("r"), ct))
        {
            var teamId = NormKey(relation.TryGetValue("g", out var g) ? g : null);
            var userId = NormKey(relation.TryGetValue("u", out var u) ? u : null);
            if (teamId is null || userId is null) continue;
            var roleCode = RoleCode(relation.TryGetValue("r", out var r) ? r : null, teamRoles);
            if (roleCode is not null && TeamRoleTags.TryGetValue(roleCode, out var tag) && tag == "creator")
                teamCreatorByTeam.TryAdd(teamId, userId);
        }
        var earliest = await MigrationBson.Collection(database, "user")
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .FirstOrDefaultAsync(ct);
        var siteCreator = earliest is null ? null : NormKey(earliest["_id"]);

        foreach (var (projectId, project) in projectById)
        {
            ownerCandidates.TryGetValue(projectId, out var candidates);
            candidates ??= [];
            var currentOwner = NormKey(project.TryGetValue("ou", out var ou) ? ou : null);
            if (currentOwner is not null)
            {
                if (!users.ContainsKey(currentOwner))
                {
                    AddIssue(state, "project", project["_id"], "owner_invalid_user",
                        SubjectRef(currentOwner));
                    Bump(state, "owner_invalid_count");
                    Bump(state, "owner_issue_count");
                }
                else if (!projectMembers.ContainsKey((projectId, currentOwner)))
                {
                    AddIssue(state, "project", project["_id"], "owner_not_in_creator_relations",
                        SubjectRef(currentOwner));
                    Bump(state, "owner_issue_count");
                }
                else if (!candidates.Contains(currentOwner))
                {
                    AddIssue(state, "project", project["_id"], "owner_not_in_creator_relations",
                        SubjectRef(currentOwner));
                    Bump(state, "owner_issue_count");
                }
                if (candidates.Count > 1)
                {
                    AddIssue(state, "project", project["_id"], "owner_candidate_conflict",
                        new BsonArray(candidates.OrderBy(c => c, StringComparer.Ordinal)
                            .Select(c => (BsonValue)new BsonString(c))));
                    Bump(state, "owner_conflict_count");
                    Bump(state, "owner_issue_count");
                }
            }
            else if (candidates.Count == 1)
            {
                var single = candidates.First();
                var ouv = project.TryGetValue("ouv", out var existing) ? existing : new BsonInt32(0);
                await MigrationBson.Collection(database, "project").UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", project["_id"]),
                    Builders<BsonDocument>.Update
                        .Set("ou", SubjectRef(single))
                        .Set("ouv", ouv),
                    cancellationToken: ct);
                Bump(state, "owner_repaired_count");
            }
            else if (candidates.Count == 0)
            {
                var teamId = NormKey(project.TryGetValue("t", out var t) ? t : null);
                string? fallback = null;
                var source = "team_creator";
                if (teamId is not null && teamCreatorByTeam.TryGetValue(teamId, out var teamCreator) &&
                    users.ContainsKey(teamCreator))
                {
                    fallback = teamCreator;
                }
                else
                {
                    source = "site_creator";
                    if (siteCreator is not null && users.ContainsKey(siteCreator))
                        fallback = siteCreator;
                }
                if (fallback is null)
                {
                    AddIssue(state, "project", project["_id"], "owner_missing", BsonNull.Value);
                    Bump(state, "owner_missing_count");
                    Bump(state, "owner_issue_count");
                    continue;
                }
                var ouv = project.TryGetValue("ouv", out var existing) ? existing : new BsonInt32(0);
                await MigrationBson.Collection(database, "project").UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", project["_id"]),
                    Builders<BsonDocument>.Update
                        .Set("ou", SubjectRef(fallback))
                        .Set("ouv", ouv),
                    cancellationToken: ct);
                var fallbackName = users[fallback];
                await UpsertProjectMemberAsync(database, new BsonDocument
                {
                    ["project"] = project["_id"],
                    ["user"] = SubjectRef(fallback),
                    ["ik"] = $"{projectId}:u:{fallback}",
                    ["display_name"] = fallbackName.Length > 0 ? fallbackName : fallback,
                    ["status"] = "active",
                    ["create_time"] = state.MigrationTime,
                    ["edit_time"] = state.MigrationTime,
                    ["version"] = 0,
                }, ["creator"], ct);
                AddIssue(state, "project", project["_id"], $"owner_bound_to_{source}",
                    SubjectRef(fallback));
                Bump(state, "owner_repaired_count");
            }
            else
            {
                AddIssue(state, "project", project["_id"], "owner_candidate_conflict",
                    new BsonArray(candidates.OrderBy(c => c, StringComparer.Ordinal)
                        .Select(c => (BsonValue)new BsonString(c))));
                Bump(state, "owner_conflict_count");
                Bump(state, "owner_issue_count");
            }
        }
    }

    private static async Task SaveReportsAsync(
        IMongoDatabase database, RunState state, CancellationToken ct)
    {
        var reports = MigrationBson.Collection(database, ReportCollection);
        foreach (var report in state.Reports.Values)
        {
            report["migration_version"] = SchemaVersion;
            report["batch_id"] = state.BatchId;
            report["create_time"] = state.MigrationTime;
            var scope = report.GetValue("scope").AsString;
            var source = report.GetValue("source_id").AsString;
            await reports.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", $"{scope}:{source}"),
                report,
                new ReplaceOptions { IsUpsert = true }, ct);
        }
        var summary = new BsonDocument
        {
            ["_id"] = ReportSummaryId,
            ["migration_version"] = SchemaVersion,
            ["summary"] = new BsonDocument(state.Summary.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new BsonElement(kv.Key, kv.Value))),
            ["batch_id"] = state.BatchId,
            ["create_time"] = state.MigrationTime,
        };
        await reports.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ReportSummaryId),
            summary,
            new ReplaceOptions { IsUpsert = true }, ct);
    }
}