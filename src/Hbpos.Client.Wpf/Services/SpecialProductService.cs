using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

public interface ISpecialProductService
{
    Task<IReadOnlyList<SellableItemDto>> MarkSpecialProductAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default);
}

public sealed class SpecialProductService(
    ILocalCatalogRepository localCatalogRepository,
    ICatalogApiClient catalogApiClient) : ISpecialProductService
{
    public async Task<IReadOnlyList<SellableItemDto>> MarkSpecialProductAsync(
        string storeCode,
        string productCode,
        bool isSpecialProduct,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);

        var response = await catalogApiClient.MarkSpecialProductAsync(
            new CatalogSpecialProductMarkRequest(storeCode, productCode, isSpecialProduct),
            cancellationToken);

        var upsertItems = response.Items
            .Select(item => item.ToSellableItemDto())
            .ToArray();
        if (upsertItems.Length > 0)
        {
            await localCatalogRepository.UpsertSellableItemsAsync(upsertItems, cancellationToken);
        }

        await localCatalogRepository.UpdateSpecialProductFlagAsync(
            response.StoreCode,
            response.ProductCode,
            response.IsSpecialProduct,
            cancellationToken);

        return await localCatalogRepository.LoadSpecialProductItemsAsync(response.StoreCode, cancellationToken);
    }
}
