using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public interface ISuspendedOrderService
{
    Task<SuspendedOrder> SuspendCurrentOrderAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingOrdersAsync(
        string storeCode,
        string? deviceCode = null,
        string? keyword = null,
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<SuspendedOrder?> GetOrderAsync(
        Guid suspendedOrderGuid,
        CancellationToken cancellationToken = default);

    Task<SuspendedOrder> RecallOrderAsync(
        Guid suspendedOrderGuid,
        CancellationToken cancellationToken = default);
}

public sealed class SuspendedOrderService(
    ISuspendedOrderRepository repository,
    PosCartService cart) : ISuspendedOrderService
{
    public async Task<SuspendedOrder> SuspendCurrentOrderAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        if (cart.IsEmpty)
        {
            throw new InvalidOperationException("Cart is empty.");
        }

        var orderGuid = Guid.NewGuid();
        var snapshot = cart.CreateSnapshot();
        var lines = snapshot.Lines
            .Select(line => new SuspendedOrderLine(
                Guid.NewGuid(),
                orderGuid,
                line.StoreCode,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.ProductImage,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.DiscountPercent,
                CalculateActualAmount(line),
                line.PriceSource,
                line.PriceSourceLabel)
            {
                Kind = line.Kind,
                ReturnSourceKey = line.ReturnSourceKey,
                OriginalOrderGuid = line.OriginalOrderGuid,
                OriginalOrderDetailGuid = line.OriginalOrderLineGuid,
                ReturnReason = line.ReturnReason
            })
            .ToArray();

        var order = new SuspendedOrder(
            orderGuid,
            session.StoreCode,
            session.DeviceCode,
            session.CashierId,
            session.CashierName,
            DateTimeOffset.Now,
            cart.TotalAmount,
            cart.DiscountAmount,
            cart.ActualAmount,
            SuspendedOrderStatus.Pending,
            lines)
        {
            ReturnPaymentCapacities = cart.ReturnPaymentCapacities.ToArray()
        };

        await repository.SaveAsync(order, cancellationToken);
        cart.Clear();
        return order;
    }

    public Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingOrdersAsync(
        string storeCode,
        string? deviceCode = null,
        string? keyword = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        return repository.GetPendingAsync(storeCode, deviceCode, keyword, take, cancellationToken);
    }

    public Task<SuspendedOrder?> GetOrderAsync(
        Guid suspendedOrderGuid,
        CancellationToken cancellationToken = default)
    {
        return repository.GetAsync(suspendedOrderGuid, cancellationToken);
    }

    public async Task<SuspendedOrder> RecallOrderAsync(
        Guid suspendedOrderGuid,
        CancellationToken cancellationToken = default)
    {
        if (!cart.IsEmpty)
        {
            throw new InvalidOperationException("Cart must be empty before recalling a suspended order.");
        }

        var order = await repository.GetAsync(suspendedOrderGuid, cancellationToken)
            ?? throw new InvalidOperationException("Suspended order was not found.");
        if (order.Status != SuspendedOrderStatus.Pending)
        {
            throw new InvalidOperationException("Suspended order is not pending.");
        }

        cart.RestoreSnapshot(new PosCartSnapshot(order.Lines
            .Select(line => new PosCartLineSnapshot(
                line.StoreCode,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.ProductImage,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.DiscountPercent,
                line.PriceSource,
                line.PriceSourceLabel,
                line.Kind,
                line.ReturnSourceKey,
                line.OriginalOrderGuid,
                line.OriginalOrderDetailGuid,
                line.ReturnReason))
            .ToArray()));
        cart.AddReturnPaymentCapacities(order.ReturnPaymentCapacities);
        await repository.MarkStatusAsync(suspendedOrderGuid, SuspendedOrderStatus.Recalled, cancellationToken);
        return order with { Status = SuspendedOrderStatus.Recalled };
    }

    private static decimal CalculateActualAmount(PosCartLineSnapshot line)
    {
        var actualAmount = decimal.Round(line.Quantity * line.UnitPrice - line.DiscountAmount, 2, MidpointRounding.AwayFromZero);
        return line.Kind == CartLineKind.Return ? -actualAmount : actualAmount;
    }
}
