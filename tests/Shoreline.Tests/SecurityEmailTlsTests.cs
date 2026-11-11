using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Shoreline.Api.Jobs;

namespace Shoreline.Tests;

// Verification-code mail TLS selection. The Python oracle
// (app/tasks/email.py:99-104) calls starttls() and only swallows
// SMTPNotSupportedError, which is StartTlsWhenAvailable, not mandatory
// STARTTLS. Implicit SMTPS comes from EMAIL_USE_SSL (or the 465 heuristic).
public sealed class SecurityEmailTlsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    [Fact]
    public void StartTlsEnabled_UsesOpportunisticStartTls()
    {
        var configuration = Configuration(
            ("Mail:Port", "587"),
            ("Mail:UseSsl", "false"),
            ("Mail:StartTls", "true"));

        Assert.Equal(
            SecureSocketOptions.StartTlsWhenAvailable,
            SecurityEmailHandler.ResolveSocketOptions(configuration));
    }

    [Fact]
    public void UseSsl_UsesImplicitTls()
    {
        var configuration = Configuration(
            ("Mail:Port", "465"),
            ("Mail:UseSsl", "true"),
            ("Mail:StartTls", "true"));

        Assert.Equal(
            SecureSocketOptions.SslOnConnect,
            SecurityEmailHandler.ResolveSocketOptions(configuration));
    }

    [Fact]
    public void Port465WithoutToggle_StillUsesImplicitTls()
    {
        var configuration = Configuration(("Mail:Port", "465"), ("Mail:UseSsl", "false"));

        Assert.Equal(
            SecureSocketOptions.SslOnConnect,
            SecurityEmailHandler.ResolveSocketOptions(configuration));
    }

    [Fact]
    public void StartTlsDisabled_UsesPlaintext()
    {
        var configuration = Configuration(
            ("Mail:Port", "25"),
            ("Mail:UseSsl", "false"),
            ("Mail:StartTls", "false"));

        Assert.Equal(
            SecureSocketOptions.None,
            SecurityEmailHandler.ResolveSocketOptions(configuration));
    }

    [Fact]
    public void MissingStartTlsToggle_FallsBackToAuto()
    {
        var configuration = Configuration(("Mail:Port", "587"), ("Mail:UseSsl", "false"));

        Assert.Equal(
            SecureSocketOptions.Auto,
            SecurityEmailHandler.ResolveSocketOptions(configuration));
    }
}
