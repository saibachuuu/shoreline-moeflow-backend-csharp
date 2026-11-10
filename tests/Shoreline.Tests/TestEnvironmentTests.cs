using System;

namespace Shoreline.Tests;

[CollectionDefinition("test-environment", DisableParallelization = true)]
public sealed class TestEnvironmentCollectionDefinition
{
}

[Collection("test-environment")]
public sealed class TestEnvironmentTests
{
    [Fact]
    public void DefaultsArePreserved()
    {
        WithEnv("SHORELINE_TEST_MONGO_PORT", null, () =>
            Assert.Equal(27128, TestEnvironment.MongoPort));
        WithEnv("SHORELINE_TEST_S3_PORT", null, () =>
            Assert.Equal(29128, TestEnvironment.S3Port));
        WithEnv("SHORELINE_TEST_SMTP_PORT", null, () =>
            Assert.Equal(21128, TestEnvironment.SmtpPort));
        WithEnv("SHORELINE_TEST_MAILPIT_PORT", null, () =>
            Assert.Equal(28128, TestEnvironment.MailpitPort));
        WithEnv("SHORELINE_TEST_API_PORT", null, () =>
            Assert.Equal(25128, TestEnvironment.ApiPort));
    }

    [Fact]
    public void LoopbackHostIsFixed()
    {
        Assert.Equal("127.0.0.1", TestEnvironment.LoopbackHost);
        Assert.Equal("127.0.0.1", TestEnvironment.Host);
        Assert.Equal("127.0.0.1", TestEnvironment.MongoHost);
        Assert.Equal("127.0.0.1", TestEnvironment.S3Host);
        Assert.Equal("127.0.0.1", TestEnvironment.SmtpHost);
        Assert.Equal("127.0.0.1", TestEnvironment.MailpitHost);
        Assert.Equal("127.0.0.1", TestEnvironment.ApiHost);
    }

    [Fact]
    public void ValidPortsAreRead()
    {
        WithEnv("SHORELINE_TEST_MONGO_PORT", "12345", () =>
            Assert.Equal(12345, TestEnvironment.MongoPort));
        WithEnv("SHORELINE_TEST_S3_PORT", "23456", () =>
            Assert.Equal(23456, TestEnvironment.S3Port));
        WithEnv("SHORELINE_TEST_SMTP_PORT", "34567", () =>
            Assert.Equal(34567, TestEnvironment.SmtpPort));
        WithEnv("SHORELINE_TEST_MAILPIT_PORT", "45678", () =>
            Assert.Equal(45678, TestEnvironment.MailpitPort));
        WithEnv("SHORELINE_TEST_API_PORT", "56789", () =>
            Assert.Equal(56789, TestEnvironment.ApiPort));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void InvalidMongoPortThrows(string value)
    {
        WithEnv("SHORELINE_TEST_MONGO_PORT", value, () =>
            Assert.Throws<ArgumentOutOfRangeException>(() => TestEnvironment.MongoPort));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidS3PortThrows(string value)
    {
        WithEnv("SHORELINE_TEST_S3_PORT", value, () =>
            Assert.Throws<ArgumentOutOfRangeException>(() => TestEnvironment.S3Port));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidSmtpPortThrows(string value)
    {
        WithEnv("SHORELINE_TEST_SMTP_PORT", value, () =>
            Assert.Throws<ArgumentOutOfRangeException>(() => TestEnvironment.SmtpPort));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidMailpitPortThrows(string value)
    {
        WithEnv("SHORELINE_TEST_MAILPIT_PORT", value, () =>
            Assert.Throws<ArgumentOutOfRangeException>(() => TestEnvironment.MailpitPort));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidApiPortThrows(string value)
    {
        WithEnv("SHORELINE_TEST_API_PORT", value, () =>
            Assert.Throws<ArgumentOutOfRangeException>(() => TestEnvironment.ApiPort));
    }

    [Fact]
    public void MongoUriUsesLoopbackAndConfiguredPort()
    {
        WithEnv("SHORELINE_TEST_MONGO_PORT", "27231", () =>
            Assert.Equal("mongodb://127.0.0.1:27231/testdb", TestEnvironment.MongoUri("testdb")));
    }

    [Fact]
    public void EmptyDatabaseNameIsRejected()
    {
        Assert.Throws<ArgumentException>(() => TestEnvironment.MongoUri(" "));
    }

    [Fact]
    public void EndpointsUseConfiguredPorts()
    {
        WithEnv("SHORELINE_TEST_S3_PORT", "29231", () =>
            Assert.Equal("http://127.0.0.1:29231", TestEnvironment.S3Endpoint));
        WithEnv("SHORELINE_TEST_MAILPIT_PORT", "28231", () =>
            Assert.Equal("http://127.0.0.1:28231", TestEnvironment.MailpitBaseUrl));
        WithEnv("SHORELINE_TEST_API_PORT", "25231", () =>
            Assert.Equal("http://127.0.0.1:25231", TestEnvironment.ApiBaseUrl));
    }

    private static void WithEnv(string name, string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
