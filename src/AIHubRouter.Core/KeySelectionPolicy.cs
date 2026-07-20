namespace AIHubRouter.Core;

public static class KeySelectionPolicy
{
    public static IReadOnlyList<long> Resolve(
        bool initialized,
        IEnumerable<long> savedIds,
        IReadOnlyList<ApiKeyInfo> keys)
    {
        ArgumentNullException.ThrowIfNull(savedIds);
        ArgumentNullException.ThrowIfNull(keys);

        if (!initialized)
        {
            var firstActive = keys.FirstOrDefault(key =>
                key.Status.Equals("active", StringComparison.OrdinalIgnoreCase));
            return firstActive is null ? [] : [firstActive.Id];
        }

        var availableIds = keys.Select(key => key.Id).ToHashSet();
        return savedIds.Where(availableIds.Contains).Distinct().ToArray();
    }
}
