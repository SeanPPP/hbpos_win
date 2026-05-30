using Hbpos.Contracts.Installments;

namespace Hbpos.Client.Wpf.Models;

public enum InstallmentWriteStatus
{
    Succeeded = 1,
    OnlineRequired = 2
}

public sealed record LocalInstallmentOrder(
    Guid OrderGuid,
    Guid InstallmentGuid,
    string InstallmentNumber,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    decimal TotalAmount,
    decimal MinimumDownPayment,
    decimal DownPaymentAmount,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentStatus Status,
    IReadOnlyList<InstallmentLineDto> Lines,
    IReadOnlyList<InstallmentPaymentDto> Payments,
    InstallmentPickupInfoDto? PickupInfo,
    string? Note = null,
    InstallmentCancellationInfoDto? CancellationInfo = null);

public sealed record InstallmentWriteResult<TResponse>(
    InstallmentWriteStatus Status,
    TResponse? Response = default,
    LocalInstallmentOrder? LocalOrder = null,
    string? Message = null)
{
    public static InstallmentWriteResult<TResponse> Success(TResponse response, LocalInstallmentOrder localOrder, string? message = null)
    {
        return new InstallmentWriteResult<TResponse>(InstallmentWriteStatus.Succeeded, response, localOrder, message);
    }

    public static InstallmentWriteResult<TResponse> OnlineRequired(string? message = null)
    {
        return new InstallmentWriteResult<TResponse>(InstallmentWriteStatus.OnlineRequired, default, null, message);
    }
}
