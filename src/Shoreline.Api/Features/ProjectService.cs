using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;
using Shoreline.Api.Storage;

namespace Shoreline.Api.Features;

// Entities behind the project endpoints. Collection/field names mirror the
// Python mongoengine documents 1:1 (project/project_set/target), so both
// backends can read the same database while the port is in progress.
public sealed class ProjectService(MongoStore store, TimeProvider clock, LegacyTypes types,
    IdentityPermissions permissions, TeamService teams, TeamMemberService members,
    ObjectStorage? storage = null, IConfiguration? configuration = null,
    TeamLifecycleLock? lifecycle = null, ProjectLabelLock? labels = null,
    LabelRequestContext? labelContext = null, ProjectStorageRouter? router = null,
    IEnumerable<IProjectCreatedHook>? projectCreatedHooks = null,
    ILogger<ProjectService>? logger = null,
    ProjectImportService? imports = null)
{
    private IMongoCollection<BsonDocument> Projects => store.Collection("project");
    private IMongoCollection<BsonDocument> Sets => store.Collection("project_set");
    private IMongoCollection<BsonDocument> Targets => store.Collection("target");
    private IMongoCollection<BsonDocument> MemberRows => store.Collection("project_member");
    private IMongoCollection<BsonDocument> Languages => store.Collection("language");

    // ---- lookups ----

    public async Task<BsonDocument> GetProjectAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key)) throw new ApiError("ProjectNotExistError", 4001, "项目不存在");
        return await Projects.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("ProjectNotExistError", 4001, "项目不存在");
    }

    public async Task<BsonDocument> GetProjectSetAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key)) throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");
        return await Sets.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");
    }

    public async Task<BsonDocument> GetTargetAsync(string id, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var key)) throw new ApiError("TargetNotExistError", 4024, "目标语言不存在");
        return await Targets.Find(new BsonDocument("_id", key)).FirstOrDefaultAsync(ct)
            ?? throw new ApiError("TargetNotExistError", 4024, "目标语言不存在");
    }

    public async Task<BsonDocument> GetTeamAsync(string id, CancellationToken ct) => await members.GetTeamAsync(id, ct);

    private async Task<BsonDocument?> LanguageAsync(ObjectId id, CancellationToken ct) =>
        await Languages.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(ct);

    private async Task<BsonDocument?> LanguageByCodeAsync(string code, CancellationToken ct) =>
        await Languages.Find(new BsonDocument("co", code)).FirstOrDefaultAsync(ct);

    // ---- permission helpers ----

    private async Task RequireTeamAsync(BsonDocument actor, BsonDocument team, string permission, CancellationToken ct)
    {
        if (! (await permissions.TeamSnapshotAsync(actor, team, ct)).Has(permission)) throw ApiError.Forbidden();
    }

    private async Task RequireProjectAsync(BsonDocument actor, BsonDocument project, string permission, CancellationToken ct)
    {
        if (! (await permissions.ProjectSnapshotAsync(actor, project, ct)).Has(permission)) throw ApiError.Forbidden();
    }

    /// <summary>Team creator check used by position-lock settings and member-management guards.</summary>
    public async Task<bool> IsTeamCreatorAsync(BsonDocument actor, BsonDocument team, CancellationToken ct) =>
        await store.Collection("team_member").Find(new BsonDocument
        {
            ["team"] = team["_id"], ["user"] = actor["_id"], ["status"] = "active", ["base_tag"] = "creator"
        }).AnyAsync(ct);

    // ---- project sets ----

    public async Task<BsonDocument> CreateProjectSetAsync(BsonDocument actor, BsonDocument team, string name, CancellationToken ct)
    {
        await RequireTeamAsync(actor, team, "team:CREATE_PROJECT_SET", ct);
        // K1: team-scoped write. No project/label lock exists for a new set, so
        // the distributed team guard is the only fence; it rejects a team whose
        // deletion claim is being held or whose durable deleting marker exists.
        if (lifecycle is not null)
            await lifecycle.EnsureWritableAsync(team["_id"].AsObjectId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name,
            ["i"] = "",
            ["t"] = team["_id"],
            ["ct"] = now,
            ["et"] = now,
            ["d"] = false
        };
        await Sets.InsertOneAsync(document, cancellationToken: ct);
        return document;
    }

    public async Task<BsonDocument> UpdateProjectSetAsync(BsonDocument actor, BsonDocument set, string name, CancellationToken ct)
    {
        var team = await GetTeamAsync(set["t"].ToString()!, ct);
        await RequireTeamAsync(actor, team, "team:CHANGE_PROJECT_SET", ct);
        if (set.Flag("d")) throw new ApiError("NoPermissionError", 103, "默认项目集不能进行设置");
        await Sets.UpdateOneAsync(new BsonDocument("_id", set["_id"]),
            new BsonDocument("$set", new BsonDocument("n", name)), cancellationToken: ct);
        set["n"] = name;
        return set;
    }

    public async Task DeleteProjectSetAsync(BsonDocument actor, BsonDocument set, CancellationToken ct)
    {
        var team = await GetTeamAsync(set["t"].ToString()!, ct);
        await RequireTeamAsync(actor, team, "team:DELETE_PROJECT_SET", ct);
        if (set.Flag("d")) throw new ApiError("NoPermissionError", 103, "默认项目集不能删除");
        // Python's ProjectSet.clear() currently only deletes (TODO: move projects
        // to the default set), while the delete rule is DENY for non-empty sets.
        // The API documents the "move to 未分类项目" behaviour, so implement that:
        // rehome projects to the team's default set before deleting.
        var defaultSet = await Sets.Find(new BsonDocument { ["t"] = team["_id"], ["d"] = true }).FirstOrDefaultAsync(ct);
        if (defaultSet is null) throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");
        await Projects.UpdateManyAsync(
            new BsonDocument { ["ps"] = set["_id"], ["t"] = team["_id"] },
            new BsonDocument("$set", new BsonDocument("ps", defaultSet["_id"])), cancellationToken: ct);
        await Sets.DeleteOneAsync(new BsonDocument("_id", set["_id"]), ct);
    }

    // ---- projects ----

    public async Task<BsonDocument> CreateProjectAsync(BsonDocument actor, BsonDocument team, System.Text.Json.JsonElement data, CancellationToken ct)
    {
        await RequireTeamAsync(actor, team, "team:CREATE_PROJECT", ct);
        // K1: the project row does not exist yet, so there is no project label
        // lock to hold. The team guard is checked before the first write; the
        // dissolution pass additionally re-counts projects before deleting the
        // team row, so a create that raced a delete cannot leave an orphan row.
        if (lifecycle is not null)
            await lifecycle.EnsureWritableAsync(team["_id"].AsObjectId, ct);
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) throw ApiError.Invalid("Invalid request");
        var name = data.RequiredText("name", 40);
        if (name.Length == 0) throw ApiError.Invalid("Invalid request");
        var intro = data.OptionalText("intro", 140);
        // Python's CreateProjectSchema declares labelplus_txt as an optional
        // string. Keep the raw value so Project.create can parse it before the
        // first row is written, exactly like its pre-save load_from_labelplus.
        var labelplusText = data.OptionalLabelplusText("labelplus_txt");
        var allowApply = AllowOrCheckType(data, "allow_apply_type", 1, 2);
        var checker = AllowOrCheckType(data, "application_check_type", 1, 2);
        // default_role: system role without creator (Python default: translator).
        ObjectId defaultRoleId;
        if (data.TryGetProperty("default_role", out var roleProperty) && roleProperty.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            try
            {
                defaultRoleId = (await types.DefaultRoleAsync("project", roleProperty.GetString()!, null, ct))["_id"].AsObjectId;
            }
            catch (ApiError error) when (error.Error == "ValidateError")
            {
                throw ApiError.Field("default_role", "默认角色必须是系统角色");
            }
        }
        else
        {
            defaultRoleId = (await types.SystemRoleAsync("project", "translator", ct))["_id"].AsObjectId;
        }
        // project_set: optional, defaults to the team's default set.
        ObjectId setId;
        if (data.TryGetProperty("project_set", out var setProperty) && setProperty.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var set = await GetProjectSetAsync(setProperty.GetString()!, ct);
            if (set["t"] != team["_id"]) throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");
            setId = set["_id"].AsObjectId;
        }
        else
        {
            var defaultSet = await Sets.Find(new BsonDocument { ["t"] = team["_id"], ["d"] = true }).FirstOrDefaultAsync(ct);
            if (defaultSet is null) throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");
            setId = defaultSet["_id"].AsObjectId;
        }
        var sourceLanguage = await LanguageByCodeAsync(data.OptionalText("source_language", 32, "ja"), ct)
            ?? throw ApiError.Field("source_language", "语言不存在");
        var targetCodes = TargetLanguageCodes(data);
        var targetLanguages = new List<BsonDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in targetCodes)
        {
            if (!seen.Add(code)) continue;
            var language = await LanguageByCodeAsync(code, ct)
                ?? throw ApiError.Field("target_languages", "语言不存在");
            targetLanguages.Add(language);
        }
        if (targetLanguages.Count == 0)
        {
            var fallback = await LanguageByCodeAsync("zh-CN", ct)
                ?? throw ApiError.Field("target_languages", "语言不存在");
            targetLanguages.Add(fallback);
        }
        // Python's Project.create parses any non-empty labelplus_txt (line 477)
        // before it rejects source == target (line 500), so a malformed file
        // wins over 6002 and no row is written in either case. Parse only after
        // the schema-level field/language validation above, which Python also
        // runs first, including the multi-target case where nothing is queued.
        if (labelplusText.Length > 0)
        {
            if (imports is null)
                throw new InvalidOperationException("LabelPlus import service is not configured");
            imports.ValidateBeforeCreate(labelplusText);
        }
        if (targetLanguages.Any(language => language["_id"] == sourceLanguage["_id"]))
            throw new ApiError("TargetAndSourceLanguageSameError", 6002, "目标语言不能与源语言相同");
        var now = clock.GetUtcNow().UtcDateTime;
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["i"] = intro,
            ["t"] = team["_id"],
            ["ps"] = setId,
            ["dr"] = defaultRoleId,
            ["m_a"] = allowApply,
            ["m_t"] = checker,
            ["u"] = 100000,
            ["ol"] = sourceLanguage["_id"],
            ["ta"] = new BsonArray(),
            ["st"] = 0,
            ["stv"] = 0,
            ["ou"] = actor["_id"],
            ["ouv"] = 0,
            ["cip"] = false,
            ["nft"] = false,
            ["oc"] = false,
            ["ple"] = false,
            ["plt"] = new BsonArray(),
            ["tc"] = 0,
            ["fo"] = 0,
            ["fc"] = 0,
            ["fs"] = 0L,
            ["cover_bytes"] = 0L,
            ["resample_bytes"] = 0L,
            ["ob"] = 0L,
            ["sc"] = 0,
            ["tsc"] = 0,
            ["csc"] = 0,
            // Python Project.import_from_labelplus_status defaults to
            // ImportFromLabelplusStatus.SUCCEEDED (2). Only a queued import
            // rewrites this to Pending/Running; a project created without an
            // import must not claim to be running.
            ["is"] = ProjectImportService.Succeeded,
            ["ip"] = 0,
            ["ie"] = 0,
            ["it"] = "",
            ["splk"] = false,
            ["label_revision"] = 0L,
            ["m_uc"] = 0,
            ["m_c"] = now,
            ["m_e"] = now
        };
        await Projects.InsertOneAsync(document, cancellationToken: ct);
        // Targets are created after the project so target_count reflects them.
        var created = new List<BsonDocument>();
        try
        {
            foreach (var language in targetLanguages)
                created.Add(await CreateTargetCoreAsync(document, language, ct));
        }
        catch
        {
            // Roll back the project on target failure (Python saves the project
            // first too, but leaving a half-created project is worse). Target
            // creation may already have installed FileTargetCache rows.
            if (created.Count > 0)
            {
                await store.Collection("file_target_cache").DeleteManyAsync(new BsonDocument(
                    "t", new BsonDocument("$in", new BsonArray(created.Select(target => target["_id"])))), ct);
            }
            await Targets.DeleteManyAsync(new BsonDocument("t", document["_id"]), ct);
            await Projects.DeleteOneAsync(new BsonDocument("_id", document["_id"]), ct);
            throw;
        }
        if (created.Count > 0)
        {
            document["tc"] = created.Count;
            await Projects.UpdateOneAsync(new BsonDocument("_id", document["_id"]),
                new BsonDocument("$set", new BsonDocument("tc", created.Count)), cancellationToken: ct);
        }
        await AddCreatorMemberAsync(document, actor, ct);
        // Python queues only when labelplus_txt is non-empty, a creator exists
        // and there is exactly one target; the creator is the actor that was
        // just inserted above. The hook persists the durable intent/enqueue
        // flags itself and is skipped for the multi-target case, though the
        // text was parsed before the first write above.
        if (labelplusText.Length > 0 && created.Count == 1)
        {
            await imports!.InitializeAfterCreateAsync(document, actor, labelplusText, string.Empty, ct);
            // Project.create reloads after calling import_from_labelplus, so the
            // response exposes the persisted pending/error state (or an already
            // completed job) rather than the stale pre-import document.
            document = await Projects.Find(new BsonDocument("_id", document["_id"])).FirstOrDefaultAsync(ct)
                       ?? document;
        }
        if (projectCreatedHooks is not null)
        {
            var projectId = document["_id"].AsObjectId;
            foreach (var hook in projectCreatedHooks)
            {
                try
                {
                    await hook.OnProjectCreatedAsync(projectId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger?.LogError(ex, "ProjectCreatedHook {HookType} failed for project {ProjectId}", hook.GetType().Name, projectId);
                }
            }
        }
        return document;
    }

    private async Task<BsonDocument> CreateTargetCoreAsync(BsonDocument project, BsonDocument language, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var document = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = project["_id"],
            ["l"] = language["_id"],
            ["tsc"] = 0,
            ["csc"] = 0,
            ["ct"] = now,
            ["et"] = now,
            ["i"] = ""
        };
        await Targets.InsertOneAsync(document, cancellationToken: ct);
        try
        {
            // Python Target.create installs a FileTargetCache for every file of
            // the project, including folders and inactive/deactivated revisions.
            await ProjectContentCleanup.CreateTargetCachesAsync(store, project, document, now, ct);
        }
        catch
        {
            await store.Collection("file_target_cache").DeleteManyAsync(
                new BsonDocument("t", document["_id"]), ct);
            await Targets.DeleteOneAsync(new BsonDocument("_id", document["_id"]), ct);
            throw;
        }
        return document;
    }

    /// <summary>Creator joins with the creator system tag as in Project.create.</summary>
    public async Task AddCreatorMemberAsync(BsonDocument project, BsonDocument creator, CancellationToken ct)
    {
        var team = await GetTeamAsync(project["t"].ToString()!, ct);
        var displayName = await DefaultDisplayNameAsync(team, creator, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var member = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["project"] = project["_id"],
            ["user"] = creator["_id"],
            ["ik"] = $"{project["_id"]}:u:{creator["_id"]}",
            ["display_name"] = displayName,
            ["dns"] = SearchText.Normalize(displayName),
            ["tags"] = new BsonArray { "creator" },
            ["status"] = "active",
            ["create_time"] = now,
            ["edit_time"] = now,
            ["version"] = 0
        };
        await MemberRows.InsertOneAsync(member, cancellationToken: ct);
        await Projects.UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$inc", new BsonDocument("m_uc", 1)), cancellationToken: ct);
        project["m_uc"] = project.Integer("m_uc") + 1;
    }

    // TeamMember.default_display_name -> user.ddn -> user.name (Python semantics).
    public async Task<string> DefaultDisplayNameAsync(BsonDocument team, BsonDocument user, CancellationToken ct)
    {
        var relation = await store.Collection("team_member").Find(new BsonDocument
        {
            ["team"] = team["_id"], ["user"] = user["_id"], ["status"] = "active"
        }).FirstOrDefaultAsync(ct);
        var displayName = relation?.Text("default_display_name", "");
        if (!string.IsNullOrEmpty(displayName)) return displayName.Trim();
        var ddn = user.Text("ddn", "");
        if (!string.IsNullOrEmpty(ddn)) return ddn.Trim();
        return user.Text("n", "").Trim();
    }

    public async Task<BsonDocument> UpdateProjectAsync(BsonDocument actor, BsonDocument project, System.Text.Json.JsonElement data, CancellationToken ct)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object || data.EnumerateObject().Count() == 0)
            throw new ApiError("RequestDataEmptyError", 110, "请求数据为空");
        // K1: project-scoped writes run inside the project label lock, the same
        // lock TeamDissolutionService acquires before deleting a project. The
        // HTTP label guard may already hold it for this request; the scoped
        // LabelRequestContext then reuses that exact lease. The optional
        // dependencies exist only for direct unit construction (cleanup tests);
        // the application container always supplies them.
        if (labels is not null && labelContext is not null)
        {
            await using var session = await labels.EnterAsync(labelContext, project["_id"].AsObjectId,
                recovery: false, expectedRevision: null, ct);
            return await UpdateProjectLockedAsync(actor, session.Project, data, ct);
        }
        return await UpdateProjectLockedAsync(actor, project, data, ct);
    }

    private async Task<BsonDocument> UpdateProjectLockedAsync(BsonDocument actor, BsonDocument project,
        System.Text.Json.JsonElement data, CancellationToken ct)
    {
        if (lifecycle is not null)
            await lifecycle.EnsureWritableAsync(project["t"].AsObjectId, ct);
        if (project.Integer("st", 0) != 0) throw new ApiError("ProjectFinishedError", 4002, "项目已完结，无法修改");
        await RequireProjectAsync(actor, project, "project:CHANGE", ct);
        var changes = new BsonDocument();
        if (data.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var name = nameProperty.GetString()!;
            if (name.Length is < 1 or > 40) throw ApiError.Field("name", "长度为1到40个字符");
            changes["n"] = name;
            changes["ns"] = SearchText.Normalize(name);
        }
        if (data.TryGetProperty("intro", out var introProperty) && introProperty.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var intro = introProperty.GetString()!;
            if (intro.Length > 140) throw ApiError.Field("intro", "长度为0到140个字符");
            changes["i"] = intro;
        }
        if (data.TryGetProperty("allow_apply_type", out var allowProperty))
            changes["m_a"] = AllowOrCheckType(data, "allow_apply_type", 1, 2);
        if (data.TryGetProperty("application_check_type", out var checkProperty))
            changes["m_t"] = AllowOrCheckType(data, "application_check_type", 1, 2);
        if (data.TryGetProperty("default_role", out var roleProperty) && roleProperty.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var role = await types.DefaultRoleAsync("project", roleProperty.GetString()!, project["_id"].AsObjectId, ct);
            changes["dr"] = role["_id"];
        }
        if (data.TryGetProperty("project_set", out var setProperty) && setProperty.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var set = await GetProjectSetAsync(setProperty.GetString()!, ct);
            if (set["t"] != project["t"]) throw new ApiError("ProjectSetNotExistError", 4022, "项目集不存在");
            changes["ps"] = set["_id"];
        }
        if (data.TryGetProperty("staff_list_page", out var pageProperty))
        {
            if (pageProperty.ValueKind == System.Text.Json.JsonValueKind.Null) changes["slp"] = BsonNull.Value;
            else if (pageProperty.ValueKind == System.Text.Json.JsonValueKind.Number && pageProperty.TryGetInt32(out var page) && page != 0) changes["slp"] = page;
            else throw ApiError.Field("staff_list_page", "Invalid staff_list_page");
        }
        if (data.TryGetProperty("position_lock_enabled", out var lockProperty) || data.TryGetProperty("position_lock_tags", out _))
        {
            var team = await GetTeamAsync(project["t"].ToString()!, ct);
            if (!await IsTeamCreatorAsync(actor, team, ct))
                throw new ApiError("NoPermissionError", 103, "只有团队创建者可以修改职位锁定设置");
            var enabled = data.TryGetProperty("position_lock_enabled", out var enabledProperty)
                && (enabledProperty.ValueKind == System.Text.Json.JsonValueKind.True || enabledProperty.GetBoolean());
            changes["ple"] = enabled;
            var lockTags = new BsonArray();
            if (enabled && data.TryGetProperty("position_lock_tags", out var tagsProperty) && tagsProperty.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var tag in tagsProperty.EnumerateArray())
                {
                    if (tag.ValueKind != System.Text.Json.JsonValueKind.String) throw ApiError.Field("position_lock_tags", "Invalid tag");
                    var value = tag.GetString()!;
                    if (!IdentityPermissions.IsWorkerTag(value)) throw ApiError.Field("position_lock_tags", "Invalid tag");
                    lockTags.Add(value);
                }
            }
            changes["plt"] = lockTags;
        }
        if (changes.ElementCount == 0) throw new ApiError("RequestDataEmptyError", 110, "请求数据为空");
        changes["m_e"] = clock.GetUtcNow().UtcDateTime;
        var updated = await Projects.FindOneAndUpdateAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", changes), new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
        return updated ?? throw new ApiError("ProjectNotExistError", 4001, "项目不存在");
    }

    // ---- targets ----

    public async Task<BsonDocument> CreateTargetAsync(BsonDocument actor, BsonDocument project, string languageCode, CancellationToken ct)
    {
        await RequireProjectAsync(actor, project, "project:ADD_TARGET", ct);
        var language = await LanguageByCodeAsync(languageCode.Trim(), ct)
            ?? throw ApiError.Field("language", "语言不存在");
        var existing = await Targets.Find(new BsonDocument { ["t"] = project["_id"], ["l"] = language["_id"] }).FirstOrDefaultAsync(ct);
        if (existing is not null) throw new ApiError("SameTargetLanguageError", 6004, "已经存在相同的目标语言");
        if (language["_id"] == project.GetValue("ol", BsonNull.Value)) throw new ApiError("TargetAndSourceLanguageSameError", 6002, "目标语言不能与源语言相同");
        var target = await CreateTargetCoreAsync(project, language, ct);
        await Projects.UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$inc", new BsonDocument("tc", 1)), cancellationToken: ct);
        return target;
    }

    public async Task DeleteTargetAsync(BsonDocument actor, BsonDocument target, CancellationToken ct)
    {
        var project = await GetProjectAsync(target["t"].ToString()!, ct);
        await RequireProjectAsync(actor, project, "project:DELETE_TARGET", ct);
        if (storage is null)
            throw IdentityErrors.ClearRetryable();
        try
        {
            // Python Target.clear: output objects best-effort, then ORM CASCADE
            // for output/translation/tip/file_target_cache, target delete, and
            // target_count decrement only when that delete actually matched.
            if (router is not null)
            {
                await ProjectContentCleanup.DeleteTargetCascadesAsync(
                    store, router, storage, configuration, project, target, ct);
            }
            else
            {
                await ProjectContentCleanup.DeleteTargetCascadesAsync(
                    store, storage, configuration, project, target, ct);
            }
        }
        catch (InvalidOperationException)
        {
            // Includes an unsupported project storage_source_id: fail closed
            // without deleting any row or object, and let the caller retry after
            // the storage-pool router is wired.
            throw IdentityErrors.ClearRetryable();
        }
    }

    // ---- views ----

    public async Task<object> ViewAsync(BsonDocument project, BsonDocument? actor, bool chinese, CancellationToken ct,
        PermissionSnapshot? snapshot = null)
    {
        snapshot ??= await permissions.ProjectSnapshotAsync(actor!, project, ct);
        var team = await GetTeamAsync(project["t"].ToString()!, ct);
        var set = await GetProjectSetAsync(project["ps"].ToString()!, ct);
        var source = await LanguageAsync(project["ol"].AsObjectId, ct);
        object? role = null;
        var autoAdmin = false;
        if (actor is not null && snapshot.SourceTags.Count != 0)
        {
            role = types.RoleView("project", await types.SystemRoleAsync("project", snapshot.SourceTags[0], ct), chinese);
            var hasRelation = await MemberRows.Find(new BsonDocument
            {
                ["project"] = project["_id"], ["user"] = actor["_id"], ["status"] = new BsonDocument("$ne", "removed")
            }).AnyAsync(ct);
            autoAdmin = !hasRelation;
        }
        var status = project.Integer("st", 0);
        var statusName = status switch { 0 => "NORMAL", 1 => "CLEARED", 5 => "COMPLETED", _ => null };
        var identityStatus = statusName;
        return new
        {
            group_type = "project",
            id = project["_id"].ToString(),
            name = project.Text("n"),
            intro = project.Text("i"),
            max_user = project.Integer("u", 100000),
            status,
            identity_status = identityStatus,
            status_name = statusName,
            status_version = project.Integer("stv"),
            owner_user_id = project.GetValue("ou", BsonNull.Value).IsObjectId ? project["ou"].ToString() : null,
            owner_version = project.Integer("ouv"),
            user_count = project.Integer("m_uc"),
            default_role = project.GetValue("dr", BsonNull.Value).IsObjectId ? project["dr"].ToString() : null,
            allow_apply_type = project.Integer("m_a", 1),
            application_check_type = project.Integer("m_t", 2),
            is_need_check_application = project.Integer("m_t", 2) != 1,
            role,
            auto_become_project_admin = autoAdmin,
            effective_permissions = snapshot.EffectivePermissions.Order(StringComparer.Ordinal).ToArray(),
            permission_sources = snapshot.PermissionSources,
            can_order_files = CanOrderFiles(project, snapshot),
            staff_list_page = project.GetValue("slp", BsonNull.Value).IsNumeric ? (object?)project.Integer("slp") : null,
            position_lock_enabled = project.Flag("ple"),
            position_lock_tags = project.GetValue("plt", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray(),
            create_time = project.Timestamp("m_c"),
            edit_time = project.Timestamp("m_e"),
            source_language = source is null ? null : LanguageView(source, chinese),
            target_count = project.Integer("tc"),
            file_size = project.GetValue("fs", 0L).ToInt64(),
            cover_bytes = project.GetValue("cover_bytes", 0L).ToInt64(),
            resample_bytes = project.GetValue("resample_bytes", 0L).ToInt64(),
            output_bytes = project.GetValue("ob", 0L).ToInt64(),
            source_count = project.Integer("sc"),
            translated_source_count = project.Integer("tsc"),
            checked_source_count = project.Integer("csc"),
            import_from_labelplus_status = project.Integer("is", 1),
            import_from_labelplus_percent = project.Integer("ip"),
            import_from_labelplus_error_type = project.Integer("ie"),
            import_from_labelplus_error_type_name = LabelplusErrorTypeName((int)project.Integer("ie")),
            storage_source_id = project.GetValue("csid", BsonNull.Value) is { } csid && csid.IsObjectId ? csid.ToString() : null,
            storage_source_switched_time = project.GetValue("csw", BsonNull.Value).BsonType == BsonType.DateTime ? project.Timestamp("csw") : null,
            storage_pool_locked = project.Flag("splk"),
            team = await teams.ViewAsync(team, actor, chinese, ct),
            project_set = ProjectSetView(set)
        };
    }

    public object ListCardAsync(BsonDocument project, BsonDocument? actor, bool chinese, PermissionSnapshot snapshot,
        BsonDocument team, BsonDocument set) => new
    {
        group_type = "project",
        id = project["_id"].ToString(),
        name = project.Text("n"),
        status = project.Integer("st", 0),
        effective_permissions = snapshot.EffectivePermissions
            .Intersect(["project:ACCESS", "project:MANAGE_MEMBERS", "project:COMPLETE_PROJECT"], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        source_count = project.Integer("sc"),
        target_count = project.Integer("tc"),
        translated_source_count = project.Integer("tsc"),
        checked_source_count = project.Integer("csc"),
        file_size = project.GetValue("fs", 0L).ToInt64(),
        team = new Dictionary<string, object?>
        {
            ["group_type"] = "team",
            ["id"] = team["_id"].ToString(),
            ["name"] = team.Text("n"),
            ["worker_qualification_mode"] = team.Text("wqm", "qualified")
        },
        project_set = new Dictionary<string, object?>
        {
            ["id"] = set["_id"].ToString(),
            ["name"] = set.Text("n"),
            ["default"] = set.Flag("d")
        }
    };

    public static Dictionary<string, object?> ProjectSetView(BsonDocument set) => new()
    {
        ["id"] = set["_id"].ToString(),
        ["name"] = set.Text("n"),
        ["intro"] = set.Text("i"),
        ["default"] = set.Flag("d"),
        ["create_time"] = set.Timestamp("ct"),
        ["edit_time"] = set.Timestamp("et")
    };

    public static Dictionary<string, object?> ProjectSetListView(BsonDocument set) => new()
    {
        ["id"] = set["_id"].ToString(),
        ["name"] = set.Text("n"),
        ["default"] = set.Flag("d")
    };

    public async Task<object> TargetViewAsync(BsonDocument target, bool chinese, CancellationToken ct)
    {
        var language = await LanguageAsync(target["l"].AsObjectId, ct);
        return new
        {
            id = target["_id"].ToString(),
            language = language is null ? null : LanguageView(language, chinese),
            translated_source_count = target.Integer("tsc"),
            checked_source_count = target.Integer("csc"),
            create_time = target.Timestamp("ct"),
            edit_time = target.Timestamp("et"),
            intro = target.Text("i")
        };
    }

    public Dictionary<string, object?> LanguageView(BsonDocument language, bool chinese) => new()
    {
        ["id"] = language["_id"].ToString(),
        ["en_name"] = language.Text("e"),
        ["lo_name"] = language.Text("c"),
        ["i18n_name"] = language.Text("c"),
        ["no_space"] = language.Flag("n"),
        ["code"] = language.Text("co"),
        ["g_tra_code"] = language.Text("gt"),
        ["g_ocr_code"] = language.Text("go"),
        ["sort"] = language.GetValue("s", 0).ToInt32()
    };

    private static string LabelplusErrorTypeName(int value) => value switch
    {
        0 => "未知错误",
        1 => "文件解析失败",
        _ => "未知错误"
    };

    private static bool CanOrderFiles(BsonDocument project, PermissionSnapshot snapshot)
    {
        if (project.Integer("st", 0) != 0) return false;
        if (!snapshot.Has("project:ACCESS")) return false;
        var allowed = new HashSet<string> { "team_inherited:creator", "team_inherited:admin", "project_tag:creator", "project_tag:admin" };
        return snapshot.PermissionSources.Values.Any(sources => sources.Any(allowed.Contains));
    }

    // ---- member summaries ----

    public async Task<object[]> MemberSummariesAsync(BsonDocument project, bool compact, CancellationToken ct)
    {
        var rows = await MemberRows.Find(new BsonDocument
        {
            ["project"] = project["_id"],
            ["status"] = new BsonDocument("$in", new BsonArray { "active", "invited" })
        }).Sort(new BsonDocument { ["display_name"] = 1, ["_id"] = 1 }).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var users = new Dictionary<ObjectId, BsonDocument>();
        var userIds = rows.Where(r => r.GetValue("user", BsonNull.Value).IsObjectId).Select(r => r["user"].AsObjectId).Distinct().ToList();
        if (userIds.Count > 0)
            foreach (var user in await store.Collection("user").Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(userIds)))).ToListAsync(ct))
                users[user["_id"].AsObjectId] = user;
        var snapshots = new Dictionary<ObjectId, PermissionSnapshot>();
        foreach (var row in rows)
        {
            if (!row.GetValue("user", BsonNull.Value).IsObjectId) continue;
            var uid = row["user"].AsObjectId;
            if (snapshots.ContainsKey(uid)) continue;
            if (users.TryGetValue(uid, out var user))
                snapshots[uid] = await permissions.ProjectSnapshotAsync(user, project, ct);
        }
        return rows.Select(row => (object)MemberSummary(row, project, users, snapshots, compact)).ToArray();
    }

    private static Dictionary<string, object?> MemberSummary(BsonDocument row, BsonDocument project,
        Dictionary<ObjectId, BsonDocument> users, Dictionary<ObjectId, PermissionSnapshot> snapshots, bool compact)
    {
        var user = users.GetValueOrDefault(row.GetValue("user", BsonNull.Value).IsObjectId ? row["user"].AsObjectId : default);
        var tags = row.GetValue("tags", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray();
        if (compact) return new Dictionary<string, object?>
        {
            ["id"] = row["_id"].ToString(),
            ["display_name"] = row.Text("display_name"),
            ["tags"] = tags,
            ["status"] = row.Text("status", "active")
        };
        var snapshot = user is null ? null : snapshots.GetValueOrDefault(user["_id"].AsObjectId);
        var isOwner = user is not null && project.GetValue("ou", BsonNull.Value).IsObjectId
            && project["ou"] == user["_id"] && row.Text("status", "active") == "active" && tags.Contains("creator");
        string? userId = null;
        Dictionary<string, object?>? userView = null;
        if (user is not null)
        {
            userId = user["_id"].ToString();
            userView = new Dictionary<string, object?>
            {
                ["id"] = userId,
                ["name"] = user.Text("n"),
                ["avatar"] = user.Text("a"),
                ["has_avatar"] = user.Text("a").Length != 0,
                ["aliases"] = user.GetValue("as", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToArray()
            };
        }
        return new Dictionary<string, object?>
        {
            ["id"] = row["_id"].ToString(),
            ["project_id"] = project["_id"].ToString(),
            ["user_id"] = userId,
            ["external_id"] = row.GetValue("external_id", BsonNull.Value).IsString ? row.Text("external_id") : null,
            ["user"] = userView,
            ["display_name"] = row.Text("display_name"),
            ["tags"] = tags,
            ["effective_permissions"] = snapshot?.EffectivePermissions.Order(StringComparer.Ordinal).ToArray() ?? [],
            ["status"] = row.Text("status", "active"),
            ["version"] = row.Integer("version"),
            ["is_owner"] = isOwner,
            ["create_time"] = row.Timestamp("create_time"),
            ["edit_time"] = row.Timestamp("edit_time")
        };
    }

    // ---- helpers ----

    private static int AllowOrCheckType(System.Text.Json.JsonElement data, string field, int allowedA, int allowedB)
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.Number || !value.TryGetInt32(out var number) || number is not (1 or 2))
            throw ApiError.Field(field, "Invalid value");
        return number;
    }

    private static string[] TargetLanguageCodes(System.Text.Json.JsonElement data)
    {
        if (!data.TryGetProperty("target_languages", out var value)) return [];
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) throw ApiError.Field("target_languages", "Invalid value");
        var codes = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw ApiError.Field("target_languages", "Invalid value");
            codes.Add(item.GetString()!.Trim());
        }
        return codes.ToArray();
    }
}

public static class ProjectRequestDataExtensions
{
    /// <summary>
    /// Reads the optional labelplus_txt string without a length cap. Python's
    /// CreateProjectSchema uses a plain fields.Str (missing=None), so a present
    /// non-string value is a validation error and an absent/null value is empty.
    /// </summary>
    public static string OptionalLabelplusText(this System.Text.Json.JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
            return "";
        if (value.ValueKind != System.Text.Json.JsonValueKind.String) throw ApiError.Field(field, "Invalid value");
        return value.GetString()!;
    }

    public static string OptionalText(this System.Text.Json.JsonElement data, string field, int max, string fallback = "")
    {
        if (!data.TryGetProperty(field, out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null) return fallback;
        if (value.ValueKind != System.Text.Json.JsonValueKind.String) throw ApiError.Field(field, "Invalid value");
        var text = value.GetString()!;
        if (text.EnumerateRunes().Count() > max) throw ApiError.Field(field, "内容过长");
        return text;
    }
}
