using Hbpos.Contracts.Catalog;

namespace Hbpos.Contracts.Orders;

public enum PaymentMethodKind
{
    Cash = 1,
    Card = 2,
    QrCode = 3
}

public sealed record OrderSyncRequest(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<OrderLineSyncDto> Lines,
    IReadOnlyList<PaymentSyncDto> Payments);

public sealed record OrderLineSyncDto(
    Guid OrderLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount,
    PriceSourceKind PriceSource,
    string? ItemNumber = null);

public sealed record PaymentSyncDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference);

public sealed record OrderSyncResponse(
    Guid OrderGuid,
    bool Accepted,
    bool AlreadySynced,
    string? Message = null);

public sealed record OrderHistoryQueryRequest(
    string StoreCode,
    string? DeviceCode = null,
    DateTimeOffset? SoldFrom = null,
    DateTimeOffset? SoldTo = null,
    string? Keyword = null,
    int Take = 100);

public sealed record OrderHistoryQueryResponse(
    IReadOnlyList<OrderHistorySummaryDto> Orders);

public sealed record OrderHistorySummaryDto(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    string PaymentSummary,
    string StatusLabel);

public sealed record OrderHistoryDetailsDto(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<OrderHistoryLineDto> Lines,
    IReadOnlyList<OrderHistoryPaymentDto> Payments);

public sealed record OrderHistoryLineDto(
    Guid OrderLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount);

public sealed record OrderHistoryPaymentDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference);
