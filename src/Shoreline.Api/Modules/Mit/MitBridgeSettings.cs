namespace Shoreline.Api.Modules.Mit;

/// <summary>
/// Configuration for the optional MIT Celery bridge (方案 A in
/// <c>docs/mit-bridge-spike.md</c> §4). The C# host publishes Celery
/// protocol v2 messages straight to the <c>mit</c> RabbitMQ queue and
/// polls results from Mongo <c>celery_taskmeta</c>; no Python sidecar.
/// The bridge stays disabled unless both <c>Enabled</c> is true and
/// <c>Url</c> is non-empty, keeping <c>/v1/mit</c> fail-closed.
/// </summary>
public sealed class MitBridgeSettings
{
    public const string SectionName = "RabbitMq";

    public const string DefaultQueue = "mit";

    public bool Enabled { get; init; }

    public string Url { get; init; } = "";

    public string Queue { get; init; } = DefaultQueue;

    /// <summary>
    /// The bridge may only map <c>/v1/mit</c> when explicitly enabled
    /// with a broker URL. Missing credentials fail closed.
    /// </summary>
    public bool IsBridgeEnabled => Enabled && !string.IsNullOrWhiteSpace(Url);

    /// <summary>Never include broker credentials in logs or diagnostics.</summary>
    public string Describe() =>
        $"{SectionName}({nameof(Enabled)}={Enabled}, {nameof(Queue)}={Queue}, " +
        $"{nameof(Url)}={(string.IsNullOrWhiteSpace(Url) ? "<empty>" : "<set>")})";

    public static MitBridgeSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var queue = (section["Queue"] ?? "").Trim();
        return new MitBridgeSettings
        {
            Enabled = section.GetValue<bool>("Enabled"),
            Url = (section["Url"] ?? "").Trim(),
            Queue = queue.Length == 0 ? DefaultQueue : queue,
        };
    }
}
