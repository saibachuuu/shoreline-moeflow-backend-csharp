using Microsoft.Extensions.Configuration;
using Shoreline.Api.Infrastructure;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1794182400);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class SecurityTests
{
    public const string Secret = "local-test-secret-012345678901234567890";
    public static LegacyToken Tokens(TimeProvider clock) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Auth:Secret"] = Secret }).Build(), clock);

    // Generated independently with Python Werkzeug, not by the C# implementation.
    [Theory]
    [InlineData("pbkdf2:sha256:1000$hQGrIkUl$fcc042403d2c238e745f43660b7ffc9ac98115fe845432dfbc0abc4ac696334a")]
    [InlineData("pbkdf2:sha1:1000$XWtKcZKZ$c29897a17b542463e53698cb6ef57309a9f90900")]
    [InlineData("pbkdf2:sha512:1000$765utCob$9e262974a42111366202774e1f75bc2fb104897026939b681830546259e8ea6194c92ef6a474cf9825307fae3aa8bff5a65b2225b64b7fbc770a6e67b27141e2")]
    [InlineData("scrypt:32768:8:1$Hupv2FC9$b3dc2bc3b2ef51e061431950104188f84bc6aa60961b844414b2bbb5e278478d59b06cc0074810b8c3c169fcdc4ca7c36d6e057ec2ee5e4ed7a78b0c254d879d")]
    public void AcceptsWerkzeugPasswords(string hash)
    {
        Assert.True(PasswordHash.Verify(hash, "test-password"));
        Assert.False(PasswordHash.Verify(hash, "wrong-password"));
    }

    [Theory]
    [InlineData("pbkdf2:sha256:1000$salt$")]
    [InlineData("pbkdf2:sha256:-1$salt$ff")]
    [InlineData("scrypt:100000000:8:1$salt$ff")]
    [InlineData("invalid")]
    public void RejectsMalformedPasswords(string hash) => Assert.False(PasswordHash.Verify(hash, "password"));

    [Fact]
    public void CreatesPasswordThatVerifies()
    {
        var first = PasswordHash.Create("密码abcdef");
        var second = PasswordHash.Create("密码abcdef");
        Assert.NotEqual(first, second);
        Assert.True(PasswordHash.Verify(first, "密码abcdef"));
        Assert.False(PasswordHash.Verify(first, "abcdef"));
    }

    [Theory]
    [InlineData(".eJyrVspMUbJSMjA0MjYxNTO3sExMSk5JTYPxlXSUCpKxyCvVAgCd3w6p.avENAA.E6Fb3vBAqcPni6zYZKaPw3irlCg")]
    [InlineData(".eJyrVspMUbJSMjA0MjYxNTO3sExMSk5JTYPxlXSUCpKxyIPEE1NSMvPSgZKJo4BooFQLABDUhBo.avENAA.vSLiEqqgvFJQKK_G-jYwSDDh_gw")]
    [InlineData("eyJhbGciOiJIUzUxMiIsImV4cCI6MjAwMDAwMDAwMCwidHlwIjoiSldUIn0.eyJpZCI6IjAxMjM0NTY3ODlhYmNkZWYwMTIzNDU2NyIsInBjIjoiMDEyMzQ1Njc4OWFiY2RlZiJ9.Pw71gY9p4__AjFn9-G8sc9UUb6kswTQXHcfw_L3noYAgx9Bq1aUkENn7q1XQIpQivc6rcIqjvR3y9V75_gGSbA")]
    public void AcceptsPythonToken(string encoded)
    {
        var token = Tokens(new TestClock()).Verify(encoded);
        Assert.Equal("0123456789abcdef01234567", token.Id);
        Assert.Equal("0123456789abcdef", token.Characteristic);
    }

    [Fact]
    public void TokenRejectsTamperingFutureAndExpiry()
    {
        var clock = new TestClock();
        var tokens = Tokens(clock);
        var token = tokens.Issue("0123456789abcdef01234567", "hash");
        Assert.Equal("0123456789abcdef01234567", tokens.Verify(token).Id);
        Assert.Throws<ApiError>(() => tokens.Verify(token + "x"));
        clock.Now = clock.Now.AddSeconds(-1);
        Assert.Throws<ApiError>(() => tokens.Verify(token));
        clock.Now = clock.Now.AddDays(31);
        Assert.Throws<ApiError>(() => tokens.Verify(token));
    }
}
