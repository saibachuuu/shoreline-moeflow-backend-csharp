using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Shoreline.Tests;

/// <summary>
/// Concurrency windows on the project-member envelope
/// (POST /v1/projects/{id}/members/changes, docs/authz-matrix.md §4).
/// Oracle: app/services/project_member.py (optimistic version CAS on update,
/// MemberAlreadyExistsError on duplicate add; unique indexes on
/// (project, user) / ik back the add path).
/// </summary>
[Collection("local-services")]
public sealed class AuthzConcurrencyTests(LocalServices services)
{
    [Fact]
    public async Task ConcurrentMemberUpdate_OneWinner_NoLostUpdateOrDuplicates()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var members = services.Store.Collection("project_member");
        var filter = new BsonDocument
        {
            ["project"] = world.ProjectId,
            ["user"] = world.Translator["_id"]
        };
        var before = await members.Find(filter).FirstAsync();
        var memberId = before["_id"].AsObjectId.ToString();
        var version = before.GetValue("version").ToInt32();
        var url = $"/v1/projects/{world.ProjectId}/members/changes";

        // Two owners race the same member with the same expected version but
        // different display names. Tags are deliberately untouched: assigning
        // worker tags to a user without team worker-qualifications is rejected
        // with 5102 on both stacks (identity_permission.py:599-617), which
        // would mask the version CAS under test. The version CAS lets exactly
        // one write through.
        var first = world.Client("owner").PostAsJsonAsync(url, new
        {
            operations = new[]
            {
                new
                {
                    operation_id = "race-update-a",
                    action = "update",
                    member_id = memberId,
                    expected_member_version = version,
                    changes = new { display_name = "race-winner-a" }
                }
            }
        });
        var second = world.Client("owner").PostAsJsonAsync(url, new
        {
            operations = new[]
            {
                new
                {
                    operation_id = "race-update-b",
                    action = "update",
                    member_id = memberId,
                    expected_member_version = version,
                    changes = new { display_name = "race-winner-b" }
                }
            }
        });
        var responses = await Task.WhenAll(first, second);

        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
        var loser = responses.First(r => !r.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(5108, await FailedCodeAsync(loser));

        // No duplicate rows, no lost update: the stored display name is exactly
        // one side's, and the version advanced by exactly one.
        var rows = await members.Find(filter).ToListAsync();
        var single = Assert.Single(rows);
        Assert.Equal(version + 1, single.GetValue("version").ToInt32());
        var displayName = single.GetValue("display_name").AsString;
        Assert.True(
            displayName == "race-winner-a" || displayName == "race-winner-b",
            "stored display_name is a blend of both racers: " + displayName);
    }

    [Fact]
    public async Task ConcurrentAddSameStranger_OnlyOneWins_LoserGetsBusinessError()
    {
        await using var world = await AuthzWorld.CreateAsync(services);
        var members = services.Store.Collection("project_member");
        var strangerId = world.Stranger["_id"].AsObjectId.ToString();
        var url = $"/v1/projects/{world.ProjectId}/members/changes";

        // Two owner requests add the same stranger under distinct operation
        // ids (sharing one id would replay the first claim as success).
        // The (project, user) unique index plus the re-read means the loser
        // must fail with MemberAlreadyExistsError (409/5106), never 500.
        var first = world.Client("owner").PostAsJsonAsync(url, new
        {
            operations = new[]
            {
                new
                {
                    operation_id = "race-add-1",
                    action = "add",
                    user_id = strangerId,
                    tags = Array.Empty<string>()
                }
            }
        });
        var second = world.Client("owner").PostAsJsonAsync(url, new
        {
            operations = new[]
            {
                new
                {
                    operation_id = "race-add-2",
                    action = "add",
                    user_id = strangerId,
                    tags = Array.Empty<string>()
                }
            }
        });
        var responses = await Task.WhenAll(first, second);

        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
        var loser = responses.First(r => !r.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(5106, await FailedCodeAsync(loser));

        var rows = await members.Find(new BsonDocument
        {
            ["project"] = world.ProjectId,
            ["user"] = world.Stranger["_id"]
        }).ToListAsync();
        var single = Assert.Single(rows);
        Assert.Equal("active", single.GetValue("status").AsString);
    }

    // The batch endpoint wraps per-operation failures in `failed.error_code`
    // instead of the flat {code} body (see AuthzWriteGateTests).
    private static async Task<int> FailedCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("failed").GetProperty("error_code").GetInt32();
    }
}
