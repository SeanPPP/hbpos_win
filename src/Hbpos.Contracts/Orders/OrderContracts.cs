using Hbpos.Contracts.Catalog;

namespace Hbpos.Contracts.Orders;

public enum PaymentMethodKind
{
    Cash = 1,
    Card = 2,
    Voucher = 3
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
    string? Reference,
    string? ReservationToken = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null);

public sealed record CardTransactionDto(
    string Processor,
    string? TxnRef,
    string? AuthCode,
    string? CardType,
    int? CardBin,
    string? MaskedCardNumber,
    string? MerchantId,
    string? ResponseCode,
    string? ResponseText,
    string? Stan,
    DateTimeOffset? BankDateTime,
    decimal Amount,
    string? ReceiptText);

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
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null);

public sealed record OrderReturnContextDto(
    OrderHistoryDetailsDto Order,
    IReadOnlyList<OrderReturnRecordDto> ReturnRecords);

public sealed record OrderReturnRecordDto(
    Guid ReturnDetailGuid,
    Guid? ReturnOrderGuid,
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderDetailGuid,
    string ProductCode,
    string? ReferenceCode,
    decimal ReturnQuantity,
    decimal ReturnAmount,
    string StaffCode,
    DateTimeOffset CreatedAt);

public sealed record OrderReturnRecordCreateRequest(
    Guid ReturnOrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    IReadOnlyList<OrderReturnRecordCreateLineDto> Lines);

public sealed record OrderReturnRecordCreateLineDto(
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderDetailGuid,
    string ProductCode,
    string? ReferenceCode,
    decimal ReturnQuantity,
    decimal ReturnAmount);

public sealed record OrderReturnRecordCreateResponse(
    Guid ReturnOrderGuid,
    IReadOnlyList<OrderReturnRecordDto> ReturnRecords);
