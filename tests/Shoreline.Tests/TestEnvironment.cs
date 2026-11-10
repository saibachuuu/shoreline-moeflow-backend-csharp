using System.Globalization;

namespace Shoreline.Tests;

/// <summary>
/// Test-only, loopback-only configuration for disposable local services.
/// Ports may be overridden through environment variables; the host is fixed to
/// loopback so this helper can never be pointed at a remote or production host.
/// </summary>
public static class TestEnvironment
{
    public const string LoopbackHost = "127.0.0.1";
    public const string Host = LoopbackHost;
    public const string MongoHost = LoopbackHost;
    public const string S3Host = LoopbackHost;
    public const string SmtpHost = LoopbackHost;
    public const string MailpitHost = LoopbackHost;
    public const string ApiHost = LoopbackHost;

    private const int DefaultMongoPort = 27128;
    private const int DefaultS3Port = 29128;
    private const int DefaultSmtpPort = 21128;
    private const int DefaultMailpitPort = 28128;
    private const int DefaultApiPort = 25128;

    public static int MongoPort => ReadPort("SHORELINE_TEST_MONGO_PORT", DefaultMongoPort);

    public static int S3Port => ReadPort("SHORELINE_TEST_S3_PORT", DefaultS3Port);

    public static int SmtpPort => ReadPort("SHORELINE_TEST_SMTP_PORT", DefaultSmtpPort);

    public static int MailpitPort => ReadPort("SHORELINE_TEST_MAILPIT_PORT", DefaultMailpitPort);

    public static int ApiPort => ReadPort("SHORELINE_TEST_API_PORT", DefaultApiPort);

    public static string S3Endpoint => $"http://{S3Host}:{S3Port}";

    public static string MailpitBaseUrl => $"http://{MailpitHost}:{MailpitPort}";

    public static string ApiBaseUrl => $"http://{ApiHost}:{ApiPort}";

    public static string MongoUri(string database)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new ArgumentException("A database name is required.", nameof(database));
        }

        return $"mongodb://{MongoHost}:{MongoPort}/{database.TrimStart('/')}";
    }

    public static string GetMongoUri(string database) => MongoUri(database);

    public static string MongoConnectionString(string database) => MongoUri(database);

    public static string BuildMongoUri(string database) => MongoUri(database);

    private static int ReadPort(string variableName, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentOutOfRangeException(variableName, raw, "Port must be an integer between 1 and 65535.");
        }

        if (value < 1 || value > 65535)
        {
            throw new ArgumentOutOfRangeException(variableName, value, "Port must be between 1 and 65535.");
        }

        return value;
    }
}