using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class InstallmentOrderServiceTests
{
    [Fact]
    public async Task CreateAsync_returns_online_required_when_session_is_offline()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient();
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateAsync(
                CreateOfflineSession(),
                CreateInstallmentCreateRequest());

            Assert.Equal(InstallmentWriteStatus.OnlineRequired, result.Status);
            Assert.Null(result.Response);
            Assert.Null(result.LocalOrder);
            Assert.Equal(0, apiClient.CreateCallCount);

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(0, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalOrderInstallments;"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task AppendPaymentAsync_returns_online_required_when_session_is_offline()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient();
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.AppendPaymentAsync(
                CreateOfflineSession(),
                CreateAppendPaymentRequest());

            Assert.Equal(InstallmentWriteStatus.OnlineRequired, result.Status);
            Assert.Null(result.Response);
            Assert.Null(result.LocalOrder);
            Assert.Equal(0, apiClient.AppendPaymentCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ConfirmPickupAsync_returns_online_required_when_session_is_offline()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient();
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.ConfirmPickupAsync(
                CreateOfflineSession(),
                CreateConfirmPickupRequest());

            Assert.Equal(InstallmentWriteStatus.OnlineRequired, result.Status);
            Assert.Null(result.Response);
            Assert.Null(result.LocalOrder);
            Assert.Equal(0, apiClient.ConfirmPickupCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateAsync_saves_local_snapshot_after_online_success()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(PaymentMethodKind.Cash)
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateAsync(
                CreateOnlineSession(),
                CreateInstallmentCreateRequest());

            Assert.Equal(InstallmentWriteStatus.Succeeded, result.Status);
            Assert.NotNull(result.LocalOrder);
            Assert.Equal(1, apiClient.CreateCallCount);
            Assert.Equal("IO-20260530-0001", result.LocalOrder!.InstallmentNumber);

            var saved = await repository.GetAsync(result.LocalOrder.InstallmentGuid);
            Assert.NotNull(saved);
            Assert.Equal(30m, saved.PaidAmount);
            Assert.Equal(90m, saved.BalanceAmount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task AppendPaymentAsync_updates_local_snapshot_after_online_success()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(PaymentMethodKind.Cash),
                AppendPaymentResponse = CreateAppendPaymentResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.AppendPaymentAsync(
                CreateOnlineSession(),
                CreateAppendPaymentRequest());

            Assert.Equal(InstallmentWriteStatus.Succeeded, result.Status);
            Assert.NotNull(result.LocalOrder);
            Assert.Equal(1, apiClient.AppendPaymentCallCount);
            Assert.Equal(70m, result.LocalOrder!.PaidAmount);
            Assert.Equal(50m, result.LocalOrder.BalanceAmount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ConfirmPickupAsync_updates_local_snapshot_after_online_success()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(PaymentMethodKind.Cash),
                ConfirmPickupResponse = CreateConfirmPickupResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.ConfirmPickupAsync(
                CreateOnlineSession(),
                CreateConfirmPickupRequest());

            Assert.Equal(InstallmentWriteStatus.Succeeded, result.Status);
            Assert.NotNull(result.LocalOrder);
            Assert.Equal(InstallmentStatus.PickedUp, result.LocalOrder!.Status);

            var saved = await repository.GetAsync(result.LocalOrder.InstallmentGuid);
            Assert.NotNull(saved);
            Assert.Equal(InstallmentStatus.PickedUp, saved.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_builds_create_request_from_create_page_inputs()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(PaymentMethodKind.Voucher)
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    6,
                    30m,
                    PaymentMethodKind.Voucher,
                    "VIP001",
                    "LOCK-001",
                    "周末取货"));

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Order);
            Assert.Equal("已创建分期单 IO-20260530-0001。", result.Message);
            Assert.Equal(PaymentMethodKind.Voucher, apiClient.LastCreateRequest!.DownPayment.Method);
            Assert.Equal("VIP001", apiClient.LastCreateRequest.DownPayment.Reference);
            Assert.Equal("LOCK-001", apiClient.LastCreateRequest.DownPayment.ReservationToken);
            var line = Assert.Single(apiClient.LastCreateRequest.Lines);
            Assert.Equal("购物车商品汇总", line.DisplayName);
            Assert.Equal(120m, line.ActualAmount);
            Assert.Equal("代金券", result.Order!.DownPaymentMethod);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task AddRepaymentAsync_builds_payment_request_and_updates_summary()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(PaymentMethodKind.Cash),
                AppendPaymentResponse = CreateAppendPaymentResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.AddRepaymentAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession(),
                40m,
                PaymentMethodKind.Voucher,
                "VIP001",
                "LOCK-002");

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Order);
            Assert.Equal(PaymentMethodKind.Voucher, apiClient.LastAppendPaymentRequest!.Method);
            Assert.Equal("VIP001", apiClient.LastAppendPaymentRequest.Reference);
            Assert.Equal("LOCK-002", apiClient.LastAppendPaymentRequest.ReservationToken);
            Assert.Equal(50m, result.Order!.OutstandingAmount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CancelWithRefundAsync_uses_recorded_payments_as_refund_inputs()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(PaymentMethodKind.Cash),
                CancelResponse = CreateCancelResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.CancelWithRefundAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession(),
                "客户取消");

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Order);
            Assert.Equal(2, apiClient.LastCancelRequest!.Refunds.Count);
            Assert.Equal(InstallmentStatus.Cancelled, apiClient.CancelResponse!.Details.Status);
            Assert.Equal("已取消", result.Order!.Status);
            Assert.False(result.Order.CanCancelRefund);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task VoidAsync_calls_void_endpoint_and_updates_summary()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(PaymentMethodKind.Cash),
                VoidResponse = CreateVoidResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.VoidAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession(),
                "门店作废");

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Order);
            Assert.Equal("门店作废", apiClient.LastVoidRequest!.Reason);
            Assert.Equal("已取消", result.Order!.Status);
            Assert.False(result.Order.CanVoid);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static PosSessionState CreateOfflineSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", false, 0);
    }

    private static PosSessionState CreateOnlineSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private static PosCartServiceSnapshot CreateCartSnapshot()
    {
        return new PosCartServiceSnapshot(130m, 10m, 120m);
    }

    private static InstallmentCreateRequest CreateInstallmentCreateRequest()
    {
        return new InstallmentCreateRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T10:00:00+10:00"),
            120m,
            30m,
            [
                new InstallmentLineDto(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "SKU-001",
                    null,
                    "Premium Rice Cooker",
                    "690001",
                    1m,
                    120m,
                    0m,
                    120m,
                    "ITEM-001")
            ],
            new InstallmentPaymentCommandDto(
                Guid.Parse("12345678-1111-2222-3333-444444444444"),
                PaymentMethodKind.Cash,
                30m,
                null),
            "张三",
            "0400111222",
            "周末取货");
    }

    private static InstallmentAppendPaymentRequest CreateAppendPaymentRequest()
    {
        return new InstallmentAppendPaymentRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("12345678-9999-aaaa-bbbb-cccccccccccc"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            40m,
            PaymentMethodKind.Voucher,
            "VIP001",
            "TOKEN-1");
    }

    private static InstallmentConfirmPickupRequest CreateConfirmPickupRequest()
    {
        return new InstallmentConfirmPickupRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-06-02T09:30:00+10:00"),
            "客户本人提货");
    }

    private static InstallmentCreateResponse CreateCreateResponse(PaymentMethodKind downPaymentMethod)
    {
        var details = CreateActiveDetails(downPaymentMethod);
        return new InstallmentCreateResponse(
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.Status,
            details.PaidAmount,
            details.BalanceAmount,
            details,
            false,
            null);
    }

    private static InstallmentAppendPaymentResponse CreateAppendPaymentResponse()
    {
        var details = CreateActiveDetails(PaymentMethodKind.Cash) with
        {
            PaidAmount = 70m,
            BalanceAmount = 50m,
            Payments =
            [
                .. CreateActiveDetails(PaymentMethodKind.Cash).Payments,
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-9999-aaaa-bbbb-cccccccccccc"),
                    PaymentMethodKind.Voucher,
                    40m,
                    "VIP001",
                    InstallmentPaymentStatus.Recorded,
                    DateTimeOffset.Parse("2026-05-30T10:20:00+10:00"),
                    "C001",
                    "POS-01")
            ]
        };

        return new InstallmentAppendPaymentResponse(
            details.InstallmentGuid,
            details.Payments[^1].PaymentGuid,
            details.PaidAmount,
            details.BalanceAmount,
            details.Status,
            details,
            false,
            null);
    }

    private static InstallmentConfirmPickupResponse CreateConfirmPickupResponse()
    {
        var pickedUpAt = DateTimeOffset.Parse("2026-06-02T09:30:00+10:00");
        var details = CreateActiveDetails(PaymentMethodKind.Cash) with
        {
            Status = InstallmentStatus.PickedUp,
            BalanceAmount = 0m,
            PaidAmount = 120m,
            PickupInfo = new InstallmentPickupInfoDto(pickedUpAt, "Alice", "客户本人提货")
        };

        return new InstallmentConfirmPickupResponse(
            details.InstallmentGuid,
            details.Status,
            pickedUpAt,
            details,
            false);
    }

    private static InstallmentCancelResponse CreateCancelResponse()
    {
        var cancelledAt = DateTimeOffset.Parse("2026-05-30T10:30:00+10:00");
        var details = CreateActiveDetails(PaymentMethodKind.Cash) with
        {
            Status = InstallmentStatus.Cancelled,
            CancellationInfo = new InstallmentCancellationInfoDto(
                InstallmentCancellationKind.RefundCancel,
                cancelledAt,
                "Alice",
                "客户取消"),
            Payments =
            [
                .. CreateActiveDetails(PaymentMethodKind.Cash).Payments,
                new InstallmentPaymentDto(
                    Guid.Parse("55555555-9999-aaaa-bbbb-cccccccccccc"),
                    PaymentMethodKind.Cash,
                    10m,
                    null,
                    InstallmentPaymentStatus.Voided,
                    cancelledAt,
                    "C001",
                    "POS-01")
            ]
        };

        return new InstallmentCancelResponse(details.InstallmentGuid, details.Status, details, false, null);
    }

    private static InstallmentVoidResponse CreateVoidResponse()
    {
        var voidedAt = DateTimeOffset.Parse("2026-05-30T10:35:00+10:00");
        var details = CreateActiveDetails(PaymentMethodKind.Cash) with
        {
            Status = InstallmentStatus.Cancelled,
            CancellationInfo = new InstallmentCancellationInfoDto(
                InstallmentCancellationKind.VoidCancel,
                voidedAt,
                "Alice",
                "门店作废")
        };

        return new InstallmentVoidResponse(details.InstallmentGuid, details.Status, details, false, null);
    }

    private static InstallmentDetailsDto CreateActiveDetails(PaymentMethodKind downPaymentMethod)
    {
        var createdAt = DateTimeOffset.Parse("2026-05-30T10:00:00+10:00");
        return new InstallmentDetailsDto(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "IO-20260530-0001",
            "S001",
            "POS-01",
            "C001",
            "Alice",
            "张三",
            "0400111222",
            createdAt,
            120m,
            20m,
            30m,
            30m,
            90m,
            InstallmentStatus.Active,
            [
                new InstallmentLineDto(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "SKU-001",
                    null,
                    "Premium Rice Cooker",
                    "690001",
                    1m,
                    120m,
                    0m,
                    120m,
                    "ITEM-001")
            ],
            [
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-1111-2222-3333-444444444444"),
                    downPaymentMethod,
                    downPaymentMethod == PaymentMethodKind.Cash ? 10m : 30m,
                    downPaymentMethod == PaymentMethodKind.Voucher ? "VIP001" : null,
                    InstallmentPaymentStatus.Recorded,
                    createdAt,
                    "C001",
                    "POS-01"),
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-5555-6666-7777-888888888888"),
                    PaymentMethodKind.Card,
                    downPaymentMethod == PaymentMethodKind.Cash ? 20m : 0m,
                    "ANZ:TXN-INS-1",
                    InstallmentPaymentStatus.Recorded,
                    createdAt.AddMinutes(1),
                    "C001",
                    "POS-01",
                    [
                        new CardTransactionDto(
                            "ANZ",
                            "TXN-INS-1",
                            "AUTH-001",
                            "VISA",
                            4,
                            "****1234",
                            "MID-001",
                            "00",
                            "APPROVED",
                            "001122",
                            createdAt.AddMinutes(1),
                            20m,
                            "merchant receipt")
                    ])
            ].Where(payment => payment.Amount > 0m).ToList(),
            null,
            null,
            "周末取货");
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-installment-service-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<int> ReadScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class StubInstallmentApiClient : IInstallmentApiClient
    {
        public InstallmentCreateResponse? CreateResponse { get; set; }

        public InstallmentAppendPaymentResponse? AppendPaymentResponse { get; set; }

        public InstallmentConfirmPickupResponse? ConfirmPickupResponse { get; set; }

        public InstallmentCancelResponse? CancelResponse { get; set; }

        public InstallmentVoidResponse? VoidResponse { get; set; }

        public InstallmentCreateRequest? LastCreateRequest { get; private set; }

        public InstallmentAppendPaymentRequest? LastAppendPaymentRequest { get; private set; }

        public InstallmentCancelRequest? LastCancelRequest { get; private set; }

        public InstallmentVoidRequest? LastVoidRequest { get; private set; }

        public int CreateCallCount { get; private set; }

        public int AppendPaymentCallCount { get; private set; }

        public int ConfirmPickupCallCount { get; private set; }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            LastCreateRequest = request;
            return Task.FromResult(CreateResponse ?? throw new InvalidOperationException("CreateResponse was not configured."));
        }

        public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            AppendPaymentCallCount++;
            LastAppendPaymentRequest = request;
            return Task.FromResult(AppendPaymentResponse ?? throw new InvalidOperationException("AppendPaymentResponse was not configured."));
        }

        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmPickupCallCount++;
            return Task.FromResult(ConfirmPickupResponse ?? throw new InvalidOperationException("ConfirmPickupResponse was not configured."));
        }

        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken = default)
        {
            LastCancelRequest = request;
            return Task.FromResult(CancelResponse ?? throw new InvalidOperationException("CancelResponse was not configured."));
        }

        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default)
        {
            LastVoidRequest = request;
            return Task.FromResult(VoidResponse ?? throw new InvalidOperationException("VoidResponse was not configured."));
        }
    }
}
