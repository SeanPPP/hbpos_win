using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Models;

public enum SuspendedOrderStatus
{
    Pending = 0,
    Recalled = 1,
    Canceled = 2
}

public sealed record SuspendedOrder(
    Guid SuspendedOrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset SuspendedAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    SuspendedOrderStatus Status,
    IReadOnlyList<SuspendedOrderLine> Lines);

public sealed record SuspendedOrderLine(
    Guid SuspendedOrderLineGuid,
    Guid SuspendedOrderGuid,
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal? DiscountPercent,
    decimal ActualAmount,
    PriceSourceKind PriceSource,
    string PriceSourceLabel);

public sealed record SuspendedOrderSummary(
    Guid SuspendedOrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SuspendedAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    SuspendedOrderStatus Status)
{
    public string ShortOrderId => SuspendedOrderGuid.ToString("N")[..8].ToUpperInvariant();

    public string SoldAtDisplay => SuspendedAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");

    public string StatusLabel => Status.ToString();
}
