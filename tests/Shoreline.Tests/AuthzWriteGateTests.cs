using System.Net;
using System.Net.Http.Json;

namespace Shoreline.Tests;

/// <summary>
/// Write-path authorization gates that must reject before mutating anything.
/// Bodies are the smallest shape that reaches the gate: the member-change
/// endpoint validates the `operations` array before the per-operation manager
/// check, and the lifecycle endpoints validate inside the service.
/// </summary>
[Collection("local-services")]
public sealed class AuthzWriteGateTests(LocalServices services)
{
    [Fact]
    public async Task ProjectMemberChanges_RequireManagerForMutations()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        var add = new
        {
            operations = new[]
            {
                new
                {
                    operation_id = "op-1",
                    action = "add",
                    user_id = world.Stranger["_id"].AsObjectId.ToString(),
                    tags = new[] { "translator" }
                }
            }
        };

        // A plain translator is not a project manager. The batch endpoint
        // reports per-operation failures in a wrapper instead of the flat
        // {code,message} error body, so assert on `failed.error_code`.
        await AssertDeniedAsync(
            await world.Client("translator").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/members/changes", add), 103);

        // Site admin has no membership-derived manager bit either.
        await AssertDeniedAsync(
            await world.Client("siteAdmin").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/members/changes", add), 103);

        // The creator is a manager; an empty changeset is a no-op success.
        await AuthzAssert.OkAsync(await world.Client("owner").PostAsJsonAsync(
            $"/v1/projects/{world.ProjectId}/members/changes",
            new { operations = Array.Empty<object>() }));
    }

    // The batch endpoint wraps per-operation failures (`failed.error_code`),
    // while a gate that fires before the loop returns the flat error body.
    private static async Task AssertDeniedAsync(HttpResponseMessage response, int expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;
        var code = root.TryGetProperty("failed", out var failed)
            ? failed.GetProperty("error_code").GetInt32()
            : root.GetProperty("code").GetInt32();
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public async Task ProjectOwnerTransfer_RequiresOwnerOrManager()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var target = world.Translator["_id"].AsObjectId.ToString();

        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/owner/transfer", new { user = target }),
            HttpStatusCode.BadRequest, 103);

        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/owner/transfer", new { user = target }),
            HttpStatusCode.BadRequest, 103);
    }

    [Fact]
    public async Task ProjectComplete_RequiresCompletePermission()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/complete", new { }),
            HttpStatusCode.BadRequest, 103);

        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/complete", new { }),
            HttpStatusCode.BadRequest, 103);
    }

    [Fact]
    public async Task TeamOutputs_RequireTeamManagement()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        // Team member only carries team:ACCESS.
        await AuthzAssert.ErrorAsync(
            await world.Client("teamMember").PostAsJsonAsync(
                $"/v1/teams/{world.TeamId}/outputs", new { projects = Array.Empty<string>() }),
            HttpStatusCode.BadRequest, 103);

        await AuthzAssert.ErrorAsync(
            await world.Client("stranger").PostAsJsonAsync(
                $"/v1/teams/{world.TeamId}/outputs", new { projects = Array.Empty<string>() }),
            HttpStatusCode.BadRequest, 103);
    }

    [Fact]
    public async Task BannedIdentity_WritesAreRejectedBeforeValidation()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        await AuthzAssert.ErrorAsync(
            await world.Client("banned").PostAsJsonAsync(
                $"/v1/projects/{world.ProjectId}/members/changes",
                new { operations = Array.Empty<object>() }),
            HttpStatusCode.BadRequest, 1006);
    }
}
