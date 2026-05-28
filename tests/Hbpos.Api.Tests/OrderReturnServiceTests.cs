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
        var returnOrderGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
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
        var service = new OrderReturnService(
            new FakeOrderHistoryRepository(
                CreateOrder(orderGuid, lineGuid, quantity: 2m, payments:
                [
                    new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 6m, null),
                    new OrderHistoryPaymentDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        3m,
                        "ANZ:SALE-1",
                        [
                            new CardTransactionDto(
                                "ANZ",
                                "SALE-1",
                                "123456",
                                "VISA",
                                4,
                                "****1234",
                                "MID-1",
                                "00",
                                "APPROVED",
                                "42",
                                DateTimeOffset.Parse("2026-05-25T10:05:00Z"),
                                3m,
                                "receipt")
                        ])
                ]),
                CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -3m, payments:
                [
                    new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, -2m, null),
                    new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -1m, CardRefundReference.Format("ANZ:REFUND-9", "ANZ:SALE-1"))
                ]),
                returnOrderGuid),
            repository);

        var context = await service.GetReturnContextAsync(orderGuid, CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal(orderGuid, context.Order.OrderGuid);
        var record = Assert.Single(context.ReturnRecords);
        Assert.Equal(lineGuid, record.OriginalOrderDetailGuid);
        Assert.Equal(1m, record.ReturnQuantity);
        var lineCapacities = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnLineCapacityDto>>(context.LineCapacities);
        var paymentCapacities = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(context.PaymentCapacities);
        var lineCapacity = Assert.Single(lineCapacities);
        Assert.Equal(9m, lineCapacity.OriginalAmount);
        Assert.Equal(4.5m, lineCapacity.ReturnedAmount);
        Assert.Equal(4.5m, lineCapacity.RemainingAmount);
        Assert.Equal(2, paymentCapacities.Count);
        var cashCapacity = Assert.Single(paymentCapacities, x => x.Method == PaymentMethodKind.Cash);
        Assert.Equal(6m, cashCapacity.OriginalAmount);
        Assert.Equal(2m, cashCapacity.RefundedAmount);
        Assert.Equal(4m, cashCapacity.RemainingAmount);
        var cardCapacity = Assert.Single(paymentCapacities, x => x.Method == PaymentMethodKind.Card);
        Assert.Equal("ANZ:SALE-1", cardCapacity.Reference);
        Assert.Equal(3m, cardCapacity.OriginalAmount);
        Assert.Equal(1m, cardCapacity.RefundedAmount);
        Assert.Equal(2m, cardCapacity.RemainingAmount);
        Assert.Single(cardCapacity.CardTransactions ?? []);
    }

    [Fact]
    public async Task CreateRecordsAsync_InsertsSalesReturnRecords()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var originalOrder = CreateOrder(orderGuid, lineGuid, quantity: 2m);
        var repository = new FakeReturnRepository
        {
            OriginalOrders = [CreateOriginalOrder(originalOrder)]
        };
        var service = new OrderReturnService(new FakeOrderHistoryRepository(originalOrder), repository);

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
        Assert.Equal(1, repository.InsertValidatedCallCount);
    }

    [Fact]
    public async Task CreateRecordsAsync_RejectsReturnQuantityAboveAvailableQuantity()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            OriginalOrders = [CreateOriginalOrder(CreateOrder(orderGuid, lineGuid, quantity: 1m))],
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

    [Fact]
    public async Task CreateRecordsAsync_ReturnsExistingRecordsForSameReturnOrderGuid()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var existingRecord = new SalesReturnRecord
        {
            ReturnDetailGuid = Guid.NewGuid().ToString("D"),
            ReturnOrderGuid = returnOrderGuid.ToString("D"),
            OriginalOrderGuid = orderGuid.ToString("D"),
            OriginalOrderDetailGuid = lineGuid.ToString("D"),
            ProductCode = "SKU-01",
            ReferenceGUID = "REF-01",
            ReturnQuantity = 1m,
            ReturnAmount = 4.5m,
            StaffCode = "C01",
            CreatedTime = DateTime.UtcNow
        };
        var repository = new FakeReturnRepository
        {
            ExistingRecords = [existingRecord]
        };
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

        Assert.Empty(repository.InsertedRecords);
        var returnedRecord = Assert.Single(response.ReturnRecords);
        Assert.Equal(returnOrderGuid, response.ReturnOrderGuid);
        Assert.Equal(existingRecord.ReturnDetailGuid, returnedRecord.ReturnDetailGuid.ToString("D"));
        Assert.Equal(1, repository.InsertValidatedCallCount);
    }

    [Fact]
    public async Task GetReturnContextAsync_MapsLegacyCardRefundToOnlyOriginalCard()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 3m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                }
            ]
        };
        var originalOrder = CreateOrder(orderGuid, lineGuid, quantity: 2m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 5m, "SQ:card-1")
        ]);
        var returnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -3m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -3m, "SQRF:legacy")
        ]) with { OrderGuid = returnOrderGuid };
        var service = new OrderReturnService(
            new FakeOrderHistoryRepository([originalOrder, returnOrder]),
            repository);

        var context = await service.GetReturnContextAsync(orderGuid, CancellationToken.None);

        var cardCapacity = Assert.Single(context!.PaymentCapacities!, x => x.Method == PaymentMethodKind.Card);
        Assert.Equal("SQ:card-1", cardCapacity.Reference);
        Assert.Equal(3m, cardCapacity.RefundedAmount);
        Assert.Equal(2m, cardCapacity.RemainingAmount);
    }

    [Fact]
    public async Task GetReturnContextAsync_BlocksAmbiguousLegacyCardRefundAcrossMultipleCards()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 3m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                }
            ]
        };
        var originalOrder = CreateOrder(orderGuid, lineGuid, quantity: 3m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 5m, "SQ:card-1"),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 7m, "SQ:card-2")
        ]);
        var returnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -3m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -3m, "SQRF:legacy")
        ]) with { OrderGuid = returnOrderGuid };
        var service = new OrderReturnService(
            new FakeOrderHistoryRepository([originalOrder, returnOrder]),
            repository);

        var context = await service.GetReturnContextAsync(orderGuid, CancellationToken.None);

        var cardCapacities = context!.PaymentCapacities!
            .Where(x => x.Method == PaymentMethodKind.Card)
            .OrderBy(x => x.Reference, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, cardCapacities.Count);
        Assert.All(cardCapacities, capacity => Assert.Equal(0m, capacity.RemainingAmount));
    }

    [Fact]
    public async Task CreateRecordsAsync_ConcurrentDifferentReturnOrdersUsesAtomicCapacity()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var originalOrder = CreateOrder(orderGuid, lineGuid, quantity: 1m);
        var repository = new FakeReturnRepository
        {
            OriginalOrders = [CreateOriginalOrder(originalOrder)]
        };
        var service = new OrderReturnService(new FakeOrderHistoryRepository(originalOrder), repository);
        var firstRequest = CreateReturnRequest(Guid.NewGuid(), orderGuid, lineGuid);
        var secondRequest = CreateReturnRequest(Guid.NewGuid(), orderGuid, lineGuid);

        var firstTask = CaptureCreateRecordsAsync(service, firstRequest);
        var secondTask = CaptureCreateRecordsAsync(service, secondRequest);
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.Exception is null);
        var exception = Assert.Single(results, result => result.Exception is not null).Exception;
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Return quantity exceeds the available original order quantity.", exception!.Message);
        Assert.Single(repository.InsertedRecords);
        Assert.Equal(2, repository.InsertValidatedCallCount);
    }

    [Fact]
    public async Task GetReturnContextAsync_OnlyCountsCurrentOrderShareWhenReturnOrderContainsMultipleOriginalOrders()
    {
        var currentOrderGuid = Guid.NewGuid();
        var otherOrderGuid = Guid.NewGuid();
        var currentLineGuid = Guid.NewGuid();
        var otherLineGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = currentOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = currentLineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 4m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = otherOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = otherLineGuid.ToString("D"),
                    ProductCode = "SKU-02",
                    ReturnQuantity = 1m,
                    ReturnAmount = 6m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                }
            ]
        };
        var service = new OrderReturnService(
            new FakeOrderHistoryRepository(
                [
                    CreateOrder(
                        currentOrderGuid,
                        currentLineGuid,
                        quantity: 2m,
                        payments:
                        [
                            new OrderHistoryPaymentDto(
                                Guid.NewGuid(),
                                PaymentMethodKind.Card,
                                10m,
                                "ANZ:SALE-1",
                                [
                                    new CardTransactionDto(
                                        "ANZ",
                                        "SALE-1",
                                        "123456",
                                        "VISA",
                                        4,
                                        "****1234",
                                        "MID-1",
                                        "00",
                                        "APPROVED",
                                        "42",
                                        DateTimeOffset.Parse("2026-05-25T10:05:00Z"),
                                        10m,
                                        "receipt")
                                ])
                        ]),
                    CreateOrder(otherOrderGuid, otherLineGuid, quantity: 2m, payments:
                    [
                        new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 10m, "ANZ:SALE-2")
                    ]),
                    (CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -10m, payments:
                    [
                        new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -10m, CardRefundReference.Format("ANZ:REFUND-9", "ANZ:SALE-1"))
                    ]) with { OrderGuid = returnOrderGuid })
                ]),
            repository);

        var context = await service.GetReturnContextAsync(currentOrderGuid, CancellationToken.None);

        var paymentCapacities = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(context!.PaymentCapacities);
        var cardCapacity = Assert.Single(paymentCapacities, x => x.Method == PaymentMethodKind.Card);
        Assert.Equal(10m, cardCapacity.OriginalAmount);
        Assert.Equal(4m, cardCapacity.RefundedAmount);
        Assert.Equal(6m, cardCapacity.RemainingAmount);
    }

    [Fact]
    public async Task GetReturnContextAsync_ReassignsRefundShareToLaterMethodWhenEarlierMethodCapacityIsExhausted()
    {
        var orderGuid = Guid.NewGuid();
        var otherOrderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var otherLineGuid = Guid.NewGuid();
        var firstReturnOrderGuid = Guid.NewGuid();
        var secondReturnOrderGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = firstReturnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 5m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = secondReturnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 5m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = secondReturnOrderGuid.ToString("D"),
                    OriginalOrderGuid = otherOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = otherLineGuid.ToString("D"),
                    ProductCode = "SKU-02",
                    ReturnQuantity = 1m,
                    ReturnAmount = 5m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow.AddSeconds(-1)
                }
            ]
        };

        var originalOrder = CreateOrder(orderGuid, lineGuid, quantity: 3m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 5m, null),
            new OrderHistoryPaymentDto(
                Guid.NewGuid(),
                PaymentMethodKind.Card,
                5m,
                "ANZ:SALE-1",
                [
                    new CardTransactionDto(
                        "ANZ",
                        "SALE-1",
                        "123456",
                        "VISA",
                        4,
                        "****1234",
                        "MID-1",
                        "00",
                        "APPROVED",
                        "42",
                        DateTimeOffset.Parse("2026-05-25T10:05:00Z"),
                        5m,
                        "receipt")
                ])
        ]);
        var otherOrder = CreateOrder(otherOrderGuid, otherLineGuid, quantity: 2m, actualAmount: 5m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 5m, null)
        ]);
        var firstReturnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -5m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, -5m, null)
        ]) with { OrderGuid = firstReturnOrderGuid };
        var secondReturnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -10m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, -5m, null),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -5m, CardRefundReference.Format("ANZ:REFUND-9", "ANZ:SALE-1"))
        ]) with { OrderGuid = secondReturnOrderGuid };

        var service = new OrderReturnService(
            new FakeOrderHistoryRepository([originalOrder, otherOrder, firstReturnOrder, secondReturnOrder]),
            repository);

        var context = await service.GetReturnContextAsync(orderGuid, CancellationToken.None);

        var paymentCapacities = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(context!.PaymentCapacities);
        var cashCapacity = Assert.Single(paymentCapacities, x => x.Method == PaymentMethodKind.Cash);
        var cardCapacity = Assert.Single(paymentCapacities, x => x.Method == PaymentMethodKind.Card);
        Assert.Equal(5m, cashCapacity.RefundedAmount);
        Assert.Equal(0m, cashCapacity.RemainingAmount);
        Assert.Equal(5m, cardCapacity.RefundedAmount);
        Assert.Equal(0m, cardCapacity.RemainingAmount);
    }

    [Fact]
    public async Task GetReturnContextAsync_UsesSharedRefundPoolsAcrossOrdersInSameReturnOrder()
    {
        var orderAGuid = Guid.NewGuid();
        var orderBGuid = Guid.NewGuid();
        var lineAGuid = Guid.NewGuid();
        var lineBGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderAGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineAGuid.ToString("D"),
                    ProductCode = "SKU-A",
                    ReturnQuantity = 1m,
                    ReturnAmount = 4m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderBGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineBGuid.ToString("D"),
                    ProductCode = "SKU-B",
                    ReturnQuantity = 1m,
                    ReturnAmount = 6m,
                    StaffCode = "C01",
                    CreatedTime = DateTime.UtcNow
                }
            ]
        };
        var orderA = CreateOrder(orderAGuid, lineAGuid, quantity: 1m, actualAmount: 4m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 4m, null)
        ]);
        var orderB = CreateOrder(orderBGuid, lineBGuid, quantity: 2m, actualAmount: 6m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 5m, null),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 5m, "ANZ:SALE-B")
        ]);
        var returnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -10m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, -5m, null),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -5m, CardRefundReference.Format("ANZ:REFUND-9", "ANZ:SALE-B"))
        ]) with { OrderGuid = returnOrderGuid };
        var service = new OrderReturnService(
            new FakeOrderHistoryRepository([orderA, orderB, returnOrder]),
            repository);

        var contextA = await service.GetReturnContextAsync(orderAGuid, CancellationToken.None);
        var contextB = await service.GetReturnContextAsync(orderBGuid, CancellationToken.None);

        var paymentCapacitiesA = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(contextA!.PaymentCapacities);
        var paymentCapacitiesB = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(contextB!.PaymentCapacities);
        var cashA = Assert.Single(paymentCapacitiesA, x => x.Method == PaymentMethodKind.Cash);
        var cashB = Assert.Single(paymentCapacitiesB, x => x.Method == PaymentMethodKind.Cash);
        var cardB = Assert.Single(paymentCapacitiesB, x => x.Method == PaymentMethodKind.Card);
        Assert.Equal(4m, cashA.RefundedAmount);
        Assert.Equal(0m, cashA.RemainingAmount);
        Assert.Equal(1m, cashB.RefundedAmount);
        Assert.Equal(4m, cashB.RemainingAmount);
        Assert.Equal(5m, cardB.RefundedAmount);
        Assert.Equal(0m, cardB.RemainingAmount);
        Assert.Equal(5m, cashA.RefundedAmount + cashB.RefundedAmount);
        Assert.Equal(5m, cardB.RefundedAmount);
        Assert.Equal(4m, cashA.RefundedAmount);
        Assert.Equal(6m, cashB.RefundedAmount + cardB.RefundedAmount);
    }

    [Fact]
    public async Task GetReturnContextAsync_KeepsStableAllocationWhenReturnRepositoryOrderVaries()
    {
        var orderAGuid = Guid.NewGuid();
        var orderBGuid = Guid.NewGuid();
        var lineAGuid = Guid.NewGuid();
        var lineBGuid = Guid.NewGuid();
        var returnOrderGuid = Guid.NewGuid();
        var earlierTime = DateTime.UtcNow.AddMinutes(-1);
        var laterTime = DateTime.UtcNow;
        var recordA = new SalesReturnRecord
        {
            ReturnDetailGuid = "b-return-detail",
            ReturnOrderGuid = returnOrderGuid.ToString("D"),
            OriginalOrderGuid = orderAGuid.ToString("D"),
            OriginalOrderDetailGuid = lineAGuid.ToString("D"),
            ProductCode = "SKU-A",
            ReturnQuantity = 1m,
            ReturnAmount = 4m,
            StaffCode = "C01",
            CreatedTime = earlierTime
        };
        var recordB = new SalesReturnRecord
        {
            ReturnDetailGuid = "a-return-detail",
            ReturnOrderGuid = returnOrderGuid.ToString("D"),
            OriginalOrderGuid = orderBGuid.ToString("D"),
            OriginalOrderDetailGuid = lineBGuid.ToString("D"),
            ProductCode = "SKU-B",
            ReturnQuantity = 1m,
            ReturnAmount = 6m,
            StaffCode = "C01",
            CreatedTime = laterTime
        };
        var repository = new FakeReturnRepository
        {
            ExistingRecords = [recordB, recordA],
            ReverseReturnOrderLookups = true
        };
        var orderA = CreateOrder(orderAGuid, lineAGuid, quantity: 1m, actualAmount: 4m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 4m, null)
        ]);
        var orderB = CreateOrder(orderBGuid, lineBGuid, quantity: 2m, actualAmount: 6m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 5m, null),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 5m, "ANZ:SALE-B")
        ]);
        var returnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -10m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, -5m, null),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -5m, CardRefundReference.Format("ANZ:REFUND-9", "ANZ:SALE-B"))
        ]) with { OrderGuid = returnOrderGuid };
        var service = new OrderReturnService(
            new FakeOrderHistoryRepository([orderA, orderB, returnOrder]),
            repository);

        var contextA = await service.GetReturnContextAsync(orderAGuid, CancellationToken.None);
        var contextB = await service.GetReturnContextAsync(orderBGuid, CancellationToken.None);

        var paymentCapacitiesA = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(contextA!.PaymentCapacities);
        var paymentCapacitiesB = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(contextB!.PaymentCapacities);
        var cashA = Assert.Single(paymentCapacitiesA, x => x.Method == PaymentMethodKind.Cash);
        var cashB = Assert.Single(paymentCapacitiesB, x => x.Method == PaymentMethodKind.Cash);
        var cardB = Assert.Single(paymentCapacitiesB, x => x.Method == PaymentMethodKind.Card);
        Assert.Equal(4m, cashA.RefundedAmount);
        Assert.Equal(1m, cashB.RefundedAmount);
        Assert.Equal(5m, cardB.RefundedAmount);
        Assert.Equal(5m, cashA.RefundedAmount + cashB.RefundedAmount);
        Assert.Equal(5m, cardB.RefundedAmount);
    }

    [Fact]
    public async Task GetReturnContextAsync_ReplaysReturnOrdersByRecordTimeNotGuidOrder()
    {
        var orderGuid = Guid.NewGuid();
        var otherOrderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        var otherLineGuid = Guid.NewGuid();
        var laterReturnOrderGuid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var earlierReturnOrderGuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var earlierTime = DateTime.UtcNow.AddMinutes(-2);
        var laterTime = DateTime.UtcNow.AddMinutes(-1);
        var repository = new FakeReturnRepository
        {
            ExistingRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = "later-current",
                    ReturnOrderGuid = laterReturnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 5m,
                    StaffCode = "C01",
                    CreatedTime = laterTime
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = "later-other",
                    ReturnOrderGuid = laterReturnOrderGuid.ToString("D"),
                    OriginalOrderGuid = otherOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = otherLineGuid.ToString("D"),
                    ProductCode = "SKU-02",
                    ReturnQuantity = 1m,
                    ReturnAmount = 5m,
                    StaffCode = "C01",
                    CreatedTime = laterTime.AddSeconds(1)
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = "earlier-current",
                    ReturnOrderGuid = earlierReturnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineGuid.ToString("D"),
                    ProductCode = "SKU-01",
                    ReturnQuantity = 1m,
                    ReturnAmount = 5m,
                    StaffCode = "C01",
                    CreatedTime = earlierTime
                }
            ]
        };
        var originalOrder = CreateOrder(orderGuid, lineGuid, quantity: 4m, actualAmount: 10m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 5m, null),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 5m, "ANZ:SALE-1")
        ]);
        var otherOrder = CreateOrder(otherOrderGuid, otherLineGuid, quantity: 2m, actualAmount: 5m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 5m, null)
        ]);
        var earlierReturnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -5m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, -5m, null)
        ]) with { OrderGuid = earlierReturnOrderGuid };
        var laterReturnOrder = CreateOrder(Guid.NewGuid(), Guid.NewGuid(), quantity: 1m, actualAmount: -10m, payments:
        [
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, -5m, null),
            new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, -5m, CardRefundReference.Format("ANZ:REFUND-9", "ANZ:SALE-1"))
        ]) with { OrderGuid = laterReturnOrderGuid };
        var service = new OrderReturnService(
            new FakeOrderHistoryRepository([originalOrder, otherOrder, earlierReturnOrder, laterReturnOrder]),
            repository);

        var context = await service.GetReturnContextAsync(orderGuid, CancellationToken.None);

        var paymentCapacities = Assert.IsAssignableFrom<IReadOnlyList<OrderReturnPaymentCapacityDto>>(context!.PaymentCapacities);
        var cashCapacity = Assert.Single(paymentCapacities, x => x.Method == PaymentMethodKind.Cash);
        var cardCapacity = Assert.Single(paymentCapacities, x => x.Method == PaymentMethodKind.Card);
        Assert.Equal(5m, cashCapacity.RefundedAmount);
        Assert.Equal(0m, cashCapacity.RemainingAmount);
        Assert.Equal(5m, cardCapacity.RefundedAmount);
        Assert.Equal(0m, cardCapacity.RemainingAmount);
    }

    private static OrderHistoryDetailsDto CreateOrder(
        Guid orderGuid,
        Guid lineGuid,
        decimal quantity,
        decimal? actualAmount = null,
        IReadOnlyList<OrderHistoryPaymentDto>? payments = null)
    {
        return new OrderHistoryDetailsDto(
            orderGuid,
            "S01",
            "POS-01",
            "Alice",
            DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            quantity * 4.5m,
            0m,
            actualAmount ?? quantity * 4.5m,
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
            payments ?? [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, quantity * 4.5m, null)]);
    }

    private static OrderReturnOriginalOrder CreateOriginalOrder(OrderHistoryDetailsDto order)
    {
        return new OrderReturnOriginalOrder(
            order.OrderGuid,
            order.Lines
                .Select(line => new OrderReturnOriginalLine(
                    line.OrderLineGuid,
                    line.Quantity,
                    line.ActualAmount))
                .ToList());
    }

    private static OrderReturnRecordCreateRequest CreateReturnRequest(
        Guid returnOrderGuid,
        Guid originalOrderGuid,
        Guid originalDetailGuid)
    {
        return new OrderReturnRecordCreateRequest(
            returnOrderGuid,
            "S01",
            "POS-01",
            "C01",
            "Alice",
            [
                new OrderReturnRecordCreateLineDto(
                    originalOrderGuid,
                    originalDetailGuid,
                    "SKU-01",
                    "REF-01",
                    1m,
                    4.5m)
            ]);
    }

    private static async Task<(OrderReturnRecordCreateResponse? Response, Exception? Exception)> CaptureCreateRecordsAsync(
        OrderReturnService service,
        OrderReturnRecordCreateRequest request)
    {
        try
        {
            return (await service.CreateRecordsAsync(request, CancellationToken.None), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private sealed class FakeOrderHistoryRepository : IOrderHistoryRepository
    {
        private readonly Dictionary<Guid, OrderHistoryDetailsDto> orders = new();

        public FakeOrderHistoryRepository(OrderHistoryDetailsDto order)
        {
            orders[order.OrderGuid] = order;
        }

        public FakeOrderHistoryRepository(OrderHistoryDetailsDto originalOrder, OrderHistoryDetailsDto returnOrder, Guid returnOrderGuid)
        {
            orders[originalOrder.OrderGuid] = originalOrder;
            orders[returnOrderGuid] = returnOrder with { OrderGuid = returnOrderGuid };
        }

        public FakeOrderHistoryRepository(IEnumerable<OrderHistoryDetailsDto> seedOrders)
        {
            foreach (var order in seedOrders)
            {
                orders[order.OrderGuid] = order;
            }
        }

        public Task<OrderHistoryQueryResponse> QueryAsync(
            OrderHistoryQueryRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new OrderHistoryQueryResponse([]));
        }

        public Task<OrderHistoryDetailsDto?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            orders.TryGetValue(orderGuid, out var order);
            return Task.FromResult(order);
        }
    }

    private sealed class FakeReturnRepository : IOrderReturnRepository
    {
        private readonly SemaphoreSlim insertGate = new(1, 1);
        private readonly object recordsGate = new();

        public IReadOnlyList<SalesReturnRecord> ExistingRecords { get; init; } = [];

        public List<SalesReturnRecord> InsertedRecords { get; } = [];

        public IReadOnlyList<OrderReturnOriginalOrder> OriginalOrders { get; init; } = [];

        public bool ReverseReturnOrderLookups { get; init; }

        public int InsertValidatedCallCount { get; private set; }

        public Task<IReadOnlyList<SalesReturnRecord>> GetByOriginalOrderGuidAsync(
            Guid originalOrderGuid,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(SnapshotRecords()
                .Where(record => record.OriginalOrderGuid == originalOrderGuid.ToString("D"))
                .ToList()
                as IReadOnlyList<SalesReturnRecord>);
        }

        public Task<IReadOnlyList<SalesReturnRecord>> GetByReturnOrderGuidAsync(
            Guid returnOrderGuid,
            CancellationToken cancellationToken)
        {
            var records = SnapshotRecords()
                .Where(record => record.ReturnOrderGuid == returnOrderGuid.ToString("D"))
                .ToList()
                as IReadOnlyList<SalesReturnRecord>;
            if (ReverseReturnOrderLookups)
            {
                records = records.Reverse().ToList();
            }

            return Task.FromResult(records);
        }

        public async Task<IReadOnlyList<SalesReturnRecord>> InsertValidatedAsync(
            IReadOnlyList<SalesReturnRecord> records,
            CancellationToken cancellationToken)
        {
            await insertGate.WaitAsync(cancellationToken);
            try
            {
                InsertValidatedCallCount++;
                var existingRecordsForReturnOrder = GetExistingRecordsForReturnOrders(records);
                if (existingRecordsForReturnOrder.Count > 0)
                {
                    return existingRecordsForReturnOrder;
                }

                await OrderReturnRecordValidator.ValidateAsync(
                    records,
                    (orderGuid, _) =>
                        Task.FromResult(OriginalOrders.FirstOrDefault(order => order.OrderGuid == orderGuid)),
                    (orderGuid, _) =>
                        Task.FromResult(SnapshotRecords()
                            .Where(record => record.OriginalOrderGuid == orderGuid.ToString("D"))
                            .ToList()
                            as IReadOnlyList<SalesReturnRecord>),
                    cancellationToken);

                lock (recordsGate)
                {
                    InsertedRecords.AddRange(records);
                }

                return records;
            }
            finally
            {
                insertGate.Release();
            }
        }

        private IReadOnlyList<SalesReturnRecord> SnapshotRecords()
        {
            lock (recordsGate)
            {
                return ExistingRecords.Concat(InsertedRecords).ToList();
            }
        }

        private IReadOnlyList<SalesReturnRecord> GetExistingRecordsForReturnOrders(
            IReadOnlyList<SalesReturnRecord> records)
        {
            var returnOrderGuids = records
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null)
                .Select(returnOrderGuid => returnOrderGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (returnOrderGuids.Count == 0)
            {
                return [];
            }

            return SnapshotRecords()
                .Where(record =>
                    Normalize(record.ReturnOrderGuid) is { } returnOrderGuid &&
                    returnOrderGuids.Contains(returnOrderGuid))
                .ToList();
        }

        private static string? Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
