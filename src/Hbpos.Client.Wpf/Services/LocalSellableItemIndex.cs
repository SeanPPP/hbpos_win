using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public sealed class LocalSellableItemIndex
{
    private readonly object _gate = new();
    private readonly List<SellableItemDto> _items = [];
    private readonly Dictionary<ExactLookupKey, List<SellableItemDto>> _exactLookupIndex = [];
    private readonly Dictionary<ExactLookupKey, List<SellableItemDto>> _metadataLookupIndex = [];

    public IReadOnlyList<SellableItemDto> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public void ReplaceAll(IEnumerable<SellableItemDto> items)
    {
        var orderedItems = items
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var exactLookupIndex = new Dictionary<ExactLookupKey, List<SellableItemDto>>();
        var metadataLookupIndex = new Dictionary<ExactLookupKey, List<SellableItemDto>>();

        foreach (var item in orderedItems)
        {
            AddLookup(exactLookupIndex, item, item.LookupCode);
            AddLookup(metadataLookupIndex, item, item.Barcode);
            AddLookup(metadataLookupIndex, item, item.ItemNumber);
            AddLookup(metadataLookupIndex, item, item.ProductCode);
        }

        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(orderedItems);
            _exactLookupIndex.Clear();
            foreach (var pair in exactLookupIndex)
            {
                _exactLookupIndex[pair.Key] = pair.Value;
            }

            _metadataLookupIndex.Clear();
            foreach (var pair in metadataLookupIndex)
            {
                _metadataLookupIndex[pair.Key] = pair.Value;
            }
        }
    }

    public IReadOnlyList<SellableItemDto> Search(string query, int take = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalized = Normalize(query);
        SellableItemDto[] snapshot;
        lock (_gate)
        {
            snapshot = _items.ToArray();
        }

        return snapshot
            .Select(item => new { Item = item, Rank = Rank(item, normalized) })
            .Where(match => match.Rank < int.MaxValue)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(take)
            .Select(match => match.Item)
            .ToList();
    }

    public IReadOnlyList<SellableItemDto> FindExactMatches(string storeCode, string query)
    {
        var normalizedStoreCode = Normalize(storeCode);
        var normalizedQuery = Normalize(query);
        if (normalizedStoreCode.Length == 0 || normalizedQuery.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            return _exactLookupIndex.TryGetValue(new ExactLookupKey(normalizedStoreCode, normalizedQuery), out var matches)
                ? matches.ToArray()
                : [];
        }
    }

    internal IReadOnlyList<SellableItemDto> FindMetadataExactMatches(string storeCode, string query)
    {
        var normalizedStoreCode = Normalize(storeCode);
        var normalizedQuery = Normalize(query);
        if (normalizedStoreCode.Length == 0 || normalizedQuery.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            return _metadataLookupIndex.TryGetValue(new ExactLookupKey(normalizedStoreCode, normalizedQuery), out var matches)
                ? matches.ToArray()
                : [];
        }
    }

    private static void AddLookup(
        Dictionary<ExactLookupKey, List<SellableItemDto>> index,
        SellableItemDto item,
        string? lookupCode)
    {
        var normalizedLookupCode = Normalize(lookupCode);
        if (normalizedLookupCode.Length == 0)
        {
            return;
        }

        var key = new ExactLookupKey(Normalize(item.StoreCode), normalizedLookupCode);
        if (!index.TryGetValue(key, out var matches))
        {
            matches = [];
            index[key] = matches;
        }

        if (!matches.Contains(item))
        {
            matches.Add(item);
        }
    }

    private static int Rank(SellableItemDto item, string query)
    {
        if (EqualsNormalized(item.Barcode, query) || EqualsNormalized(item.LookupCode, query))
        {
            return 0;
        }

        if (EqualsNormalized(item.ItemNumber, query) || EqualsNormalized(item.ProductCode, query))
        {
            return 1;
        }

        if (ContainsNormalized(item.DisplayName, query))
        {
            return 2;
        }

        if (ContainsNormalized(item.LookupCode, query) || ContainsNormalized(item.ReferenceCode, query))
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static bool EqualsNormalized(string? value, string query)
    {
        return Normalize(value) == query;
    }

    private static bool ContainsNormalized(string? value, string query)
    {
        return Normalize(value).Contains(query, StringComparison.Ordinal);
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private sealed record ExactLookupKey(string StoreCode, string LookupCode);
}
