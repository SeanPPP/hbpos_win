using BlazorApp.Shared.Models.POSM;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Services;

public interface IOrderSyncPlanner
{
    OrderSyncPlan CreatePlan(OrderSyncRequest request);
}

public sealed class OrderSyncPlanner : IOrderSyncPlanner
{
    public OrderSyncPlan CreatePlan(OrderSyncRequest request)
    {
        var now = DateTime.UtcNow;
        var orderGuid = request.OrderGuid.ToString("D");
        var itemCount = request.Lines.Sum(x => (int)x.Quantity);

        var order = new SalesOrder
        {
            OrderGuid = orderGuid,
            OrderTime = request.SoldAt.UtcDateTime,
            BranchCode = request.StoreCode,
            DeviceCode = request.DeviceCode,
            TotalAmount = request.TotalAmount,
            DiscountAmount = request.DiscountAmount,
            ActualAmount = request.ActualAmount,
            ItemCount = itemCount,
            CashierId = request.CashierId,
            CashierName = request.CashierName,
            Status = 1,
            LastUploadTime = now,
            CreatedBy = request.CashierId,
            CreatedTime = now,
            UpdatedBy = request.CashierId,
            UpdatedTime = now
        };

        var lines = request.Lines.Select(line => new SalesOrderDetail
        {
            OrderDetailGuid = line.OrderLineGuid.ToString("D"),
            OrderGuid = orderGuid,
            ProductCode = line.ProductCode,
            ReferenceGUID = line.ReferenceCode ?? string.Empty,
            ProductName = line.DisplayName,
            Barcode = line.LookupCode,
            Price = line.UnitPrice,
            Quantity = (int)line.Quantity,
            Subtotal = line.Quantity * line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            ActualAmount = line.ActualAmount,
            CreatedBy = request.CashierId,
            CreatedTime = now,
            UpdatedBy = request.CashierId,
            UpdatedTime = now,
            LastUploadTime = now,
            Remark = string.IsNullOrWhiteSpace(line.ItemNumber)
                ? $"priceSource={(int)line.PriceSource}"
                : $"priceSource={(int)line.PriceSource};itemNo={line.ItemNumber.Trim()}"
        }).ToList();

        var payments = request.Payments.Select(payment => new PaymentDetail
        {
            PaymentGuid = payment.PaymentGuid.ToString("D"),
            OrderGuid = orderGuid,
            PaymentMethod = (int)payment.Method,
            Amount = payment.Amount,
            Reference = payment.Reference,
            CashierId = request.CashierId,
            CashierName = request.CashierName,
            CreatedBy = request.CashierId,
            CreatedTime = now,
            UpdatedBy = request.CashierId,
            UpdatedTime = now,
            LastUploadTime = now
        }).ToList();

        var bankTransactions = request.Payments
            .Where(payment => payment.Method == PaymentMethodKind.Card)
            .SelectMany(payment => (payment.CardTransactions ?? []).Select(transaction => new BankTransaction
            {
                Id = Guid.NewGuid(),
                PaymentGuid = payment.PaymentGuid.ToString("D"),
                OrderGuid = orderGuid,
                TxnRef = transaction.TxnRef,
                Caid = transaction.MerchantId,
                AuthCode = transaction.AuthCode,
                CardType = transaction.CardType,
                CardBIN = transaction.CardBin,
                CardNumber = transaction.MaskedCardNumber,
                BankDateTime = transaction.BankDateTime?.UtcDateTime,
                ResponseCode = transaction.ResponseCode,
                ResponseText = transaction.ResponseText,
                Stan = transaction.Stan,
                Amount = transaction.Amount,
                ReceiptText = Limit(transaction.ReceiptText, 1000)
            })).ToList();

        return new OrderSyncPlan(order, lines, payments, bankTransactions);
    }

    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

public sealed record OrderSyncPlan(
    SalesOrder Order,
    IReadOnlyList<SalesOrderDetail> Lines,
    IReadOnlyList<PaymentDetail> Payments,
    IReadOnlyList<BankTransaction> BankTransactions);
