using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public interface IReceiptQueryService
{
    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default);

    Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default);
}

public sealed class ReceiptQueryService(ILocalOrderRepository orderRepository) : IReceiptQueryService
{
    public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        return orderRepository.GetRecentOrdersAsync(take, cancellationToken);
    }

    public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
        LocalOrderHistoryQuery query,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        return orderRepository.GetRecentOrdersAsync(query, take, cancellationToken);
    }

    public async Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        var order = await orderRepository.GetOrderAsync(orderGuid, cancellationToken);
        return order is null ? null : CreateReceipt(order);
    }

    public async Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
    {
        var latest = (await orderRepository.GetRecentOrdersAsync(1, cancellationToken)).FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        return await GetReceiptAsync(latest.OrderGuid, cancellationToken);
    }

    public static ReceiptDetails CreateReceipt(LocalOrder order)
    {
        return new ReceiptDetails(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.Lines.Select(line => new ReceiptPreviewLine(
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount)).ToList(),
            order.Payments.Select(payment => new ReceiptPaymentLine(
                payment.Method,
                payment.Amount,
                payment.Reference)).ToList());
    }
}

public sealed record ReceiptDetails(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    IReadOnlyList<ReceiptPreviewLine> Lines,
    IReadOnlyList<ReceiptPaymentLine> Payments)
{
    public string TransactionIdDisplay => $"#{OrderGuid.ToString("N")[..10].ToUpperInvariant()}";

    public string SoldAtDisplay => SoldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");
}
