namespace Hbpos.Contracts.Catalog;

public enum PriceSourceKind
{
    ProductBase = 0,
    StoreRetailPrice = 1,
    ProductSetCode = 2,
    StoreMultiCodeProduct = 3,
    StoreClearancePrice = 4
}

public sealed record StoreDto(
    string StoreCode,
    string StoreName,
    bool IsActive);

public sealed record SellableItemDto(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? Barcode,
    decimal RetailPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    decimal QuantityFactor,
    DateTimeOffset? UpdatedAt,
    string? ProductImage = null,
    decimal? DiscountRate = null,
    bool IsSpecialProduct = false);

public sealed record SellableItemsResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SellableItemDto> Items);

/// <summary>
/// Local sync key: StoreCode + LookupCodeNormalized.
/// LookupCode is the actual sale/search code: barcode, item number, multi code, set code, or clearance code.
/// Suggested backend index only: StoreCode + LookupCodeNormalized, no automatic DDL.
/// </summary>
public sealed record CatalogLookupItemDto(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string LookupCodeNormalized,
    string? ItemNumber,
    string? Barcode,
    decimal RetailPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    decimal QuantityFactor,
    DateTimeOffset? UpdatedAt,
    string? RowVersion,
    string? ProductImage = null,
    decimal? DiscountRate = null,
    bool IsSpecialProduct = false);

public sealed record CatalogLocalLookupVersionDto(
    string StoreCode,
    string LookupCode,
    string LookupCodeNormalized,
    DateTimeOffset? UpdatedAt,
    string? RowVersion);

public sealed record CatalogCompareRequest(
    string StoreCode,
    IReadOnlyList<CatalogLocalLookupVersionDto> LocalLookups);

/// <summary>
/// Exact delete tombstone for StoreCode + LookupCode/LookupCodeNormalized; never implies store/table clearing.
/// </summary>
public sealed record DeletedLookupDto(
    string StoreCode,
    string LookupCode,
    string LookupCodeNormalized,
    DateTimeOffset? DeletedAt);

public sealed record CatalogCompareResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CatalogLookupItemDto> UpsertedLookups,
    IReadOnlyList<DeletedLookupDto> DeletedLookups,
    string? NextCursor,
    bool HasMore);

public sealed record CatalogSyncPageResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    string? Cursor,
    IReadOnlyList<CatalogLookupItemDto> Items,
    IReadOnlyList<DeletedLookupDto> DeletedLookups,
    string? NextCursor,
    bool HasMore,
    int TotalCount);

public sealed record CatalogLookupResponse(
    string StoreCode,
    string LookupCode,
    string LookupCodeNormalized,
    bool Found,
    CatalogLookupItemDto? Item);

public sealed record CatalogSpecialProductMarkRequest(
    string StoreCode,
    string ProductCode,
    bool IsSpecialProduct);

public sealed record CatalogSpecialProductMarkResponse(
    string StoreCode,
    string ProductCode,
    bool IsSpecialProduct,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CatalogLookupItemDto> Items);
