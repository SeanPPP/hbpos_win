using System.Reflection;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

public sealed class PosmSchemaBoundaryTests
{
    private static readonly string[] SalesOrderCoreColumns =
    [
        nameof(SalesOrder.OrderGuid),
        nameof(SalesOrder.OrderTime),
        nameof(SalesOrder.BranchCode),
        nameof(SalesOrder.DeviceCode),
        nameof(SalesOrder.TotalAmount),
        nameof(SalesOrder.DiscountAmount),
        nameof(SalesOrder.ActualAmount),
        nameof(SalesOrder.ItemCount),
        nameof(SalesOrder.CashierId),
        nameof(SalesOrder.CashierName),
        nameof(SalesOrder.Status),
        nameof(SalesOrder.LastUploadTime),
        nameof(SalesOrder.Remark),
        nameof(SalesOrder.CreatedBy),
        nameof(SalesOrder.CreatedTime),
        nameof(SalesOrder.UpdatedBy),
        nameof(SalesOrder.UpdatedTime)
    ];

    private static readonly string[] ForbiddenSalesOrderAggregateColumns =
    [
        "CustomerCode",
        "InstallmentPaidAmount",
        "InstallmentRemainingAmount",
        "InstallmentStatus",
        "InstallmentCompletedTime",
        "RefundAmount",
        "RefundItemCount",
        "RefundStatus",
        "LastRefundTime",
        "TradeGuid",
        "OrderSourceType"
    ];

    [Fact]
    public void SalesOrder_model_keeps_only_core_persistent_columns()
    {
        var persistentProperties = typeof(SalesOrder)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<SqlSugar.SugarColumn>()?.IsIgnore != true)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // 主表只允许订单核心字段，退款和分期聚合必须从分表查询。
        Assert.Equal(SalesOrderCoreColumns.OrderBy(name => name, StringComparer.Ordinal), persistentProperties);
        Assert.DoesNotContain(persistentProperties, ForbiddenSalesOrderAggregateColumns.Contains);
    }

    [Fact]
    public void OrderSyncPlanner_routes_refunds_to_payment_and_return_tables()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var refundPaymentGuid = Guid.NewGuid();
        var refundLineGuid = Guid.NewGuid();
        var planner = new OrderSyncPlanner();

        var plan = planner.CreatePlan(new OrderSyncRequest(
            orderGuid,
            "S01",
            "POS01",
            "C01",
            "Alice",
            DateTimeOffset.Parse("2026-06-03T10:00:00+10:00"),
            -12.50m,
            0m,
            -12.50m,
            [
                new OrderLineSyncDto(
                    refundLineGuid,
                    "P01",
                    "REF01",
                    "Return Item",
                    "BAR01",
                    -1m,
                    12.50m,
                    0m,
                    -12.50m,
                    PriceSourceKind.StoreRetailPrice,
                    Kind: OrderLineKind.Return,
                    OriginalOrderGuid: originalOrderGuid,
                    OriginalOrderDetailGuid: originalDetailGuid)
            ],
            [
                new PaymentSyncDto(
                    refundPaymentGuid,
                    PaymentMethodKind.Card,
                    -12.50m,
                    CardRefundReference.Format("RFN-1", "SALE-1"),
                    CardTransactions:
                    [
                        new CardTransactionDto(
                            "Linkly",
                            "TXN-REFUND-1",
                            "AUTH1",
                            "Visa",
                            4,
                            "411111******1111",
                            "MID1",
                            "00",
                            "APPROVED",
                            "123456",
                            DateTimeOffset.Parse("2026-06-03T10:01:00+10:00"),
                            12.50m,
                            "receipt",
                            "RFN-1")
                    ])
            ]));

        Assert.Equal(orderGuid.ToString("D"), plan.Order.OrderGuid);
        Assert.Empty(plan.Lines);

        var payment = Assert.Single(plan.Payments);
        Assert.Equal(refundPaymentGuid.ToString("D"), payment.PaymentGuid);
        Assert.Equal(-12.50m, payment.Amount);

        var bankTransaction = Assert.Single(plan.BankTransactions);
        Assert.Equal(orderGuid.ToString("D"), bankTransaction.OrderGuid);
        Assert.Equal(refundPaymentGuid.ToString("D"), bankTransaction.PaymentGuid);
        Assert.Equal(-12.50m, bankTransaction.Amount);

        var returnRecord = Assert.Single(plan.ReturnRecords);
        Assert.Equal(refundLineGuid.ToString("D"), returnRecord.ReturnDetailGuid);
        Assert.Equal(orderGuid.ToString("D"), returnRecord.ReturnOrderGuid);
        Assert.Equal(originalOrderGuid.ToString("D"), returnRecord.OriginalOrderGuid);
        Assert.Equal(originalDetailGuid.ToString("D"), returnRecord.OriginalOrderDetailGuid);
        Assert.Equal(1m, returnRecord.ReturnQuantity);
        Assert.Equal(12.50m, returnRecord.ReturnAmount);
    }
}
