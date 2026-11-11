using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Tests;

/// <summary>
/// Extended authorization gates for join flows (project/team invitations,
/// project/team applications), term-bank terms, and site-admin endpoints.
/// Every expectation was checked against the Flask oracle first
/// (app/apis/{invitation,application,term}.py, app/decorators/auth.py);
/// legacy "forbidden" is 400 + code 103 on both stacks.
/// </summary>
[Collection("local-services")]
public sealed class AuthzJoinAdminGateTests(LocalServices services)
{
    [Fact]
    public async Task ProjectInvitations_MutationGates()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/projects/{world.ProjectId}/invitations";

        // Python invitation.py:124-125: project-tag invites need INVITE_USER.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(path, new
            {
                user_id = world.Stranger["_id"].AsObjectId.ToString(),
                tags = new[] { "admin" }, message = "加入项目"
            }),
            HttpStatusCode.BadRequest, 103);
        var created = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync(path, new
            {
                user_id = world.Stranger["_id"].AsObjectId.ToString(),
                tags = new[] { "admin" }, message = "加入项目"
            }));
        var invitationId = created.GetProperty("invitation").GetProperty("id").GetString()!;
        var invitationPath = $"/v1/invitations/{invitationId}";

        // Python invitation.py:268-269: only the invitee may accept.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PatchAsJsonAsync(invitationPath, new { allow = true }),
            HttpStatusCode.BadRequest, 103);
        // Python invitation.py:300-303: deleting needs INVITE_USER.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").DeleteAsync(invitationPath),
            HttpStatusCode.BadRequest, 103);
        // The invitee accepts.
        var accepted = await AuthzAssert.OkAsync(
            await world.Client("stranger").PatchAsJsonAsync(invitationPath, new { allow = true }));
        Assert.Equal("Approved", accepted.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TeamInvitations_ListAndCreateGates()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}/invitations";

        // Python invitation.py:71-72: listing needs INVITE_USER; a plain
        // team member (ACCESS only) is denied.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").GetAsync(path),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.OkAsync(await world.Client("owner").GetAsync(path));

        // Creating needs INVITE_USER too (user.py invite -> NoPermissionError).
        var memberRole = (await services.Store.Collection("team_role")
            .Find(new BsonDocument("m_o", "member")).FirstAsync())["_id"].AsObjectId.ToString();
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(path, new
            {
                user_id = world.Stranger["_id"].AsObjectId.ToString(),
                role_id = memberRole, message = "加入团队"
            }),
            HttpStatusCode.BadRequest, 103);
    }

    [Fact]
    public async Task ProjectApplications_FlowGates()
    {
        // allowApplyType 2 opens the project to any applicant (ApplyAll);
        // the default world (1) is closed.
        await using var world = await AuthzWorld.CreateAsync(services, allowApplyType: 2);
        var path = $"/v1/projects/{world.ProjectId}/applications";

        var applied = await AuthzAssert.OkAsync(
            await world.Client("stranger").PostAsJsonAsync(path, new { message = "求加入" }));
        // Python user.py:701: needs-check groups answer with the pending copy.
        Assert.Equal("Application Sent. Please wait for administrator to review.", applied.GetProperty("message").GetString());
        var applicationId = (await ApplicationAsync(world.ProjectId, world.Stranger))["_id"]
            .AsObjectId.ToString();
        var applicationPath = $"/v1/applications/{applicationId}";

        // Python application.py:139-142: approving needs CHECK_USER.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PatchAsJsonAsync(applicationPath, new { allow = true }),
            HttpStatusCode.BadRequest, 103);
        var approved = await AuthzAssert.OkAsync(
            await world.Client("owner").PatchAsJsonAsync(applicationPath, new { allow = true }));
        Assert.Equal("Approved", approved.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProjectApplications_DeleteOnlyByApplicant()
    {
        await using var world = await AuthzWorld.CreateAsync(services, allowApplyType: 2);
        var path = $"/v1/projects/{world.ProjectId}/applications";

        await AuthzAssert.OkAsync(
            await world.Client("siteAdmin").PostAsJsonAsync(path, new { message = "旁观申请" }));
        var applicationId = (await ApplicationAsync(world.ProjectId, world.SiteAdmin))["_id"]
            .AsObjectId.ToString();
        var applicationPath = $"/v1/applications/{applicationId}";

        // Python application.py:180-181: only the applicant may delete.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").DeleteAsync(applicationPath),
            HttpStatusCode.BadRequest, 103);
        var deleted = await AuthzAssert.OkAsync(
            await world.Client("siteAdmin").DeleteAsync(applicationPath));
        Assert.Equal("Removed", deleted.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TeamApplications_ListRequiresCheckUser()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}/applications";

        // Python application.py:68-69: listing needs CHECK_USER.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").GetAsync(path),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.OkAsync(await world.Client("owner").GetAsync(path));
    }

    [Fact]
    public async Task Terms_MutationGates()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var bankId = await BankAsync(world, source, target);
        var termId = await TermAsync(world, bankId);

        // Python term.py:176-179 / 266-270 / 301-305: ACCESS_TERM_BANK for
        // listing, CHANGE_TERM for edit, DELETE_TERM for delete. A plain
        // team member (ACCESS only) has none of them.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").GetAsync($"/v1/term-banks/{bankId}/terms"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PutAsJsonAsync($"/v1/terms/{termId}",
                new { source = "x", target = "y", tip = "" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PutAsJsonAsync($"/v1/terms/{termId}",
                new { source = "x", target = "y", tip = "" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").DeleteAsync($"/v1/terms/{termId}"),
            HttpStatusCode.BadRequest, 103);
        // The team creator passes (CHANGE_TERM/owner branch).
        var edited = await AuthzAssert.OkAsync(
            await world.Client("owner").PutAsJsonAsync($"/v1/terms/{termId}",
                new { source = "hello2", target = "你好2", tip = "tip2" }));
        Assert.Equal("Updated", edited.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AdminEndpoints_RequireSiteAdmin()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        // Python admin_required -> NoPermissionError -> 400/103, matching C#
        // RequireAdminAsync -> Forbidden (400/103). Banned callers are
        // rejected by RequireAsync first (400/1006); the Python oracle does
        // not check banned on admin routes (see docs/authz-matrix.md §0).
        await AuthzAssert.ErrorAsync(
            await world.Client("owner").GetAsync("/v1/admin/users"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("owner").GetAsync("/v1/admin/v-codes"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("owner").GetAsync("/v1/admin/storage-pool/sources"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Anon.GetAsync("/v1/admin/users"),
            HttpStatusCode.Unauthorized, 1003);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").GetAsync("/v1/admin/users"),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.OkAsync(await world.Client("siteAdmin").GetAsync("/v1/admin/users"));
        await AuthzAssert.OkAsync(await world.Client("siteAdmin").GetAsync("/v1/admin/v-codes"));
    }

    // ---- helpers ----

    private Task<BsonDocument> ApplicationAsync(ObjectId group, BsonDocument user) =>
        services.Store.Collection("application").Find(new BsonDocument
        {
            ["u"] = user["_id"], ["g._ref.$id"] = group
        }).Sort(Builders<BsonDocument>.Sort.Descending("_id")).FirstAsync();

    private async Task<BsonDocument> LanguageAsync(string code, string name)
    {
        var existing = await services.Store.Collection("language")
            .Find(new BsonDocument("co", code)).FirstOrDefaultAsync();
        if (existing is not null) return existing;
        var language = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["e"] = name, ["c"] = name,
            ["co"] = code, ["gt"] = code, ["go"] = code, ["n"] = false, ["s"] = 0
        };
        await services.Store.Collection("language").InsertOneAsync(language);
        return language;
    }

    private async Task<string> BankAsync(AuthzWorld world, BsonDocument source, BsonDocument target)
    {
        var created = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync(
                $"/v1/teams/{world.TeamId}/term-banks", new
                {
                    name = "bank_" + ObjectId.GenerateNewId().ToString()[12..],
                    tip = "",
                    source_language_id = source["_id"].ToString(),
                    target_language_id = target["_id"].ToString()
                }));
        Assert.Equal("Created successfully.", created.GetProperty("message").GetString());
        var banks = await AuthzAssert.OkAsync(
            await world.Client("owner").GetAsync(
                $"/v1/teams/{world.TeamId}/term-banks?limit=100"));
        return banks.EnumerateArray().Last().GetProperty("id").GetString()!;
    }

    private async Task<string> TermAsync(AuthzWorld world, string bankId)
    {
        var created = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync($"/v1/term-banks/{bankId}/terms",
                new { source = "hello", target = "你好", tip = "" }));
        Assert.Equal("Created successfully.", created.GetProperty("message").GetString());
        var terms = await AuthzAssert.OkAsync(
            await world.Client("owner").GetAsync($"/v1/term-banks/{bankId}/terms?word=hello"));
        return terms.EnumerateArray().Last().GetProperty("id").GetString()!;
    }
}
