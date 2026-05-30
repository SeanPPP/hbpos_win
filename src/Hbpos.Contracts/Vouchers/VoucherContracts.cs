namespace Hbpos.Contracts.Vouchers;

public sealed record StoreVoucherDto(
    string VoucherCode,
    string? StoreCode,
    int VoucherType,
    decimal Amount,
    decimal RemainingAmount,
    string Status,
    DateTimeOffset? ExpiredAt,
    string? CustomerCode,
    decimal DiscountRate,
    string? Remark);

public sealed record StoreVoucherQueryResponse(
    bool Found,
    StoreVoucherDto? Voucher,
    string? Message = null);

public sealed record StoreVoucherLockRequest(
    string StoreCode,
    string VoucherCode,
    decimal RequestedAmount);

public sealed record StoreVoucherLockResponse(
    string VoucherCode,
    decimal LockedAmount,
    string ReservationToken,
    DateTimeOffset ExpiresAt);

public sealed record StoreVoucherIssueRefundRequest(
    string StoreCode,
    decimal Amount,
    string CashierId,
    string? IdempotencyKey = null,
    string? OrderReference = null,
    string? Reason = null);

public sealed record StoreVoucherIssueRefundResponse(
    string VoucherCode,
    decimal Amount,
    decimal RemainingAmount,
    string Status,
    DateTimeOffset ExpiredAt);

public sealed record StoreVoucherIssueRequest(
    string? StoreCode,
    decimal Amount,
    string CashierId,
    string IdempotencyKey,
    DateTimeOffset? ExpiredAt = null,
    string? CustomerCode = null,
    string? Reason = null);

public sealed record StoreVoucherIssueResponse(
    string VoucherCode,
    decimal Amount,
    decimal RemainingAmount,
    string Status,
    DateTimeOffset ExpiredAt,
    string? StoreCode,
    string? CustomerCode);
