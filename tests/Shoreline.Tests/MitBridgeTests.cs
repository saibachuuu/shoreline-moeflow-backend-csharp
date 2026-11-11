using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;
using Shoreline.Api.Modules.Mit;

namespace Shoreline.Tests;

/// <summary>
/// Golden tests locking the Celery protocol v2 envelope against the实测报文
/// in <c>docs/mit-bridge-spike.md</c> §3. No broker, no Mongo: pure builder.
/// </summary>
public sealed class MitBridgeEnvelopeTests
{
    private const string SpikeTaskId = "f59b14f9-490c-42b9-9885-f95f2155b6eb";
    private const string SpikeOrigin = "gen1@236550727f2e";
    private const string SpikeReplyTo = "a9b3c669-1e3e-3cba-a3fe-d496293decc7";

    private static Dictionary<string, object?> SpikeDetectKwargs() => new()
    {
        ["detector_key"] = "default",
        ["detect_size"] = 2560,
        ["text_threshold"] = 0.5,
        ["box_threshold"] = 0.7,
        ["unclip_ratio"] = 2.3,
        ["invert"] = false,
        ["gamma_correct"] = false,
        ["rotate"] = false,
        ["verbose"] = true,
        ["device"] = "cuda",
    };

    [Fact]
    public void Build_DetectText_MatchesSpikeHeaders()
    {
        var args = new List<object?> { "/MIT_STORAGE_ROOT/spike_page.png" };
        var message = MitBridgeEnvelope.Build(
            "tasks.mit.detect_text", SpikeTaskId, args, SpikeDetectKwargs(),
            SpikeOrigin, SpikeReplyTo);

        var headers = message.Headers;
        Assert.Equal("tasks.mit.detect_text", headers["task"]);
        Assert.Equal(SpikeTaskId, headers["id"]);
        Assert.Equal(SpikeTaskId, headers["root_id"]);
        Assert.Equal(0, headers["retries"]);
        Assert.Equal(new List<object?> { null, null }, headers["timelimit"]);
        Assert.Equal("py", headers["lang"]);
        Assert.Equal(SpikeOrigin, headers["origin"]);
        Assert.Equal("['/MIT_STORAGE_ROOT/spike_page.png']", headers["argsrepr"]);
        Assert.Equal(
            "{'detector_key': 'default', 'detect_size': 2560, 'text_threshold': 0.5, " +
            "'box_threshold': 0.7, 'unclip_ratio': 2.3, 'invert': False, " +
            "'gamma_correct': False, 'rotate': False, 'verbose': True, 'device': 'cuda'}",
            headers["kwargsrepr"]);
        foreach (var key in new[] { "eta", "expires", "group", "group_index", "parent_id", "shadow", "stamped_headers" })
        {
            Assert.True(headers.ContainsKey(key), $"Missing header {key}.");
            Assert.Null(headers[key]);
        }
        Assert.Equal(false, headers["ignore_result"]);
        Assert.Equal(new Dictionary<string, object?>(), headers["stamps"]);

        Assert.Equal(SpikeTaskId, message.TaskId);
        Assert.Equal(SpikeReplyTo, message.ReplyTo);
    }

    [Fact]
    public void Build_DetectText_MatchesSpikeBody()
    {
        var args = new List<object?> { "/MIT_STORAGE_ROOT/spike_page.png" };
        var message = MitBridgeEnvelope.Build(
            "tasks.mit.detect_text", SpikeTaskId, args, SpikeDetectKwargs(),
            SpikeOrigin, SpikeReplyTo);

        using var body = JsonDocument.Parse(message.Body);
        var root = body.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(3, root.GetArrayLength());

        var bodyArgs = root[0];
        Assert.Equal(1, bodyArgs.GetArrayLength());
        Assert.Equal("/MIT_STORAGE_ROOT/spike_page.png", bodyArgs[0].GetString());

        var bodyKwargs = root[1];
        Assert.Equal(JsonValueKind.Object, bodyKwargs.ValueKind);
        Assert.Equal("default", bodyKwargs.GetProperty("detector_key").GetString());
        Assert.Equal(2560, bodyKwargs.GetProperty("detect_size").GetInt32());
        Assert.Equal(0.5, bodyKwargs.GetProperty("text_threshold").GetDouble());
        Assert.Equal(0.7, bodyKwargs.GetProperty("box_threshold").GetDouble());
        Assert.Equal(2.3, bodyKwargs.GetProperty("unclip_ratio").GetDouble());
        Assert.False(bodyKwargs.GetProperty("invert").GetBoolean());
        Assert.False(bodyKwargs.GetProperty("gamma_correct").GetBoolean());
        Assert.False(bodyKwargs.GetProperty("rotate").GetBoolean());
        Assert.True(bodyKwargs.GetProperty("verbose").GetBoolean());
        Assert.Equal("cuda", bodyKwargs.GetProperty("device").GetString());

        var embed = root[2];
        foreach (var key in new[] { "callbacks", "errbacks", "chain", "chord" })
        {
            Assert.True(embed.TryGetProperty(key, out var prop), $"Missing embed {key}.");
            Assert.Equal(JsonValueKind.Null, prop.ValueKind);
        }
    }

    [Fact]
    public void Build_Translate_EmptyArgsUsesTupleRepr()
    {
        var kwargs = new Dictionary<string, object?>
        {
            ["query"] = "こんにちは",
            ["translator"] = "gpt3.5",
            ["target_lang"] = "CHT",
        };
        var taskId = Guid.NewGuid().ToString("D");
        var message = MitBridgeEnvelope.Build(
            "tasks.mit.translate", taskId, [], kwargs, "gen9@testhost", "reply-to-id");

        Assert.Equal("tasks.mit.translate", message.Headers["task"]);
        Assert.Equal("()", message.Headers["argsrepr"]);
        Assert.Equal(
            "{'query': 'こんにちは', 'translator': 'gpt3.5', 'target_lang': 'CHT'}",
            message.Headers["kwargsrepr"]);

        using var body = JsonDocument.Parse(message.Body);
        Assert.Equal(0, body.RootElement[0].GetArrayLength());
        Assert.Equal("こんにちは", body.RootElement[1].GetProperty("query").GetString());
    }

    [Fact]
    public void Build_GeneratesReplyTo_WhenNotProvided()
    {
        var message = MitBridgeEnvelope.Build(
            "tasks.mit.ocr", Guid.NewGuid().ToString("D"),
            new List<object?> { "x.png" }, new Dictionary<string, object?>(),
            "gen9@testhost");
        Assert.True(Guid.TryParse(message.ReplyTo, out _));
    }

    [Fact]
    public void FullTaskName_MapsShortKinds()
    {
        Assert.Equal("tasks.mit.detect_text", MitBridgeEnvelope.FullTaskName("mit_detect_text"));
        Assert.Equal("tasks.mit.ocr", MitBridgeEnvelope.FullTaskName("mit_ocr"));
        Assert.Equal("tasks.mit.translate", MitBridgeEnvelope.FullTaskName("mit_translate"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MitBridgeEnvelope.FullTaskName("mit_inpaint"));
    }

    [Fact]
    public void Settings_GatingRequiresEnabledAndUrl()
    {
        static MitBridgeSettings Read(Dictionary<string, string?> values) =>
            MitBridgeSettings.FromConfiguration(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.True(Read(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "true",
            ["RabbitMq:Url"] = "amqp://127.0.0.1:32110//",
        }).IsBridgeEnabled);
        Assert.False(Read(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "true",
            ["RabbitMq:Url"] = "",
        }).IsBridgeEnabled);
        Assert.False(Read(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "false",
            ["RabbitMq:Url"] = "amqp://127.0.0.1:32110//",
        }).IsBridgeEnabled);
        Assert.False(Read(new Dictionary<string, string?>()).IsBridgeEnabled);
        Assert.Equal("mit", Read(new Dictionary<string, string?>()).Queue);
    }
}

[Collection("local-services")]
public sealed class MitBridgeHttpTests(LocalServices services) : IDisposable
{
    private readonly List<string> _tempDirectories = [];
    private readonly List<string> _seededTaskMetaIds = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Ignored in cleanup
            }
        }
    }

    private string CreateTempStorageRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mit_bridge_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirectories.Add(dir);
        return dir;
    }

    private WebApplicationFactory<Program> CreateBridgeApp(
        string storageRoot, InMemoryMitBrokerClient fake, string? rabbitUrl) =>
        services.App.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mit:StorageRoot"] = storageRoot,
                    ["RabbitMq:Enabled"] = "true",
                    ["RabbitMq:Url"] = rabbitUrl,
                    ["RabbitMq:Queue"] = "mit",
                }));
            builder.ConfigureTestServices(testServices =>
            {
                foreach (var descriptor in testServices
                             .Where(descriptor => descriptor.ServiceType == typeof(IMitBrokerClient))
                             .ToList())
                {
                    testServices.Remove(descriptor);
                }
                testServices.AddSingleton<IMitBrokerClient>(fake);
            });
        });

    private Task<long> CountMitJobsAsync() =>
        services.Store.Collection("csharp_job")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.In(
                "kind", new[] { "mit_detect_text", "mit_ocr", "mit_translate" }));

    [Fact]
    public async Task BridgeEnabled_WithoutConsumers_MapsRoutesAndPublishesDetectText()
    {
        var storageRoot = CreateTempStorageRoot();
        var seededPath = Path.Combine(storageRoot, "page.png");
        await File.WriteAllBytesAsync(seededPath, [1, 2, 3]);
        var fake = new InMemoryMitBrokerClient();

        using var app = CreateBridgeApp(storageRoot, fake, "amqp://127.0.0.1:1//");
        using var client = app.CreateClient();
        var jobsBefore = await CountMitJobsAsync();

        var marker = Guid.NewGuid().ToString("N");
        var res = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename = "page.png",
            task_name = "mit_detect_text",
            detect_size = 1280,
            marker,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = json.GetProperty("task_id").GetString()!;
        Assert.True(Guid.TryParse(taskId, out _));

        Assert.Equal(jobsBefore, await CountMitJobsAsync());
        Assert.Equal(0, await services.Store.Collection("csharp_job")
            .CountDocumentsAsync(new BsonDocument("payload.marker", marker)));

        var published = Assert.Single(fake.Published);
        Assert.Equal("mit", published.RoutingKey);
        Assert.Equal(taskId, published.CorrelationId);
        Assert.True(Guid.TryParse(published.ReplyTo, out _));
        Assert.Equal("tasks.mit.detect_text", published.Headers["task"]);
        Assert.Equal(taskId, published.Headers["id"]);
        Assert.Equal(taskId, published.Headers["root_id"]);

        using var body = JsonDocument.Parse(published.Body);
        Assert.Equal(3, body.RootElement.GetArrayLength());
        var bodyArgs = body.RootElement[0];
        Assert.Equal(1, bodyArgs.GetArrayLength());
        Assert.EndsWith("page.png", bodyArgs[0].GetString());
        var bodyKwargs = body.RootElement[1];
        Assert.Equal("default", bodyKwargs.GetProperty("detector_key").GetString());
        Assert.Equal(1280, bodyKwargs.GetProperty("detect_size").GetInt32());
    }

    [Fact]
    public async Task BridgeEnabled_TranslateTask_PollsCeleryTaskMeta()
    {
        var storageRoot = CreateTempStorageRoot();
        var fake = new InMemoryMitBrokerClient();

        using var app = CreateBridgeApp(storageRoot, fake, "amqp://127.0.0.1:1//");
        using var client = app.CreateClient();

        var postRes = await client.PostAsJsonAsync("/v1/mit/translate-tasks", new
        {
            query = "hello",
            translator = "gpt3.5",
            target_lang = "CHS",
        });
        Assert.Equal(HttpStatusCode.OK, postRes.StatusCode);
        var postJson = await postRes.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = postJson.GetProperty("task_id").GetString()!;
        _seededTaskMetaIds.Add(taskId);

        var published = Assert.Single(fake.Published);
        Assert.Equal("tasks.mit.translate", published.Headers["task"]);
        using var body = JsonDocument.Parse(published.Body);
        Assert.Equal(0, body.RootElement[0].GetArrayLength());
        Assert.Equal("hello", body.RootElement[1].GetProperty("query").GetString());

        try
        {
            var pendingRes = await client.GetAsync($"/v1/mit/translate-tasks/{taskId}");
            Assert.Equal(HttpStatusCode.OK, pendingRes.StatusCode);
            var pendingJson = await pendingRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pending", pendingJson.GetProperty("status").GetString());

            await services.Store.Collection("celery_taskmeta").InsertOneAsync(new BsonDocument
            {
                ["_id"] = taskId,
                ["status"] = "SUCCESS",
                ["result"] = "你好",
            });
            var successRes = await client.GetAsync($"/v1/mit/translate-tasks/{taskId}");
            var successJson = await successRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("success", successJson.GetProperty("status").GetString());
            Assert.Equal("你好", successJson.GetProperty("result").GetString());

            await services.Store.Collection("celery_taskmeta").UpdateOneAsync(
                new BsonDocument("_id", taskId),
                Builders<BsonDocument>.Update.Set("status", "FAILURE").Set("traceback", "boom"));
            var failRes = await client.GetAsync($"/v1/mit/translate-tasks/{taskId}");
            var failJson = await failRes.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("fail", failJson.GetProperty("status").GetString());
            Assert.Equal("boom", failJson.GetProperty("message").GetString());
        }
        finally
        {
            await services.Store.Collection("celery_taskmeta")
                .DeleteManyAsync(new BsonDocument("_id", taskId));
            _seededTaskMetaIds.Remove(taskId);
        }
    }

    [Fact]
    public async Task BridgeEnabledWithoutUrl_StaysFailClosed()
    {
        var storageRoot = CreateTempStorageRoot();
        await File.WriteAllBytesAsync(Path.Combine(storageRoot, "page.png"), [1, 2, 3]);
        var fake = new InMemoryMitBrokerClient();

        using var app = CreateBridgeApp(storageRoot, fake, "");
        using var client = app.CreateClient();

        var imageTaskRes = await client.PostAsJsonAsync("/v1/mit/image-tasks", new
        {
            filename = "page.png",
            task_name = "mit_detect_text",
        });
        Assert.Equal(HttpStatusCode.NotFound, imageTaskRes.StatusCode);

        var translateRes = await client.PostAsJsonAsync("/v1/mit/translate-tasks", new { query = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, translateRes.StatusCode);
        Assert.Empty(fake.Published);
    }

    private sealed class InMemoryMitBrokerClient : IMitBrokerClient
    {
        public List<PublishedBridgeMessage> Published { get; } = [];

        public Task PublishAsync(
            string routingKey,
            byte[] body,
            IReadOnlyDictionary<string, object?> headers,
            string correlationId,
            string replyTo,
            CancellationToken ct)
        {
            Published.Add(new PublishedBridgeMessage(
                routingKey, body, new Dictionary<string, object?>(headers),
                correlationId, replyTo));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedBridgeMessage(
        string RoutingKey,
        byte[] Body,
        Dictionary<string, object?> Headers,
        string CorrelationId,
        string ReplyTo);
}

/// <summary>
/// Wire-compatibility probe against a throwaway RabbitMQ broker. Runs only
/// when <c>MIT_BRIDGE_RABBITMQ_URL</c> is set (e.g. a local
/// <c>rabbitmq:3-management</c> on <c>127.0.0.1:32110</c>); otherwise the
/// test passes without touching any broker. Manual run:
/// <c>wsl -e bash -lc "docker run -d --name r2mit-32110 -p 127.0.0.1:32110:5672
/// rabbitmq:3-management"</c>, publish, consume, <c>docker rm -f</c>.
/// </summary>
public sealed class MitBridgeIntegrationTests
{
    private static string HeaderString(object? value) =>
        value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value?.ToString() ?? "";

    [Fact]
    public async Task PublishConsume_RoundTripAgainstThrowawayBroker()
    {
        var url = Environment.GetEnvironmentVariable("MIT_BRIDGE_RABBITMQ_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var queue = "mit-test-" + Guid.NewGuid().ToString("N");
        var settings = new MitBridgeSettings { Enabled = true, Url = url, Queue = queue };

        await using var broker = new RabbitMqBrokerClient(
            settings, NullLogger<RabbitMqBrokerClient>.Instance);
        var publisher = new MitBridgePublisher(
            broker, settings, NullLogger<MitBridgePublisher>.Instance);
        var taskId = await publisher.PublishAsync(
            "mit_translate",
            [],
            new Dictionary<string, object?>
            {
                ["query"] = "こんにちは",
                ["translator"] = "gpt3.5",
                ["target_lang"] = "CHT",
            },
            ct);

        var factory = new ConnectionFactory { Uri = new Uri(url) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        var declared = await channel.QueueDeclarePassiveAsync(queue, ct);
        Assert.Equal(1u, declared.MessageCount);

        var gotten = await channel.BasicGetAsync(queue, autoAck: false, ct);
        Assert.NotNull(gotten);
        try
        {
            var properties = gotten.BasicProperties;
            Assert.Equal("application/json", properties.ContentType);
            Assert.Equal("utf-8", properties.ContentEncoding);
            Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
            Assert.Equal(0, properties.Priority);
            Assert.Equal(taskId, properties.CorrelationId);
            Assert.True(Guid.TryParse(properties.ReplyTo, out _));

            var headers = properties.Headers!;
            Assert.Equal("tasks.mit.translate", HeaderString(headers["task"]));
            Assert.Equal(taskId, HeaderString(headers["id"]));
            Assert.Equal(taskId, HeaderString(headers["root_id"]));
            Assert.Equal("()", HeaderString(headers["argsrepr"]));

            using var body = JsonDocument.Parse(gotten.Body);
            Assert.Equal(3, body.RootElement.GetArrayLength());
            Assert.Equal(0, body.RootElement[0].GetArrayLength());
            Assert.Equal("こんにちは", body.RootElement[1].GetProperty("query").GetString());
            Assert.Equal("gpt3.5", body.RootElement[1].GetProperty("translator").GetString());
            Assert.Equal("CHT", body.RootElement[1].GetProperty("target_lang").GetString());
        }
        finally
        {
            await channel.BasicAckAsync(gotten.DeliveryTag, false, ct);
        }

        var drained = await channel.BasicGetAsync(queue, autoAck: true, ct);
        Assert.Null(drained);
        await channel.QueueDeleteAsync(queue, cancellationToken: ct);
    }
}
