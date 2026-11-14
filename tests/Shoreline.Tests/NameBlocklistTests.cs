using System.Text.Json;
using MongoDB.Bson;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Xunit;

namespace Shoreline.Tests;

public sealed class NameBlocklistTests
{
    private static BsonDocument Team(params string[] words) =>
        new() { [NameBlocklist.Field] = new BsonArray(words) };

    [Fact]
    public void Parse_drops_empties_and_case_insensitive_duplicates()
    {
        using var document = JsonDocument.Parse("""[" Spam ", "", "spam", "Bad"]""");

        var parsed = NameBlocklist.Parse(document.RootElement);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Spam", parsed[0].AsString);
        Assert.Equal("Bad", parsed[1].AsString);
    }

    [Fact]
    public void Parse_treats_null_as_an_empty_list()
    {
        using var document = JsonDocument.Parse("null");

        Assert.Empty(NameBlocklist.Parse(document.RootElement));
    }

    [Fact]
    public void Parse_rejects_a_non_array()
    {
        using var document = JsonDocument.Parse("123");

        var error = Assert.Throws<ApiError>(() => NameBlocklist.Parse(document.RootElement));

        Assert.Equal("ValidateError", error.Error);
        Assert.Equal(2, error.Code);
    }

    [Fact]
    public void FirstMatch_is_a_case_insensitive_substring_test()
    {
        var team = Team("school", "广告");

        Assert.Equal("school", NameBlocklist.FirstMatch(team, "Old SCHOOL Project"));
        Assert.Equal("广告", NameBlocklist.FirstMatch(team, "测试广告位"));
        Assert.Null(NameBlocklist.FirstMatch(team, "Old Town"));
    }

    [Fact]
    public void FirstMatch_is_null_when_the_team_has_no_list()
    {
        Assert.Null(NameBlocklist.FirstMatch(new BsonDocument(), "anything"));
        Assert.Null(NameBlocklist.FirstMatch(new BsonDocument { [NameBlocklist.Field] = BsonNull.Value }, "anything"));
    }

    [Fact]
    public void FirstMatch_ignores_non_string_entries()
    {
        var team = new BsonDocument { [NameBlocklist.Field] = new BsonArray { 42, "广告" } };

        Assert.Equal("广告", NameBlocklist.FirstMatch(team, "测试广告位"));
    }
}
