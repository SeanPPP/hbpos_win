using System.Globalization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface IReceiptReturnsWorkflowService
{
    Task<ReceiptReturnLookupResult> LookupOrderAsync(
        PosSessionState session,
        string orderQuery,
        CancellationToken cancellationToken = default);

    ReceiptReturnProductLookupResult LookupNoReceiptProduct(
        PosSessionState session,
        string productQuery);

    IReadOnlyList<CartLine> AddReturnLinesToCart(
        IEnumerable<PendingReturnLine> lines,
        IReadOnlyList<OrderReturnPaymentCapacityDto>? paymentCapacities = null);
}

public sealed record ReceiptReturnLookupResult(
    ReceiptReturnOrder? Order,
    bool IsRemote,
    bool ReturnRecordsMayBeStale,
    string StatusMessage);

public sealed record ReceiptReturnProductLookupResult(
    SellableItemDto? Item,
    string StatusMessage);

public sealed record ReceiptReturnOrder(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal ActualAmount,
    IReadOnlyList<ReceiptReturnOrderLine> Lines,
    IReadOnlyList<OrderReturnRecordDto> ReturnRecords,
    IReadOnlyList<OrderReturnPaymentCapacityDto> PaymentCapacities);

public sealed record ReceiptReturnOrderLine(
    Guid OrderLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    decimal OriginalQuantity,
    decimal UnitPrice,
    decimal OriginalActualAmount,
    decimal ReturnedQuantity)
{
    public decimal AvailableQuantity => Math.Max(0m, OriginalQuantity - ReturnedQuantity);

    public decimal ReturnUnitAmount => OriginalQuantity <= 0m
        ? UnitPrice
        : decimal.Round(OriginalActualAmount / OriginalQuantity, 2, MidpointRounding.AwayFromZero);
}

public sealed record PendingReturnLine(
    string StoreCode,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    string? ItemNumber,
    string? ProductImage,
    decimal Quantity,
    decimal UnitPrice,
    PriceSourceKind PriceSource,
    string PriceSourceLabel,
    string ReturnSourceKey,
    Guid? OriginalOrderGuid,
    Guid? OriginalOrderLineGuid);

public sealed class ReceiptReturnsWorkflowService(
    IReceiptQueryService receiptQueryService,
    ILocalOrderRepository localOrderRepository,
    IRemoteOrderHistoryService? remoteOrderHistoryService,
    LocalSellableItemIndex priceIndex,
    PosCartService cart,
    ILocalizationService? localization = null) : IReceiptReturnsWorkflowService
{
    public async Task<ReceiptReturnLookupResult> LookupOrderAsync(
        PosSessionState session,
        string orderQuery,
        CancellationToken cancellationToken = default)
    {
        var query = NormalizeQuery(orderQuery);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ReceiptReturnLookupResult(null, false, false, T("returns.status.lookupPrompt", "Scan or enter an order number."));
        }

        if (session.IsOnline && remoteOrderHistoryService is not null)
        {
            try
            {
                var remoteOrderGuid = await ResolveRemoteOrderGuidAsync(session, query, cancellationToken);
                if (remoteOrderGuid is not null)
                {
                    var context = await remoteOrderHistoryService.GetReturnContextAsync(remoteOrderGuid.Value, cancellationToken);
                    if (context is not null)
                    {
                        return new ReceiptReturnLookupResult(
                            MapRemote(context),
                            true,
                            false,
                            T("returns.status.loadedOnline", "Loaded online order and return records."));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var fallback = await LookupLocalOrderAsync(session, query, cancellationToken);
                return fallback.Order is null
                    ? new ReceiptReturnLookupResult(null, false, true, Format("returns.status.lookupFailed", "Online order lookup failed: {0}", ex.Message))
                    : fallback with
                    {
                        ReturnRecordsMayBeStale = true,
                        StatusMessage = Format("returns.status.loadedLocalStaleWithError", "Loaded local order; online return records may be stale. {0}", ex.Message)
                    };
            }
        }

        return await LookupLocalOrderAsync(session, query, cancellationToken);
    }

    public ReceiptReturnProductLookupResult LookupNoReceiptProduct(
        PosSessionState session,
        string productQuery)
    {
        var query = NormalizeQuery(productQuery);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ReceiptReturnProductLookupResult(null, T("returns.status.scanProduct", "Scan a product barcode."));
        }

        var exactMatches = priceIndex.FindExactMatches(session.StoreCode, query);
        var matches = exactMatches.Count > 0 ? exactMatches : priceIndex.Search(session.StoreCode, query, 8);
        var item = matches.FirstOrDefault();
        return item is null
            ? new ReceiptReturnProductLookupResult(null, T("returns.status.productNotFound", "Product was not found."))
            : new ReceiptReturnProductLookupResult(item, Format("returns.status.addedNoReceipt", "Added no-receipt return item: {0}", item.DisplayName));
    }

    public IReadOnlyList<CartLine> AddReturnLinesToCart(
        IEnumerable<PendingReturnLine> lines,
        IReadOnlyList<OrderReturnPaymentCapacityDto>? paymentCapacities = null)
    {
        var added = new List<CartLine>();
        foreach (var pending in lines)
        {
            added.Add(cart.AddReturnLine(new ReturnCartLineRequest(
                pending.StoreCode,
                pending.ProductCode,
                pending.ReferenceCode,
                pending.DisplayName,
                pending.LookupCode,
                pending.ItemNumber,
                pending.ProductImage,
                pending.Quantity,
                pending.UnitPrice,
                pending.PriceSource,
                pending.PriceSourceLabel,
                pending.ReturnSourceKey,
                pending.OriginalOrderGuid,
                pending.OriginalOrderLineGuid)));
        }

        cart.AddReturnPaymentCapacities(paymentCapacities ?? []);
        return added;
    }

    private async Task<Guid?> ResolveRemoteOrderGuidAsync(
        PosSessionState session,
        string query,
        CancellationToken cancellationToken)
    {
        if (TryParseOrderGuid(query, out var orderGuid))
        {
            return orderGuid;
        }

        if (remoteOrderHistoryService is null)
        {
            return null;
        }

        var result = await remoteOrderHistoryService.QueryAsync(
            new RemoteOrderHistoryQuery(
                session.StoreCode,
                SoldFrom: null,
                SoldTo: null,
                DeviceCode: null,
                Keyword: query,
                Take: 1),
            cancellationToken);
        return result.Orders.FirstOrDefault()?.OrderGuid;
    }

    private async Task<ReceiptReturnLookupResult> LookupLocalOrderAsync(
        PosSessionState session,
        string query,
        CancellationToken cancellationToken)
    {
        LocalOrder? order = null;
        if (TryParseOrderGuid(query, out var orderGuid))
        {
            order = await localOrderRepository.GetOrderAsync(orderGuid, cancellationToken);
        }

        if (order is null)
        {
            var summaries = await receiptQueryService.GetRecentOrdersAsync(
                new LocalOrderHistoryQuery(
                    DeviceCode: null,
                    Keyword: query),
                1,
                cancellationToken);
            var summary = summaries.FirstOrDefault(summary => string.Equals(summary.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase))
                ?? summaries.FirstOrDefault();
            if (summary is not null)
            {
                order = await localOrderRepository.GetOrderAsync(summary.OrderGuid, cancellationToken);
            }
        }

        return order is null
            ? new ReceiptReturnLookupResult(null, false, false, T("returns.status.orderNotFound", "Order was not found."))
            : new ReceiptReturnLookupResult(
                MapLocal(order),
                false,
                true,
                T("returns.status.loadedLocalStale", "Loaded local order; return records may be stale."));
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        return string.Format(
            localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
            localization?.T(key) ?? fallback,
            args);
    }

    private static ReceiptReturnOrder MapRemote(OrderReturnContextDto context)
    {
        var returnedByLine = context.ReturnRecords
            .Where(record => record.OriginalOrderDetailGuid is not null)
            .GroupBy(record => record.OriginalOrderDetailGuid!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(record => record.ReturnQuantity));

        return new ReceiptReturnOrder(
            context.Order.OrderGuid,
            context.Order.StoreCode,
            context.Order.DeviceCode,
            context.Order.CashierName,
            context.Order.SoldAt,
            context.Order.ActualAmount,
            context.Order.Lines.Select(line => new ReceiptReturnOrderLine(
                line.OrderLineGuid,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.Quantity,
                line.UnitPrice,
                line.ActualAmount,
                returnedByLine.TryGetValue(line.OrderLineGuid, out var returnedQuantity) ? returnedQuantity : 0m)).ToList(),
            context.ReturnRecords,
            context.PaymentCapacities ?? []);
    }

    private static ReceiptReturnOrder MapLocal(LocalOrder order)
    {
        return new ReceiptReturnOrder(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.ActualAmount,
            order.Lines.Select(line => new ReceiptReturnOrderLine(
                line.OrderLineGuid,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.Quantity,
                line.UnitPrice,
                line.ActualAmount,
                0m)).ToList(),
            [],
            BuildPaymentCapacities(order));
    }

    private static IReadOnlyList<OrderReturnPaymentCapacityDto> BuildPaymentCapacities(LocalOrder order)
    {
        return order.Payments
            .Where(payment => payment.Amount > 0m)
            .GroupBy(payment => new
            {
                payment.Method,
                payment.Reference
            })
            .Select(group => new OrderReturnPaymentCapacityDto(
                group.Key.Method,
                group.Sum(payment => payment.Amount),
                0m,
                group.Sum(payment => payment.Amount),
                group.Key.Reference))
            .ToList();
    }

    private static string NormalizeQuery(string? query)
    {
        return query?.Trim() ?? string.Empty;
    }

    private static bool TryParseOrderGuid(string query, out Guid orderGuid)
    {
        return Guid.TryParse(query, out orderGuid);
    }
}
