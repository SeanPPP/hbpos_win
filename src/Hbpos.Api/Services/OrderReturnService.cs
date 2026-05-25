using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Services;

public interface IOrderReturnService
{
    Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken);

    Task<OrderReturnRecordCreateResponse> CreateRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken);
}

public sealed class OrderReturnService(
    IOrderHistoryRepository orderHistoryRepository,
    IOrderReturnRepository returnRepository) : IOrderReturnService
{
    public async Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var order = await orderHistoryRepository.GetDetailsAsync(orderGuid, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var records = await returnRepository.GetByOriginalOrderGuidAsync(orderGuid, cancellationToken);
        return new OrderReturnContextDto(order, records.Select(MapRecord).ToList());
    }

    public async Task<OrderReturnRecordCreateResponse> CreateRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("Return records cannot be empty.");
        }

        await ValidateReceiptReturnsAsync(request, cancellationToken);

        var now = DateTime.UtcNow;
        var records = request.Lines.Select(line => new SalesReturnRecord
        {
            ReturnDetailGuid = Guid.NewGuid().ToString("D"),
            ReturnOrderGuid = request.ReturnOrderGuid.ToString("D"),
            OriginalOrderGuid = line.OriginalOrderGuid?.ToString("D") ?? string.Empty,
            OriginalOrderDetailGuid = line.OriginalOrderDetailGuid?.ToString("D") ?? string.Empty,
            ProductCode = line.ProductCode,
            ReferenceGUID = line.ReferenceCode ?? string.Empty,
            ReturnQuantity = line.ReturnQuantity,
            ReturnAmount = line.ReturnAmount,
            StaffCode = request.CashierId,
            CreatedBy = request.CashierId,
            CreatedTime = now,
            UpdatedBy = request.CashierId,
            UpdatedTime = now
        }).ToList();

        await returnRepository.InsertAsync(records, cancellationToken);
        return new OrderReturnRecordCreateResponse(request.ReturnOrderGuid, records.Select(MapRecord).ToList());
    }

    private async Task ValidateReceiptReturnsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var line in request.Lines)
        {
            if (line.ReturnQuantity <= 0m)
            {
                throw new InvalidOperationException("Return quantity must be greater than zero.");
            }

            if (line.ReturnAmount < 0m)
            {
                throw new InvalidOperationException("Return amount cannot be negative.");
            }
        }

        var receiptGroups = request.Lines
            .Where(line => line.OriginalOrderGuid is not null && line.OriginalOrderDetailGuid is not null)
            .GroupBy(line => line.OriginalOrderGuid!.Value)
            .ToList();

        foreach (var orderGroup in receiptGroups)
        {
            var order = await orderHistoryRepository.GetDetailsAsync(orderGroup.Key, cancellationToken)
                ?? throw new InvalidOperationException("Original order was not found.");
            var existingRecords = await returnRepository.GetByOriginalOrderGuidAsync(orderGroup.Key, cancellationToken);

            foreach (var lineGroup in orderGroup.GroupBy(line => line.OriginalOrderDetailGuid!.Value))
            {
                var originalLine = order.Lines.FirstOrDefault(line => line.OrderLineGuid == lineGroup.Key)
                    ?? throw new InvalidOperationException("Original order line was not found.");
                var existingQuantity = existingRecords
                    .Where(record => TryParseGuid(record.OriginalOrderDetailGuid) == lineGroup.Key)
                    .Sum(record => record.ReturnQuantity ?? 0m);
                var requestedQuantity = lineGroup.Sum(line => line.ReturnQuantity);
                if (existingQuantity + requestedQuantity > originalLine.Quantity)
                {
                    throw new InvalidOperationException("Return quantity exceeds the available original order quantity.");
                }
            }
        }
    }

    private static OrderReturnRecordDto MapRecord(SalesReturnRecord record)
    {
        return new OrderReturnRecordDto(
            TryParseGuid(record.ReturnDetailGuid) ?? Guid.Empty,
            TryParseGuid(record.ReturnOrderGuid),
            TryParseGuid(record.OriginalOrderGuid),
            TryParseGuid(record.OriginalOrderDetailGuid),
            record.ProductCode ?? string.Empty,
            string.IsNullOrWhiteSpace(record.ReferenceGUID) ? null : record.ReferenceGUID,
            record.ReturnQuantity ?? 0m,
            record.ReturnAmount ?? 0m,
            record.StaffCode ?? string.Empty,
            ToDateTimeOffset(record.CreatedTime));
    }

    private static Guid? TryParseGuid(string? value)
    {
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime? value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value ?? DateTime.MinValue, DateTimeKind.Utc));
    }
}

public interface IOrderReturnRepository
{
    Task<IReadOnlyList<SalesReturnRecord>> GetByOriginalOrderGuidAsync(
        Guid originalOrderGuid,
        CancellationToken cancellationToken);

    Task InsertAsync(
        IReadOnlyList<SalesReturnRecord> records,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarOrderReturnRepository(HbposSqlSugarContext dbContext) : IOrderReturnRepository
{
    public async Task<IReadOnlyList<SalesReturnRecord>> GetByOriginalOrderGuidAsync(
        Guid originalOrderGuid,
        CancellationToken cancellationToken)
    {
        var originalOrderGuidText = originalOrderGuid.ToString("D");
        return await dbContext.PosmDb.Queryable<SalesReturnRecord>()
            .Where(x => x.OriginalOrderGuid == originalOrderGuidText)
            .ToListAsync(cancellationToken);
    }

    public async Task InsertAsync(
        IReadOnlyList<SalesReturnRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        await dbContext.PosmDb.Insertable(records.ToList()).ExecuteCommandAsync(cancellationToken);
    }
}
