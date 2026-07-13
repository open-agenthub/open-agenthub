using System.Text;
using AgentHub.Api.Otel;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Tests the hand-written OTLP protobuf parser against payloads built by an independent
/// protobuf encoder (below). Encoder and decoder follow the same public wire spec but are
/// separate implementations, so a passing round-trip exercises the decoder's handling of the
/// real nested OTLP structure (ResourceMetrics -> ScopeMetrics -> Metric -> Sum -> NumberDataPoint),
/// varints, fixed64 int/double values, and resource vs. data-point attributes.
/// </summary>
public class OtlpMetricsParserTests
{
    [Fact]
    public void ExtractsTokensByTypeAndCost_FromResourceAttributes()
    {
        var payload = new OtlpBuilder()
            .Resource(("session.id", "sess-1"), ("user.id", "alice"))
            .TokenPoint("input", 100)
            .TokenPoint("output", 40)
            .TokenPoint("cacheRead", 7)
            .TokenPoint("cacheCreation", 3)
            .CostPoint(0.125)
            .Build();

        var result = OtlpMetricsParser.Parse(payload);

        var d = Assert.Single(result);
        Assert.Equal("sess-1", d.SessionId);
        Assert.Equal("alice", d.UserId);
        Assert.Equal(100, d.InputTokens);
        Assert.Equal(40, d.OutputTokens);
        Assert.Equal(7, d.CacheReadTokens);
        Assert.Equal(3, d.CacheCreationTokens);
        Assert.Equal(0.125, d.CostUsd, 6);
    }

    [Fact]
    public void DataPointAttributes_OverrideResourceSession()
    {
        // session.id absent on the resource, present on the data point (as Claude Code sends it).
        var payload = new OtlpBuilder()
            .Resource(("service.name", "claude-code"))
            .TokenPoint("input", 55, ("session.id", "sess-dp"), ("user.id", "bob"))
            .Build();

        var d = Assert.Single(OtlpMetricsParser.Parse(payload));
        Assert.Equal("sess-dp", d.SessionId);
        Assert.Equal("bob", d.UserId);
        Assert.Equal(55, d.InputTokens);
    }

    [Fact]
    public void AggregatesAcrossTwoResourceMetrics_ForSameSession()
    {
        var payload = new OtlpBuilder()
            .Resource(("session.id", "sess-2"))
            .TokenPoint("input", 10)
            .NextResource(("session.id", "sess-2"))
            .TokenPoint("input", 5)
            .CostPoint(1.5)
            .Build();

        var d = Assert.Single(OtlpMetricsParser.Parse(payload));
        Assert.Equal("sess-2", d.SessionId);
        Assert.Equal(15, d.InputTokens);
        Assert.Equal(1.5, d.CostUsd, 6);
    }

    [Fact]
    public void SeparatesDistinctSessions()
    {
        var payload = new OtlpBuilder()
            .Resource(("session.id", "a"))
            .TokenPoint("input", 1)
            .NextResource(("session.id", "b"))
            .TokenPoint("output", 2)
            .Build();

        var result = OtlpMetricsParser.Parse(payload);
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result.Single(x => x.SessionId == "a").InputTokens);
        Assert.Equal(2, result.Single(x => x.SessionId == "b").OutputTokens);
    }

    [Fact]
    public void IgnoresUnknownTokenTypeAndMissingSession()
    {
        // Unknown "type" is not bucketed; a point without any session.id is dropped entirely.
        var payload = new OtlpBuilder()
            .Resource(("service.name", "x"))
            .TokenPoint("weird", 999)
            .Build();

        Assert.Empty(OtlpMetricsParser.Parse(payload));
    }

    [Fact]
    public void EmptyPayload_ReturnsEmpty()
        => Assert.Empty(OtlpMetricsParser.Parse(ReadOnlySpan<byte>.Empty));

    // ----------------------------------------------------------------- test-only OTLP encoder

    /// <summary>Independent protobuf/OTLP encoder for building test payloads.</summary>
    private sealed class OtlpBuilder
    {
        private readonly List<byte[]> _resourceMetrics = new();
        private byte[]? _currentResource;
        private readonly List<byte[]> _currentPoints = new(); // token points
        private readonly List<byte[]> _currentCostPoints = new();

        public OtlpBuilder Resource(params (string, string)[] attrs)
        {
            _currentResource = EncodeResource(attrs);
            return this;
        }

        public OtlpBuilder NextResource(params (string, string)[] attrs)
        {
            Flush();
            _currentResource = EncodeResource(attrs);
            return this;
        }

        public OtlpBuilder TokenPoint(string type, long value, params (string, string)[] extraAttrs)
        {
            var attrs = new List<(string, string)> { ("type", type) };
            attrs.AddRange(extraAttrs);
            _currentPoints.Add(EncodeNumberDataPointInt(value, attrs.ToArray()));
            return this;
        }

        public OtlpBuilder CostPoint(double value, params (string, string)[] extraAttrs)
        {
            _currentCostPoints.Add(EncodeNumberDataPointDouble(value, extraAttrs));
            return this;
        }

        public byte[] Build()
        {
            Flush();
            var req = new Pb();
            foreach (var rm in _resourceMetrics) req.Len(1, rm);
            return req.ToArray();
        }

        private void Flush()
        {
            if (_currentPoints.Count == 0 && _currentCostPoints.Count == 0 && _currentResource is null) return;

            var metrics = new List<byte[]>();
            if (_currentPoints.Count > 0)
                metrics.Add(EncodeSumMetric(OtlpMetricsParser.TokenMetric, _currentPoints));
            if (_currentCostPoints.Count > 0)
                metrics.Add(EncodeSumMetric(OtlpMetricsParser.CostMetric, _currentCostPoints));

            // ScopeMetrics: metrics at field 2.
            var scope = new Pb();
            foreach (var m in metrics) scope.Len(2, m);

            // ResourceMetrics: resource=1, scope_metrics=2.
            var rm = new Pb();
            if (_currentResource is not null) rm.Len(1, _currentResource);
            rm.Len(2, scope.ToArray());
            _resourceMetrics.Add(rm.ToArray());

            _currentResource = null;
            _currentPoints.Clear();
            _currentCostPoints.Clear();
        }

        private static byte[] EncodeResource((string, string)[] attrs)
        {
            var res = new Pb(); // Resource: attributes at field 1 (repeated KeyValue).
            foreach (var (k, v) in attrs) res.Len(1, EncodeKeyValue(k, v));
            return res.ToArray();
        }

        private static byte[] EncodeSumMetric(string name, List<byte[]> points)
        {
            var sum = new Pb(); // Sum: data_points at field 1.
            foreach (var p in points) sum.Len(1, p);
            var metric = new Pb(); // Metric: name=1, sum=7.
            metric.Len(1, Encoding.UTF8.GetBytes(name));
            metric.Len(7, sum.ToArray());
            return metric.ToArray();
        }

        private static byte[] EncodeNumberDataPointInt(long value, (string, string)[] attrs)
        {
            var dp = new Pb();
            dp.Fixed64(6, unchecked((ulong)value)); // as_int (sfixed64) -> wire I64
            foreach (var (k, v) in attrs) dp.Len(7, EncodeKeyValue(k, v));
            return dp.ToArray();
        }

        private static byte[] EncodeNumberDataPointDouble(double value, (string, string)[] attrs)
        {
            var dp = new Pb();
            dp.Fixed64(4, unchecked((ulong)BitConverter.DoubleToInt64Bits(value))); // as_double -> wire I64
            foreach (var (k, v) in attrs) dp.Len(7, EncodeKeyValue(k, v));
            return dp.ToArray();
        }

        private static byte[] EncodeKeyValue(string key, string value)
        {
            var anyValue = new Pb();
            anyValue.Len(1, Encoding.UTF8.GetBytes(value)); // string_value = field 1
            var kv = new Pb();
            kv.Len(1, Encoding.UTF8.GetBytes(key));
            kv.Len(2, anyValue.ToArray());
            return kv.ToArray();
        }
    }

    /// <summary>Bare-bones protobuf message writer used only by the test encoder.</summary>
    private sealed class Pb
    {
        private readonly List<byte> _buf = new();

        public void Len(int field, byte[] bytes)
        {
            Tag(field, 2);
            Varint((ulong)bytes.Length);
            _buf.AddRange(bytes);
        }

        public void Fixed64(int field, ulong value)
        {
            Tag(field, 1);
            for (int i = 0; i < 8; i++) _buf.Add((byte)(value >> (8 * i)));
        }

        private void Tag(int field, int wire) => Varint((ulong)((field << 3) | wire));

        private void Varint(ulong v)
        {
            do
            {
                var b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                _buf.Add(b);
            } while (v != 0);
        }

        public byte[] ToArray() => _buf.ToArray();
    }
}
