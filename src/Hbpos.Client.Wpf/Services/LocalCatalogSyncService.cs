using System.Diagnostics;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalCatalogSyncService
{
    Task<LocalCatalogSyncResult> FullSyncAsync(
        string storeCode,
        CancellationToken cancellationToken = default);
}

public sealed record LocalCatalogSyncResult(
    string StoreCode,
    int ComparePages,
    int RemotePages,
    int UpsertedCount,
    int DeletedCount);

public sealed class LocalCatalogSyncService(
    ILocalCatalogRepository localCatalogRepository,
    ICatalogApiClient catalogApiClient) : ILocalCatalogSyncService
{
    private const int PageSize = 500;

    public async Task<LocalCatalogSyncResult> FullSyncAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        var totalStopwatch = Stopwatch.StartNew();
        Log($"full sync start store={storeCode} pageSize={PageSize}");

        var comparePages = 0;
        var remotePages = 0;
        var upsertedCount = 0;
        var deletedCount = 0;
        string? afterLookupCodeNormalized = null;

        while (true)
        {
            var localPage = await localCatalogRepository.LoadSellableItemComparePageAsync(
                storeCode,
                afterLookupCodeNormalized,
                PageSize,
                cancellationToken);

            if (localPage.Count == 0)
            {
                Log($"local compare finished store={storeCode} pages={comparePages}");
                break;
            }

            afterLookupCodeNormalized = localPage[^1].LookupCodeNormalized;
            Log($"local compare page store={storeCode} page={comparePages + 1} rows={localPage.Count} after={afterLookupCodeNormalized}");
            var request = new CatalogCompareRequest(
                storeCode,
                localPage.Select(row => row.ToCompareVersion()).ToArray());
            var compareStopwatch = Stopwatch.StartNew();
            var response = await catalogApiClient.CompareSellableItemsAsync(request, cancellationToken);
            compareStopwatch.Stop();
            Log($"compare response store={storeCode} page={comparePages + 1} upsertedLookups={response.UpsertedLookups.Count} deletedLookups={response.DeletedLookups.Count} elapsedMs={compareStopwatch.ElapsedMilliseconds}");

            var applyStopwatch = Stopwatch.StartNew();
            var applied = await ApplyChangesAsync(
                storeCode,
                response.UpsertedLookups,
                response.DeletedLookups,
                cancellationToken);
            applyStopwatch.Stop();

            comparePages++;
            upsertedCount += applied.UpsertedCount;
            deletedCount += applied.DeletedCount;
            Log($"compare applied store={storeCode} page={comparePages} upserted={applied.UpsertedCount} deleted={applied.DeletedCount} elapsedMs={applyStopwatch.ElapsedMilliseconds}");
        }

        string? cursor = null;
        while (true)
        {
            Log($"download page request store={storeCode} page={remotePages + 1} cursor={cursor ?? "<start>"}");
            var downloadStopwatch = Stopwatch.StartNew();
            var response = await catalogApiClient.GetSellableItemsPageAsync(
                storeCode,
                cursor,
                PageSize,
                cancellationToken);
            downloadStopwatch.Stop();
            Log($"download page response store={storeCode} page={remotePages + 1} items={response.Items.Count} deletedLookups={response.DeletedLookups.Count} hasMore={response.HasMore} next={response.NextCursor ?? "<end>"} elapsedMs={downloadStopwatch.ElapsedMilliseconds}");

            var applyStopwatch = Stopwatch.StartNew();
            var applied = await ApplyChangesAsync(
                storeCode,
                response.Items,
                response.DeletedLookups,
                cancellationToken);
            applyStopwatch.Stop();

            remotePages++;
            upsertedCount += applied.UpsertedCount;
            deletedCount += applied.DeletedCount;
            Log($"download page applied store={storeCode} page={remotePages} upserted={applied.UpsertedCount} deleted={applied.DeletedCount} elapsedMs={applyStopwatch.ElapsedMilliseconds}");

            if (!response.HasMore)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(response.NextCursor))
            {
                throw new CatalogApiException("Catalog API indicated more pages but did not return a next cursor.");
            }

            cursor = response.NextCursor;
        }

        totalStopwatch.Stop();
        Log($"full sync completed store={storeCode} comparePages={comparePages} remotePages={remotePages} upserted={upsertedCount} deleted={deletedCount} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
        return new LocalCatalogSyncResult(
            storeCode,
            comparePages,
            remotePages,
            upsertedCount,
            deletedCount);
    }

    private async Task<(int UpsertedCount, int DeletedCount)> ApplyChangesAsync(
        string storeCode,
        IReadOnlyList<CatalogLookupItemDto> upsertedLookups,
        IReadOnlyList<DeletedLookupDto> deletedLookups,
        CancellationToken cancellationToken)
    {
        var upsertItems = upsertedLookups
            .Select(item => item.ToSellableItemDto())
            .ToArray();
        if (upsertItems.Length > 0)
        {
            await localCatalogRepository.UpsertSellableItemsAsync(upsertItems, cancellationToken);
        }

        var deletedCodes = deletedLookups
            .Select(GetDeleteLookupCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deletedCount = deletedCodes.Length == 0
            ? 0
            : await localCatalogRepository.DeleteByLookupCodesAsync(storeCode, deletedCodes, cancellationToken);

        return (upsertItems.Length, deletedCount);
    }

    private static string GetDeleteLookupCode(DeletedLookupDto deletedLookup)
    {
        return string.IsNullOrWhiteSpace(deletedLookup.LookupCodeNormalized)
            ? deletedLookup.LookupCode
            : deletedLookup.LookupCodeNormalized;
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("CatalogSync", message);
    }
}
