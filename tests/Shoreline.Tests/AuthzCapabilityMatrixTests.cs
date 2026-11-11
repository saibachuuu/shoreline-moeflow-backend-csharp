using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Tests;

/// <summary>
/// Full capabilities matrix for docs/authz-matrix.md §6 (label-clear, label-merge,
/// label-recover) across owner / project-admin / proofreader / translator /
/// teamMember / stranger / banned (+ anon and siteAdmin as reference rows).
/// Oracle: app/apis/label_snapshot.py:13-35 (capabilities), :106-182 (gates);
/// Python maps "forbidden" to 400 + code 103 on both stacks.
/// </summary>
[Collection("local-services")]
public sealed class AuthzCapabilityMatrixTests(LocalServices services)
{
    [Fact]
    public async Task LabelClear_FullCapabilityMatrix()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        await PromoteTeamAdminToProjectAdminAsync(world);
        var basePath = $"/v1/projects/{world.ProjectId}";

        // Revision 0: the label guard lets every write through to the
        // capability gate (Python label_lock.py:345-365), so no header needed.
        await AuthzAssert.ErrorAsync(
            await world.Anon.PostAsJsonAsync($"{basePath}/label-clear", new { field = "source" }),
            HttpStatusCode.Unauthorized, 1003);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").PostAsJsonAsync($"{basePath}/label-clear", new { field = "source" }),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync($"{basePath}/label-clear", new { field = "source" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync($"{basePath}/label-clear", new { field = "source" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PostAsJsonAsync($"{basePath}/label-clear", new { field = "source" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").PostAsJsonAsync($"{basePath}/label-clear", new { field = "source" }),
            HttpStatusCode.BadRequest, 103);
        // Proofreader holds clear_proofread but not restore (label_snapshot.py:124-128).
        await AuthzAssert.ErrorAsync(
            await world.Client("proofreader").PostAsJsonAsync($"{basePath}/label-clear", new { field = "source" }),
            HttpStatusCode.BadRequest, 103);

        // Allowed writes bump the revision; every later write carries the
        // current revision so denied callers still reach the gate (and get
        // 103 rather than a stale-revision error).
        var clearedOwner = await AuthzAssert.OkAsync(await PostWithRevisionAsync(
            world.Client("owner"), $"{basePath}/label-clear", new { field = "source" }, await RevisionAsync(world)));
        Assert.False(string.IsNullOrWhiteSpace(clearedOwner.GetProperty("snapshot_id").GetString()));

        var clearedAdmin = await AuthzAssert.OkAsync(await PostWithRevisionAsync(
            world.Client("teamAdmin"), $"{basePath}/label-clear", new { field = "source" }, await RevisionAsync(world)));
        Assert.False(string.IsNullOrWhiteSpace(clearedAdmin.GetProperty("snapshot_id").GetString()));

        var clearedProofread = await AuthzAssert.OkAsync(await PostWithRevisionAsync(
            world.Client("proofreader"), $"{basePath}/label-clear", new { field = "proofread" }, await RevisionAsync(world)));
        Assert.False(string.IsNullOrWhiteSpace(clearedProofread.GetProperty("snapshot_id").GetString()));

        // Denied roles stay denied after the revision moved.
        var revision = await RevisionAsync(world);
        await AuthzAssert.ErrorAsync(await PostWithRevisionAsync(
            world.Client("translator"), $"{basePath}/label-clear", new { field = "proofread" }, revision),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await PostWithRevisionAsync(
            world.Client("teamMember"), $"{basePath}/label-clear", new { field = "source" }, revision),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await PostWithRevisionAsync(
            world.Client("stranger"), $"{basePath}/label-clear", new { field = "source" }, revision),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await PostWithRevisionAsync(
            world.Client("siteAdmin"), $"{basePath}/label-clear", new { field = "source" }, revision),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(await PostWithRevisionAsync(
            world.Client("banned"), $"{basePath}/label-clear", new { field = "source" }, revision),
            HttpStatusCode.BadRequest, 1006);
    }

    [Fact]
    public async Task LabelMerge_FullCapabilityMatrix()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        await PromoteTeamAdminToProjectAdminAsync(world);
        var basePath = $"/v1/projects/{world.ProjectId}";
        var target = world.TargetId.ToString();

        // GET preview carries the same `merge` gate as POST (label_snapshot.py:145-158).
        await AuthzAssert.ErrorAsync(
            await world.Anon.GetAsync($"{basePath}/label-merge?target_id={target}"),
            HttpStatusCode.Unauthorized, 1003);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").GetAsync($"{basePath}/label-merge?target_id={target}"),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").GetAsync($"{basePath}/label-merge?target_id={target}"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("typesetter").GetAsync($"{basePath}/label-merge?target_id={target}"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").GetAsync($"{basePath}/label-merge?target_id={target}"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").GetAsync($"{basePath}/label-merge?target_id={target}"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").GetAsync($"{basePath}/label-merge?target_id={target}"),
            HttpStatusCode.BadRequest, 103);
        // Manager or (proofread & CHECK_TRA & ADD_TRA): owner, project admin, proofreader pass.
        await AuthzAssert.OkAsync(await world.Client("owner").GetAsync($"{basePath}/label-merge?target_id={target}"));
        await AuthzAssert.OkAsync(await world.Client("teamAdmin").GetAsync($"{basePath}/label-merge?target_id={target}"));
        await AuthzAssert.OkAsync(await world.Client("proofreader").GetAsync($"{basePath}/label-merge?target_id={target}"));

        // POST without the gate bit is rejected before the preview token is
        // even looked at (revision 0, so no revision header needed).
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync($"{basePath}/label-merge", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync($"{basePath}/label-merge", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PostAsJsonAsync($"{basePath}/label-merge", new { }),
            HttpStatusCode.BadRequest, 103);

        // Full preview-token flow for every merge holder: GET preview, then
        // POST with its token. Each merge bumps the revision, so re-read it.
        foreach (var role in new[] { "owner", "teamAdmin", "proofreader" })
        {
            var preview = await AuthzAssert.OkAsync(
                await world.Client(role).GetAsync($"{basePath}/label-merge?target_id={target}"));
            var token = preview.TryGetProperty("token", out var tokenProperty)
                ? tokenProperty.GetString() : null;
            var merged = await AuthzAssert.OkAsync(await PostWithRevisionAsync(
                world.Client(role),
                $"{basePath}/label-merge",
                new
                {
                    target_id = target,
                    token,
                    versions = new Dictionary<string, string>(),
                    selections = new Dictionary<string, string>()
                },
                await RevisionAsync(world)));
            Assert.False(string.IsNullOrWhiteSpace(merged.GetProperty("snapshot_id").GetString()));
        }
    }

    [Fact]
    public async Task LabelRecover_FullCapabilityMatrix()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        await PromoteTeamAdminToProjectAdminAsync(world);
        var basePath = $"/v1/projects/{world.ProjectId}";

        // Recover is manager-only (restore gate, label_snapshot.py:176-182).
        // With no pending operation both managers pass the gate and the
        // service reports "nothing to recover" (409/code 1, both stacks).
        await AuthzAssert.ErrorAsync(
            await world.Anon.PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.Unauthorized, 1003);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(
            await world.Client("proofreader").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("owner").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.Conflict, 1);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PostAsJsonAsync($"{basePath}/label-recover", new { }),
            HttpStatusCode.Conflict, 1);
    }

    // The world's teamAdmin is a team-level admin only; granting them the
    // project `admin` tag makes them the "admin" matrix role (a manager via
    // MANAGE_MEMBERS, without touching any other fixture identity).
    private static async Task PromoteTeamAdminToProjectAdminAsync(AuthzWorld world)
    {
        var response = await world.Client("owner").PostAsJsonAsync(
            $"/v1/projects/{world.ProjectId}/members/changes",
            new
            {
                operations = new[]
                {
                    new
                    {
                        operation_id = "promote-project-admin",
                        action = "add",
                        user_id = world.TeamAdmin["_id"].AsObjectId.ToString(),
                        tags = new[] { "admin" }
                    }
                }
            });
        await AuthzAssert.OkAsync(response);
    }

    private async Task<string> RevisionAsync(AuthzWorld world) =>
        (await services.Store.Collection("project")
            .Find(new BsonDocument("_id", world.ProjectId)).FirstAsync())
            .GetValue("label_revision", 0).ToInt32().ToString(CultureInfo.InvariantCulture);

    private static async Task<HttpResponseMessage> PostWithRevisionAsync(
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
