using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public delegate IPosTerminalWorkflowService PosTerminalWorkflowFactory(
    Func<string, string, CancellationToken, Task<RemoteLookupRefreshResult>> remoteLookupRefreshAsync,
    Func<CancellationToken, Task<IReadOnlyList<SellableItemDto>>> reloadCatalogAsync);

public interface IShellCatalogService
{
    Task ReplacePreviewCatalogAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogAsync(
        string storeCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
        string storeCode,
        bool forceFullDownload,
        IProgress<CatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ShellCatalogService(
    LocalSellableItemIndex priceIndex,
    ILocalCatalogRepository catalogRepository,
    ILocalCatalogSyncService catalogSync) : IShellCatalogService
{
    public async Task ReplacePreviewCatalogAsync(
        IEnumerable<SellableItemDto> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var itemList = items as IReadOnlyList<SellableItemDto> ?? items.ToArray();
        await catalogRepository.ReplaceSellableItemsAsync(itemList, cancellationToken);
        priceIndex.ReplaceAll(itemList);
    }

    public async Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var cachedItems = await catalogRepository.LoadSellableItemsAsync(storeCode, cancellationToken);
        priceIndex.ReplaceAll(cachedItems);
        return cachedItems;
    }

    public async Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
        string storeCode,
        bool forceFullDownload,
        IProgress<CatalogSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await catalogSync.FullSyncAsync(storeCode, cancellationToken, progress, forceFullDownload);
        return await LoadLocalCatalogAsync(storeCode, cancellationToken);
    }
}
