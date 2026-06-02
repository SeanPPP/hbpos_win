using System.Text.Json;
using Hbpos.Api.Data;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IInstallmentService
{
    Task<InstallmentCreateResponse> CreateAsync(
        InstallmentCreateRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentCancelResponse> CancelAsync(
        InstallmentCancelRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentVoidResponse> VoidAsync(
        InstallmentVoidRequest request,
        CancellationToken cancellationToken);
}

public interface IInstallmentHistoryService
{
    Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken);
}

public sealed class InstallmentService(
    IInstallmentRepository repository,
    IStoreVoucherReservationService reservationService,
    TimeProvider? timeProvider = null) : IInstallmentService, IInstallmentHistoryService
{
    public const decimal MinimumDownPaymentAmount = 20m;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<InstallmentCreateResponse> CreateAsync(
        InstallmentCreateRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeCreateRequest(request);
        var existing = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken);
        if (existing is not null)
        {
            return new InstallmentCreateResponse(
                existing.InstallmentGuid,
                existing.InstallmentNumber,
                existing.Status,
                existing.PaidAmount,
                existing.BalanceAmount,
                existing,
                AlreadyExists: true,
                Message: "AlreadyExists");
        }

        ValidateDownPayment(normalized.TotalAmount, normalized.DownPaymentAmount);
        if (normalized.DownPayment.Amount != normalized.DownPaymentAmount)
        {
            throw new InvalidOperationException("Down payment amount must match the payment amount.");
        }

        await ValidateVoucherPaymentAsync(
            normalized.StoreCode,
            normalized.DownPayment.Method,
            normalized.DownPayment.Reference,
            normalized.DownPayment.ReservationToken,
            normalized.DownPayment.Amount,
            cancellationToken);

        var createdAt = normalized.CreatedAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.CreatedAt.ToUniversalTime();
        var paidAmount = RoundCurrency(normalized.DownPaymentAmount);
        var balanceAmount = RoundCurrency(normalized.TotalAmount - paidAmount);
        var status = balanceAmount == 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active;
        var installmentNumber = CreateInstallmentNumber(normalized.StoreCode, normalized.InstallmentGuid);
        var details = new InstallmentDetailsDto(
            normalized.InstallmentGuid,
            installmentNumber,
            normalized.StoreCode,
            normalized.DeviceCode,
            normalized.CashierId,
            normalized.CashierName,
            normalized.CustomerName,
            normalized.CustomerPhone,
            createdAt,
            normalized.TotalAmount,
            MinimumDownPaymentAmount,
            normalized.DownPaymentAmount,
            paidAmount,
            balanceAmount,
            status,
            normalized.Lines,
            [MapPayment(normalized.DownPayment, normalized.CashierId, normalized.DeviceCode, createdAt)],
            PickupInfo: null,
            CancellationInfo: null,
            normalized.Note);

        await repository.CreateAsync(details, cancellationToken);
        if (normalized.DownPayment.Method == PaymentMethodKind.Voucher)
        {
            await reservationService.ConsumeAsync(normalized.DownPayment.ReservationToken!, cancellationToken);
        }

        return new InstallmentCreateResponse(
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.Status,
            details.PaidAmount,
            details.BalanceAmount,
            details);
    }

    public async Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(
        InstallmentAppendPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePaymentRequest(request);
        var existingPayment = await repository.FindPaymentAsync(normalized.PaymentGuid, cancellationToken);
        if (existingPayment is null && !string.IsNullOrWhiteSpace(normalized.IdempotencyKey))
        {
            // 幂等键只在当前分期单内复用，避免同店同设备的其他分期单误命中。
            existingPayment = await repository.FindPaymentByIdempotencyKeyAsync(
                normalized.InstallmentGuid,
                normalized.IdempotencyKey,
                cancellationToken);
        }

        if (existingPayment is not null)
        {
            var existingDetails = await repository.GetDetailsAsync(existingPayment.InstallmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            ValidateInstallmentScope(existingDetails, normalized.StoreCode, normalized.DeviceCode);
            return new InstallmentAppendPaymentResponse(
                existingPayment.InstallmentGuid,
                existingPayment.Payment.PaymentGuid,
                existingDetails.PaidAmount,
                existingDetails.BalanceAmount,
                existingDetails.Status,
                existingDetails,
                AlreadyRecorded: true,
                Message: "AlreadyRecorded");
        }

        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentScope(details, normalized.StoreCode, normalized.DeviceCode);
        if (details.Status == InstallmentStatus.PickedUp)
        {
            throw new InvalidOperationException("Picked up installment cannot accept payments.");
        }

        if (details.Status == InstallmentStatus.Cancelled)
        {
            throw new InvalidOperationException("Cancelled installment cannot accept payments.");
        }

        if (details.BalanceAmount <= 0m)
        {
            throw new InvalidOperationException("Installment is already paid off.");
        }

        if (normalized.Amount <= 0m)
        {
            throw new InvalidOperationException("Payment amount must be greater than zero.");
        }

        if (normalized.Method != PaymentMethodKind.Cash && normalized.Amount > details.BalanceAmount)
        {
            throw new InvalidOperationException("Non-cash payment cannot exceed the balance amount.");
        }

        var appliedAmount = RoundCurrency(Math.Min(normalized.Amount, details.BalanceAmount));
        await ValidateVoucherPaymentAsync(
            details.StoreCode,
            normalized.Method,
            normalized.Reference,
            normalized.ReservationToken,
            appliedAmount,
            cancellationToken);

        var recordedAt = _timeProvider.GetUtcNow();
        var payment = new InstallmentPaymentDto(
            normalized.PaymentGuid,
            normalized.Method,
            appliedAmount,
            normalized.Reference,
            InstallmentPaymentStatus.Recorded,
            recordedAt,
            normalized.CashierId,
            normalized.DeviceCode,
            normalized.CardTransactions,
            normalized.IdempotencyKey,
            normalized.ReservationToken);

        var updated = await repository.AppendPaymentAsync(
            details.InstallmentGuid,
            payment,
            cancellationToken);
        if (normalized.Method == PaymentMethodKind.Voucher)
        {
            await reservationService.ConsumeAsync(normalized.ReservationToken!, cancellationToken);
        }

        return new InstallmentAppendPaymentResponse(
            updated.InstallmentGuid,
            payment.PaymentGuid,
            updated.PaidAmount,
            updated.BalanceAmount,
            updated.Status,
            updated);
    }

    public async Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(
        InstallmentConfirmPickupRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePickupRequest(request);
        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentScope(details, normalized.StoreCode, normalized.DeviceCode);
        if (details.Status == InstallmentStatus.PickedUp && details.PickupInfo is not null)
        {
            return new InstallmentConfirmPickupResponse(
                details.InstallmentGuid,
                details.Status,
                details.PickupInfo.PickedUpAt,
                details,
                AlreadyConfirmed: true);
        }

        if (details.Status != InstallmentStatus.PaidOff || details.BalanceAmount != 0m)
        {
            throw new InvalidOperationException("Installment must be paid off before pickup.");
        }

        var confirmedAt = normalized.ConfirmedAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.ConfirmedAt.ToUniversalTime();
        var updated = await repository.ConfirmPickupAsync(
            normalized.InstallmentGuid,
            confirmedAt,
            normalized.CashierName,
            normalized.Note,
            cancellationToken);

        return new InstallmentConfirmPickupResponse(
            updated.InstallmentGuid,
            updated.Status,
            updated.PickupInfo!.PickedUpAt,
            updated);
    }

    public async Task<InstallmentCancelResponse> CancelAsync(
        InstallmentCancelRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeCancelRequest(request);
        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentScope(details, normalized.StoreCode, normalized.DeviceCode);
        if (TryCreateExistingCancellationResponse(details, InstallmentCancellationKind.RefundCancel, out var existing))
        {
            return new InstallmentCancelResponse(details.InstallmentGuid, details.Status, details, AlreadyCancelled: true, existing);
        }

        ValidateCancellable(details);
        var refunds = NormalizeAndValidateRefunds(details, normalized);
        var cancelledAt = normalized.CancelledAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.CancelledAt.ToUniversalTime();
        var cancellationInfo = new InstallmentCancellationInfoDto(
            InstallmentCancellationKind.RefundCancel,
            cancelledAt,
            normalized.CashierName,
            normalized.Reason,
            normalized.IdempotencyKey);
        var updated = await repository.CancelWithRefundAsync(
            normalized.InstallmentGuid,
            refunds.Select(refund => MapRefundPayment(refund, normalized.CashierId, normalized.DeviceCode, cancelledAt)).ToList(),
            cancellationInfo,
            cancellationToken);
        return new InstallmentCancelResponse(updated.InstallmentGuid, updated.Status, updated);
    }

    public async Task<InstallmentVoidResponse> VoidAsync(
        InstallmentVoidRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeVoidRequest(request);
        var details = await repository.GetDetailsAsync(normalized.InstallmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
        ValidateInstallmentScope(details, normalized.StoreCode, normalized.DeviceCode);
        if (TryCreateExistingCancellationResponse(details, InstallmentCancellationKind.VoidCancel, out var existing))
        {
            return new InstallmentVoidResponse(details.InstallmentGuid, details.Status, details, AlreadyVoided: true, existing);
        }

        ValidateCancellable(details);
        var voidedAt = normalized.VoidedAt == default
            ? _timeProvider.GetUtcNow()
            : normalized.VoidedAt.ToUniversalTime();
        var cancellationInfo = new InstallmentCancellationInfoDto(
            InstallmentCancellationKind.VoidCancel,
            voidedAt,
            normalized.CashierName,
            normalized.Reason,
            normalized.IdempotencyKey);
        var updated = await repository.VoidAsync(
            normalized.InstallmentGuid,
            cancellationInfo,
            cancellationToken);
        return new InstallmentVoidResponse(updated.InstallmentGuid, updated.Status, updated);
    }

    public Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeOptional(request.DeviceCode),
            Keyword = NormalizeOptional(request.Keyword),
            Take = Math.Clamp(request.Take, 1, 200)
        };
        return repository.QueryAsync(normalized, cancellationToken);
    }

    public Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        return repository.GetDetailsAsync(installmentGuid, cancellationToken);
    }

    private static InstallmentCreateRequest NormalizeCreateRequest(InstallmentCreateRequest request)
    {
        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("Installment lines are required.");
        }

        var lines = request.Lines.Select(NormalizeLine).ToList();
        var normalizedTotal = RoundCurrency(request.TotalAmount);
        var lineTotal = RoundCurrency(lines.Sum(line => line.ActualAmount));
        if (lineTotal != normalizedTotal)
        {
            throw new InvalidOperationException("Installment line total must match total amount.");
        }

        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            CustomerName = NormalizeRequired(request.CustomerName, "Customer name is required."),
            CustomerPhone = NormalizeRequired(request.CustomerPhone, "Customer phone is required."),
            Note = NormalizeOptional(request.Note),
            TotalAmount = normalizedTotal,
            DownPaymentAmount = RoundCurrency(request.DownPaymentAmount),
            Lines = lines,
            DownPayment = request.DownPayment with
            {
                Reference = NormalizeOptional(request.DownPayment.Reference),
                ReservationToken = NormalizeOptional(request.DownPayment.ReservationToken),
                IdempotencyKey = NormalizeOptional(request.DownPayment.IdempotencyKey),
                Amount = RoundCurrency(request.DownPayment.Amount)
            }
        };
    }

    private static InstallmentLineDto NormalizeLine(InstallmentLineDto line)
    {
        if (line.Quantity <= 0m)
        {
            throw new InvalidOperationException("Installment line quantity must be greater than zero.");
        }

        if (line.UnitPrice <= 0m)
        {
            throw new InvalidOperationException("Installment line unit price must be greater than zero.");
        }

        if (line.ActualAmount <= 0m)
        {
            throw new InvalidOperationException("Installment line amount must be greater than zero.");
        }

        return line with
        {
            ProductCode = NormalizeRequired(line.ProductCode, "Product code is required."),
            DisplayName = NormalizeRequired(line.DisplayName, "Display name is required."),
            LookupCode = NormalizeRequired(line.LookupCode, "Lookup code is required."),
            ReferenceCode = NormalizeOptional(line.ReferenceCode),
            ItemNumber = NormalizeOptional(line.ItemNumber),
            UnitPrice = RoundCurrency(line.UnitPrice),
            DiscountAmount = RoundCurrency(line.DiscountAmount),
            ActualAmount = RoundCurrency(line.ActualAmount)
        };
    }

    private static void ValidateInstallmentScope(
        InstallmentDetailsDto details,
        string storeCode,
        string deviceCode)
    {
        if (!string.Equals(details.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Installment does not belong to this store.");
        }

        if (!string.Equals(details.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Installment does not belong to this device.");
        }
    }

    private static InstallmentAppendPaymentRequest NormalizePaymentRequest(InstallmentAppendPaymentRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Reference = NormalizeOptional(request.Reference),
            ReservationToken = NormalizeOptional(request.ReservationToken),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey),
            Amount = RoundCurrency(request.Amount)
        };
    }

    private static InstallmentConfirmPickupRequest NormalizePickupRequest(InstallmentConfirmPickupRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Note = NormalizeOptional(request.Note)
        };
    }

    private static InstallmentCancelRequest NormalizeCancelRequest(InstallmentCancelRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Reason = NormalizeOptional(request.Reason),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey),
            Refunds = request.Refunds.Select(NormalizeRefund).ToList()
        };
    }

    private static InstallmentVoidRequest NormalizeVoidRequest(InstallmentVoidRequest request)
    {
        return request with
        {
            StoreCode = NormalizeRequired(request.StoreCode, "Store code is required."),
            DeviceCode = NormalizeRequired(request.DeviceCode, "Device code is required."),
            CashierId = NormalizeRequired(request.CashierId, "Cashier id is required."),
            CashierName = NormalizeRequired(request.CashierName, "Cashier name is required."),
            Reason = NormalizeOptional(request.Reason),
            IdempotencyKey = NormalizeOptional(request.IdempotencyKey)
        };
    }

    private static InstallmentRefundPaymentCommandDto NormalizeRefund(InstallmentRefundPaymentCommandDto refund)
    {
        if (refund.Amount <= 0m)
        {
            throw new InvalidOperationException("Refund amount must be greater than zero.");
        }

        return refund with
        {
            Amount = RoundCurrency(refund.Amount),
            Reference = NormalizeOptional(refund.Reference),
            IdempotencyKey = NormalizeOptional(refund.IdempotencyKey)
        };
    }

    private static bool TryCreateExistingCancellationResponse(
        InstallmentDetailsDto details,
        InstallmentCancellationKind expectedKind,
        out string? message)
    {
        message = null;
        if (details.Status != InstallmentStatus.Cancelled)
        {
            return false;
        }

        if (details.CancellationInfo?.Kind == expectedKind)
        {
            message = expectedKind == InstallmentCancellationKind.RefundCancel ? "AlreadyCancelled" : "AlreadyVoided";
            return true;
        }

        throw new InvalidOperationException("Installment cancellation kind conflicts with the existing cancelled record.");
    }

    private static void ValidateCancellable(InstallmentDetailsDto details)
    {
        if (details.Status != InstallmentStatus.Active || details.BalanceAmount <= 0m)
        {
            throw new InvalidOperationException("Only active unpaid installments can be cancelled or voided.");
        }
    }

    private static IReadOnlyList<InstallmentRefundPaymentCommandDto> NormalizeAndValidateRefunds(
        InstallmentDetailsDto details,
        InstallmentCancelRequest request)
    {
        if (request.Refunds.Count == 0)
        {
            throw new InvalidOperationException("Refund payments are required when cancelling an installment.");
        }

        var paidByMethod = details.Payments
            .Where(payment => payment.Status == InstallmentPaymentStatus.Recorded && payment.Amount > 0m)
            .GroupBy(payment => payment.Method)
            .ToDictionary(group => group.Key, group => RoundCurrency(group.Sum(payment => payment.Amount)));
        var refundByMethod = request.Refunds
            .GroupBy(refund => refund.Method)
            .ToDictionary(group => group.Key, group => RoundCurrency(group.Sum(refund => refund.Amount)));
        if (paidByMethod.Count != refundByMethod.Count ||
            paidByMethod.Any(pair => !refundByMethod.TryGetValue(pair.Key, out var refundAmount) || refundAmount != pair.Value))
        {
            throw new InvalidOperationException("Refund payments must cover all recorded installment payments by method.");
        }

        return request.Refunds;
    }

    private async Task ValidateVoucherPaymentAsync(
        string storeCode,
        PaymentMethodKind method,
        string? reference,
        string? reservationToken,
        decimal amount,
        CancellationToken cancellationToken)
    {
        if (method != PaymentMethodKind.Voucher)
        {
            return;
        }

        var voucherCode = NormalizeRequired(reference, "Voucher payment reference is required.");
        var token = NormalizeRequired(reservationToken, "Voucher reservation token is required.");
        var reservation = await reservationService.GetAsync(token, cancellationToken)
            ?? throw new InvalidOperationException("Voucher reservation token is invalid or expired.");
        if (!string.Equals(reservation.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voucher reservation store does not match the installment store.");
        }

        if (!string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voucher reservation does not match the voucher code.");
        }

        if (reservation.LockedAmount < amount)
        {
            throw new InvalidOperationException("Voucher payment amount exceeds the locked amount.");
        }
    }

    private static void ValidateDownPayment(decimal totalAmount, decimal downPaymentAmount)
    {
        if (totalAmount <= 0m)
        {
            throw new InvalidOperationException("Total amount must be greater than zero.");
        }

        if (downPaymentAmount <= 0m)
        {
            throw new InvalidOperationException("Down payment amount must be greater than zero.");
        }

        if (downPaymentAmount > totalAmount)
        {
            throw new InvalidOperationException("Down payment amount cannot exceed total amount.");
        }

        if (totalAmount < MinimumDownPaymentAmount && downPaymentAmount != totalAmount)
        {
            throw new InvalidOperationException("Down payment must pay off orders below the minimum amount.");
        }

        if (totalAmount >= MinimumDownPaymentAmount && downPaymentAmount < MinimumDownPaymentAmount)
        {
            throw new InvalidOperationException("Down payment amount must be at least $20.");
        }
    }

    private static InstallmentPaymentDto MapPayment(
        InstallmentPaymentCommandDto payment,
        string cashierId,
        string deviceCode,
        DateTimeOffset recordedAt)
    {
        return new InstallmentPaymentDto(
            payment.PaymentGuid,
            payment.Method,
            payment.Amount,
            payment.Reference,
            InstallmentPaymentStatus.Recorded,
            recordedAt,
            cashierId,
            deviceCode,
            payment.CardTransactions,
            payment.IdempotencyKey,
            payment.ReservationToken);
    }

    private static InstallmentPaymentDto MapRefundPayment(
        InstallmentRefundPaymentCommandDto payment,
        string cashierId,
        string deviceCode,
        DateTimeOffset recordedAt)
    {
        return new InstallmentPaymentDto(
            payment.PaymentGuid,
            payment.Method,
            -payment.Amount,
            payment.Reference,
            InstallmentPaymentStatus.Recorded,
            recordedAt,
            cashierId,
            deviceCode,
            payment.CardTransactions,
            payment.IdempotencyKey,
            ReservationToken: null);
    }

    private static string CreateInstallmentNumber(string storeCode, Guid installmentGuid)
    {
        return $"IP-{storeCode}-{installmentGuid:N}"[..Math.Min(40, $"IP-{storeCode}-{installmentGuid:N}".Length)].ToUpperInvariant();
    }

    private static decimal RoundCurrency(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeRequired(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public interface IInstallmentRepository
{
    Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> AppendPaymentAsync(
        Guid installmentGuid,
        InstallmentPaymentDto payment,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> ConfirmPickupAsync(
        Guid installmentGuid,
        DateTimeOffset pickedUpAt,
        string pickedUpBy,
        string? note,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> CancelWithRefundAsync(
        Guid installmentGuid,
        IReadOnlyList<InstallmentPaymentDto> refunds,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto> VoidAsync(
        Guid installmentGuid,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken);

    Task<InstallmentPaymentLookup?> FindPaymentAsync(
        Guid paymentGuid,
        CancellationToken cancellationToken);

    Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(
        Guid installmentGuid,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken);
}

public sealed record InstallmentPaymentLookup(Guid InstallmentGuid, InstallmentPaymentDto Payment);

public sealed class SqlSugarInstallmentRepository(HbposSqlSugarContext dbContext) : IInstallmentRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Insertable(MapOrder(details)).ExecuteCommandAsync(cancellationToken);
            await db.Insertable(details.Lines.Select(line => MapLine(details.InstallmentGuid, line)).ToList())
                .ExecuteCommandAsync(cancellationToken);
            foreach (var payment in details.Payments.Where(payment => payment.Method == PaymentMethodKind.Voucher))
            {
                // 分期首付用券必须在同一事务里先占用 reservation，再扣减券余额。
                await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                    db,
                    payment.ReservationToken ?? string.Empty,
                    details.StoreCode,
                    payment.Reference ?? string.Empty,
                    payment.Amount,
                    details.InstallmentGuid.ToString("D"),
                    payment.RecordedAt,
                    cancellationToken);
                await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                    db,
                    details.StoreCode,
                    payment.Reference ?? string.Empty,
                    payment.Amount,
                    details.CashierId,
                    cancellationToken);
            }

            await db.Insertable(details.Payments.Select(payment => MapPayment(details.InstallmentGuid, payment)).ToList())
                .ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<InstallmentDetailsDto> AppendPaymentAsync(
        Guid installmentGuid,
        InstallmentPaymentDto payment,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        await db.Ado.BeginTranAsync();
        try
        {
            var current = await GetDetailsInsideTransactionAsync(db, installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
            var existingPayment = await db.Queryable<InstallmentPaymentEntity>()
                .AnyAsync(x => x.PaymentGuid == payment.PaymentGuid.ToString("D"), cancellationToken);
            if (!existingPayment)
            {
                if (payment.Method == PaymentMethodKind.Voucher)
                {
                    // 补款用券同样通过 reservation claim 做一次性闸门。
                    await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                        db,
                        payment.ReservationToken ?? string.Empty,
                        current.StoreCode,
                        payment.Reference ?? string.Empty,
                        payment.Amount,
                        payment.PaymentGuid.ToString("D"),
                        payment.RecordedAt,
                        cancellationToken);
                    await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                        db,
                        current.StoreCode,
                        payment.Reference ?? string.Empty,
                        payment.Amount,
                        payment.CashierId,
                        cancellationToken);
                }

                await db.Insertable(MapPayment(installmentGuid, payment)).ExecuteCommandAsync(cancellationToken);
            }

            var paidAmount = RoundCurrency(await db.Queryable<InstallmentPaymentEntity>()
                .Where(x => x.InstallmentGuid == installmentGuid.ToString("D") && x.Status == (int)InstallmentPaymentStatus.Recorded)
                .SumAsync(x => x.Amount));
            var balanceAmount = RoundCurrency(Math.Max(0m, current.TotalAmount - paidAmount));
            var status = balanceAmount == 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active;
            await db.Updateable<InstallmentOrderEntity>()
                .SetColumns(x => x.PaidAmount == paidAmount)
                .SetColumns(x => x.BalanceAmount == balanceAmount)
                .SetColumns(x => x.Status == (int)status)
                .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                .Where(x => x.InstallmentGuid == installmentGuid.ToString("D"))
                .ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
            return await GetDetailsAsync(installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<InstallmentDetailsDto> ConfirmPickupAsync(
        Guid installmentGuid,
        DateTimeOffset pickedUpAt,
        string pickedUpBy,
        string? note,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        await db.Updateable<InstallmentOrderEntity>()
            .SetColumns(x => x.Status == (int)InstallmentStatus.PickedUp)
            .SetColumns(x => x.PickedUpAt == pickedUpAt.UtcDateTime)
            .SetColumns(x => x.PickedUpBy == pickedUpBy)
            .SetColumns(x => x.PickupNote == note)
            .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
            .Where(x => x.InstallmentGuid == installmentGuid.ToString("D"))
            .ExecuteCommandAsync(cancellationToken);
        return await GetDetailsAsync(installmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
    }

    public async Task<InstallmentDetailsDto> CancelWithRefundAsync(
        Guid installmentGuid,
        IReadOnlyList<InstallmentPaymentDto> refunds,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        await db.Ado.BeginTranAsync();
        try
        {
            foreach (var refund in refunds)
            {
                var existingPayment = await db.Queryable<InstallmentPaymentEntity>()
                    .AnyAsync(x => x.PaymentGuid == refund.PaymentGuid.ToString("D"), cancellationToken);
                if (!existingPayment)
                {
                    await db.Insertable(MapPayment(installmentGuid, refund)).ExecuteCommandAsync(cancellationToken);
                }
            }

            var paidAmount = RoundCurrency(await db.Queryable<InstallmentPaymentEntity>()
                .Where(x => x.InstallmentGuid == installmentGuid.ToString("D") && x.Status == (int)InstallmentPaymentStatus.Recorded)
                .SumAsync(x => x.Amount));
            await db.Updateable<InstallmentOrderEntity>()
                .SetColumns(x => x.PaidAmount == paidAmount)
                .SetColumns(x => x.BalanceAmount == 0m)
                .SetColumns(x => x.Status == (int)InstallmentStatus.Cancelled)
                .SetColumns(x => x.CancellationKind == (int)cancellationInfo.Kind)
                .SetColumns(x => x.CancelledAt == cancellationInfo.CancelledAt.UtcDateTime)
                .SetColumns(x => x.CancelledBy == cancellationInfo.CancelledBy)
                .SetColumns(x => x.CancellationReason == cancellationInfo.Reason)
                .SetColumns(x => x.CancellationIdempotencyKey == cancellationInfo.IdempotencyKey)
                .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                .Where(x => x.InstallmentGuid == installmentGuid.ToString("D"))
                .ExecuteCommandAsync(cancellationToken);
            await db.Ado.CommitTranAsync();
            return await GetDetailsAsync(installmentGuid, cancellationToken)
                ?? throw new InvalidOperationException("Installment was not found.");
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task<InstallmentDetailsDto> VoidAsync(
        Guid installmentGuid,
        InstallmentCancellationInfoDto cancellationInfo,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        await db.Updateable<InstallmentOrderEntity>()
            .SetColumns(x => x.Status == (int)InstallmentStatus.Cancelled)
            .SetColumns(x => x.CancellationKind == (int)cancellationInfo.Kind)
            .SetColumns(x => x.CancelledAt == cancellationInfo.CancelledAt.UtcDateTime)
            .SetColumns(x => x.CancelledBy == cancellationInfo.CancelledBy)
            .SetColumns(x => x.CancellationReason == cancellationInfo.Reason)
            .SetColumns(x => x.CancellationIdempotencyKey == cancellationInfo.IdempotencyKey)
            .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
            .Where(x => x.InstallmentGuid == installmentGuid.ToString("D"))
            .ExecuteCommandAsync(cancellationToken);
        return await GetDetailsAsync(installmentGuid, cancellationToken)
            ?? throw new InvalidOperationException("Installment was not found.");
    }

    public async Task<InstallmentPaymentLookup?> FindPaymentAsync(
        Guid paymentGuid,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        var entity = await db.Queryable<InstallmentPaymentEntity>()
            .FirstAsync(x => x.PaymentGuid == paymentGuid.ToString("D"), cancellationToken);
        return entity is null ? null : new InstallmentPaymentLookup(ParseGuid(entity.InstallmentGuid), MapPayment(entity));
    }

    public async Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(
        Guid installmentGuid,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        var installmentGuidText = installmentGuid.ToString("D");
        var entity = await db.Queryable<InstallmentPaymentEntity>()
            .FirstAsync(x => x.InstallmentGuid == installmentGuidText && x.IdempotencyKey == idempotencyKey, cancellationToken);
        return entity is null ? null : new InstallmentPaymentLookup(ParseGuid(entity.InstallmentGuid), MapPayment(entity));
    }

    public async Task<InstallmentHistoryQueryResponse> QueryAsync(
        InstallmentHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        var query = db.Queryable<InstallmentOrderEntity>()
            .Where(x => x.StoreCode == request.StoreCode);
        if (!string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            query = query.Where(x => x.DeviceCode == request.DeviceCode);
        }

        if (request.CreatedFrom is not null)
        {
            query = query.Where(x => x.CreatedAt >= request.CreatedFrom.Value.UtcDateTime);
        }

        if (request.CreatedTo is not null)
        {
            query = query.Where(x => x.CreatedAt <= request.CreatedTo.Value.UtcDateTime);
        }

        if (request.Status is not null)
        {
            query = query.Where(x => x.Status == (int)request.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            query = query.Where(x =>
                x.InstallmentGuid.Contains(keyword) ||
                x.InstallmentNumber.Contains(keyword) ||
                x.CustomerName.Contains(keyword) ||
                x.CustomerPhone.Contains(keyword));
        }

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(request.Take, 1, 200))
            .ToListAsync(cancellationToken);
        return new InstallmentHistoryQueryResponse(rows.Select(MapSummary).ToList());
    }

    public async Task<InstallmentDetailsDto?> GetDetailsAsync(
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        await EnsureTablesAsync(db, cancellationToken);
        return await GetDetailsInsideTransactionAsync(db, installmentGuid, cancellationToken);
    }

    private static async Task<InstallmentDetailsDto?> GetDetailsInsideTransactionAsync(
        ISqlSugarClient db,
        Guid installmentGuid,
        CancellationToken cancellationToken)
    {
        var guidText = installmentGuid.ToString("D");
        var order = await db.Queryable<InstallmentOrderEntity>()
            .FirstAsync(x => x.InstallmentGuid == guidText, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var lines = await db.Queryable<InstallmentOrderLineEntity>()
            .Where(x => x.InstallmentGuid == guidText)
            .ToListAsync(cancellationToken);
        var payments = await db.Queryable<InstallmentPaymentEntity>()
            .Where(x => x.InstallmentGuid == guidText)
            .OrderBy(x => x.RecordedAt)
            .ToListAsync(cancellationToken);
        return MapDetails(order, lines, payments);
    }

    private static Task EnsureTablesAsync(ISqlSugarClient db, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.CodeFirst.InitTables<InstallmentOrderEntity, InstallmentOrderLineEntity, InstallmentPaymentEntity>();
        return Task.CompletedTask;
    }

    private static InstallmentOrderEntity MapOrder(InstallmentDetailsDto details)
    {
        return new InstallmentOrderEntity
        {
            InstallmentGuid = details.InstallmentGuid.ToString("D"),
            InstallmentNumber = details.InstallmentNumber,
            StoreCode = details.StoreCode,
            DeviceCode = details.DeviceCode,
            CashierId = details.CashierId,
            CashierName = details.CashierName,
            CustomerName = details.CustomerName,
            CustomerPhone = details.CustomerPhone,
            TotalAmount = details.TotalAmount,
            MinimumDownPayment = details.MinimumDownPayment,
            DownPaymentAmount = details.DownPaymentAmount,
            PaidAmount = details.PaidAmount,
            BalanceAmount = details.BalanceAmount,
            Status = (int)details.Status,
            CreatedAt = details.CreatedAt.UtcDateTime,
            UpdatedAt = DateTime.UtcNow,
            Note = details.Note,
            CancellationKind = details.CancellationInfo is null ? null : (int)details.CancellationInfo.Kind,
            CancelledAt = details.CancellationInfo?.CancelledAt.UtcDateTime,
            CancelledBy = details.CancellationInfo?.CancelledBy,
            CancellationReason = details.CancellationInfo?.Reason,
            CancellationIdempotencyKey = details.CancellationInfo?.IdempotencyKey
        };
    }

    private static InstallmentOrderLineEntity MapLine(Guid installmentGuid, InstallmentLineDto line)
    {
        return new InstallmentOrderLineEntity
        {
            InstallmentLineGuid = line.InstallmentLineGuid.ToString("D"),
            InstallmentGuid = installmentGuid.ToString("D"),
            ProductCode = line.ProductCode,
            ReferenceCode = line.ReferenceCode,
            DisplayName = line.DisplayName,
            LookupCode = line.LookupCode,
            ItemNumber = line.ItemNumber,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            ActualAmount = line.ActualAmount
        };
    }

    private static InstallmentPaymentEntity MapPayment(Guid installmentGuid, InstallmentPaymentDto payment)
    {
        return new InstallmentPaymentEntity
        {
            PaymentGuid = payment.PaymentGuid.ToString("D"),
            InstallmentGuid = installmentGuid.ToString("D"),
            Method = (int)payment.Method,
            Amount = payment.Amount,
            Reference = payment.Reference,
            Status = (int)payment.Status,
            RecordedAt = payment.RecordedAt.UtcDateTime,
            CashierId = payment.CashierId,
            DeviceCode = payment.DeviceCode,
            CardTransactionsJson = payment.CardTransactions is null ? null : JsonSerializer.Serialize(payment.CardTransactions, JsonOptions),
            IdempotencyKey = payment.IdempotencyKey
        };
    }

    private static InstallmentSummaryDto MapSummary(InstallmentOrderEntity order)
    {
        return new InstallmentSummaryDto(
            ParseGuid(order.InstallmentGuid),
            order.InstallmentNumber,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.CustomerName,
            order.CustomerPhone,
            ToDateTimeOffset(order.CreatedAt),
            order.TotalAmount,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.BalanceAmount,
            (InstallmentStatus)order.Status,
            ToDateTimeOffset(order.UpdatedAt));
    }

    private static InstallmentDetailsDto MapDetails(
        InstallmentOrderEntity order,
        IReadOnlyList<InstallmentOrderLineEntity> lines,
        IReadOnlyList<InstallmentPaymentEntity> payments)
    {
        var pickupInfo = order.PickedUpAt is null
            ? null
            : new InstallmentPickupInfoDto(
                ToDateTimeOffset(order.PickedUpAt.Value),
                order.PickedUpBy ?? string.Empty,
                order.PickupNote);
        var cancellationInfo = order.CancellationKind is null || order.CancelledAt is null
            ? null
            : new InstallmentCancellationInfoDto(
                (InstallmentCancellationKind)order.CancellationKind.Value,
                ToDateTimeOffset(order.CancelledAt.Value),
                order.CancelledBy ?? string.Empty,
                order.CancellationReason,
                order.CancellationIdempotencyKey);
        return new InstallmentDetailsDto(
            ParseGuid(order.InstallmentGuid),
            order.InstallmentNumber,
            order.StoreCode,
            order.DeviceCode,
            order.CashierId,
            order.CashierName,
            order.CustomerName,
            order.CustomerPhone,
            ToDateTimeOffset(order.CreatedAt),
            order.TotalAmount,
            order.MinimumDownPayment,
            order.DownPaymentAmount,
            order.PaidAmount,
            order.BalanceAmount,
            (InstallmentStatus)order.Status,
            lines.Select(MapLine).ToList(),
            payments.Select(MapPayment).ToList(),
            pickupInfo,
            cancellationInfo,
            order.Note);
    }

    private static InstallmentLineDto MapLine(InstallmentOrderLineEntity line)
    {
        return new InstallmentLineDto(
            ParseGuid(line.InstallmentLineGuid),
            line.ProductCode,
            line.ReferenceCode,
            line.DisplayName,
            line.LookupCode,
            line.Quantity,
            line.UnitPrice,
            line.DiscountAmount,
            line.ActualAmount,
            line.ItemNumber);
    }

    private static InstallmentPaymentDto MapPayment(InstallmentPaymentEntity payment)
    {
        IReadOnlyList<CardTransactionDto>? cardTransactions = null;
        if (!string.IsNullOrWhiteSpace(payment.CardTransactionsJson))
        {
            cardTransactions = JsonSerializer.Deserialize<IReadOnlyList<CardTransactionDto>>(payment.CardTransactionsJson, JsonOptions);
        }

        return new InstallmentPaymentDto(
            ParseGuid(payment.PaymentGuid),
            (PaymentMethodKind)payment.Method,
            payment.Amount,
            payment.Reference,
            (InstallmentPaymentStatus)payment.Status,
            ToDateTimeOffset(payment.RecordedAt),
            payment.CashierId,
            payment.DeviceCode,
            cardTransactions,
            payment.IdempotencyKey);
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static Guid ParseGuid(string value)
    {
        return Guid.TryParse(value, out var guid) ? guid : Guid.Empty;
    }

    private static decimal RoundCurrency(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

[SugarTable("InstallmentOrder")]
public sealed class InstallmentOrderEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string InstallmentGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 40)]
    public string InstallmentNumber { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string CashierId { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string CashierName { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string CustomerName { get; set; } = string.Empty;

    [SugarColumn(Length = 40)]
    public string CustomerPhone { get; set; } = string.Empty;

    public decimal TotalAmount { get; set; }

    public decimal MinimumDownPayment { get; set; }

    public decimal DownPaymentAmount { get; set; }

    public decimal PaidAmount { get; set; }

    public decimal BalanceAmount { get; set; }

    public int Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? PickedUpAt { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? PickedUpBy { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Note { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? PickupNote { get; set; }

    public int? CancellationKind { get; set; }

    public DateTime? CancelledAt { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CancelledBy { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? CancellationReason { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? CancellationIdempotencyKey { get; set; }
}

[SugarTable("InstallmentOrderLine")]
public sealed class InstallmentOrderLineEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string InstallmentLineGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 36)]
    public string InstallmentGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ReferenceCode { get; set; }

    [SugarColumn(Length = 255)]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string LookupCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ItemNumber { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal ActualAmount { get; set; }
}

[SugarTable("InstallmentPayment")]
public sealed class InstallmentPaymentEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string PaymentGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 36)]
    public string InstallmentGuid { get; set; } = string.Empty;

    public int Method { get; set; }

    public decimal Amount { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Reference { get; set; }

    public int Status { get; set; }

    public DateTime RecordedAt { get; set; }

    [SugarColumn(Length = 50)]
    public string CashierId { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? CardTransactionsJson { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? IdempotencyKey { get; set; }
}
