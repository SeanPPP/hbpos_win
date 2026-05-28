using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Orders;
using SqlSugar;
using StoreVoucherEntity = BlazorApp.Service.Models.HBPOSM_POSM.StoreVoucher;

namespace Hbpos.Api.Services;

public interface IOrderSyncService
{
    Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken);
}

public sealed class OrderSyncService(
    IOrderRepository repository,
    IOrderSyncPlanner planner,
    IStoreVoucherReservationService reservationService) : IOrderSyncService
{
    public async Task<OrderSyncResponse> SyncAsync(
        OrderSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (await repository.ExistsAsync(request.OrderGuid, cancellationToken))
        {
            return new OrderSyncResponse(request.OrderGuid, true, true, "AlreadySynced");
        }

        var voucherRedemptions = await BuildVoucherRedemptionsAsync(request, cancellationToken);
        var plan = planner.CreatePlan(request);
        await repository.InsertAsync(plan, voucherRedemptions, cancellationToken);

        foreach (var redemption in voucherRedemptions)
        {
            await reservationService.ConsumeAsync(redemption.ReservationToken, cancellationToken);
        }

        return new OrderSyncResponse(request.OrderGuid, true, false, "Synced");
    }

    private async Task<IReadOnlyList<StoreVoucherRedemptionCommit>> BuildVoucherRedemptionsAsync(
        OrderSyncRequest request,
        CancellationToken cancellationToken)
    {
        var voucherPayments = request.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Voucher)
            .ToList();
        if (voucherPayments.Count == 0)
        {
            return [];
        }

        var redemptions = new List<StoreVoucherRedemptionCommit>(voucherPayments.Count);
        var seenTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var payment in voucherPayments)
        {
            if (payment.Amount < 0m)
            {
                continue;
            }

            if (payment.Amount == 0m)
            {
                throw new InvalidOperationException("Voucher payment amount must not be zero.");
            }

            var voucherCode = NormalizeRequired(payment.Reference, "Voucher payment reference is required.");
            var reservationToken = NormalizeRequired(payment.ReservationToken, "Voucher reservation token is required.");
            if (!seenTokens.Add(reservationToken))
            {
                throw new InvalidOperationException("Voucher reservation token cannot be reused in the same order.");
            }

            var reservation = await reservationService.GetAsync(reservationToken, cancellationToken)
                ?? throw new InvalidOperationException("Voucher reservation token is invalid or expired.");
            if (!string.Equals(reservation.StoreCode, request.StoreCode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Voucher reservation store does not match the order store.");
            }

            if (!string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Voucher reservation does not match the voucher code.");
            }

            if (reservation.LockedAmount < payment.Amount)
            {
                throw new InvalidOperationException("Voucher payment amount exceeds the locked amount.");
            }

            redemptions.Add(new StoreVoucherRedemptionCommit(voucherCode, reservationToken, payment.Amount));
        }

        return redemptions;
    }

    private static string NormalizeRequired(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }

        return value.Trim();
    }
}

public interface IOrderRepository
{
    Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken);

    Task InsertAsync(
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        CancellationToken cancellationToken);
}

public sealed record StoreVoucherRedemptionCommit(
    string VoucherCode,
    string ReservationToken,
    decimal Amount);

public sealed class SqlSugarOrderRepository(HbposSqlSugarContext dbContext) : IOrderRepository
{
    public async Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken)
    {
        var orderGuidText = orderGuid.ToString("D");
        return await dbContext.PosmDb.Queryable<SalesOrder>()
            .AnyAsync(x => x.OrderGuid == orderGuidText, cancellationToken);
    }

    public async Task InsertAsync(
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        CancellationToken cancellationToken)
    {
        var db = dbContext.PosmDb;
        if (plan.ReturnRecords.Count > 0)
        {
            await SalesReturnRecordPersistence.BeginSerializableTransactionAsync(db);
        }
        else
        {
            await db.Ado.BeginTranAsync();
        }

        try
        {
            var existing = await db.Queryable<SalesOrder>()
                .AnyAsync(x => x.OrderGuid == plan.Order.OrderGuid, cancellationToken);

            if (existing)
            {
                await db.Ado.CommitTranAsync();
                return;
            }

            if (voucherRedemptions.Count > 0)
            {
                await ApplyVoucherPaymentsAsync(db, plan, voucherRedemptions, cancellationToken);
            }

            await db.Insertable(plan.Order).ExecuteCommandAsync(cancellationToken);
            if (plan.Lines.Count > 0)
            {
                await db.Insertable(plan.Lines.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            if (plan.Payments.Count > 0)
            {
                await db.Insertable(plan.Payments.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            if (plan.BankTransactions.Count > 0)
            {
                await db.Insertable(plan.BankTransactions.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            var returnRecordPreparation = await SalesReturnRecordPersistence.PrepareValidatedInsertAsync(
                db,
                plan.ReturnRecords,
                cancellationToken,
                plan.Payments);
            if (returnRecordPreparation.RecordsToInsert.Count > 0)
            {
                await db.Insertable(returnRecordPreparation.RecordsToInsert.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static async Task ApplyVoucherPaymentsAsync(
        ISqlSugarClient db,
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        CancellationToken cancellationToken)
    {
        foreach (var redemption in voucherRedemptions)
        {
            var storeCode = plan.Order.BranchCode ?? string.Empty;
            var voucher = await db.Queryable<StoreVoucherEntity>()
                .Where(x => x.VoucherCode == redemption.VoucherCode)
                .Where(x => x.StoreCode == null || x.StoreCode == string.Empty || x.StoreCode == storeCode)
                .OrderBy(x => x.StoreCode == storeCode, OrderByType.Desc)
                .FirstAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Voucher {redemption.VoucherCode} was not found.");
            ValidateVoucherForOrder(voucher, storeCode);

            var remaining = voucher.RemainingAmount ?? 0m;
            if (remaining < redemption.Amount)
            {
                throw new InvalidOperationException($"Voucher {redemption.VoucherCode} balance is not enough.");
            }

            var newRemaining = decimal.Round(remaining - redemption.Amount, 2, MidpointRounding.AwayFromZero);
            voucher.RemainingAmount = newRemaining <= 0m ? 0m : newRemaining;
            voucher.Status = voucher.RemainingAmount <= 0m ? "0" : "1";
            voucher.UpdateTime = DateTime.UtcNow;
            voucher.UpdateUser = plan.Order.CashierId ?? plan.Order.CashierName;

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
    }

    private static void ValidateVoucherForOrder(StoreVoucherEntity voucher, string storeCode)
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

}
