using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

[Collection("local-services")]
public sealed class UserTests(LocalServices services)
{
    [Fact]
    public async Task LoginConsumesCaptchaPasswordChangeInvalidatesTokenAndPublicInfoHidesEmail()
    {
        var id = ObjectId.GenerateNewId();
        var email = id + "@example.test";
        var user = new BsonDocument
        {
            ["_id"] = id, ["e"] = email, ["n"] = "user" + id, ["ns"] = "user" + id,
            ["p"] = PasswordHash.Create("old-password"), ["s"] = "test signature", ["l"] = "zh_CN"
        };
        await services.Store.Collection("user").InsertOneAsync(user);
        var info = await SeedCaptcha();
        var login = new { email, password = "old-password", captcha_info = info, captcha = "1234" };
        using var response = await services.Http.PostAsJsonAsync("/v1/user/token", login);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var replay = await services.Http.PostAsJsonAsync("/v1/user/token", login);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var profile = await client.GetFromJsonAsync<JsonElement>("/v1/user/info");
        Assert.Equal(id.ToString(), profile.GetProperty("id").GetString());
        Assert.False(profile.TryGetProperty("email", out _));
        Assert.True(profile.TryGetProperty("can_manage_site_admins", out _));
        var publicInfo = await client.GetFromJsonAsync<JsonElement>("/v1/users/user" + id);
        Assert.False(publicInfo.TryGetProperty("email", out _));
        Assert.False(publicInfo.TryGetProperty("can_manage_site_admins", out _));
        using var change = await client.PutAsJsonAsync("/v1/user/password", new { old_password = "old-password", new_password = "new-password" });
        change.EnsureSuccessStatusCode();
        using var invalidated = await client.GetAsync("/v1/user/info");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidated.StatusCode);
    }

    [Fact]
    public async Task MissingAndMalformedTokensUseLegacyErrors()
    {
        using var missing = await services.Http.GetAsync("/v1/user/info");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(1003, (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
        using var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        using var bad = await client.GetAsync("/v1/user/info");
        Assert.Equal(1004, (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task CaptchaIsRasterAndConcurrentVerificationIsSingleUse()
    {
        using var response = await services.Http.PostAsync("/v1/captchas", null);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var image = result.GetProperty("image").GetString()!;
        Assert.StartsWith("data:image/png;base64,", image);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, Convert.FromBase64String(image.Split(',')[1])[..4]);
        var info = await SeedCaptcha();
        var captcha = new CaptchaService(services.Store, TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try { await captcha.VerifyAsync(info, "1234", default); return true; }
            catch (Exception) { return false; }
        }));
        Assert.Single(results, r => r);
    }

    private async Task<string> SeedCaptcha()
    {
        var info = Guid.NewGuid().ToString("N");
        await services.Store.Collection("csharp_captcha").InsertOneAsync(new BsonDocument
        {
            ["_id"] = info, ["expires_at"] = DateTime.UtcNow.AddMinutes(2),
            ["digest"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(info + ":1234")))
        });
        return info;
    }
}
