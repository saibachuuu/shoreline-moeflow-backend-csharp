using System.Net;

namespace Shoreline.Tests;

/// <summary>
/// Read-path authorization coverage on top of <see cref="AuthzMatrixTests"/>:
/// the three "manager-ish" surfaces that gate who may see invitations,
/// applications and target outputs. Expectations mirror docs/authz-matrix.md
/// (Python oracle checked first); the matrix maps "forbidden" to 400/103.
/// </summary>
[Collection("local-services")]
public sealed class AuthzReadGateTests(LocalServices services)
{
    [Fact]
    public async Task ProjectInvitations_RequireInviteUser()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        // Project translator has no INVITE_USER by default.
        await AuthzAssert.ErrorAsync(
            await world.Client("translator").GetAsync($"/v1/projects/{world.ProjectId}/invitations"),
            HttpStatusCode.BadRequest, 103);

        // A plain team member without a project row is a stranger here too.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").GetAsync($"/v1/projects/{world.ProjectId}/invitations"),
            HttpStatusCode.BadRequest, 103);

        // The project creator carries the management defaults.
        var ok = await AuthzAssert.OkAsync(
            await world.Client("owner").GetAsync($"/v1/projects/{world.ProjectId}/invitations"));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, ok.ValueKind);
    }

    [Fact]
    public async Task ProjectApplications_RequireCheckUser()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        await AuthzAssert.ErrorAsync(
            await world.Client("translator").GetAsync($"/v1/projects/{world.ProjectId}/applications"),
            HttpStatusCode.BadRequest, 103);

        var ok = await AuthzAssert.OkAsync(
            await world.Client("owner").GetAsync($"/v1/projects/{world.ProjectId}/applications"));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, ok.ValueKind);
    }

    [Fact]
    public async Task TargetOutputs_RequireOutputPermission()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        var path = $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs";

        // Team member without OUTPUT_TRA.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").GetAsync(path), HttpStatusCode.BadRequest, 103);

        // Site admin still has no membership-derived business permission.
        await AuthzAssert.ErrorAsync(
            await world.Client("siteAdmin").GetAsync(path), HttpStatusCode.BadRequest, 103);

        // Translator holds OUTPUT_TRA by default.
        await AuthzAssert.OkAsync(await world.Client("translator").GetAsync(path));
    }

    [Fact]
    public async Task BannedIdentity_IsRejectedBeforePermissionChecks()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        // The fixture gives the banned user a translator row; the ban must win.
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").GetAsync($"/v1/projects/{world.ProjectId}/invitations"),
            HttpStatusCode.BadRequest, 1006);
        await AuthzAssert.ErrorAsync(
            await world.Client("banned").GetAsync(
                $"/v1/projects/{world.ProjectId}/targets/{world.TargetId}/outputs"),
            HttpStatusCode.BadRequest, 1006);
    }
}
