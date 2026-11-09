using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

// Direct-service compatibility facts for app/apis/term.py and app/models/term.py.
// The F1 routes are deliberately not registered by Program.cs in this slice, so
// these tests do not depend on HTTP route wiring.
[Collection("local-services")]
public sealed class TermTests(LocalServices services)
{
    private TermService Terms() => new(services.Store,
        services.App.Services.GetRequiredService<TimeProvider>(),
        services.App.Services.GetRequiredService<IdentityPermissions>(),
        services.App.Services.GetRequiredService<TeamMemberService>(),
        services.App.Services.GetRequiredService<UserView>());

    [Fact]
    public async Task CreateBankPersistsLegacyBsonAndReturnsLegacyView()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();

        var bank = await terms.CreateBankAsync(owner, team["_id"].AsObjectId.ToString()!, Body(new
        {
            name = "术语库",
            tip = "提示",
            source_language_id = source["_id"].AsObjectId.ToString()!,
            target_language_id = target["_id"].AsObjectId.ToString()!
        }));

        Assert.Equal(team["_id"], bank["t"]);
        Assert.Equal("术语库", bank.Text("n"));
        Assert.Equal(source["_id"], bank["sl"]);
        Assert.Equal(target["_id"], bank["tl"]);
        Assert.Equal("提示", bank.Text("ti"));
        Assert.Equal(owner["_id"], bank["u"]);
        Assert.Equal(DateTimeKind.Utc, bank["c"].AsBsonDateTime.ToUniversalTime().Kind);
        Assert.True(bank["e"].AsBsonDateTime.ToUniversalTime() >= bank["c"].AsBsonDateTime.ToUniversalTime());

        var view = Assert.IsType<Dictionary<string, object?>>(await terms.BankViewAsync(bank, null));
        Assert.Equal(bank["_id"].AsObjectId.ToString()!, view["id"]);
        Assert.Equal("术语库", view["name"]);
        Assert.Equal("提示", view["tip"]);
        var sourceView = Assert.IsType<Dictionary<string, object?>>(view["source_language"]);
        Assert.Equal(source.Text("co"), sourceView["code"]);
        Assert.Equal(source.Text("e"), sourceView["en_name"]);
        var userView = Assert.IsType<Dictionary<string, object?>>(view["user"]);
        Assert.Equal(owner.Text("n"), userView["name"]);
    }

    [Fact]
    public async Task BankPutRequiresEveryFieldAndUsesRuneLengthsAndSameLanguageGuard()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var bank = await BankAsync(team, owner, source, target);
        var bankId = bank["_id"].AsObjectId.ToString()!;

        Assert.Equal(2, (await ErrorAsync(() => terms.EditBankAsync(owner, bankId, Body(new
        {
            name = "only-name",
            source_language_id = source["_id"].AsObjectId.ToString()!,
            target_language_id = target["_id"].AsObjectId.ToString()!
        })))).Code);
        Assert.Equal(2, (await ErrorAsync(() => terms.EditBankAsync(owner, bankId, Body(new
        {
            name = 1,
            tip = "",
            source_language_id = source["_id"].AsObjectId.ToString()!,
            target_language_id = target["_id"].AsObjectId.ToString()!
        })))).Code);
        Assert.Equal(6002, (await ErrorAsync(() => terms.EditBankAsync(owner, bankId, Body(new
        {
            name = "same",
            tip = "",
            source_language_id = source["_id"].AsObjectId.ToString()!,
            target_language_id = source["_id"].AsObjectId.ToString()!
        })))).Code);
        Assert.Equal(101, (await ErrorAsync(() => terms.EditBankAsync(owner, bankId, Body(new
        {
            name = "invalid-language",
            tip = "",
            source_language_id = "not-an-object-id",
            target_language_id = target["_id"].AsObjectId.ToString()!
        })))).Code);
        Assert.Equal(6001, (await ErrorAsync(() => terms.EditBankAsync(owner, bankId, Body(new
        {
            name = "missing-language",
            tip = "",
            source_language_id = ObjectId.GenerateNewId().ToString(),
            target_language_id = target["_id"].AsObjectId.ToString()!
        })))).Code);

        // Python len() counts code points, so 40 emoji are accepted although
        // their UTF-16 length is 80.
        var forty = string.Concat(Enumerable.Repeat("😀", 40));
        var edited = await terms.EditBankAsync(owner, bankId, Body(new
        {
            name = forty,
            tip = "new-tip",
            source_language_id = source["_id"].AsObjectId.ToString()!,
            target_language_id = target["_id"].AsObjectId.ToString()!
        }));
        Assert.Equal(forty, edited.Text("n"));
        Assert.Equal("new-tip", edited.Text("ti"));
        Assert.Equal(2, (await ErrorAsync(() => terms.EditBankAsync(owner, bankId, Body(new
        {
            name = forty + "😀",
            tip = "",
            source_language_id = source["_id"].AsObjectId.ToString()!,
            target_language_id = target["_id"].AsObjectId.ToString()!
        })))).Code);

        var stored = await FindBankAsync(bank["_id"].AsObjectId);
        Assert.Equal(forty, stored.Text("n"));
        Assert.Equal("new-tip", stored.Text("ti"));
    }

    [Fact]
    public async Task TeamAccessAndOwnerBranchesRemainDistinctAndSiteAdminDoesNotBypassMembership()
    {
        var creator = await UserAsync();
        var member = await UserAsync();
        var admin = await UserAsync(admin: true);
        var team = await TeamAsync();
        await MemberAsync(team, creator, "creator");
        await MemberAsync(team, member, "member");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var teamId = team["_id"].AsObjectId.ToString()!;

        var created = await terms.CreateBankAsync(creator, teamId, BankBody(source, target));
        var bankId = created["_id"].AsObjectId.ToString()!;

        // Plain member has team:ACCESS, so the team list is visible.
        var (memberRows, _) = await terms.ListBanksAsync(member, teamId, null, 0, 30);
        Assert.Single(memberRows);
        // But CREATE_TERM_BANK is not implied by ACCESS.
        Assert.Equal(103, (await ErrorAsync(() => terms.CreateBankAsync(member, teamId,
            BankBody(source, target)))).Code);
        Assert.Equal(103, (await ErrorAsync(() => terms.ListBanksAsync(admin, teamId, null, 0, 30))).Code);

        // Owner branch: bank owned by member can be edited/deleted without
        // CHANGE_TERM_BANK/DELETE_TERM_BANK.
        var ownedByMember = await BankAsync(team, member, source, target);
        await terms.EditBankAsync(member, ownedByMember["_id"].AsObjectId.ToString()!, BankBody(source, target, "owned"));
        await terms.ListTermsAsync(member, ownedByMember["_id"].AsObjectId.ToString()!, null, 0, 30);

        // Owner branch: term owner can edit/delete without CHANGE_TERM/DELETE_TERM,
        // even when the bank belongs to somebody else.
        var foreignBank = await BankAsync(team, creator, source, target);
        var term = await TermAsync(foreignBank, member, "source", "target", "");
        await terms.EditTermAsync(member, term["_id"].AsObjectId.ToString()!, TermBody("source2", "target", ""));
        await terms.DeleteTermAsync(member, term["_id"].AsObjectId.ToString()!);

        var outsider = await UserAsync();
        Assert.Equal(103, (await ErrorAsync(() => terms.ListBanksAsync(outsider, teamId, null, 0, 30))).Code);
        Assert.Equal(103, (await ErrorAsync(() => terms.EditBankAsync(outsider, bankId,
            BankBody(source, target)))).Code);
        Assert.Equal(103, (await ErrorAsync(() => terms.DeleteBankAsync(outsider, bankId))).Code);
    }

    [Fact]
    public async Task CrossTeamActorCannotReadOrCreateInAnotherTeamsBank()
    {
        var actor = await UserAsync();
        var otherOwner = await UserAsync();
        var teamA = await TeamAsync();
        var teamB = await TeamAsync();
        await MemberAsync(teamA, actor, "creator");
        await MemberAsync(teamB, otherOwner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var bank = await BankAsync(teamB, otherOwner, source, target);

        Assert.Equal(103, (await ErrorAsync(() => terms.CreateTermAsync(actor, bank["_id"].AsObjectId.ToString()!,
            TermBody("a", "b", "")))).Code);
        Assert.Equal(103, (await ErrorAsync(() => terms.ListTermsAsync(actor, bank["_id"].AsObjectId.ToString()!,
            null, 0, 30))).Code);
        Assert.Equal(103, (await ErrorAsync(() => terms.EditBankAsync(actor, bank["_id"].AsObjectId.ToString()!,
            BankBody(source, target)))).Code);
    }

    [Fact]
    public async Task ListBanksIsTeamScopedAndSearchTotalCountsOnlyThatTeam()
    {
        var actor = await UserAsync();
        var otherOwner = await UserAsync();
        var teamA = await TeamAsync();
        var teamB = await TeamAsync();
        await MemberAsync(teamA, actor, "creator");
        await MemberAsync(teamB, otherOwner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();

        var localAlpha = await terms.CreateBankAsync(actor, teamA["_id"].AsObjectId.ToString()!,
            BankBody(source, target, "Alpha Local"));
        var localBeta = await BankAsync(teamA, actor, source, target, "Beta Local");
        var foreignAlpha = await BankAsync(teamB, otherOwner, source, target, "Alpha Foreign");

        var (all, total) = await terms.ListBanksAsync(actor, teamA["_id"].AsObjectId.ToString()!, null, 0, 30);
        Assert.Equal(2L, total);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, row => row["_id"] == localAlpha["_id"]);
        Assert.Contains(all, row => row["_id"] == localBeta["_id"]);
        Assert.DoesNotContain(all, row => row["_id"] == foreignAlpha["_id"]);

        // A search matching both teams' banks must still count only team A.
        // Without the mandatory t filter this would return 2 / count 2.
        var (searched, searchedTotal) = await terms.ListBanksAsync(actor, teamA["_id"].AsObjectId.ToString()!,
            "Alpha", 0, 30);
        Assert.Equal(1L, searchedTotal);
        Assert.Equal(localAlpha["_id"], Assert.Single(searched)["_id"]);
        Assert.DoesNotContain(searched, row => row["_id"] == foreignAlpha["_id"]);

        var (foreignRows, foreignTotal) = await terms.ListBanksAsync(otherOwner, teamB["_id"].AsObjectId.ToString()!,
            null, 0, 30);
        Assert.Equal(1L, foreignTotal);
        Assert.Equal(foreignAlpha["_id"], Assert.Single(foreignRows)["_id"]);
    }

    [Fact]
    public async Task PaginationSearchIsEscapedAndBulkViewsLoadUsersAndLanguages()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        await BankAsync(team, owner, source, target, "alpha");
        await BankAsync(team, owner, source, target, "a.b");
        await BankAsync(team, owner, source, target, "axb");
        var teamId = team["_id"].AsObjectId.ToString()!;

        var (literal, literalCount) = await terms.ListBanksAsync(owner, teamId, "a.b", 0, 30);
        Assert.Equal(1L, literalCount);
        Assert.Equal("a.b", Assert.Single(literal).Text("n"));

        var (all, count) = await terms.ListBanksAsync(owner, teamId, null, -3, 500);
        Assert.Equal(3L, count);
        Assert.Equal(3, all.Count);
        var page = TermEndpoints.ParsePage(Context("?page=2&limit=250"));
        Assert.Equal(2, page.Page);
        Assert.Equal(100, page.Limit);
        Assert.Equal(100, page.Skip);
        var defaults = TermEndpoints.ParsePage(Context(""));
        Assert.Equal(1, defaults.Page);
        Assert.Equal(30, defaults.Limit);
        Assert.Equal(0, defaults.Skip);

        var views = await terms.BanksViewAsync(all.ToList(), null);
        Assert.Equal(3, views.Length);
        Assert.All(views, row =>
        {
            var view = Assert.IsType<Dictionary<string, object?>>(row);
            Assert.NotNull(view["source_language"]);
            Assert.NotNull(view["target_language"]);
            Assert.NotNull(view["user"]);
        });
    }

    [Fact]
    public async Task TermSearchUsesEscapedRegexAcrossSourceTargetAndTip()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var bank = await BankAsync(team, owner, source, target);
        await TermAsync(bank, owner, "hello.world", "x", "");
        await TermAsync(bank, owner, "helloXworld", "x", "");
        await TermAsync(bank, owner, "x", "终.点", "");
        await TermAsync(bank, owner, "x", "y", "tip.value");

        var (rows, count) = await terms.ListTermsAsync(owner, bank["_id"].AsObjectId.ToString()!, "hello.world", 0, 30);
        Assert.Equal(1L, count);
        Assert.Equal("hello.world", Assert.Single(rows).Text("o"));
        var (tipRows, tipCount) = await terms.ListTermsAsync(owner, bank["_id"].AsObjectId.ToString()!, "tip.value", 0, 30);
        Assert.Equal(1L, tipCount);
        Assert.Equal("tip.value", Assert.Single(tipRows).Text("ti"));
    }

    [Fact]
    public async Task TermCreateAndSourceOnlyEditMarkProjectsForRefresh()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var bank = await BankAsync(team, owner, source, target);
        var project = await ProjectAsync(team, bank);
        var unrelated = await ProjectAsync(team, null);

        var term = await terms.CreateTermAsync(owner, bank["_id"].AsObjectId.ToString()!, TermBody("原文", "译文", ""));
        Assert.True((await FindProjectAsync(project)).Flag("nft"));
        Assert.False((await FindProjectAsync(unrelated)).Flag("nft"));

        await SetNftAsync(project, false);
        await terms.EditTermAsync(owner, term["_id"].AsObjectId.ToString()!, TermBody("原文", "新译文", ""));
        Assert.False((await FindProjectAsync(project)).Flag("nft"));

        await SetNftAsync(project, false);
        await terms.EditTermAsync(owner, term["_id"].AsObjectId.ToString()!, TermBody("原文", "新译文", "新提示"));
        Assert.False((await FindProjectAsync(project)).Flag("nft"));

        await SetNftAsync(project, false);
        await terms.EditTermAsync(owner, term["_id"].AsObjectId.ToString()!, TermBody("新原文", "新译文", "新提示"));
        Assert.True((await FindProjectAsync(project)).Flag("nft"));

        await SetNftAsync(project, false);
        await terms.EditBankAsync(owner, bank["_id"].AsObjectId.ToString()!, BankBody(source, target, "renamed"));
        Assert.False((await FindProjectAsync(project)).Flag("nft"));
    }

    [Fact]
    public async Task DeleteBankCascadesTermsProjectReferencesAndSourcePossibleTerms()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var bank = await BankAsync(team, owner, source, target);
        var first = await TermAsync(bank, owner, "一", "1", "");
        var second = await TermAsync(bank, owner, "二", "2", "");
        var firstSource = await SourceAsync(first);
        var secondSource = await SourceAsync(second);
        var project = await ProjectAsync(team, bank);

        // More than one 200-document cursor batch: deletion must not build a
        // single unbounded term-id $in command.
        await services.Store.Collection("term").InsertManyAsync(Enumerable.Range(0, 205).Select(i =>
            new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["tb"] = bank["_id"],
                ["o"] = "bulk-" + i,
                ["t"] = "bulk-" + i,
                ["ti"] = "",
                ["u"] = owner["_id"],
                ["c"] = DateTime.UtcNow,
                ["e"] = DateTime.UtcNow
            }));

        await terms.DeleteBankAsync(owner, bank["_id"].AsObjectId.ToString()!);

        Assert.Empty(await services.Store.Collection("term")
            .Find(new BsonDocument("tb", bank["_id"])).ToListAsync());
        Assert.Empty(await services.Store.Collection("term_bank")
            .Find(new BsonDocument("_id", bank["_id"])).ToListAsync());
        Assert.Empty((await FindProjectAsync(project)).GetValue("tb", new BsonArray()).AsBsonArray);
        Assert.Empty((await FindSourceAsync(firstSource)).GetValue("pt", new BsonArray()).AsBsonArray);
        Assert.Empty((await FindSourceAsync(secondSource)).GetValue("pt", new BsonArray()).AsBsonArray);
    }

    [Fact]
    public async Task DeleteTermPullsOnlyItsSourcePossibleTermsReference()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var bank = await BankAsync(team, owner, source, target);
        var removed = await TermAsync(bank, owner, "删", "x", "");
        var kept = await TermAsync(bank, owner, "留", "y", "");
        var sourceDoc = await SourceAsync(removed);
        await services.Store.Collection("source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["pt"] = new BsonArray { kept["_id"] }
        });

        await terms.DeleteTermAsync(owner, removed["_id"].AsObjectId.ToString()!);

        var pulled = await FindSourceAsync(sourceDoc);
        Assert.Empty(pulled.GetValue("pt", new BsonArray()).AsBsonArray);
        Assert.Empty(await services.Store.Collection("term")
            .Find(new BsonDocument("_id", removed["_id"])).ToListAsync());
        Assert.Single(await services.Store.Collection("term")
            .Find(new BsonDocument("_id", kept["_id"])).ToListAsync());
    }

    [Fact]
    public async Task SystemBankWithoutTeamUsesOwnerBranchAndNeverObjectIdEmpty()
    {
        var owner = await UserAsync();
        var outsider = await UserAsync();
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();
        var bank = await BankAsync(null, owner, source, target, "system");

        Assert.False(bank.Contains("t"));
        var created = await terms.CreateTermAsync(owner, bank["_id"].AsObjectId.ToString()!, TermBody("sys", "系统", ""));
        Assert.Equal(bank["_id"], created["tb"]);
        var (rows, count) = await terms.ListTermsAsync(owner, bank["_id"].AsObjectId.ToString()!, null, 0, 30);
        Assert.Equal(1L, count);
        Assert.Single(rows);
        await terms.EditBankAsync(owner, bank["_id"].AsObjectId.ToString()!, BankBody(source, target, "system2"));
        Assert.NotNull(await terms.BankViewAsync(bank, null));
        Assert.Equal(103, (await ErrorAsync(() => terms.CreateTermAsync(outsider, bank["_id"].AsObjectId.ToString()!,
            TermBody("no", "no", "")))).Code);
        Assert.Equal(103, (await ErrorAsync(() => terms.ListTermsAsync(outsider, bank["_id"].AsObjectId.ToString()!,
            null, 0, 30))).Code);
        await terms.DeleteBankAsync(owner, bank["_id"].AsObjectId.ToString()!);
        Assert.Empty(await services.Store.Collection("term_bank")
            .Find(new BsonDocument("_id", bank["_id"])).ToListAsync());
    }

    [Fact]
    public async Task MissingIdsUse7001And7002AndViewsTolerateDanglingReferences()
    {
        var owner = await UserAsync();
        var team = await TeamAsync();
        await MemberAsync(team, owner, "creator");
        var source = await LanguageAsync("ja", "日语");
        var target = await LanguageAsync("zh-CN", "简体中文");
        var terms = Terms();

        Assert.Equal(7001, (await ErrorAsync(() => terms.FindBankAsync("not-an-id"))).Code);
        Assert.Equal(7001, (await ErrorAsync(() => terms.FindBankAsync(ObjectId.GenerateNewId().ToString()))).Code);
        Assert.Equal(7002, (await ErrorAsync(() => terms.FindTermAsync("not-an-id"))).Code);
        Assert.Equal(7002, (await ErrorAsync(() => terms.FindTermAsync(ObjectId.GenerateNewId().ToString()))).Code);

        var dangling = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = team["_id"],
            ["n"] = "dangling",
            ["sl"] = ObjectId.GenerateNewId(),
            ["tl"] = ObjectId.GenerateNewId(),
            ["ti"] = "",
            ["u"] = ObjectId.GenerateNewId(),
            ["c"] = DateTime.UtcNow,
            ["e"] = DateTime.UtcNow
        };
        var view = Assert.IsType<Dictionary<string, object?>>(await terms.BankViewAsync(dangling, null));
        Assert.Null(view["source_language"]);
        Assert.Null(view["target_language"]);
        Assert.Null(view["user"]);

        Assert.Equal(7001, (await ErrorAsync(() => terms.CreateTermAsync(owner, "not-an-id",
            TermBody("a", "b", "")))).Code);
        Assert.Equal(7001, (await ErrorAsync(() => terms.CreateTermAsync(owner,
            ObjectId.GenerateNewId().ToString(), TermBody("a", "b", "")))).Code);
        Assert.Equal(2, (await ErrorAsync(() => terms.CreateBankAsync(owner, team["_id"].AsObjectId.ToString()!,
            Body(new { name = 1, tip = "", source_language_id = source["_id"].AsObjectId.ToString()!,
                target_language_id = target["_id"].AsObjectId.ToString()! })))).Code);
    }

    // ---- helpers ---------------------------------------------------------

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement BankBody(BsonDocument source, BsonDocument target, string name = "bank") =>
        JsonSerializer.SerializeToElement(new
        {
            name,
            tip = "",
            source_language_id = source["_id"].AsObjectId.ToString(),
            target_language_id = target["_id"].AsObjectId.ToString()
        });

    private static JsonElement TermBody(string source, string target, string tip) =>
        JsonSerializer.SerializeToElement(new { source, target, tip });

    private static HttpContext Context(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private async Task<BsonDocument> UserAsync(bool admin = false)
    {
        var id = ObjectId.GenerateNewId();
        var name = "u" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["n"] = name,
            ["ns"] = SearchText.Normalize(name),
            ["as"] = new BsonArray(),
            ["asrch"] = new BsonArray(),
            ["e"] = name + "@example.test",
            ["p"] = PasswordHash.Create("test-password"),
            ["admin"] = admin,
            ["b"] = false
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        return user;
    }

    private async Task<BsonDocument> TeamAsync()
    {
        var team = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = "team_" + ObjectId.GenerateNewId().ToString()[12..],
            ["c"] = DateTime.UtcNow
        };
        await services.Store.Collection("team").InsertOneAsync(team);
        return team;
    }

    private async Task MemberAsync(BsonDocument team, BsonDocument user, string baseTag) =>
        await services.Store.Collection("team_member").InsertOneAsync(new BsonDocument
        {
            ["team"] = team["_id"],
            ["user"] = user["_id"],
            ["base_tag"] = baseTag,
            ["status"] = "active",
            ["tags"] = new BsonArray(),
            ["worker_qualifications"] = new BsonArray(),
            ["version"] = 0
        });

    private async Task<BsonDocument> LanguageAsync(string code, string name)
    {
        var existing = await services.Store.Collection("language")
            .Find(new BsonDocument("co", code)).FirstOrDefaultAsync();
        if (existing is not null) return existing;
        var language = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["e"] = name,
            ["c"] = name,
            ["co"] = code,
            ["gt"] = code,
            ["go"] = code,
            ["n"] = false,
            ["s"] = 0
        };
        await services.Store.Collection("language").InsertOneAsync(language);
        return language;
    }

    private async Task<BsonDocument> BankAsync(BsonDocument? team, BsonDocument owner, BsonDocument source,
        BsonDocument target, string name = "bank")
    {
        var now = DateTime.UtcNow;
        var bank = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["n"] = name,
            ["sl"] = source["_id"],
            ["tl"] = target["_id"],
            ["ti"] = "",
            ["u"] = owner["_id"],
            ["c"] = now,
            ["e"] = now
        };
        if (team is not null) bank["t"] = team["_id"];
        await services.Store.Collection("term_bank").InsertOneAsync(bank);
        return bank;
    }

    private async Task<BsonDocument> TermAsync(BsonDocument bank, BsonDocument owner, string source,
        string target, string tip)
    {
        var now = DateTime.UtcNow;
        var term = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["tb"] = bank["_id"],
            ["o"] = source,
            ["t"] = target,
            ["ti"] = tip,
            ["u"] = owner["_id"],
            ["c"] = now,
            ["e"] = now
        };
        await services.Store.Collection("term").InsertOneAsync(term);
        return term;
    }

    private async Task<BsonDocument> ProjectAsync(BsonDocument team, BsonDocument? bank)
    {
        var project = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["t"] = team["_id"],
            ["n"] = "project",
            ["tb"] = bank is null ? new BsonArray() : new BsonArray { bank["_id"] },
            ["nft"] = false
        };
        await services.Store.Collection("project").InsertOneAsync(project);
        return project;
    }

    private async Task<BsonDocument> SourceAsync(BsonDocument term)
    {
        var source = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["pt"] = new BsonArray { term["_id"] }
        };
        await services.Store.Collection("source").InsertOneAsync(source);
        return source;
    }

    private Task<BsonDocument> FindBankAsync(ObjectId id) => services.Store.Collection("term_bank")
        .Find(new BsonDocument("_id", id)).FirstAsync();

    private Task<BsonDocument> FindProjectAsync(BsonDocument project) => services.Store.Collection("project")
        .Find(new BsonDocument("_id", project["_id"])).FirstAsync();

    private Task<BsonDocument> FindSourceAsync(BsonDocument source) => services.Store.Collection("source")
        .Find(new BsonDocument("_id", source["_id"])).FirstAsync();

    private async Task SetNftAsync(BsonDocument project, bool value) =>
        await services.Store.Collection("project").UpdateOneAsync(new BsonDocument("_id", project["_id"]),
            new BsonDocument("$set", new BsonDocument("nft", value)));

    private static async Task<ApiError> ErrorAsync(Func<Task> action) =>
        await Assert.ThrowsAsync<ApiError>(action);
}
