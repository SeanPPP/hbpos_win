using System.Text.Json;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Diagnostics;

namespace Hbpos.Api.Services;

public interface IOrderSyncService
{
    Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken);
}

public sealed class OrderSyncService(
    IOrderRepository repository,
    IOrderSyncPlanner planner,
    IStoreVoucherReservationService reservationService,
    ILogger<OrderSyncService>? logger = null) : IOrderSyncService
{
    public async Task<OrderSyncResponse> SyncAsync(
        OrderSyncRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log(
            $"service start orderGuid={request.OrderGuid:D} store={request.StoreCode} device={request.DeviceCode} " +
            $"lines={request.Lines.Count} payments={request.Payments.Count}");
        if (await repository.ExistsAsync(request.OrderGuid, cancellationToken))
        {
            Log($"service completed orderGuid={request.OrderGuid:D} status=already-synced elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new OrderSyncResponse(request.OrderGuid, true, true, "AlreadySynced");
        }

        var voucherRedemptions = await BuildVoucherRedemptionsAsync(request, cancellationToken);
        Log($"voucher redemptions prepared orderGuid={request.OrderGuid:D} count={voucherRedemptions.Count}");
        var plan = planner.CreatePlan(request);
        Log(
            $"plan created orderGuid={request.OrderGuid:D} saleLines={plan.Lines.Count} payments={plan.Payments.Count} " +
            $"bankTransactions={plan.BankTransactions.Count} returns={plan.ReturnRecords.Count}");
        var inserted = await repository.InsertAsync(plan, voucherRedemptions, cancellationToken);
        if (!inserted)
        {
            // 并发重复上传时不再消费预占令牌，直接向上层返回已同步结果。
            Log($"service completed orderGuid={request.OrderGuid:D} status=already-synced-after-insert elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new OrderSyncResponse(request.OrderGuid, true, true, "AlreadySynced");
        }

        Log($"repository insert completed orderGuid={request.OrderGuid:D}");

        foreach (var redemption in voucherRedemptions)
        {
            await reservationService.ConsumeAsync(redemption.ReservationToken, cancellationToken);
            Log(
                $"voucher reservation consumed orderGuid={request.OrderGuid:D} token={ShortToken(redemption.ReservationToken)} " +
                $"voucher={redemption.VoucherCode} amount={redemption.Amount}");
        }

        Log($"service completed orderGuid={request.OrderGuid:D} status=synced elapsedMs={stopwatch.ElapsedMilliseconds}");
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
                Log($"voucher redemption skipped orderGuid={request.OrderGuid:D} reason=refund-payment paymentGuid={payment.PaymentGuid:D} amount={payment.Amount}");
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

    private void Log(string message)
    {
        logger?.LogInformation("OrderSyncService {Message}", message);
    }

    private static string ShortToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "<null>";
        }

        var trimmed = token.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
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

    Task<bool> InsertAsync(
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        CancellationToken cancellationToken);
}

public sealed record StoreVoucherRedemptionCommit(
    string VoucherCode,
    string ReservationToken,
    decimal Amount);

public sealed class SqlSugarOrderRepository(
    HbposSqlSugarContext dbContext,
    ILogger<SqlSugarOrderRepository> logger) : IOrderRepository
{
    private const int LogPreviewMaxLength = 80;

    public async Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken)
    {
        var orderGuidText = orderGuid.ToString("D");
        return await dbContext.PosmDb.Queryable<SalesOrder>()
            .AnyAsync(x => x.OrderGuid == orderGuidText, cancellationToken);
    }

    public async Task<bool> InsertAsync(
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "OrderSyncRepository insert start OrderGuid={OrderGuid} Lines={LineCount} Payments={PaymentCount} BankTransactions={BankTransactionCount} Returns={ReturnCount} Vouchers={VoucherCount}",
            plan.Order.OrderGuid,
            plan.Lines.Count,
            plan.Payments.Count,
            plan.BankTransactions.Count,
            plan.ReturnRecords.Count,
            voucherRedemptions.Count);
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
                logger.LogInformation(
                    "OrderSyncRepository insert skipped OrderGuid={OrderGuid} Reason=already-exists ElapsedMs={ElapsedMs}",
                    plan.Order.OrderGuid,
                    stopwatch.ElapsedMilliseconds);
                await db.Ado.CommitTranAsync();
                return false;
            }

            if (voucherRedemptions.Count > 0)
            {
                // 代金券余额扣减与订单写入放在同一事务内，避免部分成功。
                await ApplyVoucherPaymentsAsync(db, plan, voucherRedemptions, logger, cancellationToken);
                logger.LogInformation(
                    "OrderSyncRepository voucher apply completed OrderGuid={OrderGuid} VoucherCount={VoucherCount}",
                    plan.Order.OrderGuid,
                    voucherRedemptions.Count);
            }

            await db.Insertable(plan.Order).ExecuteCommandAsync(cancellationToken);
            if (plan.Lines.Count > 0)
            {
                try
                {
                    await db.Insertable(plan.Lines.ToList()).ExecuteCommandAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    LogSalesOrderDetailInsertFailure(logger, ex, plan);
                    throw;
                }
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
            logger.LogInformation(
                "OrderSyncRepository insert completed OrderGuid={OrderGuid} Lines={LineCount} Payments={PaymentCount} BankTransactions={BankTransactionCount} Returns={ReturnCount} ElapsedMs={ElapsedMs}",
                plan.Order.OrderGuid,
                plan.Lines.Count,
                plan.Payments.Count,
                plan.BankTransactions.Count,
                plan.ReturnRecords.Count,
                stopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            await db.Ado.RollbackTranAsync();
            logger.LogError(
                ex,
                "OrderSyncRepository insert failed OrderGuid={OrderGuid} ElapsedMs={ElapsedMs}",
                plan.Order.OrderGuid,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static async Task ApplyVoucherPaymentsAsync(
        ISqlSugarClient db,
        OrderSyncPlan plan,
        IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var redemption in voucherRedemptions)
        {
            var storeCode = plan.Order.BranchCode ?? string.Empty;
            logger.LogInformation(
                "OrderSyncRepository voucher claim start OrderGuid={OrderGuid} Voucher={VoucherCode} Token={ReservationToken} Amount={Amount}",
                plan.Order.OrderGuid,
                redemption.VoucherCode,
                ShortToken(redemption.ReservationToken),
                redemption.Amount);
            await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                db,
                redemption.ReservationToken,
                storeCode,
                redemption.VoucherCode,
                redemption.Amount,
                plan.Order.OrderGuid,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                db,
                storeCode,
                redemption.VoucherCode,
                redemption.Amount,
                plan.Order.CashierId ?? plan.Order.CashierName,
                cancellationToken);
            logger.LogInformation(
                "OrderSyncRepository voucher redeemed OrderGuid={OrderGuid} Voucher={VoucherCode} Token={ReservationToken} Amount={Amount}",
                plan.Order.OrderGuid,
                redemption.VoucherCode,
                ShortToken(redemption.ReservationToken),
                redemption.Amount);
        }
    }

    private static string ShortToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "<null>";
        }

        var trimmed = token.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }

    internal static IReadOnlyList<SalesOrderDetailInsertDiagnostic> BuildSalesOrderDetailDiagnostics(
        IReadOnlyList<SalesOrderDetail> lines)
    {
        return lines.Select(line => new SalesOrderDetailInsertDiagnostic(
                line.OrderDetailGuid,
                BuildTextDiagnostic(line.ProductCode),
                BuildTextDiagnostic(line.ReferenceGUID),
                BuildTextDiagnostic(line.ProductName),
                BuildTextDiagnostic(line.Barcode),
                BuildTextDiagnostic(line.Remark),
                BuildTextDiagnostic(line.CreatedBy),
                BuildTextDiagnostic(line.UpdatedBy)))
            .ToList();
    }

    internal static string BuildSalesOrderDetailDiagnosticsText(IReadOnlyList<SalesOrderDetail> lines)
    {
        return JsonSerializer.Serialize(BuildSalesOrderDetailDiagnostics(lines));
    }

    private static void LogSalesOrderDetailInsertFailure(
        ILogger logger,
        Exception exception,
        OrderSyncPlan plan)
    {
        // 明细插入失败时保留字段长度和安全预览，便于定位数据库触发器字符串解析问题。
        logger.LogError(
            exception,
            "Sales order detail insert failed. OrderGuid={OrderGuid} LineCount={LineCount} Stage={Stage} Lines={LineDiagnostics}",
            plan.Order.OrderGuid,
            plan.Lines.Count,
            "sales_order_detail_insert",
            BuildSalesOrderDetailDiagnosticsText(plan.Lines));
    }

    private static TextDiagnostic BuildTextDiagnostic(string? value)
    {
        return new TextDiagnostic(value?.Length ?? 0, Preview(value));
    }

    private static string Preview(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var preview = value.Length <= LogPreviewMaxLength
            ? value
            : value[..LogPreviewMaxLength];
        return RemoveLogControlChars(preview);
    }

    private static string RemoveLogControlChars(string value)
    {
        return new string(value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
    }

}

internal sealed record TextDiagnostic(int Length, string Preview);

internal sealed record SalesOrderDetailInsertDiagnostic(
    string? OrderDetailGuid,
    TextDiagnostic ProductCode,
    TextDiagnostic ReferenceGUID,
    TextDiagnostic ProductName,
    TextDiagnostic Barcode,
    TextDiagnostic Remark,
    TextDiagnostic CreatedBy,
    TextDiagnostic UpdatedBy);
