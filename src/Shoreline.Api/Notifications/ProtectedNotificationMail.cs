using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Shoreline.Api.Notifications;

public sealed record NotificationMailMessage(
    string To,
    string Subject,
    string Text,
    string? Html,
    string? MessageId,
    string? ReplyTo);

public sealed record NotificationMailResult(string Status, string Reason);

/// <summary>
/// Which legacy SMTP channel a send belongs to. The Python adapter selects
/// <c>ENABLE_USER_EMAIL</c> or <c>ENABLE_LOG_EMAIL</c> independently; the two
/// switches never OR together.
/// </summary>
public enum NotificationMailChannel
{
    User,
    Log
}

public interface INotificationMailTransport
{
    Task<NotificationMailResult> SendAsync(NotificationMailMessage message, CancellationToken ct);
}

public sealed record NotificationMailSettings(
    bool UserEmailEnabled,
    bool LogEmailEnabled,
    bool Testing,
    bool Debugging,
    string Host,
    int Port,
    bool UseSsl,
    string From,
    string? Username,
    string? Password,
    bool Unrestricted,
    IReadOnlySet<string> Allowlist)
{
    public static NotificationMailSettings FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddList(allowlist, configuration["NOTIFICATION_EMAIL_ALLOWLIST"]);
        AddList(allowlist, configuration["Notifications:EmailAllowlist"]);
        foreach (var child in configuration.GetSection("Notifications:EmailAllowlist").GetChildren())
            AddList(allowlist, child.Value);

        return new NotificationMailSettings(
            NotificationMailSettings.ParseBool(
                configuration["Notifications:UserEmailEnabled"] ?? configuration["ENABLE_USER_EMAIL"]),
            NotificationMailSettings.ParseBool(
                configuration["Notifications:LogEmailEnabled"] ?? configuration["ENABLE_LOG_EMAIL"]),
            environment.IsEnvironment("Testing") ||
            string.Equals(configuration["TESTING"], "YES", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["TESTING"], "True", StringComparison.OrdinalIgnoreCase),
            environment.IsDevelopment() ||
            string.Equals(configuration["DEBUG"], "True", StringComparison.OrdinalIgnoreCase),
            configuration["Mail:Host"] ?? configuration["EMAIL_SMTP_HOST"] ?? "",
            int.TryParse(configuration["Mail:Port"] ?? configuration["EMAIL_SMTP_PORT"], out var port) ? port : 465,
            NotificationMailSettings.ParseBool(configuration["Mail:UseSsl"] ?? configuration["EMAIL_USE_SSL"]),
            configuration["Mail:From"] ?? configuration["EMAIL_ADDRESS"] ?? "",
            configuration["Mail:Username"] ?? configuration["EMAIL_USERNAME"],
            configuration["Mail:Password"] ?? configuration["EMAIL_PASSWORD"],
            NotificationMailSettings.ParseBool(
                configuration["Notifications:EmailUnrestricted"] ?? configuration["NOTIFICATION_EMAIL_UNRESTRICTED"]),
            allowlist);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value?.Trim(), "True", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
        value?.Trim() == "1";

    private static void AddList(HashSet<string> target, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add(part.ToLowerInvariant());
    }
}

/// <summary>
/// Header and recipient protection shared by every SMTP transport.
/// Mirrors the legacy Python send_protected_mail guards.
/// </summary>
public static partial class ProtectedNotificationMail
{
    [GeneratedRegex(@"^[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    public static bool IsValidAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && AddressPattern().IsMatch(value);

    /// <summary>
    /// Returns status "ok" with a normalized address, or a skipped/failed reason
    /// without ever touching the network.
    /// </summary>
    public static (string Status, string Reason, string Address) Check(
        NotificationMailMessage message,
        NotificationMailSettings settings,
        NotificationMailChannel channel = NotificationMailChannel.User)
    {
        var channelEnabled = channel switch
        {
            NotificationMailChannel.User => settings.UserEmailEnabled,
            NotificationMailChannel.Log => settings.LogEmailEnabled,
            _ => false
        };
        if (!channelEnabled)
            return ("skipped", "channel_disabled", "");
        if (!IsValidAddress(message.To))
            return ("skipped", "invalid_email", "");

        var address = message.To.Trim().ToLowerInvariant();
        if ((settings.Testing || settings.Debugging || !settings.Unrestricted) &&
            !settings.Allowlist.Contains(address))
            return ("skipped", "recipient_not_allowed", address);

        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.From))
            return ("skipped", "smtp_unconfigured", address);

        if (HasHeaderBreak(message.Subject) || HasHeaderBreak(settings.From) ||
            HasHeaderBreak(message.ReplyTo) || HasHeaderBreak(message.MessageId))
            return ("failed", "invalid_header", address);

        return ("ok", "", address);
    }

    public static SecureSocketOptions SocketOptions(bool useSsl) =>
        useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

    /// <summary>
    /// Legacy-compatible TLS selection with an explicit opt-out.
    /// The Python oracle (<c>app/services/notification_mail.py:72-83</c>)
    /// uses implicit TLS (<c>SMTP_SSL</c>) iff <c>EMAIL_USE_SSL</c>, otherwise
    /// mandatory <c>starttls()</c>; <see cref="SocketOptions(bool)"/> is that
    /// mapping and stays the default. <c>Mail:SecureSocketOptions</c>
    /// (<c>None|Auto|StartTls|StartTlsWhenAvailable|SslOnConnect</c>,
    /// case-insensitive) overrides it explicitly — e.g. <c>None</c> for a
    /// disposable Mailpit listener with no TLS. An absent or unparsable value
    /// falls back to the legacy mapping; the override never downgrades
    /// implicitly.
    /// </summary>
    public static SecureSocketOptions SocketOptions(IConfiguration configuration, bool useSsl)
    {
        var raw = configuration["Mail:SecureSocketOptions"];
        if (!string.IsNullOrWhiteSpace(raw) &&
            Enum.TryParse<SecureSocketOptions>(raw.Trim(), ignoreCase: true, out var parsed))
            return parsed;
        return SocketOptions(useSsl);
    }

    private static bool HasHeaderBreak(string? value) =>
        value is not null && (value.Contains('\r') || value.Contains('\n'));
}

/// <summary>
/// Sends notification mail through MailKit. TLS verification always uses the OS
/// trust store; there is deliberately no option to disable it.
/// </summary>
public sealed class MailKitNotificationTransport(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<MailKitNotificationTransport> logger) : INotificationMailTransport
{
    private const int TimeoutMilliseconds = 10_000;

    public static NotificationMailResult ClassifyFailure(Exception error, bool sending, bool accepted)
    {
        if (error is SslHandshakeException
            or MailKit.Security.AuthenticationException
            or System.Security.Authentication.AuthenticationException
            or NotSupportedException or ArgumentException or FormatException)
        {
            return new NotificationMailResult("failed", "smtp_configuration");
        }

        var status = accepted ? "accepted" : sending ? "unknown" : "retry_wait";
        return new NotificationMailResult(status, accepted ? "" : "transport_interrupted");
    }

    public async Task<NotificationMailResult> SendAsync(NotificationMailMessage message, CancellationToken ct)
    {
        var settings = NotificationMailSettings.FromConfiguration(configuration, environment);
        var (status, reason, address) = ProtectedNotificationMail.Check(
            message, settings, NotificationMailChannel.User);
        if (status != "ok")
        {
            Log(status, reason);
            return new NotificationMailResult(status, reason);
        }

        string? current = null;
        var sending = false;
        var accepted = false;
        SmtpClient? client = null;
        try
        {
            MimeMessage mime;
            try
            {
                mime = BuildMime(message, settings, address);
            }
            catch (Exception error) when (error is FormatException or ParseException or ArgumentException)
            {
                Log("failed", "smtp_configuration");
                return new NotificationMailResult("failed", "smtp_configuration");
            }

            client = new SmtpClient { Timeout = TimeoutMilliseconds };
            await client.ConnectAsync(
                settings.Host,
                settings.Port,
                ProtectedNotificationMail.SocketOptions(configuration, settings.UseSsl),
                ct);
            if (!string.IsNullOrEmpty(settings.Username))
                await client.AuthenticateAsync(settings.Username, settings.Password ?? "", ct);

            sending = true;
            await client.SendAsync(mime, ct);
            accepted = true;
            Log("accepted", "");
            return new NotificationMailResult("accepted", "");
        }
        catch (SmtpCommandException error)
        {
            var statusCode = (int)error.StatusCode;
            current = statusCode is >= 400 and < 500 ? "retry_wait" : "failed";
            var codeReason = error.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted
                ? "recipient_refused"
                : "smtp_response";
            Log(current, codeReason);
            return new NotificationMailResult(current, codeReason);
        }
        catch (Exception error)
        {
            var result = ClassifyFailure(error, sending, accepted);
            Log(result.Status, result.Reason);
            return result;
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    if (client.IsConnected)
                    {
                        using var disconnect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await client.DisconnectAsync(true, disconnect.Token);
                    }
                }
                catch
                {
                    // Disconnect failures never override an accepted send.
                }
                client.Dispose();
            }
        }
    }

    private static MimeMessage BuildMime(NotificationMailMessage message, NotificationMailSettings settings, string address)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(settings.From));
        mime.To.Add(MailboxAddress.Parse(address));
        mime.Subject = message.Subject;
        if (!string.IsNullOrEmpty(message.MessageId))
            mime.MessageId = message.MessageId;
        if (!string.IsNullOrEmpty(message.ReplyTo))
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));

        var body = new BodyBuilder { TextBody = message.Text };
        if (!string.IsNullOrEmpty(message.Html))
            body.HtmlBody = message.Html;
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private void Log(string status, string reason) =>
        logger.LogWarning("notification mail result {Status}/{Reason}", status, reason);
}
