using BlazorApp.Service.Models.HBPOSM_POSM;
using Hbpos.Api.Services;
using Hbpos.Contracts.Vouchers;

namespace Hbpos.Api.Tests;

public sealed class StoreVoucherServiceTests
{
    [Fact]
    public async Task QueryAsync_ReturnsFoundVoucher()
    {
        var voucher = CreateVoucher(remainingAmount: 12.5m);
        var service = new StoreVoucherService(
            new FakeStoreVoucherRepository(voucher),
            new InMemoryStoreVoucherReservationService(new FakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"))));

        var response = await service.QueryAsync("S01", "V001", CancellationToken.None);

        Assert.True(response.Found);
        Assert.NotNull(response.Voucher);
        Assert.Equal("V001", response.Voucher.VoucherCode);
        Assert.Equal(12.5m, response.Voucher.RemainingAmount);
    }

    [Fact]
    public async Task LockAsync_ReturnsPartialAmountWhenExistingReservationReducesAvailability()
    {
        var reservationService = new InMemoryStoreVoucherReservationService(new FakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z")));
        var service = new StoreVoucherService(
            new FakeStoreVoucherRepository(CreateVoucher(remainingAmount: 10m)),
            reservationService);

        var first = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 6m), CancellationToken.None);
        var second = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 6m), CancellationToken.None);

        Assert.Equal(6m, first.LockedAmount);
        Assert.Equal(4m, second.LockedAmount);
        Assert.NotEqual(first.ReservationToken, second.ReservationToken);
    }

    [Fact]
    public async Task IssueRefundAsync_CreatesRefundVoucherWithTwelveMonthExpiry()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "RF001"
        };
        var service = new StoreVoucherService(
            repository,
            new InMemoryStoreVoucherReservationService(new FakeTimeProvider(time)),
            new FakeTimeProvider(time));

        var response = await service.IssueRefundAsync(
            new StoreVoucherIssueRefundRequest("S01", 18.5m, "C001", IdempotencyKey: "ORDER-1:PAY-1", OrderReference: "ORDER-1", Reason: "Refund"),
            CancellationToken.None);

        Assert.Equal("RF001", response.VoucherCode);
        Assert.Equal(18.5m, response.Amount);
        Assert.Equal(18.5m, response.RemainingAmount);
        Assert.Equal("1", response.Status);
        Assert.Equal(time.AddMonths(12), response.ExpiredAt);
        Assert.NotNull(repository.LastRefundRequest);
        Assert.Equal("S01", repository.LastRefundRequest!.StoreCode);
        Assert.Equal("C001", repository.LastRefundRequest.CashierId);
        Assert.Equal("ORDER-1:PAY-1", repository.LastRefundRequest.IdempotencyKey);
        Assert.Equal("ORDER-1", repository.LastRefundRequest.OrderReference);
    }

    [Fact]
    public async Task IssueRefundAsync_ReturnsExistingVoucherForSameIdempotencyKey()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "RF001"
        };
        var service = new StoreVoucherService(
            repository,
            new InMemoryStoreVoucherReservationService(new FakeTimeProvider(time)),
            new FakeTimeProvider(time));
        var request = new StoreVoucherIssueRefundRequest(
            "S01",
            18.5m,
            "C001",
            IdempotencyKey: "ORDER-1:PAY-1",
            OrderReference: "ORDER-1",
            Reason: "Refund");

        var first = await service.IssueRefundAsync(request, CancellationToken.None);
        repository.CreatedVoucherCode = "RF002";
        var second = await service.IssueRefundAsync(request, CancellationToken.None);

        Assert.Equal("RF001", first.VoucherCode);
        Assert.Equal("RF001", second.VoucherCode);
    }

    [Fact]
    public async Task IssueAsync_CreatesIssuedVoucherWithCustomerAndExpiry()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var expiredAt = time.AddDays(30);
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "VC001"
        };
        var service = new StoreVoucherService(
            repository,
            new InMemoryStoreVoucherReservationService(new FakeTimeProvider(time)),
            new FakeTimeProvider(time));

        var response = await service.IssueAsync(
            new StoreVoucherIssueRequest("S01", 25m, "C001", "ISSUE-1", expiredAt, "CUS001", "Manual issue"),
            CancellationToken.None);

        Assert.Equal("VC001", response.VoucherCode);
        Assert.Equal(25m, response.Amount);
        Assert.Equal(25m, response.RemainingAmount);
        Assert.Equal("1", response.Status);
        Assert.Equal(expiredAt, response.ExpiredAt);
        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("CUS001", response.CustomerCode);
        Assert.NotNull(repository.LastIssueRequest);
        Assert.Equal("ISSUE-1", repository.LastIssueRequest!.IdempotencyKey);
        Assert.Equal("Manual issue", repository.LastIssueRequest.Reason);
    }

    [Fact]
    public async Task IssueAsync_ReturnsExistingVoucherForSameStoreAndIdempotencyKey()
    {
        var time = DateTimeOffset.Parse("2026-05-26T10:00:00Z");
        var repository = new FakeStoreVoucherRepository(null)
        {
            CreatedVoucherCode = "VC001"
        };
        var service = new StoreVoucherService(
            repository,
            new InMemoryStoreVoucherReservationService(new FakeTimeProvider(time)),
            new FakeTimeProvider(time));
        var request = new StoreVoucherIssueRequest("S01", 25m, "C001", "ISSUE-1", CustomerCode: "CUS001");

        var first = await service.IssueAsync(request, CancellationToken.None);
        repository.CreatedVoucherCode = "VC002";
        var second = await service.IssueAsync(request, CancellationToken.None);

        Assert.Equal("VC001", first.VoucherCode);
        Assert.Equal("VC001", second.VoucherCode);
    }

    private static StoreVoucher CreateVoucher(decimal remainingAmount)
    {
        return new StoreVoucher
        {
            StoreCode = "S01",
            VoucherCode = "V001",
            VoucherType = 3,
            Amount = 20m,
            RemainingAmount = remainingAmount,
            Status = "1",
            ExpiredDate = DateTime.UtcNow.AddDays(3),
            CustomerCode = "C01",
            DiscountRate = 0m,
            Remark = "cash voucher",
            IsDelete = false
        };
    }

    private sealed class FakeStoreVoucherRepository(StoreVoucher? voucher) : IStoreVoucherRepository
    {
        private readonly Dictionary<string, StoreVoucher> refundVouchersByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoreVoucher> issuedVouchersByKey = new(StringComparer.Ordinal);

        public string CreatedVoucherCode { get; set; } = "RF001";

        public RefundVoucherCreateModel? LastRefundRequest { get; private set; }

        public IssuedVoucherCreateModel? LastIssueRequest { get; private set; }

        public Task<StoreVoucher?> FindAvailableAsync(
            string storeCode,
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(voucher);
        }

        public Task<StoreVoucher> CreateRefundVoucherAsync(
            RefundVoucherCreateModel request,
            CancellationToken cancellationToken)
        {
            LastRefundRequest = request;
            if (!refundVouchersByKey.TryGetValue(request.IdempotencyKey, out var createdVoucher))
            {
                createdVoucher = new StoreVoucher
                {
                    StoreCode = request.StoreCode,
                    VoucherCode = CreatedVoucherCode,
                    VoucherType = 3,
                    Amount = request.Amount,
                    RemainingAmount = request.Amount,
                    Status = "1",
                    ExpiredDate = request.ExpiredAt.UtcDateTime,
                    CustomerCode = null,
                    DiscountRate = 0m,
                    Remark = $"RefundKey={request.IdempotencyKey}",
                    IsDelete = false
                };
                refundVouchersByKey[request.IdempotencyKey] = createdVoucher;
            }

            return Task.FromResult(createdVoucher);
        }

        public Task<StoreVoucher> CreateIssuedVoucherAsync(
            IssuedVoucherCreateModel request,
            CancellationToken cancellationToken)
        {
            LastIssueRequest = request;
            var key = $"{request.StoreCode ?? string.Empty}:{request.IdempotencyKey}";
            if (!issuedVouchersByKey.TryGetValue(key, out var createdVoucher))
            {
                createdVoucher = new StoreVoucher
                {
                    StoreCode = request.StoreCode,
                    VoucherCode = CreatedVoucherCode,
                    VoucherType = 3,
                    Amount = request.Amount,
                    RemainingAmount = request.Amount,
                    Status = "1",
                    ExpiredDate = request.ExpiredAt.UtcDateTime,
                    CustomerCode = request.CustomerCode,
                    DiscountRate = 0m,
                    Remark = $"IssueKey={request.IdempotencyKey}",
                    IsDelete = false
                };
                issuedVouchersByKey[key] = createdVoucher;
            }

            return Task.FromResult(createdVoucher);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
