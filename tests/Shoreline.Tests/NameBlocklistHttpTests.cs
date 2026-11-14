using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;

namespace Shoreline.Tests;

/// <summary>
/// A team creator may forbid words in project names. The list lives on the team
/// document (field `nw`) and is edited through the ordinary team update
/// endpoint; every project create checks it before writing a row.
/// </summary>
[Collection("local-services")]
public sealed class NameBlocklistHttpTests(LocalServices services)
{
    private static object Project(string name) => new
    {
        name,
        allow_apply_type = 1,
        application_check_type = 1
    };

    [Fact]
    public async Task Owner_sets_the_list_and_a_matching_create_is_rejected()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var teamPath = $"/v1/teams/{world.TeamId}";

        var updated = await AuthzAssert.OkAsync(
            await world.Client("owner").PutAsJsonAsync(teamPath,
                new { project_name_blocklist = new[] { "school", "广告" } }));
        Assert.Equal(2, updated.GetProperty("team").GetProperty("project_name_blocklist").GetArrayLength());

        var read = await AuthzAssert.OkAsync(await world.Client("owner").GetAsync(teamPath));
        Assert.Equal(2, read.GetProperty("project_name_blocklist").GetArrayLength());

        // Matching is case-insensitive and substring based.
        var blockedBySchool = await AuthzAssert.ErrorAsync(
            await world.Client("owner").PostAsJsonAsync($"{teamPath}/projects",
                Project("Old SCHOOL Project")),
            HttpStatusCode.BadRequest, 2);
        Assert.Equal("项目名称包含禁止使用的内容",
            blockedBySchool.GetProperty("message").GetString());
        Assert.DoesNotContain("school",
            blockedBySchool.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        await AuthzAssert.ErrorAsync(
            await world.Client("owner").PostAsJsonAsync($"{teamPath}/projects",
                Project("测试广告位")),
            HttpStatusCode.BadRequest, 2);

        var created = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync($"{teamPath}/projects",
                Project("普通项目")));
        Assert.Equal("普通项目", created.GetProperty("project").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Only_the_team_creator_may_change_the_list()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var teamPath = $"/v1/teams/{world.TeamId}";

        await AuthzAssert.ErrorAsync(
            await world.Client("teamAdmin").PutAsJsonAsync(teamPath,
                new { project_name_blocklist = new[] { "spam" } }),
            HttpStatusCode.BadRequest, 103);

        await AuthzAssert.ErrorAsync(
            await world.Client("translator").PutAsJsonAsync(teamPath,
                new { project_name_blocklist = new[] { "spam" } }),
            HttpStatusCode.BadRequest, 103);
    }

    [Fact]
    public async Task An_empty_list_blocks_nothing()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var teamPath = $"/v1/teams/{world.TeamId}";

        await AuthzAssert.OkAsync(
            await world.Client("owner").PutAsJsonAsync(teamPath,
                new { project_name_blocklist = new[] { "school" } }));
        await AuthzAssert.OkAsync(
            await world.Client("owner").PutAsJsonAsync(teamPath,
                new { project_name_blocklist = Array.Empty<string>() }));

        var created = await AuthzAssert.OkAsync(
            await world.Client("owner").PostAsJsonAsync($"{teamPath}/projects",
                Project("Old School Project")));
        Assert.Equal("Old School Project", created.GetProperty("project").GetProperty("name").GetString());
    }
}
