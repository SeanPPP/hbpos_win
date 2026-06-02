using BlazorApp.Shared.Models.POSM;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Api.Tests;

public sealed class OrderSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_ReturnsAlreadySyncedWhenOrderExists()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: true);
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var response = await service.SyncAsync(CreateRequest(orderGuid), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(response.AlreadySynced);
        Assert.Equal("AlreadySynced", response.Message);
        Assert.False(repository.InsertCalled);
    }

    [Fact]
    public async Task SyncAsync_DoesNotConsumeVoucherReservationWhenOrderAlreadySyncedDuringInsert()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            InsertResult = false
        };
        var reservationService = new FakeReservationService();
        reservationService.Add(new StoreVoucherReservation("token-1", "S01", "V001", 5m, DateTimeOffset.UtcNow.AddMinutes(5)));
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), reservationService);

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "V001", "token-1")
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(response.AlreadySynced);
        Assert.Equal("AlreadySynced", response.Message);
        Assert.True(repository.InsertCalled);
        Assert.Empty(reservationService.ConsumedTokens);
        Assert.NotNull(await reservationService.GetAsync("token-1", CancellationToken.None));
    }

    [Fact]
    public async Task SyncAsync_InsertsSnapshotWhenOrderDoesNotExist()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false);
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var response = await service.SyncAsync(CreateRequest(orderGuid), CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.True(repository.InsertCalled);
        Assert.Equal(orderGuid.ToString("D"), repository.LastPlan?.Order.OrderGuid);
        Assert.Empty(repository.LastVoucherRedemptions);
        Assert.Equal(9.99m, repository.LastPlan?.Lines.Single().Price);
        Assert.Equal("SOURCE-GUID-01", repository.LastPlan?.Lines.Single().ReferenceGUID);
        Assert.Equal("priceSource=1", repository.LastPlan?.Lines.Single().Remark);
        Assert.Equal("POS_S01_POS01", repository.LastPlan?.Order.CreatedBy);
        Assert.Equal("POS_S01_POS01", repository.LastPlan?.Lines.Single().CreatedBy);
        Assert.Equal("POS_S01_POS01", repository.LastPlan?.Payments.Single().CreatedBy);
    }

    [Fact]
    public async Task SyncAsync_ForwardsReturnRecordsInPlan()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var returnLineGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    new OrderLineSyncDto(
                        returnLineGuid,
                        "P02",
                        "REF-RETURN",
                        "Returned Apple",
                        "BAR02",
                        -1m,
                        9.99m,
                        0m,
                        -9.99m,
                        PriceSourceKind.StoreRetailPrice,
                        Kind: OrderLineKind.Return,
                        ReturnSourceKey: "RETURN-SOURCE",
                        OriginalOrderGuid: originalOrderGuid,
                        OriginalOrderDetailGuid: originalDetailGuid)
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        var record = Assert.Single(repository.LastPlan!.ReturnRecords);
        Assert.Equal(returnLineGuid.ToString("D"), record.ReturnDetailGuid);
        Assert.Equal(orderGuid.ToString("D"), record.ReturnOrderGuid);
        Assert.Equal(originalOrderGuid.ToString("D"), record.OriginalOrderGuid);
        Assert.Equal(originalDetailGuid.ToString("D"), record.OriginalOrderDetailGuid);
        Assert.Equal(1m, record.ReturnQuantity);
        Assert.Equal(9.99m, record.ReturnAmount);
        Assert.Equal("C01", record.StaffCode);
        Assert.Equal("POS_S01_POS01", record.CreatedBy);
        Assert.Equal("POS_S01_POS01", record.UpdatedBy);
        Assert.Empty(repository.LastPlan.Lines);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsReturnLineExceedingOriginalRemaining()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -2m,
                        actualAmount: -19.98m)
                ]),
            CancellationToken.None));

        Assert.Equal("Return quantity exceeds the available original order quantity.", ex.Message);
        Assert.True(repository.InsertCalled);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsReturnLineWhenExistingRecordsExhaustCapacity()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ],
            ExistingReturnRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = Guid.NewGuid().ToString("D"),
                    OriginalOrderGuid = originalOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = originalDetailGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 9.99m
                }
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None));

        Assert.Equal("Return quantity exceeds the available original order quantity.", ex.Message);
        Assert.True(repository.InsertCalled);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_SkipsDuplicateReturnRecordsWhenReturnOrderAlreadyExists()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            ExistingReturnRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = orderGuid.ToString("D"),
                    OriginalOrderGuid = originalOrderGuid.ToString("D"),
                    OriginalOrderDetailGuid = originalDetailGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 9.99m
                }
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.False(response.AlreadySynced);
        Assert.True(repository.InsertCalled);
        Assert.Single(repository.LastPlan!.ReturnRecords);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundWithoutOriginalCardCapacity()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ]
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Card, -9.99m, "SQRF:refund-1")
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refunds require an original card payment reference.", ex.Message);
        Assert.True(repository.InsertCalled);
        Assert.Equal(1, repository.AtomicReturnValidationCallCount);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_AllowsCardRefundWithinOriginalCardCapacity()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 9.99m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:payment-1"] = 9.99m
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -9.99m,
                        CardRefundReference.Format("SQRF:refund-1", "SQ:payment-1"))
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -9.99m)
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal(1, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundExceedingMatchedOriginalCardReference()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderGuid = Guid.NewGuid();
        var originalDetailGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderGuid, originalDetailGuid, quantity: 1m, actualAmount: 12m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:card-1"] = 5m,
                ["SQ:card-2"] = 7m
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -7m,
                        CardRefundReference.Format("SQRF:refund-1", "SQ:card-1"))
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderGuid,
                        originalDetailGuid,
                        quantity: -1m,
                        actualAmount: -12m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refund amount exceeds the available original card payment capacity.", ex.Message);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundExceedingCurrentReturnAmountForOriginalCardOrder()
    {
        var orderGuid = Guid.NewGuid();
        var originalOrderA = Guid.NewGuid();
        var originalOrderB = Guid.NewGuid();
        var originalDetailA = Guid.NewGuid();
        var originalDetailB = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(originalOrderA, originalDetailA, quantity: 1m, actualAmount: 10m),
                CreateOriginalOrder(originalOrderB, originalDetailB, quantity: 1m, actualAmount: 90m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:card-a"] = 100m,
                ["SQ:card-b"] = 90m
            },
            OriginalCardPaymentOrderGuidsByReference = new Dictionary<string, Guid>
            {
                ["SQ:card-a"] = originalOrderA,
                ["SQ:card-b"] = originalOrderB
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -100m,
                        CardRefundReference.Format("SQRF:refund-1", "SQ:card-a"))
                ],
                lines:
                [
                    CreateReturnLine(
                        originalOrderA,
                        originalDetailA,
                        quantity: -1m,
                        actualAmount: -10m),
                    CreateReturnLine(
                        originalOrderB,
                        originalDetailB,
                        quantity: -1m,
                        actualAmount: -90m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refund amount exceeds the return amount for the original card order.", ex.Message);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RejectsCardRefundWhenExistingLegacyRefundSpansMultipleOriginalOrders()
    {
        var returnOrderGuid = Guid.NewGuid();
        var orderAGuid = Guid.NewGuid();
        var orderBGuid = Guid.NewGuid();
        var lineAGuid = Guid.NewGuid();
        var lineBGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false)
        {
            OriginalOrders =
            [
                CreateOriginalOrder(orderAGuid, lineAGuid, quantity: 2m, actualAmount: 5m)
            ],
            OriginalCardPaymentAmountsByReference = new Dictionary<string, decimal>
            {
                ["SQ:card-a"] = 5m
            },
            ExistingReturnRecords =
            [
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderAGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineAGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 3m
                },
                new SalesReturnRecord
                {
                    ReturnDetailGuid = Guid.NewGuid().ToString("D"),
                    ReturnOrderGuid = returnOrderGuid.ToString("D"),
                    OriginalOrderGuid = orderBGuid.ToString("D"),
                    OriginalOrderDetailGuid = lineBGuid.ToString("D"),
                    ReturnQuantity = 1m,
                    ReturnAmount = 3m
                }
            ],
            ExistingCardRefundsByReturnOrder = new Dictionary<Guid, IReadOnlyList<PaymentDetail>>
            {
                [returnOrderGuid] =
                [
                    new PaymentDetail
                    {
                        OrderGuid = returnOrderGuid.ToString("D"),
                        PaymentMethod = (int)PaymentMethodKind.Card,
                        Amount = -3m,
                        Reference = "SQRF:legacy"
                    }
                ]
            }
        };
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                Guid.NewGuid(),
                payments:
                [
                    new PaymentSyncDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Card,
                        -1m,
                        CardRefundReference.Format("SQRF:new", "SQ:card-a"))
                ],
                lines:
                [
                    CreateReturnLine(
                        orderAGuid,
                        lineAGuid,
                        quantity: -1m,
                        actualAmount: -1m)
                ]),
            CancellationToken.None));

        Assert.Equal("Card refund amount exceeds the available original card payment capacity.", ex.Message);
        Assert.Equal(0, repository.InsertedReturnRecordCount);
    }

    [Fact]
    public async Task SyncAsync_RequiresReservationTokenForVoucherPayments()
    {
        var repository = new FakeOrderRepository(exists: false);
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), new FakeReservationService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync(
            CreateRequest(
                Guid.NewGuid(),
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "V001")
                ]),
            CancellationToken.None));

        Assert.Equal("Voucher reservation token is required.", ex.Message);
        Assert.False(repository.InsertCalled);
    }

    [Fact]
    public async Task SyncAsync_ForwardsVoucherRedemptionAndConsumesReservation()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false);
        var reservationService = new FakeReservationService();
        reservationService.Add(new StoreVoucherReservation("token-1", "S01", "V001", 5m, DateTimeOffset.UtcNow.AddMinutes(5)));
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), reservationService);

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "V001", "token-1")
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Single(repository.LastVoucherRedemptions);
        var redemption = repository.LastVoucherRedemptions.Single();
        Assert.Equal("V001", redemption.VoucherCode);
        Assert.Equal("token-1", redemption.ReservationToken);
        Assert.Equal(5m, redemption.Amount);
        Assert.Equal(["token-1"], reservationService.ConsumedTokens);
    }

    [Fact]
    public async Task SyncAsync_AllowsNegativeVoucherPaymentWithoutReservation()
    {
        var orderGuid = Guid.NewGuid();
        var repository = new FakeOrderRepository(exists: false);
        var reservationService = new FakeReservationService();
        var service = new OrderSyncService(repository, new OrderSyncPlanner(), reservationService);

        var response = await service.SyncAsync(
            CreateRequest(
                orderGuid,
                payments:
                [
                    new PaymentSyncDto(Guid.NewGuid(), PaymentMethodKind.Voucher, -5m, "V001")
                ]),
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.True(repository.InsertCalled);
        Assert.Empty(repository.LastVoucherRedemptions);
        Assert.Empty(reservationService.ConsumedTokens);
    }

    [Fact]
    public void Planner_WritesItemNumberAsItemNoMetadata()
    {
        var request = CreateRequest(Guid.NewGuid(), itemNumber: "ITEM-1001");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var line = Assert.Single(plan.Lines);
        Assert.Equal("P01", line.ProductCode);
        Assert.Contains("itemNo=ITEM-1001", line.Remark);
    }

    [Fact]
    public void Planner_SanitizesSalesOrderDetailTextBeforeInsert()
    {
        var request = new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "  Cashier-01  ",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    $"  {new string('P', 130)}  ",
                    $"  {new string('R', 130)}  ",
                    $"  {new string('N', 280)}  ",
                    $"  {new string('B', 130)}  ",
                    1m,
                    9.99m,
                    0m,
                    9.99m,
                    PriceSourceKind.StoreRetailPrice,
                    new string('I', 260))
            ],
            []);

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(50, line.ProductCode.Length);
        Assert.Equal(50, line.ReferenceGUID.Length);
        Assert.Equal(255, line.ProductName!.Length);
        Assert.Equal(50, line.Barcode!.Length);
        Assert.Equal(50, line.Remark!.Length);
        Assert.Equal("POS_S01_POS01", line.CreatedBy);
        Assert.Equal("POS_S01_POS01", line.UpdatedBy);
        Assert.DoesNotContain("  ", line.ProductCode);
        Assert.StartsWith("priceSource=1;itemNo=", line.Remark);
    }

    [Fact]
    public void Planner_ConvertsBlankSalesOrderDetailTextToEmptyStrings()
    {
        var request = new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "   ",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    "   ",
                    "   ",
                    "   ",
                    "   ",
                    1m,
                    9.99m,
                    0m,
                    9.99m,
                    PriceSourceKind.StoreRetailPrice,
                    "   ")
            ],
            []);

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var line = Assert.Single(plan.Lines);
        Assert.Equal(string.Empty, line.ProductCode);
        Assert.Equal(string.Empty, line.ReferenceGUID);
        Assert.Equal(string.Empty, line.ProductName);
        Assert.Equal(string.Empty, line.Barcode);
        Assert.Equal("priceSource=1", line.Remark);
        Assert.Equal("POS_S01_POS01", line.CreatedBy);
        Assert.Equal("POS_S01_POS01", line.UpdatedBy);
    }

    [Fact]
    public void Planner_UsesExistingPosmDeviceCodeForAuditFields()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "POS_1042_1234");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        Assert.Equal("POS_1042_1234", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_1234", plan.Order.UpdatedBy);
        Assert.Equal("POS_1042_1234", Assert.Single(plan.Lines).CreatedBy);
        Assert.Equal("POS_1042_1234", Assert.Single(plan.Payments).CreatedBy);
        Assert.Equal("POS_1042_1234", Assert.Single(plan.Payments).UpdatedBy);
    }

    [Fact]
    public void Planner_PreservesExistingPosmDeviceCodeWithUnderscoreSuffix()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "POS_1042_TILL_01");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        Assert.Equal("POS_1042_TILL_01", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_TILL_01", Assert.Single(plan.Lines).CreatedBy);
    }

    [Fact]
    public void Planner_SynthesizesPosmAuditFieldsFromStoreAndDeviceSuffix()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "Register-A");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        Assert.Equal("POS_1042_Register-A", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_Register-A", Assert.Single(plan.Lines).UpdatedBy);
        Assert.Equal("POS_1042_Register-A", Assert.Single(plan.Payments).CreatedBy);
    }

    [Fact]
    public void Planner_FallsBackToCashierWhenDeviceCodeIsBlank()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: "   ",
            cashierId: "Cashier-7");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        Assert.Equal("POS_1042_Cashier-7", plan.Order.CreatedBy);
        Assert.Equal("POS_1042_Cashier-7", Assert.Single(plan.Lines).UpdatedBy);
    }

    [Fact]
    public void Planner_TruncatesPosmAuditFieldsWithoutBreakingShopCodePrefix()
    {
        var request = CreateRequest(
            Guid.NewGuid(),
            storeCode: "1042",
            deviceCode: $"POS_1042_{new string('X', 80)}");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        Assert.Equal(50, plan.Order.CreatedBy!.Length);
        Assert.StartsWith("POS_1042_", plan.Order.CreatedBy);
        Assert.Equal(2, plan.Order.CreatedBy.Count(ch => ch == '_'));
        Assert.Equal(plan.Order.CreatedBy, Assert.Single(plan.Lines).CreatedBy);
    }

    [Fact]
    public void Repository_BuildsSalesOrderDetailDiagnosticsWithLengthsAndSafePreview()
    {
        var line = new SalesOrderDetail
        {
            OrderDetailGuid = "detail-1",
            ProductCode = new string('P', 90),
            ReferenceGUID = "REF-1",
            ProductName = "Name\r\nWithControl",
            Barcode = null,
            Remark = new string('R', 120),
            CreatedBy = "POS_1042_1234",
            UpdatedBy = "POS_1042_1234"
        };

        var diagnostic = Assert.Single(SqlSugarOrderRepository.BuildSalesOrderDetailDiagnostics([line]));

        Assert.Equal("detail-1", diagnostic.OrderDetailGuid);
        Assert.Equal(90, diagnostic.ProductCode.Length);
        Assert.Equal(80, diagnostic.ProductCode.Preview.Length);
        Assert.Equal(5, diagnostic.ReferenceGUID.Length);
        Assert.Equal("REF-1", diagnostic.ReferenceGUID.Preview);
        Assert.Equal(17, diagnostic.ProductName.Length);
        Assert.Equal("Name  WithControl", diagnostic.ProductName.Preview);
        Assert.Equal(0, diagnostic.Barcode.Length);
        Assert.Equal(string.Empty, diagnostic.Barcode.Preview);
        Assert.Equal(120, diagnostic.Remark.Length);
        Assert.Equal(80, diagnostic.Remark.Preview.Length);
        Assert.Equal("POS_1042_1234", diagnostic.CreatedBy.Preview);
        Assert.Equal("POS_1042_1234", diagnostic.UpdatedBy.Preview);

        var diagnosticsText = SqlSugarOrderRepository.BuildSalesOrderDetailDiagnosticsText([line]);
        Assert.Contains("\"ProductName\"", diagnosticsText);
        Assert.Contains("\"CreatedBy\"", diagnosticsText);
        Assert.Contains("\"UpdatedBy\"", diagnosticsText);
        Assert.Contains("\"Preview\":\"Name  WithControl\"", diagnosticsText);
        Assert.DoesNotContain("\r", diagnosticsText);
        Assert.DoesNotContain("\n", diagnosticsText);
    }

    [Fact]
    public void Planner_CreatesBankTransactionForCardPayment()
    {
        var paymentGuid = Guid.NewGuid();
        var orderGuid = Guid.NewGuid();
        var request = CreateRequest(
            orderGuid,
            payments:
            [
                new PaymentSyncDto(
                    paymentGuid,
                    PaymentMethodKind.Card,
                    12.34m,
                    "ANZ:TXN-1",
                    CardTransactions:
                    [
                        new CardTransactionDto(
                            "ANZ",
                            "TXN-1",
                            "123456",
                            "VISA",
                            4,
                            "****1234",
                            "MID-1",
                            "00",
                            "APPROVED",
                            "42",
                            DateTimeOffset.Parse("2026-05-26T00:00:00Z"),
                            12.34m,
                            "merchant receipt")
                    ])
            ]);

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var bankTransaction = Assert.Single(plan.BankTransactions);
        Assert.Equal(paymentGuid.ToString("D"), bankTransaction.PaymentGuid);
        Assert.Equal(orderGuid.ToString("D"), bankTransaction.OrderGuid);
        Assert.Equal("TXN-1", bankTransaction.TxnRef);
        Assert.Equal("123456", bankTransaction.AuthCode);
        Assert.Equal("VISA", bankTransaction.CardType);
        Assert.Equal(4, bankTransaction.CardBIN);
        Assert.Equal("****1234", bankTransaction.CardNumber);
        Assert.Equal("MID-1", bankTransaction.Caid);
        Assert.Equal("00", bankTransaction.ResponseCode);
        Assert.Equal("APPROVED", bankTransaction.ResponseText);
        Assert.Equal("42", bankTransaction.Stan);
        Assert.Equal(12.34m, bankTransaction.Amount);
        Assert.Equal("merchant receipt", bankTransaction.ReceiptText);
    }

    [Fact]
    public void Planner_CreatesNegativeBankTransactionForCardRefund()
    {
        var paymentGuid = Guid.NewGuid();
        var request = CreateRequest(
            Guid.NewGuid(),
            payments:
            [
                new PaymentSyncDto(
                    paymentGuid,
                    PaymentMethodKind.Card,
                    -12.34m,
                    "SQRF:refund-1",
                    CardTransactions:
                    [
                        new CardTransactionDto(
                            "Square",
                            "refund-1",
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            "PENDING",
                            null,
                            DateTimeOffset.Parse("2026-05-26T00:00:00Z"),
                            12.34m,
                            null)
                    ])
            ]);

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var bankTransaction = Assert.Single(plan.BankTransactions);
        Assert.Equal(paymentGuid.ToString("D"), bankTransaction.PaymentGuid);
        Assert.Equal(-12.34m, bankTransaction.Amount);
    }

    [Fact]
    public void Planner_SkipsReturnLinesFromSalesOrderDetailsAndItemCount()
    {
        var saleLineGuid = Guid.NewGuid();
        var returnLineGuid = Guid.NewGuid();
        var request = new OrderSyncRequest(
            Guid.NewGuid(),
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            4.99m,
            0m,
            0m,
            [
                new OrderLineSyncDto(
                    saleLineGuid,
                    "P01",
                    "SOURCE-GUID-01",
                    "Apple",
                    "BAR01",
                    2m,
                    9.99m,
                    0m,
                    19.98m,
                    PriceSourceKind.StoreRetailPrice),
                new OrderLineSyncDto(
                    returnLineGuid,
                    "P02",
                    "RETURN-SOURCE-01",
                    "Orange",
                    "BAR02",
                    1m,
                    15m,
                    0m,
                    15m,
                    PriceSourceKind.StoreRetailPrice,
                    null,
                    OrderLineKind.Return,
                    "RETURN-SOURCE-KEY",
                    Guid.NewGuid(),
                    Guid.NewGuid())
            ],
            []);

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var saleLine = Assert.Single(plan.Lines);
        Assert.Equal(saleLineGuid.ToString("D"), saleLine.OrderDetailGuid);
        Assert.Equal(2, plan.Order.ItemCount);
        var returnRecord = Assert.Single(plan.ReturnRecords);
        Assert.Equal(returnLineGuid.ToString("D"), returnRecord.ReturnDetailGuid);
        Assert.Equal(request.OrderGuid.ToString("D"), returnRecord.ReturnOrderGuid);
        Assert.Equal(1m, returnRecord.ReturnQuantity);
        Assert.Equal(15m, returnRecord.ReturnAmount);
    }

    private static OrderSyncRequest CreateRequest(
        Guid orderGuid,
        string? itemNumber = null,
        IReadOnlyList<PaymentSyncDto>? payments = null,
        IReadOnlyList<OrderLineSyncDto>? lines = null,
        string storeCode = "S01",
        string deviceCode = "POS01",
        string cashierId = "C01")
    {
        return new OrderSyncRequest(
            orderGuid,
            storeCode,
            deviceCode,
            cashierId,
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
            lines ??
            [
                new OrderLineSyncDto(
                    Guid.NewGuid(),
                    "P01",
                    "SOURCE-GUID-01",
                    "Apple",
                    "BAR01",
                    1m,
                    9.99m,
                    0m,
                    9.99m,
                    PriceSourceKind.StoreRetailPrice,
                    itemNumber)
            ],
            payments ??
            [
                new PaymentSyncDto(
                    Guid.NewGuid(),
                    PaymentMethodKind.Cash,
                    9.99m,
                null)
            ]);
    }

    private static OrderLineSyncDto CreateReturnLine(
        Guid originalOrderGuid,
        Guid originalDetailGuid,
        decimal quantity,
        decimal actualAmount)
    {
        return new OrderLineSyncDto(
            Guid.NewGuid(),
            "P02",
            "REF-RETURN",
            "Returned Apple",
            "BAR02",
            quantity,
            Math.Abs(actualAmount),
            0m,
            actualAmount,
            PriceSourceKind.StoreRetailPrice,
            Kind: OrderLineKind.Return,
            ReturnSourceKey: "RETURN-SOURCE",
            OriginalOrderGuid: originalOrderGuid,
            OriginalOrderDetailGuid: originalDetailGuid);
    }

    private static OrderReturnOriginalOrder CreateOriginalOrder(
        Guid orderGuid,
        Guid lineGuid,
        decimal quantity,
        decimal actualAmount)
    {
        return new OrderReturnOriginalOrder(
            orderGuid,
            [new OrderReturnOriginalLine(lineGuid, quantity, actualAmount)]);
    }

    private sealed class FakeOrderRepository(bool exists) : IOrderRepository
    {
        public bool InsertCalled { get; private set; }

        public OrderSyncPlan? LastPlan { get; private set; }

        public IReadOnlyList<StoreVoucherRedemptionCommit> LastVoucherRedemptions { get; private set; } = [];

        public IReadOnlyList<OrderReturnOriginalOrder> OriginalOrders { get; init; } = [];

        public IReadOnlyList<SalesReturnRecord> ExistingReturnRecords { get; init; } = [];

        public IReadOnlyDictionary<string, decimal> OriginalCardPaymentAmountsByReference { get; init; } =
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, Guid> OriginalCardPaymentOrderGuidsByReference { get; init; } =
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<Guid, IReadOnlyList<PaymentDetail>> ExistingCardRefundsByReturnOrder { get; init; } =
            new Dictionary<Guid, IReadOnlyList<PaymentDetail>>();

        public int AtomicReturnValidationCallCount { get; private set; }

        public int InsertedReturnRecordCount { get; private set; }

        public bool InsertResult { get; init; } = true;

        public Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            return Task.FromResult(exists);
        }

        public async Task<bool> InsertAsync(
            OrderSyncPlan plan,
            IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
            CancellationToken cancellationToken)
        {
            InsertCalled = true;
            LastPlan = plan;
            LastVoucherRedemptions = voucherRedemptions;
            if (!InsertResult)
            {
                return false;
            }

            var returnRecords = await PrepareReturnRecordsAsync(plan.ReturnRecords, plan.Payments, cancellationToken);
            InsertedReturnRecordCount = returnRecords.Count;
            return true;
        }

        private async Task<IReadOnlyList<SalesReturnRecord>> PrepareReturnRecordsAsync(
            IReadOnlyList<SalesReturnRecord> returnRecords,
            IReadOnlyList<PaymentDetail> payments,
            CancellationToken cancellationToken)
        {
            if (returnRecords.Count == 0)
            {
                return [];
            }

            AtomicReturnValidationCallCount++;
            var returnOrderGuids = returnRecords
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null)
                .Select(returnOrderGuid => returnOrderGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ExistingReturnRecords.Any(record =>
                Normalize(record.ReturnOrderGuid) is { } returnOrderGuid &&
                returnOrderGuids.Contains(returnOrderGuid)))
            {
                return [];
            }

            await OrderReturnRecordValidator.ValidateAsync(
                returnRecords,
                (orderGuid, _) =>
                    Task.FromResult(OriginalOrders.FirstOrDefault(order => order.OrderGuid == orderGuid)),
                (orderGuid, _) =>
                    Task.FromResult(ExistingReturnRecords
                        .Where(record => record.OriginalOrderGuid == orderGuid.ToString("D"))
                        .ToList()
                        as IReadOnlyList<SalesReturnRecord>),
                    cancellationToken);

            ValidateCardRefundCapacity(returnRecords, payments);

            return returnRecords;
        }

        private void ValidateCardRefundCapacity(
            IReadOnlyList<SalesReturnRecord> returnRecords,
            IReadOnlyList<PaymentDetail> payments)
        {
            var requestedCardRefundsByReference = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var payment in payments
                .Where(payment => payment.PaymentMethod == (int)PaymentMethodKind.Card)
                .Where(payment => (payment.Amount ?? 0m) < 0m))
            {
                if (!CardRefundReference.TryGetOriginalReference(payment.Reference, out var originalReference) ||
                    Normalize(originalReference) is not { } normalizedOriginalReference)
                {
                    throw new InvalidOperationException("Card refunds require an original card payment reference.");
                }

                requestedCardRefundsByReference[normalizedOriginalReference] =
                    requestedCardRefundsByReference.GetValueOrDefault(normalizedOriginalReference) + Math.Abs(payment.Amount ?? 0m);
            }

            if (requestedCardRefundsByReference.Count == 0)
            {
                return;
            }

            var originalOrderGuids = returnRecords
                .Select(record => Guid.TryParse(record.OriginalOrderGuid, out var guid) ? guid : (Guid?)null)
                .OfType<Guid>()
                .Distinct()
                .ToList();
            if (originalOrderGuids.Count == 0)
            {
                throw new InvalidOperationException("Card refunds require an original card payment.");
            }

            var requestedReturnAmountByOriginalOrder = returnRecords
                .Select(record => new
                {
                    OriginalOrderGuid = Guid.TryParse(record.OriginalOrderGuid, out var guid) ? guid : (Guid?)null,
                    ReturnAmount = Math.Abs(record.ReturnAmount ?? 0m)
                })
                .Where(item => item.OriginalOrderGuid is not null)
                .GroupBy(item => item.OriginalOrderGuid!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(item => item.ReturnAmount));
            var remainingByReference = OriginalCardPaymentAmountsByReference
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var originalOrdersByReference = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var originalReference in remainingByReference.Keys)
            {
                if (OriginalCardPaymentOrderGuidsByReference.TryGetValue(originalReference, out var mappedOriginalOrderGuid))
                {
                    originalOrdersByReference[originalReference] = [mappedOriginalOrderGuid];
                }
                else if (originalOrderGuids.Count == 1)
                {
                    originalOrdersByReference[originalReference] = [originalOrderGuids[0]];
                }
            }

            var currentReturnOrderGuids = returnRecords
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingReturnOrderGuids = ExistingReturnRecords
                .Where(record =>
                    Guid.TryParse(record.OriginalOrderGuid, out var originalOrderGuid) &&
                    originalOrderGuids.Contains(originalOrderGuid))
                .Select(record => Normalize(record.ReturnOrderGuid))
                .Where(returnOrderGuid => returnOrderGuid is not null && !currentReturnOrderGuids.Contains(returnOrderGuid))
                .Select(returnOrderGuid => Guid.TryParse(returnOrderGuid, out var guid) ? guid : (Guid?)null)
                .OfType<Guid>()
                .Distinct();
            foreach (var returnOrderGuid in existingReturnOrderGuids)
            {
                var originalGuidsForExistingReturnOrder = ExistingReturnRecords
                    .Where(record => string.Equals(Normalize(record.ReturnOrderGuid), returnOrderGuid.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    .Select(record => Guid.TryParse(record.OriginalOrderGuid, out var guid) ? guid : (Guid?)null)
                    .OfType<Guid>()
                    .Distinct()
                    .ToList();
                foreach (var payment in ExistingCardRefundsByReturnOrder.GetValueOrDefault(returnOrderGuid) ?? [])
                {
                    if (CardRefundReference.TryGetOriginalReference(payment.Reference, out var existingOriginalReference) &&
                        Normalize(existingOriginalReference) is { } normalizedExistingOriginalReference)
                    {
                        remainingByReference[normalizedExistingOriginalReference] =
                            remainingByReference.GetValueOrDefault(normalizedExistingOriginalReference) - Math.Abs(payment.Amount ?? 0m);
                    }
                    else if (originalGuidsForExistingReturnOrder.Count == 1 && remainingByReference.Count == 1)
                    {
                        var singleReference = remainingByReference.Keys.Single();
                        remainingByReference[singleReference] -= Math.Abs(payment.Amount ?? 0m);
                    }
                    else
                    {
                        foreach (var originalReference in remainingByReference.Keys.ToList())
                        {
                            remainingByReference[originalReference] = 0m;
                        }
                    }
                }
            }

            var requestedCardRefundsByOriginalOrder = new Dictionary<Guid, decimal>();
            foreach (var (originalReference, requestedAmount) in requestedCardRefundsByReference)
            {
                if (originalOrdersByReference.TryGetValue(originalReference, out var referenceOriginalOrders) &&
                    referenceOriginalOrders.Count == 1)
                {
                    var originalOrderGuid = referenceOriginalOrders.Single();
                    requestedCardRefundsByOriginalOrder[originalOrderGuid] =
                        requestedCardRefundsByOriginalOrder.GetValueOrDefault(originalOrderGuid) + requestedAmount;
                }

                if (requestedAmount > Math.Max(0m, remainingByReference.GetValueOrDefault(originalReference)))
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
    }

    private sealed class FakeReservationService : IStoreVoucherReservationService
    {
        private readonly Dictionary<string, StoreVoucherReservation> reservations = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ConsumedTokens { get; } = [];

        public void Add(StoreVoucherReservation reservation)
        {
            reservations[reservation.Token] = reservation;
        }

        public Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
        {
            reservations.TryGetValue(token, out var reservation);
            return Task.FromResult(reservation);
        }

        public Task<StoreVoucherReservation> ReserveAsync(
            string storeCode,
            string voucherCode,
            decimal requestedAmount,
            decimal currentRemainingAmount,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<StoreVoucherReservation> ClaimAsync(
            string token,
            string storeCode,
            string voucherCode,
            decimal amount,
            string? consumedByReference,
            CancellationToken cancellationToken)
        {
            if (!reservations.TryGetValue(token, out var reservation) ||
                !string.Equals(reservation.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reservation.VoucherCode, voucherCode, StringComparison.OrdinalIgnoreCase) ||
                reservation.LockedAmount < amount)
            {
                throw new InvalidOperationException("Voucher reservation token is invalid, expired, or already claimed.");
            }

            reservations.Remove(token);
            return Task.FromResult(reservation);
        }

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            ConsumedTokens.Add(token);
            reservations.Remove(token);
            return Task.CompletedTask;
        }
    }
}
