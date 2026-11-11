using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using RabbitMQ.Client;
using Shoreline.Api.Infrastructure;

namespace Shoreline.Api.Modules.Mit;

/// <summary>
/// Celery protocol v2 envelope builder for the MIT bridge (方案 A).
/// The wire shape is locked by the实测报文 in
/// <c>docs/mit-bridge-spike.md</c> §3 (celery 5.6.3 / kombu 5.6.2):
/// the AMQP body is JSON <c>[args, kwargs, embed]</c> published to the
/// default exchange with <c>routing_key = mit</c>, while task metadata
/// travels in headers. <c>argsrepr</c>/<c>kwargsrepr</c> are diagnostic
/// only (celery <c>saferepr</c>); the worker ignores them.
/// </summary>
public static class MitBridgeEnvelope
{
    public const string ContentType = "application/json";
    public const string ContentEncoding = "utf-8";

    public const string DetectTextKind = "mit_detect_text";
    public const string OcrKind = "mit_ocr";
    public const string TranslateKind = "mit_translate";

    public static string FullTaskName(string kind) => kind switch
    {
        DetectTextKind => "tasks.mit.detect_text",
        OcrKind => "tasks.mit.ocr",
        TranslateKind => "tasks.mit.translate",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown MIT job kind."),
    };

    public static string DefaultOrigin() =>
        $"gen{Environment.ProcessId}@{Environment.MachineName}";

    /// <summary>
    /// Build the v2 envelope. <paramref name="args"/> and
    /// <paramref name="kwargs"/> preserve insertion order; kwargs keys
    /// follow the Python side (<c>app/tasks/mit.py</c> defaults).
    /// </summary>
    public static MitBridgeMessage Build(
        string taskFullName,
        string taskId,
        IList<object?> args,
        IDictionary<string, object?> kwargs,
        string origin,
        string? replyTo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var resolvedReplyTo = replyTo ?? Guid.NewGuid().ToString("D");
        // Celery uses `saferepr(args or ())`: an empty arg list degrades to
        // the tuple repr "()", a non-empty one keeps the list repr.
        var argsRepr = args.Count == 0 ? "()" : ToPythonRepr(args);
        var headers = new Dictionary<string, object?>
        {
            ["task"] = taskFullName,
            ["id"] = taskId,
            ["root_id"] = taskId,
            ["retries"] = 0,
            ["timelimit"] = new List<object?> { null, null },
            ["lang"] = "py",
            ["origin"] = origin,
            ["argsrepr"] = argsRepr,
            ["kwargsrepr"] = ToPythonRepr(kwargs),
            ["eta"] = null,
            ["expires"] = null,
            ["group"] = null,
            ["group_index"] = null,
            ["parent_id"] = null,
            ["shadow"] = null,
            ["ignore_result"] = false,
            ["stamped_headers"] = null,
            ["stamps"] = new Dictionary<string, object?>(),
        };

        var embed = new Dictionary<string, object?>
        {
            ["callbacks"] = null,
            ["errbacks"] = null,
            ["chain"] = null,
            ["chord"] = null,
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new object?[] { args, kwargs, embed });

        return new MitBridgeMessage(
            TaskFullName: taskFullName,
            TaskId: taskId,
            ReplyTo: resolvedReplyTo,
            Origin: origin,
            Headers: headers,
            Body: body);
    }

    /// <summary>
    /// Best-effort Python <c>repr</c> for the diagnostic-only
    /// <c>argsrepr</c>/<c>kwargsrepr</c> headers. Covers the JSON-shaped
    /// values the MIT tasks use (str/int/float/bool/None/list/dict);
    /// the worker never parses these.
    /// </summary>
    internal static string ToPythonRepr(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        sbyte n => n.ToString(CultureInfo.InvariantCulture),
        byte n => n.ToString(CultureInfo.InvariantCulture),
        short n => n.ToString(CultureInfo.InvariantCulture),
        ushort n => n.ToString(CultureInfo.InvariantCulture),
        int n => n.ToString(CultureInfo.InvariantCulture),
        uint n => n.ToString(CultureInfo.InvariantCulture),
        long n => n.ToString(CultureInfo.InvariantCulture),
        ulong n => n.ToString(CultureInfo.InvariantCulture),
        float f => ToPythonFloatRepr(f),
        double d => ToPythonFloatRepr(d),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IDictionary<string, object?> dict => "{" + string.Join(", ",
            dict.Select(pair => $"{ToPythonRepr(pair.Key)}: {ToPythonRepr(pair.Value)}")) + "}",
        System.Collections.IEnumerable sequence => "[" + string.Join(", ",
            sequence.Cast<object?>().Select(ToPythonRepr)) + "]",
        _ => "'" + value.ToString()?.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
    };

    private static string ToPythonFloatRepr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal)
            || text.Contains('e', StringComparison.OrdinalIgnoreCase)
            ? text : text + ".0";
    }
}

/// <summary>An immutable Celery protocol v2 message ready to publish.</summary>
public sealed record MitBridgeMessage(
    string TaskFullName,
    string TaskId,
    string ReplyTo,
    string Origin,
    Dictionary<string, object?> Headers,
    byte[] Body);

/// <summary>
/// Minimal broker abstraction so the envelope and the endpoints stay
/// unit-testable without a broker. Production uses
/// <see cref="RabbitMqBrokerClient"/>; tests substitute an in-memory fake.
/// </summary>
public interface IMitBrokerClient
{
    Task PublishAsync(
        string routingKey,
        byte[] body,
        IReadOnlyDictionary<string, object?> headers,
        string correlationId,
        string replyTo,
        CancellationToken ct);
}

/// <summary>
/// RabbitMQ-backed <see cref="IMitBrokerClient"/> (Celery protocol v2).
/// Publishes to the default exchange with <c>delivery_mode=2</c>,
/// declares the durable queue (<c>arguments={}</c>) idempotently, and
/// connects lazily so construction never throws when the bridge is
/// configured but unused. Not thread-safe for disposal; publish calls
/// are serialized on a gate.
/// </summary>
public sealed class RabbitMqBrokerClient(
    MitBridgeSettings settings,
    ILogger<RabbitMqBrokerClient> logger) : IMitBrokerClient, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public async Task PublishAsync(
        string routingKey,
        byte[] body,
        IReadOnlyDictionary<string, object?> headers,
        string correlationId,
        string replyTo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Url))
            throw new InvalidOperationException("RabbitMq:Url is not configured.");
        var channel = await EnsureChannelAsync(ct).ConfigureAwait(false);
        var properties = new BasicProperties
        {
            ContentType = MitBridgeEnvelope.ContentType,
            ContentEncoding = MitBridgeEnvelope.ContentEncoding,
            DeliveryMode = DeliveryModes.Persistent,
            Priority = 0,
            CorrelationId = correlationId,
            ReplyTo = replyTo,
            Headers = new Dictionary<string, object?>(headers),
        };
        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;
            if (_connection is not { IsOpen: true })
            {
                _channel = null;
                _connection?.Dispose();
                var factory = new ConnectionFactory { Uri = new Uri(settings.Url) };
                _connection = await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
            }
            var channel = await _connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);
            await channel.QueueDeclareAsync(
                queue: settings.Queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>(),
                cancellationToken: ct).ConfigureAwait(false);
            _channel = channel;
            logger.LogInformation(
                "MIT bridge connected to queue {Queue}.", settings.Queue);
            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        if (_channel is not null) await _channel.DisposeAsync().ConfigureAwait(false);
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Publishes MIT jobs as Celery protocol v2 messages. Each call mints a
/// fresh celery task id (uuid) used both as <c>id</c>/<c>root_id</c> and
/// as <c>correlation_id</c>; callers poll
/// <see cref="MitBridgeResultReader"/> with the returned id.
/// </summary>
public sealed class MitBridgePublisher(
    IMitBrokerClient broker,
    MitBridgeSettings settings,
    ILogger<MitBridgePublisher> logger)
{
    public Task<string> PublishAsync(
        string kind,
        IList<object?> args,
        IDictionary<string, object?> kwargs,
        CancellationToken ct) =>
        PublishAsync(kind, args, kwargs, taskId: null, origin: null, ct);

    public async Task<string> PublishAsync(
        string kind,
        IList<object?> args,
        IDictionary<string, object?> kwargs,
        string? taskId,
        string? origin,
        CancellationToken ct)
    {
        var fullName = MitBridgeEnvelope.FullTaskName(kind);
        var resolvedTaskId = taskId ?? Guid.NewGuid().ToString("D");
        var message = MitBridgeEnvelope.Build(
            fullName, resolvedTaskId, args, kwargs,
            origin ?? MitBridgeEnvelope.DefaultOrigin());
        await broker.PublishAsync(
            settings.Queue, message.Body, message.Headers,
            message.TaskId, message.ReplyTo, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Published MIT bridge task {Task} id {TaskId} to queue {Queue}.",
            fullName, resolvedTaskId, settings.Queue);
        return resolvedTaskId;
    }
}

/// <summary>
/// Polls Celery results from Mongo <c>celery_taskmeta</c>
/// (<c>_id = task_id</c>, written by the worker's MongoBackend with
/// <c>replace_one(upsert=True)</c>). Maps <c>SUCCESS→success</c>,
/// <c>FAILURE→fail</c>, anything else or a missing document to
/// <c>pending</c>, mirroring Python <c>_wait_task_result</c> (1s get ≈
/// one lookup, no blocking): callers short-poll.
/// </summary>
public sealed class MitBridgeResultReader(MongoStore store)
{
    public const string TaskMetaCollection = "celery_taskmeta";

    public async Task<Dictionary<string, object?>> GetStatusAsync(
        string taskId, CancellationToken ct)
    {
        var pending = new Dictionary<string, object?>
        {
            ["task_id"] = taskId,
            ["status"] = "pending",
        };
        if (string.IsNullOrWhiteSpace(taskId))
            return pending;

        var doc = await store.Collection(TaskMetaCollection)
            .Find(new BsonDocument("_id", taskId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (doc is null)
            return pending;

        var status = doc.TryGetValue("status", out var statusValue) && statusValue.IsString
            ? statusValue.AsString
            : "";
        switch (status)
        {
            case "SUCCESS":
                return new Dictionary<string, object?>
                {
                    ["task_id"] = taskId,
                    ["status"] = "success",
                    ["result"] = doc.TryGetValue("result", out var result)
                        ? MitBridgeBson.ToNetObject(result) : null,
                };
            case "FAILURE":
            {
                var message = doc.TryGetValue("traceback", out var traceback)
                    && traceback.IsString && !string.IsNullOrEmpty(traceback.AsString)
                    ? traceback.AsString
                    : MitBridgeBson.ToNetObject(doc.TryGetValue("result", out var failure) ? failure : BsonNull.Value)?.ToString()
                        is { Length: > 0 } text ? text : "Task failed";
                return new Dictionary<string, object?>
                {
                    ["task_id"] = taskId,
                    ["status"] = "fail",
                    ["message"] = message,
                };
            }
            default:
                return pending;
        }
    }
}

/// <summary>Shared BSON→plain-object conversion for bridge results.</summary>
public static class MitBridgeBson
{
    public static object? ToNetObject(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => null,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.String => value.AsString,
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.DateTime => value.ToUniversalTime(),
        BsonType.Array => value.AsBsonArray.Select(ToNetObject).ToList(),
        BsonType.Document => value.AsBsonDocument.ToDictionary(
            element => element.Name, element => ToNetObject(element.Value)),
        _ => value.ToString(),
    };

    [SuppressMessage("Performance", "CA1860:Avoid using 'Enumerable.Any()' to check if a collection is empty",
        Justification = "Operates on BsonValue, not a concrete collection.")]
    public static bool IsMissingOrNull(BsonDocument doc, string name) =>
        !doc.TryGetValue(name, out var value) || value.IsBsonNull;
}

/// <summary>DI registration for the optional MIT bridge.</summary>
public static class MitBridgeRegistration
{
    public static IServiceCollection AddMitBridge(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
            MitBridgeSettings.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IMitBrokerClient, RabbitMqBrokerClient>();
        services.AddSingleton<MitBridgePublisher>();
        services.AddSingleton<MitBridgeResultReader>();
        return services;
    }
}
