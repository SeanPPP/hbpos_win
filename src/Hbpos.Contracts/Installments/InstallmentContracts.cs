using Hbpos.Contracts.Orders;

namespace Hbpos.Contracts.Installments;

public enum InstallmentStatus
{
    Active = 1,
    PaidOff = 2,
    PickedUp = 3,
    Cancelled = 4
}

public enum InstallmentPaymentStatus
{
    Recorded = 1,
    Voided = 2
}

public enum InstallmentCancellationKind
{
    RefundCancel = 1,
    VoidCancel = 2
}

public sealed record InstallmentLineDto(
    Guid InstallmentLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount,
    string? ItemNumber = null);

public sealed record InstallmentPaymentCommandDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    string? ReservationToken = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null);

public sealed record InstallmentCreateRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset CreatedAt,
    decimal TotalAmount,
    decimal DownPaymentAmount,
    IReadOnlyList<InstallmentLineDto> Lines,
    InstallmentPaymentCommandDto DownPayment,
    string CustomerName,
    string CustomerPhone,
    string? Note = null);

public sealed record InstallmentCreateResponse(
    Guid InstallmentGuid,
    string InstallmentNumber,
    InstallmentStatus Status,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentDetailsDto Details,
    bool AlreadyExists = false,
    string? Message = null);

public sealed record InstallmentAppendPaymentRequest(
    Guid InstallmentGuid,
    Guid PaymentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    decimal Amount,
    PaymentMethodKind Method,
    string? Reference,
    string? ReservationToken = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null);

public sealed record InstallmentAppendPaymentResponse(
    Guid InstallmentGuid,
    Guid PaymentGuid,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentStatus Status,
    InstallmentDetailsDto Details,
    bool AlreadyRecorded = false,
    string? Message = null);

public sealed record InstallmentConfirmPickupRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset ConfirmedAt,
    string? Note = null);

public sealed record InstallmentConfirmPickupResponse(
    Guid InstallmentGuid,
    InstallmentStatus Status,
    DateTimeOffset PickedUpAt,
    InstallmentDetailsDto Details,
    bool AlreadyConfirmed = false);

public sealed record InstallmentRefundPaymentCommandDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null);

public sealed record InstallmentCancelRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset CancelledAt,
    IReadOnlyList<InstallmentRefundPaymentCommandDto> Refunds,
    string? Reason = null,
    string? IdempotencyKey = null);

public sealed record InstallmentCancelResponse(
    Guid InstallmentGuid,
    InstallmentStatus Status,
    InstallmentDetailsDto Details,
    bool AlreadyCancelled = false,
    string? Message = null);

public sealed record InstallmentVoidRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset VoidedAt,
    string? Reason = null,
    string? IdempotencyKey = null);

public sealed record InstallmentVoidResponse(
    Guid InstallmentGuid,
    InstallmentStatus Status,
    InstallmentDetailsDto Details,
    bool AlreadyVoided = false,
    string? Message = null);

public sealed record InstallmentHistoryQueryRequest(
    string StoreCode,
    string? DeviceCode = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    string? Keyword = null,
    InstallmentStatus? Status = null,
    int Take = 100);

public sealed record InstallmentHistoryQueryResponse(
    IReadOnlyList<InstallmentSummaryDto> Orders);

public sealed record InstallmentSummaryDto(
    Guid InstallmentGuid,
    string InstallmentNumber,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset CreatedAt,
    decimal TotalAmount,
    decimal DownPaymentAmount,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentStatus Status,
    DateTimeOffset UpdatedAt);

public sealed record InstallmentDetailsDto(
    Guid InstallmentGuid,
    string InstallmentNumber,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset CreatedAt,
    decimal TotalAmount,
    decimal MinimumDownPayment,
    decimal DownPaymentAmount,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentStatus Status,
    IReadOnlyList<InstallmentLineDto> Lines,
    IReadOnlyList<InstallmentPaymentDto> Payments,
    InstallmentPickupInfoDto? PickupInfo,
    InstallmentCancellationInfoDto? CancellationInfo = null,
    string? Note = null);

public sealed record InstallmentPaymentDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    InstallmentPaymentStatus Status,
    DateTimeOffset RecordedAt,
    string CashierId,
    string DeviceCode,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null);

public sealed record InstallmentPickupInfoDto(
    DateTimeOffset PickedUpAt,
    string PickedUpBy,
    string? Note = null);

public sealed record InstallmentCancellationInfoDto(
    InstallmentCancellationKind Kind,
    DateTimeOffset CancelledAt,
    string CancelledBy,
    string? Reason = null,
    string? IdempotencyKey = null);
