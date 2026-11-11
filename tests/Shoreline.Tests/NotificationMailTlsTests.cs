using System.Net.Http.Json;
using System.Text.Json;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shoreline.Api.Notifications;

namespace Shoreline.Tests;

// TLS policy parity for the notification mail transport.
// Python oracle (app/services/notification_mail.py:72-83): SMTP_SSL iff
// EMAIL_USE_SSL, otherwise mandatory starttls(). Mail:SecureSocketOptions
// overrides that default explicitly (None for a TLS-less Mailpit).
public sealed class NotificationMailTlsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();

    private static NotificationMailMessage Message(string to) =>
        new(to, "TLS probe", "probe body", null, "<tls-probe@example.test>", null);

    private static IConfiguration MailpitConfiguration(string? secureSocketOptions, string recipient) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:UserEmailEnabled"] = "true",
            ["Notifications:LogEmailEnabled"] = "false",
            ["TESTING"] = "YES",
            ["Notifications:EmailUnrestricted"] = "true",
            ["Notifications:EmailAllowlist:0"] = recipient,
            ["Mail:Host"] = "127.0.0.1",
            ["Mail:Port"] = TestEnvironment.SmtpPort.ToString(),
            ["Mail:UseSsl"] = "false",
            ["Mail:From"] = "noreply@example.test",
            ["Mail:SecureSocketOptions"] = secureSocketOptions
        }).Build();

    private static async Task<int> MailpitMessageCountAsync(string email)
    {
        using var mailpit = new HttpClient
        {
            BaseAddress = new Uri(TestEnvironment.MailpitBaseUrl),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var result = await mailpit.GetFromJsonAsync<JsonElement>(
            "/api/v1/search?query=" + Uri.EscapeDataString("to:" + email));
        return result.GetProperty("messages").GetArrayLength();
    }

    [Theory]
    [InlineData(false, SecureSocketOptions.StartTls)]
    [InlineData(true, SecureSocketOptions.SslOnConnect)]
    public void SocketOptions_DefaultMatchesLegacy(bool useSsl, SecureSocketOptions expected)
    {
        var configuration = Configuration(("Mail:Host", "127.0.0.1"));

        Assert.Equal(expected, ProtectedNotificationMail.SocketOptions(configuration, useSsl));
        // The bool-only overload is the frozen legacy mapping itself.
        Assert.Equal(expected, ProtectedNotificationMail.SocketOptions(useSsl));
    }

    [Theory]
    [InlineData("None", SecureSocketOptions.None)]
    [InlineData("none", SecureSocketOptions.None)]
    [InlineData("Auto", SecureSocketOptions.Auto)]
    [InlineData("StartTls", SecureSocketOptions.StartTls)]
    [InlineData("starttlswhenavailable", SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData("SslOnConnect", SecureSocketOptions.SslOnConnect)]
    public void SocketOptions_ExplicitValueWinsRegardlessOfCase(
        string raw, SecureSocketOptions expected)
    {
        var configuration = Configuration(
            ("Mail:UseSsl", "false"),
            ("Mail:SecureSocketOptions", raw));

        Assert.Equal(expected, ProtectedNotificationMail.SocketOptions(configuration, false));
    }

    [Fact]
    public void SocketOptions_ExplicitNoneOverridesUseSsl()
    {
        var configuration = Configuration(
            ("Mail:UseSsl", "true"),
            ("Mail:SecureSocketOptions", "None"));

        Assert.Equal(SecureSocketOptions.None, ProtectedNotificationMail.SocketOptions(configuration, true));
    }

    [Fact]
    public void SocketOptions_ExplicitSslOnConnectNeverDowngrades()
    {
        var configuration = Configuration(
            ("Mail:UseSsl", "false"),
            ("Mail:SecureSocketOptions", "SslOnConnect"));

        Assert.Equal(
            SecureSocketOptions.SslOnConnect,
            ProtectedNotificationMail.SocketOptions(configuration, false));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("   ")]
    public void SocketOptions_UnparsableValueFallsBackToLegacy(string raw)
    {
        var configuration = Configuration(("Mail:SecureSocketOptions", raw));

        Assert.Equal(SecureSocketOptions.StartTls, ProtectedNotificationMail.SocketOptions(configuration, false));
        Assert.Equal(SecureSocketOptions.SslOnConnect, ProtectedNotificationMail.SocketOptions(configuration, true));
    }

    [Fact]
    public async Task SendAsync_ExplicitNone_DeliversToTlsLessMailpit()
    {
        var email = "tls-probe-" + Guid.NewGuid().ToString("N") + "@example.test";
        var transport = new MailKitNotificationTransport(
            MailpitConfiguration("None", email),
            new NotificationE2ETestHostEnvironment(),
            NullLogger<MailKitNotificationTransport>.Instance);

        var result = await transport.SendAsync(Message(email), default);

        Assert.Equal("accepted", result.Status);
        Assert.Equal("", result.Reason);
        Assert.Equal(1, await MailpitMessageCountAsync(email));
    }

    [Fact]
    public async Task SendAsync_LegacyDefault_DoesNotDeliverToTlsLessMailpit()
    {
        // The legacy policy mandates STARTTLS when UseSsl is false; the
        // disposable Mailpit listener offers no TLS, so the send must fail
        // closed instead of silently downgrading to plaintext.
        var email = "tls-probe-" + Guid.NewGuid().ToString("N") + "@example.test";
        var transport = new MailKitNotificationTransport(
            MailpitConfiguration(null, email),
            new NotificationE2ETestHostEnvironment(),
            NullLogger<MailKitNotificationTransport>.Instance);

        var result = await transport.SendAsync(Message(email), default);

        Assert.NotEqual("accepted", result.Status);
        Assert.Equal(0, await MailpitMessageCountAsync(email));
    }

    private sealed class NotificationE2ETestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Shoreline.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
