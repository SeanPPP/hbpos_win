using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Shared.Models;
using Hbpos.Api.Data;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Services;

public interface ICatalogService
{
    Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken cancellationToken);

    Task<SellableItemsResponse?> GetSellableItemsAsync(
        string storeCode,
        DateTimeOffset? since,
        CancellationToken cancellationToken);

    Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<CatalogCompareResponse?> CompareSellableItemsAsync(
        CatalogCompareRequest request,
        CancellationToken cancellationToken);

    Task<CatalogLookupResponse?> LookupSellableItemAsync(
        string storeCode,
        string? lookupCode,
        string? lookupCodeNormalized,
        CancellationToken cancellationToken);
}

public sealed class CatalogService(
    HbposSqlSugarContext dbContext,
    IPriceIndexBuilder priceIndexBuilder) : ICatalogService
{
    public async Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken cancellationToken)
    {
        var stores = await dbContext.MainDb.Queryable<Store>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .OrderBy(x => x.StoreCode)
            .Select(x => new StoreDto(x.StoreCode, x.StoreName, x.IsActive))
            .ToListAsync(cancellationToken);

        return stores;
    }

    public async Task<SellableItemsResponse?> GetSellableItemsAsync(
        string storeCode,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var index = await BuildSellableIndexAsync(storeCode, since, cancellationToken);
        return index is null
            ? null
            : new SellableItemsResponse(index.StoreCode, index.GeneratedAt, index.SellableItems);
    }

    public async Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
        string storeCode,
        DateTimeOffset? since,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var index = await BuildSellableIndexAsync(storeCode, since, cancellationToken);
        return index?.CatalogIndex.GetPage(cursor, pageSize);
    }

    public async Task<CatalogCompareResponse?> CompareSellableItemsAsync(
        CatalogCompareRequest request,
        CancellationToken cancellationToken)
    {
        var index = await BuildSellableIndexAsync(request.StoreCode, since: null, cancellationToken);
        return index?.CatalogIndex.Compare(request);
    }

    public async Task<CatalogLookupResponse?> LookupSellableItemAsync(
        string storeCode,
        string? lookupCode,
        string? lookupCodeNormalized,
        CancellationToken cancellationToken)
    {
        var index = await BuildSellableIndexAsync(storeCode, since: null, cancellationToken);
        return index?.CatalogIndex.Lookup(lookupCode, lookupCodeNormalized);
    }

    private async Task<SellableIndexBuildResult?> BuildSellableIndexAsync(
        string storeCode,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        Log($"build index start store={normalizedStoreCode} since={since?.ToString("O") ?? "<null>"}");

        var stepStopwatch = Stopwatch.StartNew();
        var store = await dbContext.MainDb.Queryable<Store>()
            .FirstAsync(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted, cancellationToken);
        stepStopwatch.Stop();
        Log($"store query store={normalizedStoreCode} found={store is not null} elapsedMs={stepStopwatch.ElapsedMilliseconds}");

        if (store is null)
        {
            totalStopwatch.Stop();
            Log($"build index store not found store={normalizedStoreCode} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return null;
        }

        stepStopwatch.Restart();
        var productEntities = await dbContext.MainDb.Queryable<Product>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        stepStopwatch.Stop();
        Log($"products query store={normalizedStoreCode} count={productEntities.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");
        var products = productEntities
            .Select(x => new ProductPriceRecord(
                x.ProductCode,
                x.ProductName,
                x.ItemNumber,
                x.Barcode,
                x.RetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt),
                x.ProductImage))
            .ToList();

        stepStopwatch.Restart();
        var storeRetailPriceEntities = await dbContext.MainDb.Queryable<StoreRetailPrice>()
            .Where(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        stepStopwatch.Stop();
        Log($"store retail prices query store={normalizedStoreCode} count={storeRetailPriceEntities.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");
        var storeRetailPrices = storeRetailPriceEntities
            .Select(x => new StoreRetailPriceRecord(
                x.ProductCode,
                x.StoreRetailPriceValue,
                ToOffset(x.UpdatedAt ?? x.CreatedAt)))
            .ToList();

        stepStopwatch.Restart();
        var multiCodeProductEntities = await dbContext.MainDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == normalizedStoreCode && x.IsActive && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        stepStopwatch.Stop();
        Log($"multi code products query store={normalizedStoreCode} count={multiCodeProductEntities.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");
        var multiCodeProducts = multiCodeProductEntities
            .Select(x => new StoreMultiCodeProductRecord(
                x.ProductCode,
                x.MultiCodeProductCode,
                x.MultiBarcode,
                x.MultiCodeRetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt)))
            .ToList();

        stepStopwatch.Restart();
        var clearancePriceEntities = await dbContext.MainDb.Queryable<StoreClearancePrice>()
            .Where(x => x.StoreCode == normalizedStoreCode && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        stepStopwatch.Stop();
        Log($"clearance prices query store={normalizedStoreCode} count={clearancePriceEntities.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");
        var clearancePrices = clearancePriceEntities
            .Select(x => new StoreClearancePriceRecord(
                x.ProductCode,
                x.ClearanceBarcode,
                x.ClearancePrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt)))
            .ToList();

        stepStopwatch.Restart();
        var setCodeEntities = await dbContext.MainDb.Queryable<ProductSetCode>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        stepStopwatch.Stop();
        Log($"set codes query store={normalizedStoreCode} count={setCodeEntities.Count} elapsedMs={stepStopwatch.ElapsedMilliseconds}");
        var setCodes = setCodeEntities
            .Select(x => new ProductSetCodeRecord(
                x.ProductCode,
                x.SetProductCode,
                x.SetBarcode,
                x.SetRetailPrice,
                ToOffset(x.UpdatedAt ?? x.CreatedAt)))
            .ToList();

        var input = new PriceIndexInput(
            since,
            products,
            storeRetailPrices,
            multiCodeProducts,
            clearancePrices,
            setCodes);

        var generatedAt = DateTimeOffset.UtcNow;
        stepStopwatch.Restart();
        var items = priceIndexBuilder.Build(store.StoreCode, input);
        stepStopwatch.Stop();
        totalStopwatch.Stop();
        Log($"build index completed store={store.StoreCode} items={items.Count} buildElapsedMs={stepStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        return new SellableIndexBuildResult(
            store.StoreCode,
            generatedAt,
            items,
            new CatalogSellableIndex(store.StoreCode, generatedAt, items));
    }

    private static DateTimeOffset? ToOffset(DateTime? value)
    {
        return value is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][CatalogService] {DateTimeOffset.Now:O} {message}");
    }

    private sealed record SellableIndexBuildResult(
        string StoreCode,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<SellableItemDto> SellableItems,
        CatalogSellableIndex CatalogIndex);
}

public sealed class CatalogSellableIndex
{
    private const int MaxPageSize = 1000;
    private readonly IReadOnlyDictionary<string, CatalogLookupItemDto> _itemsByNormalizedLookup;

    public CatalogSellableIndex(
        string storeCode,
        DateTimeOffset generatedAt,
        IEnumerable<SellableItemDto> items)
    {
        StoreCode = NormalizeStoreCode(storeCode);
        GeneratedAt = generatedAt;

        Items = items
            .Select(ToLookupItem)
            .Where(x => HasText(x.StoreCode) && HasText(x.LookupCodeNormalized))
            .GroupBy(x => x.LookupCodeNormalized, StringComparer.Ordinal)
            .Select(x => x
                .OrderByDescending(item => item.PriceSource)
                .ThenByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.LookupCode, StringComparer.Ordinal)
                .First())
            .OrderBy(x => x.LookupCodeNormalized, StringComparer.Ordinal)
            .ToArray();

        _itemsByNormalizedLookup = Items.ToDictionary(
            x => x.LookupCodeNormalized,
            StringComparer.Ordinal);
    }

    public string StoreCode { get; }

    public DateTimeOffset GeneratedAt { get; }

    public IReadOnlyList<CatalogLookupItemDto> Items { get; }

    public CatalogSyncPageResponse GetPage(string? cursor, int pageSize)
    {
        var normalizedCursor = NormalizeLookupCode(cursor);
        var take = Math.Clamp(pageSize, 1, MaxPageSize);
        var pageCandidates = Items
            .Where(x => string.IsNullOrEmpty(normalizedCursor)
                || string.Compare(x.LookupCodeNormalized, normalizedCursor, StringComparison.Ordinal) > 0)
            .Take(take + 1)
            .ToArray();

        var pageItems = pageCandidates.Take(take).ToArray();
        var hasMore = pageCandidates.Length > take;
        var nextCursor = hasMore && pageItems.Length > 0
            ? pageItems[^1].LookupCodeNormalized
            : null;

        return new CatalogSyncPageResponse(
            StoreCode,
            GeneratedAt,
            string.IsNullOrEmpty(normalizedCursor) ? null : normalizedCursor,
            pageItems,
            [],
            nextCursor,
            hasMore);
    }

    public CatalogCompareResponse Compare(CatalogCompareRequest request)
    {
        var localByLookup = new Dictionary<string, CatalogLocalLookupVersionDto>(StringComparer.Ordinal);

        foreach (var local in request.LocalLookups ?? [])
        {
            var normalizedLookup = NormalizeLookupCode(
                HasText(local.LookupCodeNormalized) ? local.LookupCodeNormalized : local.LookupCode);
            if (string.IsNullOrEmpty(normalizedLookup))
            {
                continue;
            }

            localByLookup.TryAdd(normalizedLookup, local);
        }

        var upserts = new List<CatalogLookupItemDto>();
        var deletes = new List<DeletedLookupDto>();

        foreach (var (normalizedLookup, local) in localByLookup)
        {
            if (!_itemsByNormalizedLookup.TryGetValue(normalizedLookup, out var current))
            {
                deletes.Add(new DeletedLookupDto(
                    StoreCode,
                    GetDeleteLookupCode(local, normalizedLookup),
                    normalizedLookup,
                    GeneratedAt));
                continue;
            }

            if (!HasMatchingVersion(local, current))
            {
                upserts.Add(current);
            }
        }

        return new CatalogCompareResponse(
            StoreCode,
            GeneratedAt,
            upserts,
            deletes,
            NextCursor: null,
            HasMore: false);
    }

    public CatalogLookupResponse Lookup(string? lookupCode, string? lookupCodeNormalized)
    {
        var normalizedLookup = NormalizeLookupCode(
            HasText(lookupCodeNormalized) ? lookupCodeNormalized : lookupCode);
        _itemsByNormalizedLookup.TryGetValue(normalizedLookup, out var item);

        return new CatalogLookupResponse(
            StoreCode,
            GetRequestedLookupCode(lookupCode, lookupCodeNormalized, normalizedLookup),
            normalizedLookup,
            item is not null,
            item);
    }

    public static string NormalizeLookupCode(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static CatalogLookupItemDto ToLookupItem(SellableItemDto item)
    {
        var storeCode = NormalizeStoreCode(item.StoreCode);
        var lookupCode = (item.LookupCode ?? string.Empty).Trim();
        var lookupCodeNormalized = NormalizeLookupCode(lookupCode);

        return new CatalogLookupItemDto(
            storeCode,
            item.ProductCode.Trim(),
            item.ReferenceCode?.Trim(),
            item.DisplayName.Trim(),
            lookupCode,
            lookupCodeNormalized,
            item.ItemNumber?.Trim(),
            item.Barcode?.Trim(),
            item.RetailPrice,
            item.PriceSource,
            item.PriceSourceLabel.Trim(),
            item.QuantityFactor,
            item.UpdatedAt,
            CreateRowVersion(
                storeCode,
                item.ProductCode.Trim(),
                item.ReferenceCode?.Trim() ?? string.Empty,
                item.DisplayName.Trim(),
                lookupCodeNormalized,
                item.ItemNumber?.Trim() ?? string.Empty,
                item.Barcode?.Trim() ?? string.Empty,
                item.RetailPrice,
                item.PriceSource,
                item.PriceSourceLabel.Trim(),
                item.QuantityFactor,
                item.ProductImage ?? string.Empty),
            item.ProductImage);
    }

    private static string CreateRowVersion(
        string storeCode,
        string productCode,
        string referenceCode,
        string displayName,
        string lookupCodeNormalized,
        string itemNumber,
        string barcode,
        decimal retailPrice,
        PriceSourceKind priceSource,
        string priceSourceLabel,
        decimal quantityFactor,
        string productImage)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, storeCode);
        AppendCanonical(builder, productCode);
        AppendCanonical(builder, referenceCode);
        AppendCanonical(builder, displayName);
        AppendCanonical(builder, lookupCodeNormalized);
        AppendCanonical(builder, itemNumber);
        AppendCanonical(builder, barcode);
        AppendCanonical(builder, retailPrice.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, ((int)priceSource).ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, priceSourceLabel);
        AppendCanonical(builder, quantityFactor.ToString("0.#############################", CultureInfo.InvariantCulture));
        AppendCanonical(builder, productImage);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    private static bool HasMatchingVersion(CatalogLocalLookupVersionDto local, CatalogLookupItemDto current)
    {
        var rowVersion = local.RowVersion?.Trim();
        if (!string.IsNullOrEmpty(rowVersion))
        {
            return string.Equals(rowVersion, current.RowVersion, StringComparison.OrdinalIgnoreCase);
        }

        return local.UpdatedAt.HasValue
            && current.UpdatedAt.HasValue
            && local.UpdatedAt.Value.ToUniversalTime() == current.UpdatedAt.Value.ToUniversalTime();
    }

    private static string GetDeleteLookupCode(CatalogLocalLookupVersionDto local, string normalizedLookup)
    {
        var lookupCode = local.LookupCode?.Trim();
        return string.IsNullOrEmpty(lookupCode) ? normalizedLookup : lookupCode;
    }

    private static string GetRequestedLookupCode(
        string? lookupCode,
        string? lookupCodeNormalized,
        string normalizedLookup)
    {
        var requestedLookupCode = lookupCode?.Trim();
        if (!string.IsNullOrEmpty(requestedLookupCode))
        {
            return requestedLookupCode;
        }

        var requestedLookupCodeNormalized = lookupCodeNormalized?.Trim();
        return !string.IsNullOrEmpty(requestedLookupCodeNormalized)
            ? requestedLookupCodeNormalized
            : normalizedLookup;
    }

    private static void AppendCanonical(StringBuilder builder, string value)
    {
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static bool HasText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }
}
