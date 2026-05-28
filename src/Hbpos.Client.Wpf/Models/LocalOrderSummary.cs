using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Models;

public sealed record LocalOrderSummary(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    string SyncStatus,
    int LineCount,
    string PaymentSummary)
{
    public string ShortOrderId => OrderGuid.ToString("N")[..8].ToUpperInvariant();

    public string SoldAtDisplay => SoldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");

    public string StatusLabel => SyncStatus;
}

public sealed record ReceiptPreviewLine(
    string DisplayName,
    string LookupCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount)
{
    public string QuantityDisplay => Quantity.ToString("0.##");
}

public sealed record ReceiptPaymentLine(
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null)
{
    public string? DisplayReference => PaymentReferenceDisplay.Format(Method, Reference);

    public string? CardSummary => CardTransactions is { Count: > 0 }
        ? PaymentReferenceDisplay.FormatCardSummary(CardTransactions[0])
        : null;

    public string? ReceiptText => CardTransactions?
        .Select(transaction => transaction.ReceiptText)
        .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    public string MethodLabel => Method switch
    {
        PaymentMethodKind.Cash => "Cash",
        PaymentMethodKind.Card => PaymentReferenceDisplay.Format(Method, Reference)?.StartsWith("SQ", StringComparison.OrdinalIgnoreCase) == true
            ? "Square"
            : PaymentReferenceDisplay.Format(Method, Reference)?.StartsWith("ANZ:", StringComparison.OrdinalIgnoreCase) == true
                ? "ANZ Linkly"
                : "Card",
        PaymentMethodKind.Voucher => "Voucher",
        _ => Method.ToString()
    };
}

public static class PaymentReferenceDisplay
{
    public static string? Format(PaymentMethodKind method, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (method == PaymentMethodKind.Voucher)
        {
            var parts = reference.Split(':', StringSplitOptions.TrimEntries);
            return parts.Length >= 2 && parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase)
                ? parts[1]
                : reference;
        }

        return method == PaymentMethodKind.Card
            ? CardRefundReference.GetDisplayReference(reference)
            : reference;
    }

    public static string? FormatCardSummary(CardTransactionDto transaction)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(transaction.CardType) ? null : transaction.CardType.Trim(),
            string.IsNullOrWhiteSpace(transaction.MaskedCardNumber) ? null : transaction.MaskedCardNumber.Trim(),
            string.IsNullOrWhiteSpace(transaction.AuthCode) ? null : $"Auth {transaction.AuthCode.Trim()}",
            string.IsNullOrWhiteSpace(transaction.ResponseText) ? null : transaction.ResponseText.Trim()
        }.Where(part => !string.IsNullOrWhiteSpace(part));

        var summary = string.Join(" | ", parts);
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }
}
