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

            var result = await service.CreateAsync(CreateOfflineSession(), CreateInstallmentCreateRequest());

            Assert.Equal(InstallmentWriteStatus.OnlineRequired, result.Status);
            Assert.Null(result.LocalOrder);
            Assert.Equal(0, apiClient.CreateCallCount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Write_operations_return_online_required_when_session_is_offline()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient();
            var service = new InstallmentOrderService(repository, apiClient);
            var offlineSession = CreateOfflineSession();

            await schema.InitializeAsync();

            var appendResult = await service.AppendPaymentAsync(offlineSession, CreateAppendPaymentRequest());
            var pickupResult = await service.ConfirmPickupAsync(offlineSession, CreateConfirmPickupRequest());
            var cancelResult = await service.CancelWithRefundAsync(offlineSession, CreateCancelRequest());
            var voidResult = await service.VoidCancelAsync(offlineSession, CreateVoidRequest());

            Assert.Equal(InstallmentWriteStatus.OnlineRequired, appendResult.Status);
            Assert.Equal(InstallmentWriteStatus.OnlineRequired, pickupResult.Status);
            Assert.Equal(InstallmentWriteStatus.OnlineRequired, cancelResult.Status);
            Assert.Equal(InstallmentWriteStatus.OnlineRequired, voidResult.Status);
            Assert.Equal(0, apiClient.AppendPaymentCallCount);
            Assert.Equal(0, apiClient.ConfirmPickupCallCount);
            Assert.Equal(0, apiClient.CancelCallCount);
            Assert.Equal(0, apiClient.VoidCallCount);
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
                CreateResponse = CreateCreateResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            Assert.Equal(InstallmentWriteStatus.Succeeded, result.Status);
            Assert.NotNull(result.LocalOrder);
            Assert.Equal(1, apiClient.CreateCallCount);
            Assert.Equal("IO-20260530-0001", result.LocalOrder!.InstallmentNumber);

            var saved = await repository.GetAsync(result.LocalOrder.InstallmentGuid);
            Assert.NotNull(saved);
            Assert.Equal(InstallmentStatus.Active, saved.Status);
            Assert.Equal(30m, saved.PaidAmount);
            Assert.Equal(90m, saved.BalanceAmount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_maps_cart_lines_and_voucher_payment_into_api_request()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-444444444444"),
                        PaymentMethodKind.Voucher,
                        30m,
                        "VIP001",
                        "LOCK-001"),
                    "周末取货"));

            Assert.True(result.Succeeded);
            Assert.Equal("已创建分期单。", result.Message);
            Assert.NotNull(result.Order);
            Assert.NotNull(apiClient.LastCreateRequest);
            Assert.Equal(30m, apiClient.LastCreateRequest!.DownPayment.Amount);
            Assert.Equal(PaymentMethodKind.Voucher, apiClient.LastCreateRequest.DownPayment.Method);
            Assert.Equal("VIP001", apiClient.LastCreateRequest.DownPayment.Reference);
            Assert.Equal("LOCK-001", apiClient.LastCreateRequest.DownPayment.ReservationToken);
            Assert.Equal(2, apiClient.LastCreateRequest.Lines.Count);
            Assert.Equal("待补款", result.Order!.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_authorizes_card_down_payment_before_create_and_uses_authorized_details()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var events = new List<string>();
            var cardTransactions = new[]
            {
                new CardTransactionDto("ANZ", "TXN-1", "123456", "VISA", 4, "****1234", "MID", "00", "APPROVED", "42", DateTimeOffset.UtcNow, 35m, "receipt")
            };
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                OnCreate = _ => events.Add("api")
            };
            var cardTerminalClient = new RecordingCardTerminalClient(
                new PaymentAuthorizationResult(true, "ANZ:TXN-1", AuthorizedAmount: 35m, CardTransactions: cardTransactions),
                () => events.Add("authorize"));
            var service = new InstallmentOrderService(repository, apiClient, cardTerminalClient: cardTerminalClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-444444444444"),
                        PaymentMethodKind.Card,
                        30m,
                        "draft-reference"),
                    "周末取货"));

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { "authorize", "api" }, events);
            Assert.Equal(1, cardTerminalClient.AuthorizeCallCount);
            Assert.Equal(30m, cardTerminalClient.LastAuthorizeAmount);
            Assert.NotNull(apiClient.LastCreateRequest);
            Assert.Equal(PaymentMethodKind.Card, apiClient.LastCreateRequest!.DownPayment.Method);
            Assert.Equal(35m, apiClient.LastCreateRequest.DownPayment.Amount);
            Assert.Equal("ANZ:TXN-1", apiClient.LastCreateRequest.DownPayment.Reference);
            Assert.Same(cardTransactions, apiClient.LastCreateRequest.DownPayment.CardTransactions);
            Assert.False(string.IsNullOrWhiteSpace(apiClient.LastCreateRequest.DownPayment.IdempotencyKey));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateOrderAsync_does_not_call_api_when_card_down_payment_authorization_fails()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse()
            };
            var cardTerminalClient = new RecordingCardTerminalClient(
                new PaymentAuthorizationResult(false, Message: "card auth declined"));
            var service = new InstallmentOrderService(repository, apiClient, cardTerminalClient: cardTerminalClient);

            await schema.InitializeAsync();

            var result = await service.CreateOrderAsync(
                new InstallmentOrderCreateRequest(
                    CreateOnlineSession(),
                    CreateCartSnapshot(),
                    "张三",
                    "0400111222",
                    30m,
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-1111-2222-3333-444444444444"),
                        PaymentMethodKind.Card,
                        30m,
                        "draft-reference"),
                    "周末取货"));

            Assert.False(result.Succeeded);
            Assert.Equal("card auth declined", result.Message);
            Assert.Equal(1, cardTerminalClient.AuthorizeCallCount);
            Assert.Equal(0, apiClient.CreateCallCount);
            Assert.Null(apiClient.LastCreateRequest);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task AddRepaymentAsync_builds_append_payment_request()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                AppendPaymentResponse = CreateAppendPaymentResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.AddRepaymentAsync(
                new InstallmentOrderRepaymentRequest(
                    Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    CreateOnlineSession(),
                    new InstallmentPaymentDraft(
                        Guid.Parse("12345678-9999-aaaa-bbbb-cccccccccccc"),
                        PaymentMethodKind.Voucher,
                        40m,
                        "VIP001",
                        "LOCK-002")));

            Assert.True(result.Succeeded);
            Assert.Equal("补款完成", result.Message);
            Assert.NotNull(apiClient.LastAppendPaymentRequest);
            Assert.Equal(PaymentMethodKind.Voucher, apiClient.LastAppendPaymentRequest!.Method);
            Assert.Equal("VIP001", apiClient.LastAppendPaymentRequest.Reference);
            Assert.Equal("LOCK-002", apiClient.LastAppendPaymentRequest.ReservationToken);
            Assert.NotNull(result.Order);
            Assert.Equal(70m, result.Order!.PaidAmount);
            Assert.Equal(50m, result.Order.OutstandingAmount);

            var saved = await repository.GetAsync(result.Order.OrderId);
            Assert.NotNull(saved);
            Assert.Equal(70m, saved.PaidAmount);
            Assert.Equal(50m, saved.BalanceAmount);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CancelWithRefundAsync_builds_refund_request_from_recorded_payments()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                CancelResponse = CreateCancelResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.CancelWithRefundAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession());

            Assert.True(result.Succeeded);
            Assert.Equal("已取消并退款", result.Message);
            Assert.NotNull(apiClient.LastCancelRequest);
            var refund = Assert.Single(apiClient.LastCancelRequest!.Refunds);
            Assert.Equal(PaymentMethodKind.Cash, refund.Method);
            Assert.NotNull(result.Order);
            Assert.False(result.Order!.CanAddRepayment);

            var saved = await repository.GetAsync(result.Order.OrderId);
            Assert.NotNull(saved);
            Assert.Equal(InstallmentStatus.Cancelled, saved.Status);
            Assert.Equal(InstallmentCancellationKind.RefundCancel, saved.CancellationInfo?.Kind);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CancelWithRefundAsync_does_not_call_api_when_card_refund_fails()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CancelResponse = CreateCancelResponse()
            };
            var service = new InstallmentOrderService(
                repository,
                apiClient,
                cardTerminalClient: new DeclinedCardTerminalClient());

            await schema.InitializeAsync();
            await repository.UpsertAsync(CreateLocalOrderWithPayments([
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-5555-6666-7777-888888888888"),
                    PaymentMethodKind.Card,
                    30m,
                    "CARD-TXN-1",
                    InstallmentPaymentStatus.Recorded,
                    DateTimeOffset.Parse("2026-05-30T10:00:00+10:00"),
                    "C001",
                    "POS-01")
            ]));

            var result = await service.CancelWithRefundAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession());

            Assert.False(result.Succeeded);
            Assert.Equal("card refund declined", result.Message);
            Assert.Null(apiClient.LastCancelRequest);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task VoidCancelAsync_builds_void_request_with_reason()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var apiClient = new StubInstallmentApiClient
            {
                CreateResponse = CreateCreateResponse(),
                VoidResponse = CreateVoidResponse()
            };
            var service = new InstallmentOrderService(repository, apiClient);

            await schema.InitializeAsync();
            await service.CreateAsync(CreateOnlineSession(), CreateInstallmentCreateRequest());

            var result = await service.VoidCancelAsync(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreateOnlineSession(),
                "门店作废");

            Assert.True(result.Succeeded);
            Assert.Equal("已作废", result.Message);
            Assert.NotNull(apiClient.LastVoidRequest);
            Assert.Equal("门店作废", apiClient.LastVoidRequest!.Reason);
            Assert.NotNull(result.Order);
            Assert.False(result.Order!.CanVoidCancel);

            var saved = await repository.GetAsync(result.Order.OrderId);
            Assert.NotNull(saved);
            Assert.Equal(InstallmentStatus.Cancelled, saved.Status);
            Assert.Equal(InstallmentCancellationKind.VoidCancel, saved.CancellationInfo?.Kind);
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
        return new PosCartServiceSnapshot(
            130m,
            10m,
            120m,
            [
                new PosCartLineServiceSnapshot("SKU-001", null, "Premium Rice Cooker", "690001", "ITEM-001", 1m, 130m, 10m, 120m),
                new PosCartLineServiceSnapshot("SKU-002", null, "Rice Bowl Set", "690002", "ITEM-002", 1m, 0m, 0m, 0m)
            ]);
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
            PaymentMethodKind.Cash,
            null,
            null);
    }

    private static InstallmentConfirmPickupRequest CreateConfirmPickupRequest()
    {
        return new InstallmentConfirmPickupRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T11:00:00+10:00"),
            "客户本人提货");
    }

    private static InstallmentCancelRequest CreateCancelRequest()
    {
        return new InstallmentCancelRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T11:10:00+10:00"),
            [
                new InstallmentRefundPaymentCommandDto(
                    Guid.Parse("55555555-9999-aaaa-bbbb-cccccccccccc"),
                    PaymentMethodKind.Cash,
                    30m,
                    null,
                    null,
                    "refund-offline-test")
            ],
            "客户取消",
            "cancel-offline-test");
    }

    private static InstallmentVoidRequest CreateVoidRequest()
    {
        return new InstallmentVoidRequest(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-05-30T11:20:00+10:00"),
            "门店作废",
            "void-offline-test");
    }

    private static InstallmentCreateResponse CreateCreateResponse()
    {
        var details = CreateActiveDetails();
        return new InstallmentCreateResponse(
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.Status,
            details.PaidAmount,
            details.BalanceAmount,
            details,
            false,
            "已创建分期单。");
    }

    private static InstallmentAppendPaymentResponse CreateAppendPaymentResponse()
    {
        var details = CreateActiveDetails() with
        {
            PaidAmount = 70m,
            BalanceAmount = 50m,
            Payments =
            [
                .. CreateActiveDetails().Payments,
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
            "补款完成");
    }

    private static InstallmentCancelResponse CreateCancelResponse()
    {
        var cancelledAt = DateTimeOffset.Parse("2026-05-30T10:30:00+10:00");
        var details = CreateActiveDetails() with
        {
            Status = InstallmentStatus.Cancelled,
            CancellationInfo = new InstallmentCancellationInfoDto(
                InstallmentCancellationKind.RefundCancel,
                cancelledAt,
                "Alice",
                "客户取消")
        };

        return new InstallmentCancelResponse(details.InstallmentGuid, details.Status, details, false, "已取消并退款");
    }

    private static InstallmentVoidResponse CreateVoidResponse()
    {
        var voidedAt = DateTimeOffset.Parse("2026-05-30T10:35:00+10:00");
        var details = CreateActiveDetails() with
        {
            Status = InstallmentStatus.Cancelled,
            CancellationInfo = new InstallmentCancellationInfoDto(
                InstallmentCancellationKind.VoidCancel,
                voidedAt,
                "Alice",
                "门店作废")
        };

        return new InstallmentVoidResponse(details.InstallmentGuid, details.Status, details, false, "已作废");
    }

    private static InstallmentDetailsDto CreateActiveDetails()
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
                    PaymentMethodKind.Cash,
                    30m,
                    null,
                    InstallmentPaymentStatus.Recorded,
                    createdAt,
                    "C001",
                    "POS-01")
            ],
            null,
            null,
            "周末取货");
    }

    private static LocalInstallmentOrder CreateLocalOrderWithPayments(IReadOnlyList<InstallmentPaymentDto> payments)
    {
        var paidAmount = payments.Where(payment => payment.Status == InstallmentPaymentStatus.Recorded).Sum(payment => payment.Amount);
        var details = CreateActiveDetails() with
        {
            PaidAmount = paidAmount,
            BalanceAmount = 120m - paidAmount,
            Payments = payments
        };
        return new LocalInstallmentOrder(
            details.InstallmentGuid,
            details.InstallmentGuid,
            details.InstallmentNumber,
            details.StoreCode,
            details.DeviceCode,
            details.CashierId,
            details.CashierName,
            details.CustomerName,
            details.CustomerPhone,
            details.CreatedAt,
            DateTimeOffset.UtcNow,
            details.TotalAmount,
            details.MinimumDownPayment,
            details.DownPaymentAmount,
            details.PaidAmount,
            details.BalanceAmount,
            details.Status,
            details.Lines,
            details.Payments,
            details.PickupInfo,
            details.Note,
            details.CancellationInfo);
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

    private sealed class StubInstallmentApiClient : IInstallmentApiClient
    {
        public InstallmentCreateResponse? CreateResponse { get; set; }

        public InstallmentAppendPaymentResponse? AppendPaymentResponse { get; set; }

        public InstallmentConfirmPickupResponse? ConfirmPickupResponse { get; set; }

        public InstallmentCancelResponse? CancelResponse { get; set; }

        public InstallmentVoidResponse? VoidResponse { get; set; }

        public Action<InstallmentCreateRequest>? OnCreate { get; set; }

        public InstallmentCreateRequest? LastCreateRequest { get; private set; }

        public InstallmentAppendPaymentRequest? LastAppendPaymentRequest { get; private set; }

        public InstallmentCancelRequest? LastCancelRequest { get; private set; }

        public InstallmentVoidRequest? LastVoidRequest { get; private set; }

        public int CreateCallCount { get; private set; }

        public int AppendPaymentCallCount { get; private set; }

        public int ConfirmPickupCallCount { get; private set; }

        public int CancelCallCount { get; private set; }

        public int VoidCallCount { get; private set; }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken = default)
        {
            OnCreate?.Invoke(request);
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
            CancelCallCount++;
            LastCancelRequest = request;
            return Task.FromResult(CancelResponse ?? throw new InvalidOperationException("CancelResponse was not configured."));
        }

        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken = default)
        {
            VoidCallCount++;
            LastVoidRequest = request;
            return Task.FromResult(VoidResponse ?? throw new InvalidOperationException("VoidResponse was not configured."));
        }
    }

    private sealed class RecordingCardTerminalClient(
        PaymentAuthorizationResult authorizeResult,
        Action? onAuthorize = null) : ICardTerminalClient
    {
        public int AuthorizeCallCount { get; private set; }

        public decimal? LastAuthorizeAmount { get; private set; }

        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            onAuthorize?.Invoke();
            AuthorizeCallCount++;
            LastAuthorizeAmount = amount;
            return Task.FromResult(authorizeResult);
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(false, Message: "card refund declined"));
        }
    }

    private sealed class DeclinedCardTerminalClient : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(false, Message: "card auth declined"));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(false, Message: "card refund declined"));
        }
    }
}
