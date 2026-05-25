using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Models;

public sealed record LocalOrder(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<LocalOrderLine> Lines,
    IReadOnlyList<LocalPayment> Payments);

public sealed record LocalOrderLine(
    Guid OrderLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount,
    PriceSourceKind PriceSource);

public sealed record LocalOrderHistoryQuery(
    DateTimeOffset? SoldFrom = null,
    DateTimeOffset? SoldTo = null,
    string? DeviceCode = null,
    string? Keyword = null);

public sealed record LocalPayment(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference);

public sealed record CashCheckoutResult(LocalOrder Order, decimal TenderedAmount, decimal ChangeAmount);
