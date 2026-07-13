namespace AgentHub.Api.Otel;

/// <summary>Token/cost usage extracted for a single session from one OTLP export.</summary>
public sealed class SessionUsageDelta
{
    public required string SessionId { get; init; }
    /// <summary>Owner from the telemetry (resource/metric attribute <c>user.id</c>); may be null.</summary>
    public string? UserId { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public double CostUsd { get; set; }

    public bool HasData =>
        InputTokens != 0 || OutputTokens != 0 || CacheReadTokens != 0 ||
        CacheCreationTokens != 0 || CostUsd != 0;
}

/// <summary>
/// Parses an OTLP/HTTP <c>ExportMetricsServiceRequest</c> (protobuf) and extracts Claude Code's
/// <c>claude_code.token.usage</c> (by <c>type</c> attribute) and <c>claude_code.cost.usage</c>
/// metrics, keyed by <c>session.id</c>. Session/owner come from resource attributes, with metric
/// (data-point) attributes taking precedence when present.
///
/// The pod exports with delta temporality (see KubernetesSessionService), so the per-request
/// values here are increments that the store adds up.
///
/// Field numbers follow the official opentelemetry-proto definitions:
///   ExportMetricsServiceRequest.resource_metrics = 1
///   ResourceMetrics.resource = 1, .scope_metrics = 2
///   Resource.attributes = 1
///   ScopeMetrics.metrics = 2
///   Metric.name = 1, .gauge = 5, .sum = 7
///   Sum.data_points = 1 ; Gauge.data_points = 1
///   NumberDataPoint.as_double = 4, .as_int = 6, .attributes = 7
///   KeyValue.key = 1, .value = 2 ; AnyValue.string_value = 1, .int_value = 3, .double_value = 4
/// </summary>
public static class OtlpMetricsParser
{
    public const string TokenMetric = "claude_code.token.usage";
    public const string CostMetric = "claude_code.cost.usage";

    private const string SessionIdKey = "session.id";
    private const string UserIdKey = "user.id";
    private const string TypeKey = "type";

    public static IReadOnlyList<SessionUsageDelta> Parse(ReadOnlySpan<byte> payload)
    {
        var bySession = new Dictionary<string, SessionUsageDelta>(StringComparer.Ordinal);
        var reader = new ProtobufReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == ProtobufReader.WireLen)
                ParseResourceMetrics(reader.ReadLengthDelimited(), bySession);
            else
                reader.SkipField(wire);
        }
        return bySession.Values.Where(v => v.HasData).ToList();
    }

    private static void ParseResourceMetrics(ReadOnlySpan<byte> data, Dictionary<string, SessionUsageDelta> acc)
    {
        string? resSession = null, resUser = null;
        // First pass collects the scope_metrics slices; the resource attributes may come before
        // or after, so we buffer the metric payloads and resolve them once the resource is known.
        var scopeSlices = new List<byte[]>();
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == ProtobufReader.WireLen) // resource
            {
                var attrs = ParseResourceAttributes(reader.ReadLengthDelimited());
                attrs.TryGetValue(SessionIdKey, out resSession);
                attrs.TryGetValue(UserIdKey, out resUser);
            }
            else if (field == 2 && wire == ProtobufReader.WireLen) // scope_metrics
            {
                scopeSlices.Add(reader.ReadLengthDelimited().ToArray());
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        foreach (var slice in scopeSlices)
            ParseScopeMetrics(slice, resSession, resUser, acc);
    }

    private static void ParseScopeMetrics(ReadOnlySpan<byte> data, string? resSession, string? resUser,
        Dictionary<string, SessionUsageDelta> acc)
    {
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 2 && wire == ProtobufReader.WireLen) // metrics
                ParseMetric(reader.ReadLengthDelimited(), resSession, resUser, acc);
            else
                reader.SkipField(wire);
        }
    }

    private static void ParseMetric(ReadOnlySpan<byte> data, string? resSession, string? resUser,
        Dictionary<string, SessionUsageDelta> acc)
    {
        string? name = null;
        byte[]? dataPointsHolder = null; // Sum or Gauge message
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == ProtobufReader.WireLen) // name
                name = reader.ReadString();
            else if ((field == 5 || field == 7) && wire == ProtobufReader.WireLen) // gauge | sum
                dataPointsHolder = reader.ReadLengthDelimited().ToArray();
            else
                reader.SkipField(wire);
        }

        if (name is not (TokenMetric or CostMetric) || dataPointsHolder is null) return;
        ParseDataPointHolder(dataPointsHolder, name, resSession, resUser, acc);
    }

    // Sum / Gauge: data_points is field 1 (repeated NumberDataPoint) in both.
    private static void ParseDataPointHolder(ReadOnlySpan<byte> data, string metric, string? resSession,
        string? resUser, Dictionary<string, SessionUsageDelta> acc)
    {
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == ProtobufReader.WireLen) // data_points
                ParseNumberDataPoint(reader.ReadLengthDelimited(), metric, resSession, resUser, acc);
            else
                reader.SkipField(wire);
        }
    }

    private static void ParseNumberDataPoint(ReadOnlySpan<byte> data, string metric, string? resSession,
        string? resUser, Dictionary<string, SessionUsageDelta> acc)
    {
        double? asDouble = null;
        long? asInt = null;
        var attrSlices = new List<byte[]>();
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 4 && wire == ProtobufReader.WireI64) // as_double
                asDouble = reader.ReadDouble();
            else if (field == 6 && wire == ProtobufReader.WireI64) // as_int (sfixed64)
                asInt = (long)reader.ReadFixed64();
            else if (field == 7 && wire == ProtobufReader.WireLen) // attributes
                attrSlices.Add(reader.ReadLengthDelimited().ToArray());
            else
                reader.SkipField(wire);
        }

        // Each field-7 slice is a bare KeyValue message (repeated), unlike Resource.attributes
        // which is a wrapper whose field 1 holds the KeyValues.
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in attrSlices)
        {
            var (k, v) = ParseKeyValue(s);
            if (k is not null && v is not null) attrs[k] = v;
        }

        var sessionId = attrs.GetValueOrDefault(SessionIdKey) ?? resSession;
        if (string.IsNullOrEmpty(sessionId)) return;
        var userId = attrs.GetValueOrDefault(UserIdKey) ?? resUser;

        if (!acc.TryGetValue(sessionId, out var delta))
            acc[sessionId] = delta = new SessionUsageDelta { SessionId = sessionId };
        if (!string.IsNullOrEmpty(userId)) delta.UserId = userId;

        if (metric == CostMetric)
        {
            delta.CostUsd += asDouble ?? asInt ?? 0;
            return;
        }

        // token metric: value is (usually) an integer count; route by the `type` attribute.
        long tokens = asInt ?? (long)Math.Round(asDouble ?? 0);
        switch (attrs.GetValueOrDefault(TypeKey))
        {
            case "input": delta.InputTokens += tokens; break;
            case "output": delta.OutputTokens += tokens; break;
            case "cacheRead": delta.CacheReadTokens += tokens; break;
            case "cacheCreation": delta.CacheCreationTokens += tokens; break;
            // Unknown/absent type: ignore rather than mis-bucket.
        }
    }

    // Resource.attributes = field 1 (repeated KeyValue). Reuses the KeyValue reader below.
    private static Dictionary<string, string> ParseResourceAttributes(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == ProtobufReader.WireLen) // KeyValue
            {
                var (key, value) = ParseKeyValue(reader.ReadLengthDelimited());
                if (key is not null && value is not null) result[key] = value;
            }
            else
            {
                reader.SkipField(wire);
            }
        }
        return result;
    }

    private static (string? key, string? value) ParseKeyValue(ReadOnlySpan<byte> data)
    {
        string? key = null, value = null;
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == ProtobufReader.WireLen) // key
                key = reader.ReadString();
            else if (field == 2 && wire == ProtobufReader.WireLen) // value (AnyValue)
                value = ParseAnyValueAsString(reader.ReadLengthDelimited());
            else
                reader.SkipField(wire);
        }
        return (key, value);
    }

    private static string? ParseAnyValueAsString(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufReader(data);
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == ProtobufReader.WireLen: return reader.ReadString();       // string_value
                case 3 when wire == ProtobufReader.WireVarint: return reader.ReadVarint().ToString(); // int_value
                case 4 when wire == ProtobufReader.WireI64: return reader.ReadDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); // double_value
                case 2 when wire == ProtobufReader.WireVarint: return reader.ReadVarint() != 0 ? "true" : "false"; // bool_value
                default: reader.SkipField(wire); break;
            }
        }
        return null;
    }
}
