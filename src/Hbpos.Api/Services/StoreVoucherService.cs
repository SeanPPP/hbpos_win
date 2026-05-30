using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Text;
using BlazorApp.Service.Models.HBPOSM_POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Vouchers;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IStoreVoucherService
{
    Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken);

    Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken);

    Task<StoreVoucherIssueRefundResponse> IssueRefundAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken);

    Task<StoreVoucherIssueResponse> IssueAsync(
        StoreVoucherIssueRequest request,
        CancellationToken cancellationToken);
}

public sealed class StoreVoucherService(
    IStoreVoucherRepository repository,
    IStoreVoucherReservationService reservationService,
    TimeProvider? timeProvider = null) : IStoreVoucherService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<StoreVoucherQueryResponse> QueryAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(storeCode, nameof(storeCode));
        var normalizedVoucherCode = NormalizeRequired(voucherCode, nameof(voucherCode));
        var voucher = await repository.FindAvailableAsync(normalizedStoreCode, normalizedVoucherCode, cancellationToken);

        return voucher is null
            ? new StoreVoucherQueryResponse(false, null, "VoucherNotFound")
            : new StoreVoucherQueryResponse(true, Map(voucher));
    }

    public async Task<StoreVoucherLockResponse> LockAsync(
        StoreVoucherLockRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(request.StoreCode, nameof(request.StoreCode));
        var normalizedVoucherCode = NormalizeRequired(request.VoucherCode, nameof(request.VoucherCode));
        if (request.RequestedAmount <= 0)
        {
            throw new InvalidOperationException("Requested amount must be greater than zero.");
        }

        var voucher = await repository.FindAvailableAsync(normalizedStoreCode, normalizedVoucherCode, cancellationToken)
            ?? throw new InvalidOperationException("Voucher is unavailable.");
        var reservation = await reservationService.ReserveAsync(
            normalizedStoreCode,
            normalizedVoucherCode,
            request.RequestedAmount,
            voucher.RemainingAmount ?? 0m,
            cancellationToken);

        return new StoreVoucherLockResponse(
            normalizedVoucherCode,
            reservation.LockedAmount,
            reservation.Token,
            reservation.ExpiresAt);
    }

    public async Task<StoreVoucherIssueRefundResponse> IssueRefundAsync(
        StoreVoucherIssueRefundRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(request.StoreCode, nameof(request.StoreCode));
        var normalizedCashierId = NormalizeRequired(request.CashierId, nameof(request.CashierId));
        var normalizedIdempotencyKey = NormalizeRequired(request.IdempotencyKey ?? string.Empty, nameof(request.IdempotencyKey));
        if (request.Amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be greater than zero.");
        }

        var now = _timeProvider.GetUtcNow();
        var voucher = await repository.CreateRefundVoucherAsync(
            new RefundVoucherCreateModel(
                normalizedStoreCode,
                decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
                normalizedCashierId,
                now,
                now.AddMonths(12),
                normalizedIdempotencyKey,
                request.OrderReference?.Trim(),
                request.Reason?.Trim()),
            cancellationToken);

        return new StoreVoucherIssueRefundResponse(
            voucher.VoucherCode ?? string.Empty,
            voucher.Amount ?? decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            voucher.RemainingAmount ?? decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            voucher.Status ?? "1",
            voucher.ExpiredDate is null
                ? now.AddMonths(12)
                : DateTime.SpecifyKind(voucher.ExpiredDate.Value, DateTimeKind.Utc));
    }

    public async Task<StoreVoucherIssueResponse> IssueAsync(
        StoreVoucherIssueRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeRequired(request.StoreCode ?? string.Empty, nameof(request.StoreCode));
        var normalizedCashierId = NormalizeRequired(request.CashierId, nameof(request.CashierId));
        var normalizedIdempotencyKey = NormalizeRequired(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.Amount <= 0m)
        {
            throw new InvalidOperationException("Amount must be greater than zero.");
        }

        var now = _timeProvider.GetUtcNow();
        var expiredAt = request.ExpiredAt ?? now.AddMonths(12);
        if (expiredAt <= now)
        {
            throw new InvalidOperationException("ExpiredAt must be in the future.");
        }

        var voucher = await repository.CreateIssuedVoucherAsync(
            new IssuedVoucherCreateModel(
                normalizedStoreCode,
                decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
                normalizedCashierId,
                now,
                expiredAt,
                normalizedIdempotencyKey,
                NormalizeOptional(request.CustomerCode),
                request.Reason?.Trim()),
            cancellationToken);

        var amount = voucher.Amount ?? decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
        return new StoreVoucherIssueResponse(
            voucher.VoucherCode ?? string.Empty,
            amount,
            voucher.RemainingAmount ?? amount,
            voucher.Status ?? "1",
            voucher.ExpiredDate is null
                ? expiredAt
                : DateTime.SpecifyKind(voucher.ExpiredDate.Value, DateTimeKind.Utc),
            NormalizeOptional(voucher.StoreCode),
            NormalizeOptional(voucher.CustomerCode));
    }

    private static StoreVoucherDto Map(StoreVoucher voucher)
    {
        return new StoreVoucherDto(
            voucher.VoucherCode ?? string.Empty,
            voucher.StoreCode,
            voucher.VoucherType ?? 0,
            voucher.Amount ?? 0m,
            voucher.RemainingAmount ?? 0m,
            voucher.Status ?? string.Empty,
            voucher.ExpiredDate is null
                ? null
                : DateTime.SpecifyKind(voucher.ExpiredDate.Value, DateTimeKind.Utc),
            voucher.CustomerCode,
            voucher.DiscountRate ?? 0m,
            voucher.Remark);
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{paramName} is required.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public interface IStoreVoucherRepository
{
    Task<StoreVoucher?> FindAvailableAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken);

    Task<StoreVoucher> CreateRefundVoucherAsync(
        RefundVoucherCreateModel request,
        CancellationToken cancellationToken);

    Task<StoreVoucher> CreateIssuedVoucherAsync(
        IssuedVoucherCreateModel request,
        CancellationToken cancellationToken);
}

public sealed record RefundVoucherCreateModel(
    string StoreCode,
    decimal Amount,
    string CashierId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiredAt,
    string IdempotencyKey,
    string? OrderReference,
    string? Reason);

public sealed record IssuedVoucherCreateModel(
    string? StoreCode,
    decimal Amount,
    string CashierId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiredAt,
    string IdempotencyKey,
    string? CustomerCode,
    string? Reason);

public sealed class SqlSugarStoreVoucherRepository(HbposSqlSugarContext dbContext) : IStoreVoucherRepository
{
    public async Task<StoreVoucher?> FindAvailableAsync(
        string storeCode,
        string voucherCode,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var voucher = await dbContext.PosmDb.Queryable<StoreVoucher>()
            .Where(x => x.VoucherCode == voucherCode)
            .Where(x => x.Status == "1")
            .Where(x => x.IsDelete == null || x.IsDelete == false)
            .Where(x => x.RemainingAmount != null && x.RemainingAmount > 0)
            .Where(x => x.ExpiredDate == null || x.ExpiredDate > now)
            .Where(x => x.StoreCode == null || x.StoreCode == string.Empty || x.StoreCode == storeCode)
            .OrderBy(x => x.StoreCode == storeCode, OrderByType.Desc)
            .FirstAsync(cancellationToken);
        return voucher;
    }

    public async Task<StoreVoucher> CreateRefundVoucherAsync(
        RefundVoucherCreateModel request,
        CancellationToken cancellationToken)
    {
        return await CreateVoucherAsync(
            new VoucherCreatePersistenceModel(
                "RF",
                request.StoreCode,
                request.Amount,
                request.CashierId,
                request.CreatedAt,
                request.ExpiredAt,
                request.IdempotencyKey,
                null,
                request.OrderReference,
                request.Reason,
                BuildRefundRemark,
                BuildRefundIdempotencyMarker),
            cancellationToken);
    }

    public async Task<StoreVoucher> CreateIssuedVoucherAsync(
        IssuedVoucherCreateModel request,
        CancellationToken cancellationToken)
    {
        return await CreateVoucherAsync(
            new VoucherCreatePersistenceModel(
                "VC",
                request.StoreCode,
                request.Amount,
                request.CashierId,
                request.CreatedAt,
                request.ExpiredAt,
                request.IdempotencyKey,
                request.CustomerCode,
                null,
                request.Reason,
                BuildIssueRemark,
                BuildIssueIdempotencyMarker),
            cancellationToken);
    }

    internal static async Task RedeemInsideTransactionAsync(
        ISqlSugarClient db,
        string storeCode,
        string voucherCode,
        decimal amount,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var voucher = await db.Queryable<StoreVoucher>()
            .Where(x => x.VoucherCode == voucherCode)
            .Where(x => x.StoreCode == null || x.StoreCode == string.Empty || x.StoreCode == storeCode)
            .OrderBy(x => x.StoreCode == storeCode, OrderByType.Desc)
            .FirstAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Voucher {voucherCode} was not found.");
        ValidateVoucherForRedemption(voucher, storeCode);

        var remaining = voucher.RemainingAmount ?? 0m;
        if (remaining < amount)
        {
            throw new InvalidOperationException($"Voucher {voucherCode} balance is not enough.");
        }

        var newRemaining = decimal.Round(remaining - amount, 2, MidpointRounding.AwayFromZero);
        voucher.RemainingAmount = newRemaining <= 0m ? 0m : newRemaining;
        voucher.Status = voucher.RemainingAmount <= 0m ? "0" : "1";
        voucher.UpdateTime = DateTime.UtcNow;
        voucher.UpdateUser = updatedBy;

        await db.Updateable(voucher)
            .UpdateColumns(x => new
            {
                x.RemainingAmount,
                x.Status,
                x.UpdateTime,
                x.UpdateUser
            })
            .ExecuteCommandAsync(cancellationToken);
    }

    private static void ValidateVoucherForRedemption(StoreVoucher voucher, string storeCode)
    {
        if (voucher.IsDelete == true)
        {
            throw new InvalidOperationException("Voucher has been deleted.");
        }

        if (!string.Equals(voucher.Status?.Trim(), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Voucher is not active.");
        }

        if (!string.IsNullOrWhiteSpace(voucher.StoreCode) &&
            !string.Equals(voucher.StoreCode.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Voucher does not belong to this store.");
        }

        if (voucher.ExpiredDate is not null && voucher.ExpiredDate.Value <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Voucher has expired.");
        }
    }

    private async Task<StoreVoucher> CreateVoucherAsync(
        VoucherCreatePersistenceModel request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dbContext.PosmDb.Ado.BeginTranAsync(IsolationLevel.Serializable);
            try
            {
                var existing = await FindVoucherByIdempotencyKeyAsync(request, cancellationToken);
                if (existing is not null)
                {
                    await dbContext.PosmDb.Ado.CommitTranAsync();
                    return existing;
                }

                var voucherCode = CreateVoucherCode(request.CodePrefix);
                var entity = new StoreVoucher();
                SetIfExists(entity, "VoucherCode", voucherCode);
                SetIfExists(entity, "StoreCode", request.StoreCode);
                SetIfExists(entity, "VoucherType", 3);
                SetIfExists(entity, "Amount", request.Amount);
                SetIfExists(entity, "RemainingAmount", request.Amount);
                SetIfExists(entity, "Status", "1");
                SetIfExists(entity, "ExpiredDate", request.ExpiredAt.UtcDateTime);
                // 备注承载发放来源与幂等标记，第一版不新增流水表时用于审计和重试去重。
                SetIfExists(entity, "Remark", request.BuildRemark(request));
                SetIfExists(entity, "DiscountRate", 0m);
                SetIfExists(entity, "IsDelete", false);
                SetIfExists(entity, "CustomerCode", request.CustomerCode);
                SetIfExists(entity, "CreatedBy", request.CashierId);
                SetIfExists(entity, "CreateUser", request.CashierId);
                SetIfExists(entity, "CreatedAt", request.CreatedAt.UtcDateTime);
                SetIfExists(entity, "CreateTime", request.CreatedAt.UtcDateTime);
                SetIfExists(entity, "UpdateTime", request.CreatedAt.UtcDateTime);
                SetIfExists(entity, "UpdatedAt", request.CreatedAt.UtcDateTime);
                await dbContext.PosmDb.Insertable(entity).ExecuteCommandAsync(cancellationToken);
                await dbContext.PosmDb.Ado.CommitTranAsync();
                return entity;
            }
            catch (SqlSugarException ex) when (LooksLikeDuplicateVoucherCode(ex))
            {
                await dbContext.PosmDb.Ado.RollbackTranAsync();
            }
            catch
            {
                await dbContext.PosmDb.Ado.RollbackTranAsync();
                throw;
            }
        }

        throw new InvalidOperationException("Unable to generate a unique voucher code.");
    }

    private async Task<StoreVoucher?> FindVoucherByIdempotencyKeyAsync(
        VoucherCreatePersistenceModel request,
        CancellationToken cancellationToken)
    {
        var marker = request.BuildIdempotencyMarker(request.IdempotencyKey);
        var query = dbContext.PosmDb.Queryable<StoreVoucher>()
            .Where(x => x.Remark != null && x.Remark.Contains(marker))
            .Where(x => x.IsDelete == null || x.IsDelete == false);
        query = string.IsNullOrWhiteSpace(request.StoreCode)
            ? query.Where(x => x.StoreCode == null || x.StoreCode == string.Empty)
            : query.Where(x => x.StoreCode == request.StoreCode);
        return await query.FirstAsync(cancellationToken);
    }

    private static string CreateVoucherCode(string prefix)
    {
        return $"{prefix}{Guid.NewGuid():N}"[..14].ToUpperInvariant();
    }

    private static string BuildRefundRemark(VoucherCreatePersistenceModel request)
    {
        var parts = new List<string> { "Refund voucher" };
        parts.Add(BuildRefundIdempotencyMarker(request.IdempotencyKey));
        if (!string.IsNullOrWhiteSpace(request.OrderReference))
        {
            parts.Add($"Order {request.OrderReference}");
        }

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            parts.Add(request.Reason!);
        }

        return string.Join(" | ", parts);
    }

    private static string BuildIssueRemark(VoucherCreatePersistenceModel request)
    {
        var parts = new List<string> { "Issued voucher" };
        parts.Add(BuildIssueIdempotencyMarker(request.IdempotencyKey));
        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            parts.Add(request.Reason!);
        }

        return string.Join(" | ", parts);
    }

    private static string BuildRefundIdempotencyMarker(string idempotencyKey)
    {
        var encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return $"RefundKey[{encodedKey}]";
    }

    private static string BuildIssueIdempotencyMarker(string idempotencyKey)
    {
        var encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return $"IssueKey[{encodedKey}]";
    }

    private sealed record VoucherCreatePersistenceModel(
        string CodePrefix,
        string? StoreCode,
        decimal Amount,
        string CashierId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiredAt,
        string IdempotencyKey,
        string? CustomerCode,
        string? OrderReference,
        string? Reason,
        Func<VoucherCreatePersistenceModel, string> BuildRemark,
        Func<string, string> BuildIdempotencyMarker);

    private static void SetIfExists(StoreVoucher entity, string propertyName, object? value)
    {
        var property = typeof(StoreVoucher).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        if (value is null)
        {
            property.SetValue(entity, null);
            return;
        }

        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var normalizedValue = targetType.IsInstanceOfType(value)
            ? value
            : Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
        property.SetValue(entity, normalizedValue);
    }

    private static bool LooksLikeDuplicateVoucherCode(SqlSugarException exception)
    {
        return exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}

public interface IStoreVoucherReservationService
{
    Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken);

    Task<StoreVoucherReservation> ReserveAsync(
        string storeCode,
        string voucherCode,
        decimal requestedAmount,
        decimal currentRemainingAmount,
        CancellationToken cancellationToken);

    Task ConsumeAsync(string token, CancellationToken cancellationToken);
}

public sealed record StoreVoucherReservation(
    string Token,
    string StoreCode,
    string VoucherCode,
    decimal LockedAmount,
    DateTimeOffset ExpiresAt);

public sealed class InMemoryStoreVoucherReservationService(TimeProvider timeProvider) : IStoreVoucherReservationService
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, StoreVoucherReservation> reservations = new(StringComparer.OrdinalIgnoreCase);

    public Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PruneExpiredReservations();

        return Task.FromResult(reservations.TryGetValue(token, out var reservation) ? reservation : null);
    }

    public Task<StoreVoucherReservation> ReserveAsync(
        string storeCode,
        string voucherCode,
        decimal requestedAmount,
        decimal currentRemainingAmount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PruneExpiredReservations();

        var reservedAmount = reservations.Values
            .Where(x =>
                string.Equals(x.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.LockedAmount);
        var lockableAmount = Math.Min(requestedAmount, Math.Max(0m, currentRemainingAmount - reservedAmount));
        if (lockableAmount <= 0)
        {
            throw new InvalidOperationException("Voucher has no remaining amount available to lock.");
        }

        var reservation = new StoreVoucherReservation(
            Guid.NewGuid().ToString("N"),
            storeCode,
            voucherCode,
            lockableAmount,
            timeProvider.GetUtcNow().Add(ReservationLifetime));
        reservations[reservation.Token] = reservation;
        return Task.FromResult(reservation);
    }

    public Task ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        reservations.TryRemove(token, out _);
        return Task.CompletedTask;
    }

    private void PruneExpiredReservations()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in reservations)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                reservations.TryRemove(pair.Key, out _);
            }
        }
    }
}
