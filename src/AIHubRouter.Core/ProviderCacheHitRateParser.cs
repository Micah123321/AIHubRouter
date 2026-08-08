using System.Globalization;
using System.Text.Json;

namespace AIHubRouter.Core;

internal static class ProviderCacheHitRateParser
{
    public static ProviderCacheHitRatePage Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new AIHubApiException("供应商缓存命中率响应缺少 items 数组。");
        }

        var groups = new Dictionary<long, (double Total, int Count)>();
        var modelHealthByGroup = new Dictionary<long, Dictionary<string, string>>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryReadInt64(item, "group_id", out var groupId) ||
                groupId <= 0)
            {
                continue;
            }

            if (TryReadModelHealth(item, out var modelHealth))
            {
                if (!modelHealthByGroup.TryGetValue(groupId, out var existingHealth))
                {
                    existingHealth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    modelHealthByGroup[groupId] = existingHealth;
                }

                foreach (var (model, status) in modelHealth)
                {
                    if (!existingHealth.TryGetValue(model, out var existingStatus) ||
                        status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                        !existingStatus.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        existingHealth[model] = status;
                    }
                }
            }

            if (TryReadCacheHitRate(item, out var cacheHitRate))
            {
                groups.TryGetValue(groupId, out var aggregate);
                groups[groupId] = (aggregate.Total + cacheHitRate, aggregate.Count + 1);
            }
        }

        return new ProviderCacheHitRatePage(
            ReadDateTimeOffset(data, "generated_at"),
            groups.ToDictionary(pair => pair.Key, pair => pair.Value.Total / pair.Value.Count))
        {
            ModelHealthByGroup = modelHealthByGroup.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)pair.Value)
        };
    }

    private static bool TryReadModelHealth(
        JsonElement item,
        out IReadOnlyDictionary<string, string> health)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        health = parsed;
        if (!item.TryGetProperty("model_health", out var raw) ||
            raw.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in raw.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var status = property.Value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(status))
            {
                parsed[property.Name] = status;
            }
        }

        return parsed.Count > 0;
    }

    private static bool TryReadCacheHitRate(JsonElement item, out double value)
    {
        value = 0;
        if (!item.TryGetProperty("cache_hit_rate", out var raw))
        {
            return false;
        }

        var isPercent = false;
        double parsed;
        if (raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out parsed))
        {
            isPercent = parsed > 1;
        }
        else if (raw.ValueKind == JsonValueKind.String)
        {
            var text = raw.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.EndsWith("%", StringComparison.Ordinal))
            {
                isPercent = true;
                text = text[..^1].Trim();
            }

            if (!double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed))
            {
                return false;
            }

            isPercent |= parsed > 1;
        }
        else
        {
            return false;
        }

        if (!double.IsFinite(parsed) || parsed < 0 || parsed > (isPercent ? 100 : 1))
        {
            return false;
        }

        value = isPercent ? parsed / 100 : parsed;
        return double.IsFinite(value) && value is >= 0 and <= 1;
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

        if (!TryReadDouble(value, out var unixTime) ||
            !double.IsFinite(unixTime) ||
            unixTime != Math.Truncate(unixTime) ||
            unixTime is < long.MinValue or > long.MaxValue)
        {
            return null;
        }

        try
        {
            var integer = (long)unixTime;
            return integer >= 100_000_000_000 || integer <= -100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(integer)
                : DateTimeOffset.FromUnixTimeSeconds(integer);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryReadDouble(JsonElement value, out double result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result))
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.String &&
            double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
    }
}
