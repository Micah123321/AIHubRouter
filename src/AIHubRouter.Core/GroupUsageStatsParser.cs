using System.Globalization;
using System.Text.Json;

namespace AIHubRouter.Core;

internal static class GroupUsageStatsParser
{
    public static GroupUsageStatsPage Parse(JsonElement data, int requestedSampleLimit)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("items", out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new AIHubApiException("实时用量响应缺少 items 数组。" );
        }

        var items = new List<GroupUsageStat>();
        foreach (var element in itemsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !TryReadInt64(element, "group_id", out var groupId) ||
                groupId <= 0)
            {
                continue;
            }

            items.Add(new GroupUsageStat
            {
                Code = ReadString(element, "code"),
                Platform = ReadString(element, "platform"),
                RateMultiplier = ReadDouble(element, "rate_multiplier") ?? double.NaN,
                AverageTtftMs = ReadDouble(element, "avg_ttft_ms"),
                SampleCount = ReadInt32(element, "sample_count") ?? 0,
                LastSampleAt = ReadDateTimeOffset(element, "last_sample_at"),
                GroupId = groupId,
                Samples = ReadSamples(element)
            });
        }

        var sampleLimit = ReadInt32(data, "sample_limit") is > 0 and var parsedLimit
            ? parsedLimit
            : requestedSampleLimit;
        return new GroupUsageStatsPage
        {
            Items = items,
            Total = ReadInt32(data, "total") ?? items.Count,
            SampleLimit = sampleLimit
        };
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

    private static double? ReadDouble(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

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
        var number = ReadDouble(parent, propertyName);
        if (number is < long.MinValue or > long.MaxValue ||
            number is null ||
            number.Value != Math.Truncate(number.Value))
        {
            return false;
        }

        result = (long)number.Value;
        return true;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return timestamp;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var unixTime))
        {
            return null;
        }

        try
        {
            return Math.Abs(unixTime) >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixTime)
                : DateTimeOffset.FromUnixTimeSeconds(unixTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static List<GroupUsageSample> ReadSamples(JsonElement parent)
    {
        if (!parent.TryGetProperty("samples", out var samples) ||
            samples.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<GroupUsageSample>();
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var timestamp = ReadFirstDateTimeOffset(
                sample,
                "timestamp",
                "sample_at",
                "created_at",
                "called_at",
                "time");
            var latency = ReadFirstDouble(
                sample,
                "ttft_ms",
                "first_token_latency_ms",
                "firstTokenLatencyMs",
                "latency_ms");
            if (timestamp is not null && latency is > 0 && double.IsFinite(latency.Value))
            {
                result.Add(new GroupUsageSample
                {
                    Timestamp = timestamp,
                    FirstTokenLatencyMs = latency
                });
            }
        }

        return result;
    }

    private static double? ReadFirstDouble(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadDouble(parent, name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadFirstDateTimeOffset(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadDateTimeOffset(parent, name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }
}
