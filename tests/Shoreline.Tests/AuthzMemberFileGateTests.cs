using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Tests;

/// <summary>
/// Extended write/manage authorization gates: project external-member
/// bind/merge/hard-delete, file rename/move/delete, and output creation.
/// Every expectation was checked against the Flask oracle first
/// (app/services/project_member.py, app/apis/file.py, app/apis/project.py);
/// legacy "forbidden" is 400 + code 103 on both stacks.
/// </summary>
[Collection("local-services")]
public sealed class AuthzMemberFileGateTests(LocalServices services)
{
    [Fact]
    public async Task ProjectMemberBind_RequiresManager()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var memberId = await ProjectMemberIdAsync(world, world.Translator);

        // Python project_member.py:704-705: non-manager -> NoPermissionError.
        // C# BindMemberAsync checks RequireOperator + IsManager before body
        // validation, so even `{}` reaches 103 for non-managers.
        var path = $"/v1/projects/{world.ProjectId}/members/{memberId}/bind";
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").PostAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").PostAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(
            await world.Anon.PostAsJsonAsync(path, new { }),
            HttpStatusCode.Unauthorized, 1003);
    }

    [Fact]
    public async Task ProjectMemberMerge_RequiresManager()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var memberId = await ProjectMemberIdAsync(world, world.Translator);

        // Python project_member.py:944-945: non-manager -> NoPermissionError.
        var path = $"/v1/projects/{world.ProjectId}/members/{memberId}/merge";
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").PostAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").PostAsJsonAsync(path, new { }),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(
            await world.Anon.PostAsJsonAsync(path, new { }),
            HttpStatusCode.Unauthorized, 1003);
    }

    [Fact]
    public async Task ProjectMemberHardDelete_RequiresOwnerOrTeamCreator()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var memberId = await ProjectMemberIdAsync(world, world.Translator);

        // Python project_member.py:867-875: only project owner or team
        // creator; everyone else -> NoPermissionError. A team *admin* is
        // still denied (creator-only, not manager).
        var path = $"/v1/projects/{world.ProjectId}/members/{memberId}";
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").DeleteAsync(path),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").DeleteAsync(path),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").DeleteAsync(path),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(
            await world.Anon.DeleteAsync(path),
            HttpStatusCode.Unauthorized, 1003);
    }

    [Fact]
    public async Task FileRenameMoveDelete_RequireFilePermissions()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var fileId = await UploadAsync(world, "gate.png");

        // Python file.py:266/274/298: MOVE_FILE / RENAME_FILE / DELETE_FILE.
        // teamMember has team ACCESS (passes the access gate) but no file
        // permission bits -> 103.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PutAsJsonAsync($"/v1/files/{fileId}",
                new { name = "renamed.png" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PutAsJsonAsync($"/v1/files/{fileId}",
                new { name = "renamed.png" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PutAsJsonAsync($"/v1/files/{fileId}",
                new { parent_id = "root" }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").DeleteAsync($"/v1/files/{fileId}"),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").PutAsJsonAsync($"/v1/files/{fileId}",
                new { name = "renamed.png" }),
            HttpStatusCode.BadRequest, 1006);
        // Translator carries RENAME_FILE: the gate passes for the permitted.
        var renamed = await AuthzAssert.OkAsync(
            await world.Client("translator").PutAsJsonAsync($"/v1/files/{fileId}",
                new { name = "renamed.png" }));
        Assert.Equal("Renamed", renamed.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProjectOutputsPost_RequiresOutputPermission()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        // Python project.py:183/257: OUTPUT_TRA on both create-all and
        // per-target creation; auth runs before `type` validation.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/outputs", new { }),
            HttpStatusCode.BadRequest, 103);
        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/outputs", new { }),
            HttpStatusCode.BadRequest, 103);
        var targetPath = $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs";
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync(targetPath, new { type = 99 }),
            HttpStatusCode.BadRequest, 103);
        // Permitted caller with a bad `type` reaches validation, not 103.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(targetPath, new { type = 99 }),
            HttpStatusCode.BadRequest, 2);
        // Permitted caller with a good `type` creates the output.
        await AuthzAssert.OkAsync(
            await world.Client("translator").PostAsJsonAsync(targetPath, new { type = 0 }));
    }

    // ---- helpers ----

    private async Task<string> ProjectMemberIdAsync(AuthzWorld world, BsonDocument user) =>
        (await services.Store.Collection("project_member").Find(new BsonDocument
        {
            ["project"] = world.ProjectId, ["user"] = user["_id"]
        }).FirstAsync())["_id"].AsObjectId.ToString();

    private static async Task<string> UploadAsync(AuthzWorld world, string name)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 });
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", name);
        var response = await world.Client("owner").PostAsync(
            $"/v1/projects/{world.ProjectId}/files", form);
        var body = await AuthzAssert.OkAsync(response);
        return body.GetProperty("id").GetString()!;
    }
}
