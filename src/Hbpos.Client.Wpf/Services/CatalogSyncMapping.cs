using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

internal static class CatalogSyncMapping
{
    public static SellableItemDto ToSellableItemDto(this CatalogLookupItemDto item)
    {
        var lookupCode = string.IsNullOrWhiteSpace(item.LookupCode)
            ? item.LookupCodeNormalized
            : item.LookupCode;

        return new SellableItemDto(
            item.StoreCode,
            item.ProductCode,
            item.ReferenceCode,
            item.DisplayName,
            lookupCode,
            item.ItemNumber,
            item.Barcode,
            item.RetailPrice,
            item.PriceSource,
            item.PriceSourceLabel,
            item.QuantityFactor,
            item.UpdatedAt,
            item.ProductImage,
            item.DiscountRate,
            item.IsSpecialProduct);
    }

    public static CatalogLocalLookupVersionDto ToCompareVersion(this LocalSellableItemCompareRow row)
    {
        return new CatalogLocalLookupVersionDto(
            row.StoreCode,
            row.LookupCodeNormalized,
            row.LookupCodeNormalized,
            row.SyncedAt,
            row.ContentHash);
    }
}
