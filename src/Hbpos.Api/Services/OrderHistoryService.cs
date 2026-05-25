using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Services;

public interface IOrderHistoryService
{
    Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken);
}

public sealed class OrderHistoryService(IOrderHistoryRepository repository) : IOrderHistoryService
{
    public Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        return repository.QueryAsync(request, cancellationToken);
    }

    public Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        return repository.GetDetailsAsync(orderGuid, cancellationToken);
    }
}

public interface IOrderHistoryRepository
{
    Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken);

    Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarOrderHistoryRepository(HbposSqlSugarContext dbContext) : IOrderHistoryRepository
{
    public async Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken)
    {
        var storeCode = request.StoreCode.Trim();
        var query = dbContext.PosmDb.Queryable<SalesOrder>()
            .Where(x => x.BranchCode == storeCode);

        if (!string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            var deviceCode = request.DeviceCode.Trim();
            query = query.Where(x => x.DeviceCode == deviceCode);
        }

        if (request.SoldFrom is not null)
        {
            var soldFrom = request.SoldFrom.Value.UtcDateTime;
            query = query.Where(x => x.OrderTime >= soldFrom);
        }

        if (request.SoldTo is not null)
        {
            var soldTo = request.SoldTo.Value.UtcDateTime;
            query = query.Where(x => x.OrderTime <= soldTo);
        }

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim();
            var normalizedGuidKeyword = keyword.Replace("-", string.Empty, StringComparison.Ordinal);
            var lineOrderGuids = await dbContext.PosmDb.Queryable<SalesOrderDetail>()
            .Where(x => x.Barcode == keyword || (x.Remark != null && x.Remark.Contains($"itemNo={keyword}")))
                .Select(x => x.OrderGuid)
                .ToListAsync(cancellationToken);

            query = query.Where(x =>
                (x.OrderGuid != null && x.OrderGuid.Contains(keyword))
                || (x.OrderGuid != null && x.OrderGuid.Contains(normalizedGuidKeyword))
                || (x.OrderGuid != null && lineOrderGuids.Contains(x.OrderGuid)));
        }

        var take = Math.Clamp(request.Take, 1, 200);
        var orders = await query
            .OrderByDescending(x => x.OrderTime)
            .Take(take)
            .ToListAsync(cancellationToken);
        var orderGuids = orders.Select(x => x.OrderGuid).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var payments = orderGuids.Count == 0
            ? new List<PaymentDetail>()
            : await dbContext.PosmDb.Queryable<PaymentDetail>()
                .Where(x => orderGuids.Contains(x.OrderGuid))
                .ToListAsync(cancellationToken);

        var paymentLabels = payments
            .Where(x => !string.IsNullOrWhiteSpace(x.OrderGuid))
            .GroupBy(x => x.OrderGuid)
            .ToDictionary(
                x => x.Key!,
                x => string.Join(", ", x.Select(payment => ((PaymentMethodKind)payment.PaymentMethod).ToString()).Distinct()));

        return new OrderHistoryQueryResponse(orders.Select(order => new OrderHistorySummaryDto(
            ParseGuid(order.OrderGuid),
            order.BranchCode ?? string.Empty,
            order.DeviceCode ?? string.Empty,
            order.CashierName ?? string.Empty,
            ToDateTimeOffset(order.OrderTime),
            Amount(order.TotalAmount),
            Amount(order.DiscountAmount),
            Amount(order.ActualAmount),
            Count(order.ItemCount),
            order.OrderGuid is not null && paymentLabels.TryGetValue(order.OrderGuid, out var paymentSummary) ? paymentSummary : string.Empty,
            FormatStatus(order.Status ?? 0))).ToList());
    }

    public async Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var orderGuidText = orderGuid.ToString("D");
        var order = await dbContext.PosmDb.Queryable<SalesOrder>()
            .FirstAsync(x => x.OrderGuid == orderGuidText, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var lines = await dbContext.PosmDb.Queryable<SalesOrderDetail>()
            .Where(x => x.OrderGuid == orderGuidText)
            .ToListAsync(cancellationToken);
        var payments = await dbContext.PosmDb.Queryable<PaymentDetail>()
            .Where(x => x.OrderGuid == orderGuidText)
            .ToListAsync(cancellationToken);

        return new OrderHistoryDetailsDto(
            orderGuid,
            order.BranchCode ?? string.Empty,
            order.DeviceCode ?? string.Empty,
            order.CashierName ?? string.Empty,
            ToDateTimeOffset(order.OrderTime),
            Amount(order.TotalAmount),
            Amount(order.DiscountAmount),
            Amount(order.ActualAmount),
            lines.Select(line => new OrderHistoryLineDto(
                ParseGuid(line.OrderDetailGuid),
                line.ProductCode ?? string.Empty,
                line.ReferenceGUID,
                line.ProductName ?? string.Empty,
                line.Barcode ?? string.Empty,
                ExtractItemNo(line.Remark),
                Count(line.Quantity),
                Amount(line.Price),
                Amount(line.DiscountAmount),
                Amount(line.ActualAmount))).ToList(),
            payments.Select(payment => new OrderHistoryPaymentDto(
                ParseGuid(payment.PaymentGuid),
                (PaymentMethodKind)payment.PaymentMethod,
                Amount(payment.Amount),
                payment.Reference)).ToList());
    }

    private static Guid ParseGuid(string? value)
    {
        return Guid.TryParse(value, out var guid) ? guid : Guid.Empty;
    }

    private static string? ExtractItemNo(string? remark)
    {
        if (string.IsNullOrWhiteSpace(remark))
        {
            return null;
        }

        const string marker = "itemNo=";
        var index = remark.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = remark.IndexOf(';', start);
        return end < 0 ? remark[start..].Trim() : remark[start..end].Trim();
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime? value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value ?? DateTime.MinValue, DateTimeKind.Utc));
    }

    private static decimal Amount(decimal? value)
    {
        return value ?? 0m;
    }

    private static int Count(int? value)
    {
        return value ?? 0;
    }

    private static string FormatStatus(int status)
    {
        return status switch
        {
            1 => "Completed",
            2 => "Voided",
            _ => status.ToString()
        };
    }
}
