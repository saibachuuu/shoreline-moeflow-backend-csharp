using System.Net;
using System.Net.Http.Json;

namespace Shoreline.Tests;

/// <summary>
/// Opening a project resolves the caller's role from the persisted member tags.
/// `typesetter` is stored on the member document but the project role it maps to
/// is `picture_editor`; looking the raw tag up returned RoleNotExistError (102)
/// instead, so a typesetter could not open the project page at all.
/// </summary>
[Collection("local-services")]
public sealed class ProjectRoleViewTests(LocalServices services)
{
    [Fact]
    public async Task Typesetter_member_opens_the_project_with_the_picture_editor_role()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        var body = await AuthzAssert.OkAsync(
            await world.Client("typesetter").GetAsync($"/v1/projects/{world.ProjectId}"));

        var role = body.GetProperty("role");
        Assert.Equal("picture_editor", role.GetProperty("system_code").GetString());
    }

    [Fact]
    public async Task Translator_member_still_resolves_their_own_role()
    {
        await using var world = await AuthzWorld.CreateAsync(services);

        var body = await AuthzAssert.OkAsync(
            await world.Client("translator").GetAsync($"/v1/projects/{world.ProjectId}"));

        Assert.Equal("translator",
            body.GetProperty("role").GetProperty("system_code").GetString());
    }
}
