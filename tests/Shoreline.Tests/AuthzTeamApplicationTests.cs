using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Tests;

/// <summary>
/// Team application flow beyond the list gate: an open team
/// (<c>allow_apply_type = 2</c>) accepts a stranger's application, the
/// approve/delete gates stay intact, and a closed team keeps rejecting.
/// Oracle: app/services/application.py (+ app/services/user.py:701 for the
/// "needs review" copy) and Flask app/apis/application.py:68-69, 139-142, 180-181.
/// </summary>
[Collection("local-services")]
public sealed class AuthzTeamApplicationTests(LocalServices services)
{
    [Fact]
    public async Task OpenTeam_AcceptsApplication_AndKeepsApproveGates()
    {
        await using var world = await AuthzWorld.CreateAsync(services, teamAllowApplyType: 2);
        var path = $"/v1/teams/{world.TeamId}/applications";

        var applied = await AuthzAssert.OkAsync(
            await world.Client("stranger").PostAsJsonAsync(path, new { message = "求加入团队" }));
        Assert.Equal("申请成功，请等待管理员审核", applied.GetProperty("message").GetString());

        var applicationId = (await ApplicationAsync(world.TeamId, world.Stranger))["_id"]
            .AsObjectId.ToString();
        var applicationPath = $"/v1/applications/{applicationId}";

        // Approving needs CHECK_USER: a plain project translator is denied.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PatchAsJsonAsync(applicationPath, new { allow = true }),
            HttpStatusCode.BadRequest, 103);

        // The team owner approves.
        var approved = await AuthzAssert.OkAsync(
            await world.Client("owner").PatchAsJsonAsync(applicationPath, new { allow = true }));
        Assert.Equal("已接受", approved.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OpenTeam_OnlyApplicantMayDelete()
    {
        await using var world = await AuthzWorld.CreateAsync(services, teamAllowApplyType: 2);
        var path = $"/v1/teams/{world.TeamId}/applications";

        await AuthzAssert.OkAsync(
            await world.Client("stranger").PostAsJsonAsync(path, new { message = "申请后撤销" }));
        var applicationId = (await ApplicationAsync(world.TeamId, world.Stranger))["_id"]
            .AsObjectId.ToString();
        var applicationPath = $"/v1/applications/{applicationId}";

        // Delete is applicant-only; a team member cannot clean it up.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").DeleteAsync(applicationPath),
            HttpStatusCode.BadRequest, 103);

        var deleted = await AuthzAssert.OkAsync(
            await world.Client("stranger").DeleteAsync(applicationPath));
        Assert.Equal("删除成功", deleted.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ClosedTeam_RejectsApplication()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var path = $"/v1/teams/{world.TeamId}/applications";

        // allow_apply_type = 1 (invite/apply disabled) keeps the stranger out;
        // Python raises GroupNotOpenError before any application record exists.
        var denied = await world.Client("stranger").PostAsJsonAsync(path, new { message = "硬申请" });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
    }

    // Applications persist the group as a mongoengine GenericReferenceField:
    // {"_cls": "Team", "_ref": {"$ref": "team", "$id": <ObjectId>}}.
    private async Task<BsonDocument> ApplicationAsync(ObjectId groupId, BsonDocument user) =>
        await services.Store.Collection("application").Find(new BsonDocument
        {
            ["g._ref.$id"] = groupId, ["u"] = user["_id"]
        }).FirstAsync();
}
