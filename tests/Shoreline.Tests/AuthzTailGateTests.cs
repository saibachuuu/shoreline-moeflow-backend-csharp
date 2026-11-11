using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

/// <summary>
/// Tail authorization coverage for docs/authz-matrix.md §6 (label snapshots,
/// clear, merge, recover), §8 (PUT /v1/invitations/{id} role hierarchy) and
/// §10 (PUT /v1/admin/admin-status configured-owner gate).
/// Every expectation was checked against the Flask oracle first:
/// app/apis/invitation.py:195-239 (PUT level rules), app/apis/label_snapshot.py:13-30
/// (capabilities), app/apis/user.py:198-231 + app/models/user.py:849-862
/// (can_manage_site_admins); legacy "forbidden" is 400 + code 103 on both stacks.
/// </summary>
[Collection("local-services")]
public sealed class AuthzTailGateTests(LocalServices services)
{
    [Fact]
    public async Task InvitationsPut_TeamRoleHierarchy_OwnerRaises_AdminCannotRaisePeer_TranslatorDenied()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var memberRole = await RoleAsync("team_role", "member");
        var adminRole = await RoleAsync("team_role", "admin");
        var path = $"/v1/teams/{world.TeamId}/invitations";

        // Owner (creator, level 500) invites the stranger as a plain member.
        var first = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync(path, new
            {
                user_id = world.Stranger["_id"].AsObjectId.ToString(),
                role_id = memberRole, message = "join"
            }));
        var firstId = first.GetProperty("invitation").GetProperty("id").GetString()!;

        // A project translator has no team INVITE_USER: cannot touch others' invites.
        // Python invitation.py:198-201 (can INVITE_USER) -> 103.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PutAsJsonAsync($"/v1/invitations/{firstId}",
                new { role_id = memberRole }),
            HttpStatusCode.BadRequest, 103);

        // Team admin (level 400) may retarget a lower (member) invitation sideways...
        var peer = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync(path, new
            {
                user_id = world.Typesetter["_id"].AsObjectId.ToString(),
                role_id = memberRole, message = "join"
            }));
        var peerId = peer.GetProperty("invitation").GetProperty("id").GetString()!;
        var kept = await AuthzAssert.OkAsync(
            await world.Client("teamAdmin").PutAsJsonAsync($"/v1/invitations/{peerId}",
                new { role_id = memberRole }));
        Assert.Equal("修改成功", kept.GetProperty("message").GetString());

        // ...but cannot mint a peer admin (Python :234-236, new role level check).
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PutAsJsonAsync($"/v1/invitations/{peerId}",
                new { role_id = adminRole }),
            HttpStatusCode.BadRequest, 103);

        // Owner raises the member invitation to admin (500 > 200, 500 > 400).
        var raised = await AuthzAssert.OkAsync(
            await world.Client("owner").PutAsJsonAsync($"/v1/invitations/{firstId}",
                new { role_id = adminRole }));
        Assert.Equal("修改成功", raised.GetProperty("message").GetString());

        // The invitation is now admin-level: the team admin can no longer edit it
        // (Python :228, current-role level check: 400 <= 400).
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PutAsJsonAsync($"/v1/invitations/{firstId}",
                new { role_id = memberRole }),
            HttpStatusCode.BadRequest, 103);
    }

    [Fact]
    public async Task InvitationsPut_ProjectTagsRequireInviteUser()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/projects/{world.ProjectId}/invitations";

        // The admin tag bypasses the worker-qualification gate (matrix D-3).
        var created = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync(path, new
            {
                user_id = world.Stranger["_id"].AsObjectId.ToString(),
                tags = new[] { "admin" }, message = "join"
            }));
        var invitationId = created.GetProperty("invitation").GetProperty("id").GetString()!;
        var invitationPath = $"/v1/invitations/{invitationId}";

        // Project-tag updates carry no level rules (invitation.py:202-225),
        // but still require INVITE_USER first.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PutAsJsonAsync(invitationPath,
                new { tags = new[] { "admin" } }),
            HttpStatusCode.BadRequest, 103);
        var updated = await AuthzAssert.OkAsync(
            await world.Client("owner").PutAsJsonAsync(invitationPath,
                new { tags = new[] { "admin" } }));
        Assert.Equal("修改成功", updated.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Snapshots_RestoreRequiresManager_MergeRequiresProofreadTriple()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var basePath = $"/v1/projects/{world.ProjectId}";

        // Snapshot endpoints exist on both stacks (Python urls.py:404-416).
        await AuthzAssert.ErrorAsync(
            await world.Anon.GetAsync($"{basePath}/snapshots"),
            HttpStatusCode.Unauthorized, 1003);
        // create = manager/proofreader/output-holder (label_snapshot.py:17-19):
        // translator (OUTPUT_TRA) and proofreader may list/create...
        await AuthzAssert.OkAsync(await world.Client("translator").GetAsync($"{basePath}/snapshots"));
        await AuthzAssert.OkAsync(await world.Client("proofreader").GetAsync($"{basePath}/snapshots"));
        // ...a plain team member and a stranger may not.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").GetAsync($"{basePath}/snapshots"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").GetAsync($"{basePath}/snapshots"),
            HttpStatusCode.BadRequest, 103);

        var created = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync($"{basePath}/snapshots", new { name = "m" }));
        var snapshotId = created.GetProperty("snapshot_id").GetString()!;

        // restore = manager only (:20): proofreader can create but not restore.
        await AuthzAssert.ErrorAsync(
            await world.Client("proofreader").PostAsJsonAsync(
                $"{basePath}/snapshots/{snapshotId}/restore", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(
                $"{basePath}/snapshots/{snapshotId}/restore", new { }),
            HttpStatusCode.BadRequest, 103);
        // The guard runs before the body/target lookup (and before the
        // endpoint gate once the revision is bumped — Python
        // label_lock.py:345-365, C# LabelRequestGuard.cs:101-102): denied
        // callers get 103 here because the revision is still 0.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync($"{basePath}/label-merge", new { }),
            HttpStatusCode.BadRequest, 103);
        var restored = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync(
                $"{basePath}/snapshots/{snapshotId}/restore", new { }));
        Assert.False(string.IsNullOrWhiteSpace(restored.GetProperty("snapshot_id").GetString()));

        // merge = manager or (proofreader & CHECK_TRA & ADD_TRA) (:21-29):
        // proofreader passes, translator/typesetter do not.
        // Reads need no revision header.
        await AuthzAssert.OkAsync(await world.Client("owner")
            .GetAsync($"{basePath}/label-merge?target_id={world.TargetId}"));
        await AuthzAssert.OkAsync(await world.Client("proofreader")
            .GetAsync($"{basePath}/label-merge?target_id={world.TargetId}"));
        await AuthzAssert.ErrorAsync(await world.Client("translator")
            .GetAsync($"{basePath}/label-merge?target_id={world.TargetId}"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await world.Client("typesetter")
            .GetAsync($"{basePath}/label-merge?target_id={world.TargetId}"),
            HttpStatusCode.BadRequest, 103);
    }

    [Fact]
    public async Task LabelClear_ProofreadVsFullGates_RecoverRequiresManager()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var basePath = $"/v1/projects/{world.ProjectId}";

        // All denied writes run at revision 0, so the revision guard
        // (Python label_lock.py:345-365) lets them reach the capability gate.
        // label-recover needs restore (manager only, :180).
        await AuthzAssert.ErrorAsync(
            await world.Client("proofreader").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 103);
        // label-clear with field=proofread needs clear_proofread
        // (manager or proofreader, label_snapshot.py:124-128)...
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync($"{basePath}/label-clear",
                new { field = "proofread" }),
            HttpStatusCode.BadRequest, 103);
        // ...any other field needs full restore (manager only).
        await AuthzAssert.ErrorAsync(
            await world.Client("proofreader").PostAsJsonAsync($"{basePath}/label-clear",
                new { field = "source" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync($"{basePath}/label-clear",
                new { field = "translation" }),
            HttpStatusCode.BadRequest, 103);
        var clearedProofread = await AuthzAssert.OkAsync(
            await world.Client("proofreader").PostAsJsonAsync($"{basePath}/label-clear",
                new { field = "proofread" }));
        Assert.False(string.IsNullOrWhiteSpace(
            clearedProofread.GetProperty("snapshot_id").GetString()));

        // The first clear bumped the revision: later writes must carry
        // X-Label-Revision past the guard before the gate sees them.
        var cleared = await AuthzAssert.OkAsync(await SendWithRevisionAsync(
            world.Client("owner"), $"{basePath}/label-clear",
            new { field = "source" }, await RevisionAsync(world)));
        Assert.False(string.IsNullOrWhiteSpace(cleared.GetProperty("snapshot_id").GetString()));

        // Manager-only recover with no pending operation surfaces the service
        // error on both stacks (Python services/label_snapshot.py:255-259).
        await AuthzAssert.ErrorAsync(await SendWithRevisionAsync(
            world.Client("owner"), $"{basePath}/label-recover", new { }, await RevisionAsync(world)),
            HttpStatusCode.Conflict, 1);
    }

    [Fact]
    public async Task AdminStatus_OnlyConfiguredOwnerMayGrant_BannedOwnerDenied()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        const string path = "/v1/admin/admin-status";

        // Anonymous and non-admin callers are rejected before the owner check.
        await AuthzAssert.ErrorAsync(
            await world.Anon.PutAsJsonAsync(path,
                new { user_id = world.Stranger["_id"].AsObjectId.ToString(), status = true }),
            HttpStatusCode.Unauthorized, 1003);
        await AuthzAssert.ErrorAsync(
            await world.Client("owner").PutAsJsonAsync(path,
                new { user_id = world.Stranger["_id"].AsObjectId.ToString(), status = true }),
            HttpStatusCode.BadRequest, 103);
        // A site admin whose email is not the configured ADMIN_EMAIL is denied
        // (Python models/user.py:849-862 can_manage_site_admins fails closed).
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").PutAsJsonAsync(path,
                new { user_id = world.Stranger["_id"].AsObjectId.ToString(), status = true }),
            HttpStatusCode.BadRequest, 103);

        // Promote the site admin to the configured owner email (LocalServices
        // sets Auth:AdminEmail=owner@example.test); the token stays valid
        // because it is password-bound, not email-bound.
        await services.Store.Collection("user").UpdateOneAsync(
            new BsonDocument("_id", world.SiteAdmin["_id"]),
            new BsonDocument("$set", new BsonDocument("e", "owner@example.test")));
        var promoted = await AuthzAssert.OkAsync(
            await world.Client("siteAdmin").PutAsJsonAsync(path,
                new { user_id = world.Stranger["_id"].AsObjectId.ToString(), status = true }));
        Assert.Equal("修改成功", promoted.GetProperty("message").GetString());
        Assert.True((await services.Store.Collection("user")
            .Find(new BsonDocument("_id", world.Stranger["_id"])).FirstAsync()).Flag("admin"));

        // A banned configured owner is rejected: C# RequireAsync fires first
        // (400/1006); Python can_manage_site_admins would fail closed with
        // NoPermissionError (400/103) instead — C# is stricter (matrix §0/D-1).
        await services.Store.Collection("user").UpdateOneAsync(
            new BsonDocument("_id", world.SiteAdmin["_id"]),
            new BsonDocument("$set", new BsonDocument("b", true)));
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").PutAsJsonAsync(path,
                new { user_id = world.Translator["_id"].AsObjectId.ToString(), status = true }),
            HttpStatusCode.BadRequest, 1006);
    }

    private async Task<string> RoleAsync(string collection, string code) =>
        (await services.Store.Collection(collection)
            .Find(new BsonDocument("m_o", code)).FirstAsync())["_id"].AsObjectId.ToString();

    private async Task<string> RevisionAsync(AuthzWorld world) =>
        (await services.Store.Collection("project")
            .Find(new BsonDocument("_id", world.ProjectId)).FirstAsync())
            .Integer("label_revision", 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<HttpResponseMessage> SendWithRevisionAsync(
        HttpClient client, string url, object body, string revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Label-Revision", revision);
        return await client.SendAsync(request);
    }
}
