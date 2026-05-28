using System.Data;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Data;
using Hbpos.Contracts.Orders;
using SqlSugar;

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

        var records = SortReturnRecords(await returnRepository.GetByOriginalOrderGuidAsync(orderGuid, cancellationToken));
        var lineCapacities = BuildLineCapacities(order, records);
        var paymentCapacities = await BuildPaymentCapacitiesAsync(order, records, cancellationToken);
        return new OrderReturnContextDto(
            order,
            records.Select(MapRecord).ToList(),
            lineCapacities,
            paymentCapacities);
    }

    public async Task<OrderReturnRecordCreateResponse> CreateRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("Return records cannot be empty.");
        }

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

        var persistedRecords = await returnRepository.InsertValidatedAsync(records, cancellationToken);
        return new OrderReturnRecordCreateResponse(request.ReturnOrderGuid, persistedRecords.Select(MapRecord).ToList());
    }

    private static IReadOnlyList<OrderReturnLineCapacityDto> BuildLineCapacities(
        OrderHistoryDetailsDto order,
        IReadOnlyList<SalesReturnRecord> records)
    {
        return order.Lines
            .Select(line =>
            {
                var returnedAmount = records
                    .Where(record => TryParseGuid(record.OriginalOrderDetailGuid) == line.OrderLineGuid)
                    .Sum(record => record.ReturnAmount ?? 0m);
                var remainingAmount = Math.Max(0m, line.ActualAmount - returnedAmount);
                return new OrderReturnLineCapacityDto(
                    line.OrderLineGuid,
                    line.ActualAmount,
                    returnedAmount,
                    remainingAmount);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<OrderReturnPaymentCapacityDto>> BuildPaymentCapacitiesAsync(
        OrderHistoryDetailsDto order,
        IReadOnlyList<SalesReturnRecord> records,
        CancellationToken cancellationToken)
    {
        var capacities = order.Payments
            .Where(payment => payment.Amount > 0m)
            .GroupBy(GetOriginalPaymentCapacityKey)
            .ToDictionary(
                group => group.Key,
                group => new PaymentCapacityAccumulator(
                    group.Key.Method,
                    group.Key.Reference,
                    order.OrderGuid,
                    group.Sum(payment => payment.Amount),
                    group
                        .Where(payment => payment.Method == PaymentMethodKind.Card)
                        .SelectMany(payment => payment.CardTransactions ?? [])
                        .ToList()));

        var returnOrderGuids = records
            .Select(record => TryParseGuid(record.ReturnOrderGuid))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var consumedCapacitiesByOrder = new Dictionary<Guid, Dictionary<PaymentCapacityKey, decimal>>();

        foreach (var returnOrderGuid in returnOrderGuids)
        {
            var returnOrder = await orderHistoryRepository.GetDetailsAsync(returnOrderGuid, cancellationToken);
            if (returnOrder is null)
            {
                continue;
            }

            var globalAllocations = await AllocateReturnOrderPaymentsAsync(
                returnOrderGuid,
                returnOrder,
                consumedCapacitiesByOrder,
                cancellationToken);
            if (!globalAllocations.TryGetValue(order.OrderGuid, out var allocatedByCapacity))
            {
                continue;
            }

            foreach (var (capacityKey, allocatedAmount) in allocatedByCapacity)
            {
                if (allocatedAmount > 0m && capacities.TryGetValue(capacityKey, out var capacity))
                {
                    capacity.RefundedAmount += allocatedAmount;
                }
            }
        }

        return capacities.Values
            .Select(capacity => capacity.ToDto())
            .ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, Dictionary<PaymentCapacityKey, decimal>>> AllocateReturnOrderPaymentsAsync(
        Guid returnOrderGuid,
        OrderHistoryDetailsDto returnOrder,
        Dictionary<Guid, Dictionary<PaymentCapacityKey, decimal>> consumedCapacitiesByOrder,
        CancellationToken cancellationToken)
    {
        var returnRecords = SortReturnRecords(await returnRepository.GetByReturnOrderGuidAsync(returnOrderGuid, cancellationToken));
        var originalOrderSequence = returnRecords
            .Select(record => TryParseGuid(record.OriginalOrderGuid))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (originalOrderSequence.Count == 0)
        {
            return new Dictionary<Guid, Dictionary<PaymentCapacityKey, decimal>>();
        }

        var originalOrderCache = new Dictionary<Guid, OrderHistoryDetailsDto?>();
        var legacyBlockedCardOrders = new HashSet<Guid>();
        var keyedRefundPayments = new List<(OrderHistoryPaymentDto Payment, PaymentCapacityKey Key)>();
        foreach (var refundPayment in returnOrder.Payments.Where(payment => payment.Amount < 0m))
        {
            var key = await TryGetRefundPaymentCapacityKeyAsync(
                refundPayment,
                originalOrderSequence,
                originalOrderCache,
                cancellationToken);
            if (key is null)
            {
                foreach (var originalOrderGuid in originalOrderSequence)
                {
                    legacyBlockedCardOrders.Add(originalOrderGuid);
                }

                continue;
            }

            keyedRefundPayments.Add((refundPayment, key.Value));
        }

        var refundCapacityOrder = keyedRefundPayments
            .Select(item => item.Key)
            .Distinct()
            .ToList();
        var remainingRefundPools = keyedRefundPayments
            .GroupBy(item => item.Key)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => Math.Abs(item.Payment.Amount)));
        var allocations = new Dictionary<Guid, Dictionary<PaymentCapacityKey, decimal>>();

        foreach (var originalOrderGuid in originalOrderSequence)
        {
            if (!originalOrderCache.TryGetValue(originalOrderGuid, out var originalOrder))
            {
                originalOrder = await orderHistoryRepository.GetDetailsAsync(originalOrderGuid, cancellationToken);
                originalOrderCache[originalOrderGuid] = originalOrder;
            }

            if (originalOrder is null)
            {
                continue;
            }

            var remainingAmountForOrder = returnRecords
                .Where(record => TryParseGuid(record.OriginalOrderGuid) == originalOrderGuid)
                .Sum(record => record.ReturnAmount ?? 0m);
            if (remainingAmountForOrder <= 0m)
            {
                continue;
            }

            var remainingOrderCapacities = originalOrder.Payments
                .Where(payment => payment.Amount > 0m)
                .GroupBy(GetOriginalPaymentCapacityKey)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var consumedAmount = consumedCapacitiesByOrder.GetValueOrDefault(originalOrderGuid)?.GetValueOrDefault(group.Key, 0m) ?? 0m;
                        return Math.Max(0m, group.Sum(payment => payment.Amount) - consumedAmount);
                    });
            var allocationByCapacity = new Dictionary<PaymentCapacityKey, decimal>();
            if (legacyBlockedCardOrders.Contains(originalOrderGuid))
            {
                foreach (var cardCapacityKey in remainingOrderCapacities.Keys
                    .Where(key => key.Method == PaymentMethodKind.Card)
                    .ToList())
                {
                    var remainingOrderCapacity = remainingOrderCapacities[cardCapacityKey];
                    if (remainingOrderCapacity <= 0m)
                    {
                        continue;
                    }

                    allocationByCapacity[cardCapacityKey] = allocationByCapacity.GetValueOrDefault(cardCapacityKey) + remainingOrderCapacity;
                    remainingOrderCapacities[cardCapacityKey] = 0m;
                }
            }

            foreach (var capacityKey in refundCapacityOrder)
            {
                if (remainingAmountForOrder <= 0m)
                {
                    break;
                }

                if (!remainingRefundPools.TryGetValue(capacityKey, out var remainingRefundPool)
                    || remainingRefundPool <= 0m)
                {
                    continue;
                }

                if (!remainingOrderCapacities.TryGetValue(capacityKey, out var remainingOrderCapacity)
                    || remainingOrderCapacity <= 0m)
                {
                    continue;
                }

                var appliedAmount = Math.Min(remainingAmountForOrder, Math.Min(remainingRefundPool, remainingOrderCapacity));
                if (appliedAmount <= 0m)
                {
                    continue;
                }

                allocationByCapacity[capacityKey] = allocationByCapacity.GetValueOrDefault(capacityKey) + appliedAmount;
                remainingAmountForOrder -= appliedAmount;
                remainingRefundPools[capacityKey] -= appliedAmount;
                remainingOrderCapacities[capacityKey] -= appliedAmount;
            }

            if (allocationByCapacity.Count > 0)
            {
                allocations[originalOrderGuid] = allocationByCapacity;
                if (!consumedCapacitiesByOrder.TryGetValue(originalOrderGuid, out var consumedByMethod))
                {
                    consumedByMethod = new Dictionary<PaymentCapacityKey, decimal>();
                    consumedCapacitiesByOrder[originalOrderGuid] = consumedByMethod;
                }

                foreach (var (capacityKey, amount) in allocationByCapacity)
                {
                    consumedByMethod[capacityKey] = consumedByMethod.GetValueOrDefault(capacityKey) + amount;
                }
            }
        }

        return allocations;
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

    private static string? NormalizeReference(string? reference)
    {
        return string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
    }

    private static IReadOnlyList<SalesReturnRecord> SortReturnRecords(IReadOnlyList<SalesReturnRecord> records)
    {
        return records
            .OrderBy(record => record.CreatedTime ?? DateTime.MinValue)
            .ThenBy(record => record.ReturnDetailGuid ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PaymentCapacityKey GetOriginalPaymentCapacityKey(OrderHistoryPaymentDto payment)
    {
        return new PaymentCapacityKey(
            payment.Method,
            payment.Method == PaymentMethodKind.Card ? NormalizeReference(payment.Reference) : null);
    }

    private async Task<PaymentCapacityKey?> TryGetRefundPaymentCapacityKeyAsync(
        OrderHistoryPaymentDto payment,
        IReadOnlyList<Guid> originalOrderSequence,
        Dictionary<Guid, OrderHistoryDetailsDto?> originalOrderCache,
        CancellationToken cancellationToken)
    {
        if (payment.Method != PaymentMethodKind.Card)
        {
            return new PaymentCapacityKey(payment.Method, null);
        }

        if (CardRefundReference.TryGetOriginalReference(payment.Reference, out var originalReference))
        {
            return new PaymentCapacityKey(payment.Method, NormalizeReference(originalReference));
        }

        if (originalOrderSequence.Count != 1)
        {
            return null;
        }

        var originalOrderGuid = originalOrderSequence[0];
        if (!originalOrderCache.TryGetValue(originalOrderGuid, out var originalOrder))
        {
            originalOrder = await orderHistoryRepository.GetDetailsAsync(originalOrderGuid, cancellationToken);
            originalOrderCache[originalOrderGuid] = originalOrder;
        }

        var cardReferences = originalOrder?.Payments
            .Where(originalPayment => originalPayment.Method == PaymentMethodKind.Card && originalPayment.Amount > 0m)
            .Select(originalPayment => NormalizeReference(originalPayment.Reference))
            .Where(reference => reference is not null)
            .Select(reference => reference!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        return cardReferences.Count == 1
            ? new PaymentCapacityKey(payment.Method, cardReferences[0])
            : null;
    }

    private readonly record struct PaymentCapacityKey(PaymentMethodKind Method, string? Reference);

    private sealed class PaymentCapacityAccumulator(
        PaymentMethodKind method,
        string? reference,
        Guid originalOrderGuid,
        decimal originalAmount,
        IReadOnlyList<CardTransactionDto> cardTransactions)
    {
        public PaymentMethodKind Method { get; } = method;

        public string? Reference { get; } = reference;

        public Guid OriginalOrderGuid { get; } = originalOrderGuid;

        public decimal OriginalAmount { get; } = originalAmount;

        public decimal RefundedAmount { get; set; }

        public IReadOnlyList<CardTransactionDto> CardTransactions { get; } = cardTransactions;

        public OrderReturnPaymentCapacityDto ToDto()
        {
            var refundedAmount = Math.Min(RefundedAmount, OriginalAmount);
            return new OrderReturnPaymentCapacityDto(
                Method,
                OriginalAmount,
                refundedAmount,
                Math.Max(0m, OriginalAmount - refundedAmount),
                Reference,
                CardTransactions.Count == 0 ? null : CardTransactions,
                OriginalOrderGuid);
        }
    }
}

internal sealed record OrderReturnOriginalOrder(
    Guid OrderGuid,
    IReadOnlyList<OrderReturnOriginalLine> Lines);

internal sealed record OrderReturnOriginalLine(
    Guid OrderLineGuid,
    decimal Quantity,
    decimal ActualAmount);

internal static class OrderReturnRecordValidator
{
    public static async Task ValidateAsync(
        IReadOnlyList<SalesReturnRecord> requestedRecords,
        Func<Guid, CancellationToken, Task<OrderReturnOriginalOrder?>> getOriginalOrderAsync,
        Func<Guid, CancellationToken, Task<IReadOnlyList<SalesReturnRecord>>> getExistingRecordsAsync,
        CancellationToken cancellationToken)
    {
        foreach (var record in requestedRecords)
        {
            if ((record.ReturnQuantity ?? 0m) <= 0m)
            {
                throw new InvalidOperationException("Return quantity must be greater than zero.");
            }

            if ((record.ReturnAmount ?? 0m) < 0m)
            {
                throw new InvalidOperationException("Return amount cannot be negative.");
            }
        }

        var receiptGroups = requestedRecords
            .Select(record => new
            {
                Record = record,
                OriginalOrderGuid = TryParseGuid(record.OriginalOrderGuid),
                OriginalOrderDetailGuid = TryParseGuid(record.OriginalOrderDetailGuid)
            })
            .Where(item => item.OriginalOrderGuid is not null && item.OriginalOrderDetailGuid is not null)
            .GroupBy(item => item.OriginalOrderGuid!.Value)
            .ToList();

        var requestedReturnOrderGuids = requestedRecords
            .Select(record => TryParseGuid(record.ReturnOrderGuid))
            .OfType<Guid>()
            .ToHashSet();

        foreach (var orderGroup in receiptGroups)
        {
            var order = await getOriginalOrderAsync(orderGroup.Key, cancellationToken)
                ?? throw new InvalidOperationException("Original order was not found.");
            var existingRecords = await getExistingRecordsAsync(orderGroup.Key, cancellationToken);

            foreach (var lineGroup in orderGroup.GroupBy(item => item.OriginalOrderDetailGuid!.Value))
            {
                var originalLine = order.Lines.FirstOrDefault(line => line.OrderLineGuid == lineGroup.Key)
                    ?? throw new InvalidOperationException("Original order line was not found.");
                var existingRecordsForLine = existingRecords
                    .Where(record => TryParseGuid(record.OriginalOrderDetailGuid) == lineGroup.Key)
                    .Where(record =>
                    {
                        var returnOrderGuid = TryParseGuid(record.ReturnOrderGuid);
                        return returnOrderGuid is null || !requestedReturnOrderGuids.Contains(returnOrderGuid.Value);
                    })
                    .ToList();
                var existingQuantity = existingRecordsForLine.Sum(record => record.ReturnQuantity ?? 0m);
                var requestedQuantity = lineGroup.Sum(item => item.Record.ReturnQuantity ?? 0m);
                if (existingQuantity + requestedQuantity > Math.Abs(originalLine.Quantity))
                {
                    throw new InvalidOperationException("Return quantity exceeds the available original order quantity.");
                }

                var existingAmount = existingRecordsForLine.Sum(record => record.ReturnAmount ?? 0m);
                var requestedAmount = lineGroup.Sum(item => item.Record.ReturnAmount ?? 0m);
                if (existingAmount + requestedAmount > Math.Abs(originalLine.ActualAmount))
                {
                    throw new InvalidOperationException("Return amount exceeds the available original order amount.");
                }
            }
        }
    }

    private static Guid? TryParseGuid(string? value)
    {
        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}

internal sealed record SalesReturnRecordInsertPreparation(
    IReadOnlyList<SalesReturnRecord> RecordsToInsert,
    IReadOnlyList<SalesReturnRecord> ExistingRecords);

internal static class SalesReturnRecordPersistence
{
    public static Task BeginSerializableTransactionAsync(
        ISqlSugarClient db)
    {
        return db.Ado.BeginTranAsync(IsolationLevel.Serializable);
    }

    public static async Task<SalesReturnRecordInsertPreparation> PrepareValidatedInsertAsync(
        ISqlSugarClient db,
        IReadOnlyList<SalesReturnRecord> requestedRecords,
        CancellationToken cancellationToken,
        IReadOnlyList<PaymentDetail>? requestedPayments = null)
    {
        if (requestedRecords.Count == 0)
        {
            return new SalesReturnRecordInsertPreparation([], []);
        }

        var existingRecordsForReturnOrder = await GetExistingRecordsForReturnOrdersAsync(
            db,
            requestedRecords,
            cancellationToken);
        if (existingRecordsForReturnOrder.Count > 0)
        {
            return new SalesReturnRecordInsertPreparation([], existingRecordsForReturnOrder);
        }

        await OrderReturnRecordValidator.ValidateAsync(
            requestedRecords,
            (orderGuid, token) => GetOriginalOrderForReturnValidationAsync(db, orderGuid, token),
            (orderGuid, token) => GetExistingReturnRecordsForOriginalOrderAsync(db, orderGuid, token),
            cancellationToken);

        await ValidateRefundPaymentCapacitiesAsync(
            db,
            requestedRecords,
            requestedPayments ?? [],
            cancellationToken);

        return new SalesReturnRecordInsertPreparation(requestedRecords, []);
    }

    public static async Task<IReadOnlyList<SalesReturnRecord>> InsertValidatedAsync(
        ISqlSugarClient db,
        IReadOnlyList<SalesReturnRecord> requestedRecords,
        CancellationToken cancellationToken)
    {
        if (requestedRecords.Count == 0)
        {
            return [];
        }

        await BeginSerializableTransactionAsync(db);
        try
        {
            var preparation = await PrepareValidatedInsertAsync(db, requestedRecords, cancellationToken);
            if (preparation.RecordsToInsert.Count > 0)
            {
                await db.Insertable(preparation.RecordsToInsert.ToList()).ExecuteCommandAsync(cancellationToken);
            }

            await db.Ado.CommitTranAsync();
            return preparation.ExistingRecords.Count > 0
                ? preparation.ExistingRecords
                : preparation.RecordsToInsert;
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<SalesReturnRecord>> GetExistingRecordsForReturnOrdersAsync(
        ISqlSugarClient db,
        IReadOnlyList<SalesReturnRecord> requestedRecords,
        CancellationToken cancellationToken)
    {
        var returnOrderGuids = requestedRecords
            .Select(record => Normalize(record.ReturnOrderGuid))
            .Where(returnOrderGuid => returnOrderGuid is not null)
            .Select(returnOrderGuid => returnOrderGuid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (returnOrderGuids.Count == 0)
        {
            return [];
        }

        var existingRecords = new List<SalesReturnRecord>();
        foreach (var returnOrderGuid in returnOrderGuids)
        {
            var records = await db.Queryable<SalesReturnRecord>()
                .Where(record => record.ReturnOrderGuid == returnOrderGuid)
                .ToListAsync(cancellationToken);
            existingRecords.AddRange(records);
        }

        return existingRecords;
    }

    private static async Task<OrderReturnOriginalOrder?> GetOriginalOrderForReturnValidationAsync(
        ISqlSugarClient db,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var orderGuidText = orderGuid.ToString("D");
        var orderExists = await db.Queryable<SalesOrder>()
            .AnyAsync(order => order.OrderGuid == orderGuidText, cancellationToken);
        if (!orderExists)
        {
            return null;
        }

        var lines = await db.Queryable<SalesOrderDetail>()
            .Where(line => line.OrderGuid == orderGuidText)
            .ToListAsync(cancellationToken);

        return new OrderReturnOriginalOrder(
            orderGuid,
            lines
                .Select(line => new
                {
                    OrderLineGuid = TryParseGuid(line.OrderDetailGuid),
                    Quantity = line.Quantity ?? 0,
                    ActualAmount = line.ActualAmount ?? 0m
                })
                .Where(line => line.OrderLineGuid is not null)
                .Select(line => new OrderReturnOriginalLine(
                    line.OrderLineGuid!.Value,
                    line.Quantity,
                    line.ActualAmount))
                .ToList());
    }

    private static async Task<IReadOnlyList<SalesReturnRecord>> GetExistingReturnRecordsForOriginalOrderAsync(
        ISqlSugarClient db,
        Guid orderGuid,
        CancellationToken cancellationToken)
    {
        var orderGuidText = orderGuid.ToString("D");
        return await db.Queryable<SalesReturnRecord>()
            .Where(record => record.OriginalOrderGuid == orderGuidText)
            .ToListAsync(cancellationToken);
    }

    private static async Task ValidateRefundPaymentCapacitiesAsync(
        ISqlSugarClient db,
        IReadOnlyList<SalesReturnRecord> requestedRecords,
        IReadOnlyList<PaymentDetail> requestedPayments,
        CancellationToken cancellationToken)
    {
        var cardPaymentMethod = (int)PaymentMethodKind.Card;
        var requestedCardRefundsByOriginalReference = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var payment in requestedPayments
            .Where(payment => payment.PaymentMethod == cardPaymentMethod)
            .Where(payment => (payment.Amount ?? 0m) < 0m))
        {
            if (!CardRefundReference.TryGetOriginalReference(payment.Reference, out var originalReference) ||
                Normalize(originalReference) is not { } normalizedOriginalReference)
            {
                throw new InvalidOperationException("Card refunds require an original card payment reference.");
            }

            requestedCardRefundsByOriginalReference[normalizedOriginalReference] =
                requestedCardRefundsByOriginalReference.GetValueOrDefault(normalizedOriginalReference) + Math.Abs(payment.Amount ?? 0m);
        }

        if (requestedCardRefundsByOriginalReference.Count == 0)
        {
            return;
        }

        var originalOrderGuids = requestedRecords
            .Select(record => TryParseGuid(record.OriginalOrderGuid))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (originalOrderGuids.Count == 0)
        {
            throw new InvalidOperationException("Card refunds require an original card payment.");
        }

        var requestedReturnAmountByOriginalOrder = requestedRecords
            .Select(record => new
            {
                OriginalOrderGuid = TryParseGuid(record.OriginalOrderGuid),
                ReturnAmount = Math.Abs(record.ReturnAmount ?? 0m)
            })
            .Where(item => item.OriginalOrderGuid is not null)
            .GroupBy(item => item.OriginalOrderGuid!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.ReturnAmount));
        var remainingByOriginalReference = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var originalOrdersByCardReference = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var originalOrderGuid in originalOrderGuids)
        {
            var orderGuidText = originalOrderGuid.ToString("D");
            var originalPayments = await db.Queryable<PaymentDetail>()
                .Where(payment => payment.OrderGuid == orderGuidText)
                .Where(payment => payment.Amount != null && payment.Amount > 0m)
                .Where(payment => payment.PaymentMethod == cardPaymentMethod)
                .ToListAsync(cancellationToken);
            var originalCardReferences = originalPayments
                .Select(payment => Normalize(payment.Reference))
                .Where(reference => reference is not null)
                .Select(reference => reference!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var payment in originalPayments)
            {
                if (Normalize(payment.Reference) is { } originalReference)
                {
                    remainingByOriginalReference[originalReference] =
                        remainingByOriginalReference.GetValueOrDefault(originalReference) + (payment.Amount ?? 0m);
                    if (!originalOrdersByCardReference.TryGetValue(originalReference, out var originalOrders))
                    {
                        originalOrders = [];
                        originalOrdersByCardReference[originalReference] = originalOrders;
                    }

                    originalOrders.Add(originalOrderGuid);
                }
            }

            var existingRecords = await GetExistingReturnRecordsForOriginalOrderAsync(db, originalOrderGuid, cancellationToken);
            var currentReturnOrderGuids = requestedRecords
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingReturnOrderGuids = existingRecords
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null && !currentReturnOrderGuids.Contains(returnOrderGuid))
                .Select(returnOrderGuid => returnOrderGuid!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var returnOrderGuid in existingReturnOrderGuids)
            {
                var existingReturnOrderOriginalGuids = existingRecords
                    .Where(record => string.Equals(Normalize(record.ReturnOrderGuid), returnOrderGuid, StringComparison.OrdinalIgnoreCase))
                    .Select(record => TryParseGuid(record.OriginalOrderGuid))
                    .OfType<Guid>()
                    .Distinct()
                    .ToList();
                var existingRefundPayments = await db.Queryable<PaymentDetail>()
                    .Where(payment => payment.OrderGuid == returnOrderGuid)
                    .Where(payment => payment.Amount != null && payment.Amount < 0m)
                    .Where(payment => payment.PaymentMethod == cardPaymentMethod)
                    .ToListAsync(cancellationToken);
                foreach (var payment in existingRefundPayments)
                {
                    if (CardRefundReference.TryGetOriginalReference(payment.Reference, out var originalReference) &&
                        Normalize(originalReference) is { } normalizedOriginalReference)
                    {
                        remainingByOriginalReference[normalizedOriginalReference] =
                            remainingByOriginalReference.GetValueOrDefault(normalizedOriginalReference) - Math.Abs(payment.Amount ?? 0m);
                    }
                    else if (existingReturnOrderOriginalGuids.Count == 1 && originalCardReferences.Count == 1)
                    {
                        remainingByOriginalReference[originalCardReferences[0]] =
                            remainingByOriginalReference.GetValueOrDefault(originalCardReferences[0]) - Math.Abs(payment.Amount ?? 0m);
                    }
                    else
                    {
                        foreach (var blockedOriginalReference in originalCardReferences)
                        {
                            remainingByOriginalReference[blockedOriginalReference] = 0m;
                        }
                    }
                }
            }
        }

        var requestedCardRefundsByOriginalOrder = new Dictionary<Guid, decimal>();
        foreach (var (originalReference, requestedAmount) in requestedCardRefundsByOriginalReference)
        {
            if (originalOrdersByCardReference.TryGetValue(originalReference, out var referenceOriginalOrders) &&
                referenceOriginalOrders.Count == 1)
            {
                var originalOrderGuid = referenceOriginalOrders.Single();
                requestedCardRefundsByOriginalOrder[originalOrderGuid] =
                    requestedCardRefundsByOriginalOrder.GetValueOrDefault(originalOrderGuid) + requestedAmount;
            }

            var remainingAmount = Math.Max(0m, remainingByOriginalReference.GetValueOrDefault(originalReference));
            if (requestedAmount > remainingAmount)
            {
                throw new InvalidOperationException("Card refund amount exceeds the available original card payment capacity.");
            }
        }

        foreach (var (originalOrderGuid, requestedAmount) in requestedCardRefundsByOriginalOrder)
        {
            var returnAmount = Math.Max(0m, requestedReturnAmountByOriginalOrder.GetValueOrDefault(originalOrderGuid));
            if (requestedAmount > returnAmount)
            {
                throw new InvalidOperationException("Card refund amount exceeds the return amount for the original card order.");
            }
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static Guid? TryParseGuid(string? value)
    {
        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}

public interface IOrderReturnRepository
{
    Task<IReadOnlyList<SalesReturnRecord>> GetByOriginalOrderGuidAsync(
        Guid originalOrderGuid,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesReturnRecord>> GetByReturnOrderGuidAsync(
        Guid returnOrderGuid,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SalesReturnRecord>> InsertValidatedAsync(
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

    public async Task<IReadOnlyList<SalesReturnRecord>> GetByReturnOrderGuidAsync(
        Guid returnOrderGuid,
        CancellationToken cancellationToken)
    {
        var returnOrderGuidText = returnOrderGuid.ToString("D");
        return await dbContext.PosmDb.Queryable<SalesReturnRecord>()
            .Where(x => x.ReturnOrderGuid == returnOrderGuidText)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SalesReturnRecord>> InsertValidatedAsync(
        IReadOnlyList<SalesReturnRecord> records,
        CancellationToken cancellationToken)
    {
        return await SalesReturnRecordPersistence.InsertValidatedAsync(
            dbContext.PosmDb,
            records,
            cancellationToken);
    }
}
