using System.Globalization;
using System.Text.Json;

namespace AIHubRouter.Core;

internal static class ProviderSeriesParser
{
    public static ProviderSeriesPage Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new AIHubApiException("供应商序列响应缺少 items 数组。");
        }

        var groups = new Dictionary<long, MetricsAccumulator>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryReadInt64(item, "group_id", out var groupId) ||
                groupId <= 0)
            {
                continue;
            }

            if (!groups.TryGetValue(groupId, out var accumulator))
            {
                accumulator = new MetricsAccumulator(groupId);
                groups.Add(groupId, accumulator);
            }

            ReadProbeSamples(item, accumulator);
            ReadUserTtft(item, accumulator);
        }

        return new ProviderSeriesPage(
            ReadDateTimeOffset(data, "generated_at"),
            ReadString(data, "range"),
            groups.ToDictionary(pair => pair.Key, pair => pair.Value.Build()));
    }

    private static void ReadProbeSamples(JsonElement item, MetricsAccumulator accumulator)
    {
        if (!item.TryGetProperty("probe", out var samples) ||
            samples.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = sample.EnumerateArray().Take(3).ToArray();
            if (values.Length < 2 ||
                ReadDateTimeOffset(values[0]) is not { } timestamp ||
                ReadBoolean(values[1]) is not { } succeeded)
            {
                continue;
            }

            accumulator.AddProbe(timestamp, succeeded, values.Length == 3
                ? ReadDouble(values[2])
                : null);
        }
    }

    private static void ReadUserTtft(JsonElement item, MetricsAccumulator accumulator)
    {
        if (!item.TryGetProperty("user_ttft", out var userTtft))
        {
            return;
        }

        if (userTtft.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in userTtft.EnumerateArray())
            {
                ReadUserTtftBucket(bucket, accumulator);
            }

            return;
        }

        ReadUserTtftBucket(userTtft, accumulator);
    }

    private static void ReadUserTtftBucket(
        JsonElement bucket,
        MetricsAccumulator accumulator)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var sampleCount = ReadInt32(bucket, "sample_count");
        var average = ReadDouble(bucket, "avg_ttft_ms");
        if (ReadBooleanProperty(bucket, "has_data") == false ||
            sampleCount is not > 0 ||
            average is not > 0 ||
            !double.IsFinite(average.Value))
        {
            return;
        }

        accumulator.AddUserTtft(
            average.Value,
            sampleCount.Value,
            ReadDateTimeOffset(bucket, "at"));
    }

    private static string ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static double? ReadDouble(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) ? ReadDouble(value) : null;

    private static double? ReadDouble(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   value.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : null;
    }

    private static int? ReadInt32(JsonElement parent, string propertyName)
    {
        var number = ReadDouble(parent, propertyName);
        return number is >= int.MinValue and <= int.MaxValue &&
               number.Value == Math.Truncate(number.Value)
            ? (int)number.Value
            : null;
    }

    private static bool TryReadInt64(JsonElement parent, string propertyName, out long result)
    {
        result = 0;
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out result);
        }

        return value.ValueKind == JsonValueKind.String &&
            long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result);
    }

    private static bool? ReadBooleanProperty(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) ? ReadBoolean(value) : null;

    private static bool? ReadBoolean(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        var number = ReadDouble(value);
        if (number == 1)
        {
            return true;
        }

        if (number == 0)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String &&
               bool.TryParse(value.GetString(), out var boolean)
            ? boolean
            : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(
        JsonElement parent,
        string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
            ? ReadDateTimeOffset(value)
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return timestamp;
        }

        var unixTime = ReadDouble(value);
        if (unixTime is null ||
            !double.IsFinite(unixTime.Value) ||
            unixTime.Value != Math.Truncate(unixTime.Value) ||
            unixTime is < long.MinValue or > long.MaxValue)
        {
            return null;
        }

        try
        {
            var integer = (long)unixTime.Value;
            return Math.Abs(integer) >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(integer)
                : DateTimeOffset.FromUnixTimeSeconds(integer);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private sealed class MetricsAccumulator(long groupId)
    {
        private int _probeCount;
        private int _successfulProbeCount;
        private double _probeLatencyTotal;
        private int _probeLatencyCount;
        private double _userTtftWeightedTotal;
        private int _userTtftSampleCount;
        private DateTimeOffset? _latestSampleAt;

        public void AddProbe(DateTimeOffset timestamp, bool succeeded, double? latencyMs)
        {
            _probeCount++;
            _latestSampleAt = Latest(_latestSampleAt, timestamp);
            if (!succeeded)
            {
                return;
            }

            _successfulProbeCount++;
            if (latencyMs is > 0 && double.IsFinite(latencyMs.Value))
            {
                _probeLatencyTotal += latencyMs.Value;
                _probeLatencyCount++;
            }
        }

        public void AddUserTtft(
            double averageTtftMs,
            int sampleCount,
            DateTimeOffset? timestamp)
        {
            _userTtftWeightedTotal += averageTtftMs * sampleCount;
            _userTtftSampleCount += sampleCount;
            if (timestamp is { } value)
            {
                _latestSampleAt = Latest(_latestSampleAt, value);
            }
        }

        public ProviderSeriesMetrics Build() => new(
            groupId,
            _probeCount > 0 ? (double)_successfulProbeCount / _probeCount : null,
            _probeLatencyCount > 0 ? _probeLatencyTotal / _probeLatencyCount : null,
            _userTtftSampleCount > 0
                ? _userTtftWeightedTotal / _userTtftSampleCount
                : null,
            _probeCount,
            _userTtftSampleCount,
            _latestSampleAt);

        private static DateTimeOffset Latest(
            DateTimeOffset? current,
            DateTimeOffset candidate) =>
            current is null || candidate > current ? candidate : current.Value;
    }
}
