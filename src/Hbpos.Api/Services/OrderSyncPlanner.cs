using BlazorApp.Shared.Models.POSM;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Services;

public interface IOrderSyncPlanner
{
    OrderSyncPlan CreatePlan(OrderSyncRequest request);
}

public sealed class OrderSyncPlanner : IOrderSyncPlanner
{
    private const int ProductCodeMaxLength = 50;
    private const int ReferenceGuidMaxLength = 50;
    private const int ProductNameMaxLength = 255;
    private const int BarcodeMaxLength = 50;
    private const int DetailRemarkMaxLength = 50;
    private const int AuditUserMaxLength = 50;

    public OrderSyncPlan CreatePlan(OrderSyncRequest request)
    {
        var now = DateTime.UtcNow;
        var orderGuid = request.OrderGuid.ToString("D");
        var auditUser = BuildPosmAuditUser(request);
        var saleLines = request.Lines
            .Where(line => line.Kind == OrderLineKind.Sale)
            .ToList();
        var returnLines = request.Lines
            .Where(line => line.Kind == OrderLineKind.Return)
            .ToList();
        var itemCount = saleLines.Sum(x => (int)x.Quantity);

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
            CreatedBy = auditUser,
            CreatedTime = now,
            UpdatedBy = auditUser,
            UpdatedTime = now
        };

        var lines = saleLines.Select(line => new SalesOrderDetail
        {
            OrderDetailGuid = line.OrderLineGuid.ToString("D"),
            OrderGuid = orderGuid,
            ProductCode = CleanDetailText(line.ProductCode, ProductCodeMaxLength),
            ReferenceGUID = CleanDetailText(line.ReferenceCode, ReferenceGuidMaxLength),
            ProductName = CleanDetailText(line.DisplayName, ProductNameMaxLength),
            Barcode = CleanDetailText(line.LookupCode, BarcodeMaxLength),
            Price = line.UnitPrice,
            Quantity = (int)line.Quantity,
            Subtotal = line.Quantity * line.UnitPrice,
            DiscountAmount = line.DiscountAmount,
            ActualAmount = line.ActualAmount,
            CreatedBy = auditUser,
            CreatedTime = now,
            UpdatedBy = auditUser,
            UpdatedTime = now,
            LastUploadTime = now,
            Remark = BuildDetailRemark(line)
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
            CreatedBy = auditUser,
            CreatedTime = now,
            UpdatedBy = auditUser,
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
                Amount = payment.Amount < 0m
                    ? -Math.Abs(transaction.Amount)
                    : Math.Abs(transaction.Amount),
                ReceiptText = Limit(transaction.ReceiptText, 1000)
            })).ToList();

        var returnRecords = returnLines.Select(line => new SalesReturnRecord
        {
            ReturnDetailGuid = line.OrderLineGuid.ToString("D"),
            ReturnOrderGuid = orderGuid,
            OriginalOrderGuid = line.OriginalOrderGuid?.ToString("D") ?? string.Empty,
            OriginalOrderDetailGuid = line.OriginalOrderDetailGuid?.ToString("D") ?? string.Empty,
            ProductCode = line.ProductCode,
            ReferenceGUID = line.ReferenceCode ?? string.Empty,
            ReturnQuantity = Math.Abs(line.Quantity),
            ReturnAmount = Math.Abs(line.ActualAmount),
            StaffCode = request.CashierId,
            CreatedBy = auditUser,
            CreatedTime = now,
            UpdatedBy = auditUser,
            UpdatedTime = now
        }).ToList();

        return new OrderSyncPlan(order, lines, payments, bankTransactions, returnRecords);
    }

    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string CleanDetailText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string BuildDetailRemark(OrderLineSyncDto line)
    {
        var remark = string.IsNullOrWhiteSpace(line.ItemNumber)
            ? $"priceSource={(int)line.PriceSource}"
            : $"priceSource={(int)line.PriceSource};itemNo={line.ItemNumber.Trim()}";

        // 数据库侧存在旧触发器/字符串解析逻辑，明细备注必须在入库前限制长度。
        return CleanDetailText(remark, DetailRemarkMaxLength);
    }

    private static string BuildPosmAuditUser(OrderSyncRequest request)
    {
        var storeCode = CleanAuditToken(request.StoreCode);
        var deviceCode = CleanAuditToken(request.DeviceCode, string.Empty);
        var cashierId = CleanAuditToken(request.CashierId, string.Empty);
        if (TryUseExistingPosmDeviceCode(deviceCode, storeCode, out var auditUser))
        {
            return LimitAuditUser(auditUser);
        }

        var suffix = GetAuditSuffix(deviceCode);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = GetAuditSuffix(cashierId);
        }

        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = "CLIENT";
        }

        // sales_order_detail.ShopCode 计算列会从 CreatedBy 解析 POS_{门店}_{后缀}。
        return LimitAuditUser($"POS_{storeCode}_{suffix}");
    }

    private static bool TryUseExistingPosmDeviceCode(
        string deviceCode,
        string storeCode,
        out string auditUser)
    {
        auditUser = string.Empty;
        var parts = deviceCode.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 ||
            !string.Equals(parts[0], "POS", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[1], storeCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        auditUser = deviceCode;
        return true;
    }

    private static string GetAuditSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return CleanAuditToken(parts.Length == 0 ? value : parts[^1]);
    }

    private static string LimitAuditUser(string value)
    {
        if (value.Length <= AuditUserMaxLength)
        {
            return value;
        }

        var secondUnderscore = value.IndexOf('_', value.IndexOf('_') + 1);
        if (secondUnderscore < 0)
        {
            return value[..AuditUserMaxLength];
        }

        var prefix = value[..(secondUnderscore + 1)];
        if (prefix.Length >= AuditUserMaxLength)
        {
            return value[..AuditUserMaxLength];
        }

        var suffixLength = Math.Max(1, AuditUserMaxLength - prefix.Length);
        var suffix = value[(secondUnderscore + 1)..];
        return prefix + suffix[..Math.Min(suffix.Length, suffixLength)];
    }

    private static string CleanAuditToken(string? value, string fallback = "UNKNOWN")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var cleaned = new string(value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }
}

public sealed record OrderSyncPlan(
    SalesOrder Order,
    IReadOnlyList<SalesOrderDetail> Lines,
    IReadOnlyList<PaymentDetail> Payments,
    IReadOnlyList<BankTransaction> BankTransactions,
    IReadOnlyList<SalesReturnRecord> ReturnRecords);
