using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

public sealed class OrderReturnServiceTests
{
    [Fact]
    public async Task GetReturnContextAsync_ReturnsOrderAndRelatedReturnRecords()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = Guid.NewGuid().ToString("D"),
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 4.5m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                }
            ]
        };
        var service = new OrderReturnService(new FakeOrderHistoryRepository(CreateOrder(orderGuid, lineGuid, quantity: 2m)), repository);

        var context = await service.GetReturnContextAsync(orderGuid, CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal(orderGuid, context.Order.OrderGuid);
        var record = Assert.Single(context.ReturnRecords);
        Assert.Equal(lineGuid, record.OriginalOrderDetailGuid);
        Assert.Equal(1m, record.ReturnQuantity);
    }

    [Fact]
    public async Task CreateRecordsAsync_InsertsSalesReturnRecords()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository();
        var service = new OrderReturnService(new FakeOrderHistoryRepository(CreateOrder(orderGuid, lineGuid, quantity: 2m)), repository);

        var response = await service.CreateRecordsAsync(
            new OrderReturnRecordCreateRequest(
                returnOrderGuid,
                "S01",
                "POS-01",
                "C01",
                "Alice",
                [
                    new OrderReturnRecordCreateLineDto(
                        orderGuid,
                        lineGuid,
                        "SKU-01",
                        "REF-01",
                        1m,
                        4.5m)
                ]),
            CancellationToken.None);

        var inserted = Assert.Single(repository.InsertedRecords);
        Assert.Equal(returnOrderGuid.ToString("D"), inserted.ReturnOrderGuid);
        Assert.Equal(orderGuid.ToString("D"), inserted.OriginalOrderGuid);
        Assert.Equal(lineGuid.ToString("D"), inserted.OriginalOrderDetailGuid);
        Assert.Equal("C01", inserted.StaffCode);
        Assert.Equal(response.ReturnOrderGuid, returnOrderGuid);
    }

    [Fact]
    public async Task CreateRecordsAsync_RejectsReturnQuantityAboveAvailableQuantity()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ReturnQuantity = 1m
                }
            ]
        };
        var service = new OrderReturnService(new FakeOrderHistoryRepository(CreateOrder(orderGuid, lineGuid, quantity: 1m)), repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRecordsAsync(
            new OrderReturnRecordCreateRequest(
                Guid.NewGuid(),
                "S01",
                "POS-01",
                "C01",
                "Alice",
                [
                    new OrderReturnRecordCreateLineDto(
                        orderGuid,
                        lineGuid,
                        "SKU-01",
                        "REF-01",
                        1m,
                        4.5m)
                ]),
            CancellationToken.None));
    }

    private static OrderHistoryDetailsDto CreateOrder(Guid orderGuid, Guid lineGuid, decimal quantity)
    {
        return new OrderHistoryDetailsDto(
            orderGuid,
            "S01",
            "POS-01",
            "Alice",
            DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            9m,
            0m,
            9m,
            [
                new OrderHistoryLineDto(
                    lineGuid,
                    "SKU-01",
                    "REF-01",
                    "Tea",
                    "930001",
                    "ITEM-01",
                    quantity,
                    4.5m,
                    0m,
                    quantity * 4.5m)
            ],
            [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 9m, null)]);
    }

    private sealed class FakeOrderHistoryRepository(OrderHistoryDetailsDto order) : IOrderHistoryRepository
    {
        public Task<OrderHistoryQueryResponse> QueryAsync(
            OrderHistoryQueryRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new OrderHistoryQueryResponse([]));
        }

        public Task<OrderHistoryDetailsDto?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            return Task.FromResult(order.OrderGuid == orderGuid ? order : null);
        }
    }

    private sealed class FakeReturnRepository : IOrderReturnRepository
    {
        public IReadOnlyList<SalesReturnRecord> ExistingRecords { get; init; } = [];

        public List<SalesReturnRecord> InsertedRecords { get; } = [];

        public Task<IReadOnlyList<SalesReturnRecord>> GetByOriginalOrderGuidAsync(
            Guid originalOrderGuid,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ExistingRecords
                .Where(record => record.OriginalOrderGuid == originalOrderGuid.ToString("D"))
                .ToList()
                as IReadOnlyList<SalesReturnRecord>);
        }

        public Task InsertAsync(
            IReadOnlyList<SalesReturnRecord> records,
            CancellationToken cancellationToken)
        {
            InsertedRecords.AddRange(records);
            return Task.CompletedTask;
        }
    }
}
