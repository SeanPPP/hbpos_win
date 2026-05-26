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
    public void Planner_WritesItemNumberAsItemNoMetadata()
    {
        var request = CreateRequest(Guid.NewGuid(), itemNumber: "ITEM-1001");

        var plan = new OrderSyncPlanner().CreatePlan(request);

        var line = Assert.Single(plan.Lines);
        Assert.Equal("P01", line.ProductCode);
        Assert.Contains("itemNo=ITEM-1001", line.Remark);
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

    private static OrderSyncRequest CreateRequest(
        Guid orderGuid,
        string? itemNumber = null,
        IReadOnlyList<PaymentSyncDto>? payments = null)
    {
        return new OrderSyncRequest(
            orderGuid,
            "S01",
            "POS01",
            "C01",
            "Cashier",
            DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            9.99m,
            0m,
            9.99m,
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

    private sealed class FakeOrderRepository(bool exists) : IOrderRepository
    {
        public bool InsertCalled { get; private set; }

        public OrderSyncPlan? LastPlan { get; private set; }

        public IReadOnlyList<StoreVoucherRedemptionCommit> LastVoucherRedemptions { get; private set; } = [];

        public Task<bool> ExistsAsync(Guid orderGuid, CancellationToken cancellationToken)
        {
            return Task.FromResult(exists);
        }

        public Task InsertAsync(
            OrderSyncPlan plan,
            IReadOnlyList<StoreVoucherRedemptionCommit> voucherRedemptions,
            CancellationToken cancellationToken)
        {
            InsertCalled = true;
            LastPlan = plan;
            LastVoucherRedemptions = voucherRedemptions;
            return Task.CompletedTask;
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

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            ConsumedTokens.Add(token);
            reservations.Remove(token);
            return Task.CompletedTask;
        }
    }
}
