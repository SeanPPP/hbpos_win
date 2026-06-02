using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Service.Models.HBPOSM_POSM;
using Hbpos.Api.Services;
using Hbpos.Api.Data;
using BlazorApp.Shared.Models.POSM;
using Hbpos.Contracts.Vouchers;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class StoreVoucherServiceTests
{
    [Fact]
    public async Task QueryAsync_ReturnsFoundVoucher()
    {
        var voucher = CreateVoucher(remainingAmount: 12.5m);
        var service = new StoreVoucherService(
            new FakeStoreVoucherRepository(voucher),
            new FakeReservationService());

        var response = await service.QueryAsync("S01", "V001", CancellationToken.None);

        Assert.True(response.Found);
        Assert.NotNull(response.Voucher);
        Assert.Equal("V001", response.Voucher.VoucherCode);
        Assert.Equal(12.5m, response.Voucher.RemainingAmount);
    }

    [Fact]
    public async Task LockAsync_ReturnsPartialAmountWhenExistingReservationReducesAvailability()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new StoreVoucherService(
            new SqlSugarStoreVoucherRepository(fixture.DbContext),
            new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider));

        var first = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 6m), CancellationToken.None);
        var second = await service.LockAsync(new StoreVoucherLockRequest("S01", "V001", 6m), CancellationToken.None);

        Assert.Equal(6m, first.LockedAmount);
        Assert.Equal(4m, second.LockedAmount);
        Assert.NotEqual(first.ReservationToken, second.ReservationToken);
    }

    [Fact]
    public async Task Reservation_GetAsync_ReturnsOnlyPendingUnexpiredRecords()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        var reservation = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);
        var found = await service.GetAsync(reservation.Token, CancellationToken.None);
        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(6);
        var expired = await service.GetAsync(reservation.Token, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Null(expired);
        Assert.NotNull(await fixture.GetReservationEntityAsync(reservation.Token));
    }

    [Fact]
    public async Task Reservation_ExpiredPendingAmount_DoesNotReduceFutureLocks()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        await service.ReserveAsync("S01", "V001", 9m, 10m, CancellationToken.None);
        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(6);
        var fresh = await service.ReserveAsync("S01", "V001", 10m, 10m, CancellationToken.None);

        Assert.Equal(10m, fresh.LockedAmount);
    }

    [Fact]
    public async Task Reservation_ConsumeAsync_MarksConsumedAndIsIdempotent()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);

        await service.ConsumeAsync(reservation.Token, CancellationToken.None);
        await service.ConsumeAsync(reservation.Token, CancellationToken.None);

        var found = await service.GetAsync(reservation.Token, CancellationToken.None);
        var entity = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Null(found);
        Assert.NotNull(entity);
        Assert.Equal("consumed", entity!.Status);
        Assert.NotNull(entity.ConsumedAtUtc);
    }

    [Fact]
    public async Task Reservation_ClaimAsync_AllowsTokenOnlyOnce()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await service.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);

        var claimed = await service.ClaimAsync(reservation.Token, "S01", "V001", 5m, "ORDER-1", CancellationToken.None);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClaimAsync(reservation.Token, "S01", "V001", 5m, "ORDER-2", CancellationToken.None));

        var entity = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal(reservation.Token, claimed.Token);
        Assert.Contains("already claimed", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await service.GetAsync(reservation.Token, CancellationToken.None));
        Assert.Equal("claimed", entity?.Status);
        Assert.Equal("ORDER-1", entity?.ConsumedByReference);

        await service.ConsumeAsync(reservation.Token, CancellationToken.None);
        var consumed = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Equal("consumed", consumed?.Status);
        Assert.Equal("ORDER-1", consumed?.ConsumedByReference);
    }

    [Fact]
    public async Task Reservation_ClaimAsync_RejectsExpiredAndConsumedTokens()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var service = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var expired = await service.ReserveAsync("S01", "V001", 4m, 10m, CancellationToken.None);
        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(6);
        var active = await service.ReserveAsync("S01", "V001", 4m, 10m, CancellationToken.None);
        await service.ConsumeAsync(active.Token, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClaimAsync(expired.Token, "S01", "V001", 4m, "ORDER-EXPIRED", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClaimAsync(active.Token, "S01", "V001", 4m, "ORDER-CONSUMED", CancellationToken.None));
    }

    [Fact]
    public async Task Reservation_CanBeReadAcrossServiceInstances()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var firstInstance = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var secondInstance = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);

        var reservation = await firstInstance.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);
        var found = await secondInstance.GetAsync(reservation.Token, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(reservation.Token, found!.Token);
    }

    [Fact]
    public async Task RedeemInsideTransactionAsync_UsesAtomicBalanceCondition()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        await RedeemVoucherAsync(fixture, 6m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RedeemVoucherAsync(fixture, 6m));
        var voucher = await fixture.GetVoucherAsync("V001");

        Assert.Contains("balance is not enough", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(voucher);
        Assert.Equal(4m, voucher!.RemainingAmount);
        Assert.Equal("1", voucher.Status);
    }

    [Fact]
    public async Task ClaimAndRedeemInsideTransaction_UsesReservationOnlyOnce()
    {
        await using var fixture = await StoreVoucherSqliteFixture.CreateAsync();
        var timeProvider = new MutableFakeTimeProvider(DateTimeOffset.Parse("2026-05-26T10:00:00Z"));
        await fixture.SeedVoucherAsync(CreateVoucher(remainingAmount: 10m));
        var reservationService = new SqlSugarStoreVoucherReservationService(fixture.DbContext, timeProvider);
        var reservation = await reservationService.ReserveAsync("S01", "V001", 5m, 10m, CancellationToken.None);

        await ClaimAndRedeemVoucherAsync(fixture, reservation.Token, 5m, "ORDER-1", timeProvider.UtcNow);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClaimAndRedeemVoucherAsync(fixture, reservation.Token, 5m, "ORDER-2", timeProvider.UtcNow));

        var voucher = await fixture.GetVoucherAsync("V001");
        var storedReservation = await fixture.GetReservationEntityAsync(reservation.Token);
        Assert.Contains("already claimed", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(voucher);
        Assert.Equal(5m, voucher!.RemainingAmount);
        Assert.Equal("claimed", storedReservation?.Status);
        Assert.Equal("ORDER-1", storedReservation?.ConsumedByReference);
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
            new FakeReservationService(),
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
            new FakeReservationService(),
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
            new FakeReservationService(),
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
            new FakeReservationService(),
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
            ID = 1,
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

    private static async Task RedeemVoucherAsync(StoreVoucherSqliteFixture fixture, decimal amount)
    {
        await fixture.Client.Ado.BeginTranAsync();
        try
        {
            await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                fixture.Client,
                "S01",
                "V001",
                amount,
                "C001",
                CancellationToken.None);
            await fixture.Client.Ado.CommitTranAsync();
        }
        catch
        {
            await fixture.Client.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static async Task ClaimAndRedeemVoucherAsync(
        StoreVoucherSqliteFixture fixture,
        string reservationToken,
        decimal amount,
        string orderReference,
        DateTimeOffset now)
    {
        await fixture.Client.Ado.BeginTranAsync();
        try
        {
            await SqlSugarStoreVoucherReservationService.ClaimInsideTransactionAsync(
                fixture.Client,
                reservationToken,
                "S01",
                "V001",
                amount,
                orderReference,
                now,
                CancellationToken.None);
            await SqlSugarStoreVoucherRepository.RedeemInsideTransactionAsync(
                fixture.Client,
                "S01",
                "V001",
                amount,
                "C001",
                CancellationToken.None);
            await fixture.Client.Ado.CommitTranAsync();
        }
        catch
        {
            await fixture.Client.Ado.RollbackTranAsync();
            throw;
        }
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

    private sealed class FakeReservationService : IStoreVoucherReservationService
    {
        public Task<StoreVoucherReservation?> GetAsync(string token, CancellationToken cancellationToken)
        {
            return Task.FromResult<StoreVoucherReservation?>(null);
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
            throw new NotSupportedException();
        }

        public Task ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableFakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class StoreVoucherSqliteFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-store-voucher-tests-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        private StoreVoucherSqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<StoreVoucher, StoreVoucherReservationEntity>();
            DbContext = CreateDbContext(client);
        }

        public SqlSugarClient Client => client;

        public HbposSqlSugarContext DbContext { get; }

        public static Task<StoreVoucherSqliteFixture> CreateAsync()
        {
            return Task.FromResult(new StoreVoucherSqliteFixture());
        }

        public Task SeedVoucherAsync(StoreVoucher voucher)
        {
            return client.Insertable(voucher).ExecuteCommandAsync();
        }

        public async Task<StoreVoucher?> GetVoucherAsync(string voucherCode)
        {
            return await client.Queryable<StoreVoucher>()
                .Where(x => x.VoucherCode == voucherCode)
                .FirstAsync();
        }

        public async Task<StoreVoucherReservationEntity?> GetReservationEntityAsync(string token)
        {
            return await client.Queryable<StoreVoucherReservationEntity>()
                .Where(x => x.Token == token)
                .FirstAsync();
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), posmDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), posmDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }
}
