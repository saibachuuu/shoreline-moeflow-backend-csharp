using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class NotificationGoldenTests
{
    private readonly LocalServices services;

    public NotificationGoldenTests(LocalServices services) => this.services = services;

    [Fact]
    public void ParseBody_MatchesPythonReferenceNodes()
    {
        var fixture = NotificationFixture();
        foreach (var item in fixture["valid"]!.AsArray())
        {
            var input = item!["input"]!.GetValue<string>();
            var expected = item["nodes"];
            var actual = JsonNode.Parse(NotificationContent.ParseBody(input).ToJson());
            Assert.True(JsonNode.DeepEquals(actual, expected),
                $"valid parse mismatch for {input}: {actual?.ToJsonString()} != {expected?.ToJsonString()}");
        }
    }

    [Fact]
    public void ParseBody_MatchesPythonReferenceErrorsAndStatuses()
    {
        var fixture = NotificationFixture();
        foreach (var item in fixture["invalid"]!.AsArray())
        {
            var input = item!["input"]!.GetValue<string>();
            var expected = item["error"]!;
            var error = Assert.Throws<ApiError>(() => NotificationContent.ParseBody(input));
            Assert.Equal(expected["type"]!.GetValue<string>(), error.Error);
            Assert.Equal(expected["status_code"]!.GetValue<int>(), error.Status);
            Assert.Equal(expected["message"]!.GetValue<string>(), error.ResponseMessage.ToString());
        }
    }

    [Fact]
    public void SafeUrl_MatchesPythonReference()
    {
        var fixture = NotificationFixture();
        foreach (var item in fixture["safe_url"]!.AsArray())
        {
            var input = item!["input"]!.GetValue<string>();
            var expected = item["expected"]!.GetValue<bool>();
            Assert.Equal(expected, NotificationContent.SafeUrl(input));
        }
    }

    [Fact]
    public void NormalizeAndSearchTokens_MatchPythonReference()
    {
        var fixture = NotificationFixture()["normalize"]!;
        var input = fixture["input"]!.GetValue<string>();
        Assert.Equal(fixture["expected"]!.GetValue<string>(), NotificationContent.Normalize(input));
        var expectedTokens = fixture["tokens"]!.AsArray()
            .Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal(expectedTokens, NotificationContent.SearchTokens(input));
    }

    [Fact]
    public async Task ProjectAccess_MatchesPythonReferenceCases()
    {
        var fixture = NotificationFixture()["project_access"]!.AsArray();
        var team = ObjectId.GenerateNewId();
        var allowedProject = ObjectId.GenerateNewId();
        var deniedProject = ObjectId.GenerateNewId();
        var bannedProject = ObjectId.GenerateNewId();
        var missingProject = ObjectId.GenerateNewId();
        var user = await InsertUserAsync(banned: false);
        var banned = await InsertUserAsync(banned: true);
        await services.Store.Collection("team").InsertOneAsync(new BsonDocument
        {
            ["_id"] = team,
            ["n"] = "golden-team"
        });
        await InsertProjectAsync(allowedProject, team, "allowed-secret-name");
        await InsertProjectAsync(deniedProject, team, "denied-secret-name");
        await InsertProjectAsync(bannedProject, team, "banned-secret-name");
        await InsertProjectMemberAsync(allowedProject, user);
        await InsertProjectMemberAsync(deniedProject, user, status: "removed");
        await InsertProjectMemberAsync(bannedProject, banned);

        var content = new NotificationContent(services.Store,
            new IdentityPermissions(services.Store, TimeProvider.System), services.Configuration);

        foreach (var item in fixture)
        {
            var name = item!["case"]!.GetValue<string>();
            var expected = item["expected"]!.GetValue<bool>();
            var project = name switch
            {
                "allowed" => allowedProject,
                "denied" => deniedProject,
                "banned" => bannedProject,
                "missing-project" => missingProject,
                "does-not-exist" => ObjectId.GenerateNewId(),
                _ => (ObjectId?)null
            };
            var viewer = name switch
            {
                "banned" => banned,
                "missing-user" => null,
                _ => user
            };
            var nodes = new BsonArray
            {
                new BsonDocument
                {
                    ["type"] = "project",
                    ["project_id"] = project?.ToString() ?? missingProject.ToString()
                }
            };
            var visible = await content.VisibleNodesAsync(nodes, viewer, CancellationToken.None);
            var node = visible[0].AsBsonDocument;
            if (expected)
            {
                Assert.Equal("project", node.Text("type"));
                Assert.Equal(project.ToString(), node.Text("project_id"));
                Assert.Equal(name == "allowed" ? "allowed-secret-name" : "", node.Text("name"));
            }
            else
            {
                Assert.Equal("unavailable", node.Text("type"));
                Assert.False(node.Contains("project_id"));
                Assert.False(node.Contains("name"));
                Assert.DoesNotContain("secret-name", node.ToJson());
            }
        }
    }

    private static JsonNode NotificationFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "tools", "legacy-reference", "fixtures", "goldens.json");
            if (File.Exists(candidate))
                return JsonNode.Parse(File.ReadAllText(candidate))!["fixtures"]!["notification"]!;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("goldens.json not found above " + AppContext.BaseDirectory);
    }

    private async Task<BsonDocument> InsertUserAsync(bool banned)
    {
        var id = ObjectId.GenerateNewId();
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "golden-user-" + id,
            ["e"] = id + "@example.test",
            ["admin"] = false,
            ["b"] = banned,
            ["p"] = "golden-password-hash"
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task InsertProjectAsync(ObjectId id, ObjectId team, string name) =>
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = name,
            ["i"] = "",
            ["t"] = team,
            ["ps"] = new BsonArray(),
            ["st"] = 0,
            ["ou"] = ObjectId.Empty
        });

    private async Task InsertProjectMemberAsync(ObjectId project, BsonDocument user, string status = "active") =>
        await services.Store.Collection("project_member").InsertOneAsync(new BsonDocument
        {
            ["project"] = project,
            ["user"] = user["_id"],
            ["tags"] = new BsonArray(),
            ["status"] = status
        });
}
