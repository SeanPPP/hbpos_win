using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class CardPaymentRecoveryServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PosSessionState Session = new("HB POS", "S001", "Main Branch", "POS-01", "C001", "Alice", true, 0);

    [Fact]
    public async Task RecoverLatestAsync_approved_matching_session_completes_order_and_acknowledges_once()
    {
        var attempt = CreateAttempt(sessionId: "SESSION-001", txnRef: "TXN-001");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus("Completed", sessionId: "SESSION-001", txnRef: "TXN-001", responseCode: "00", responseText: "APPROVED")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.NotNull(result.Order);
        Assert.Equal(CreateOrderGuid(), result.Order!.OrderGuid);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal("SESSION-001", backend.AcknowledgedSessionId);
        Assert.NotNull(attempts.AcknowledgedAt);
    }

    [Fact]
    public async Task RecoverLatestAsync_approved_acknowledge_failure_still_returns_completed_order_without_retrying_save()
    {
        var attempt = CreateAttempt(sessionId: "SESSION-001", txnRef: "TXN-001");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus("Completed", sessionId: "SESSION-001", txnRef: "TXN-001", responseCode: "00", responseText: "APPROVED"),
            AcknowledgeException = new InvalidOperationException("ack failed")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.NotNull(result.Order);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Null(attempts.AcknowledgedAt);
    }

    [Fact]
    public async Task RecoverLatestAsync_order_completed_without_acknowledgement_retries_ack_only()
    {
        var attempt = CreateAttempt(
            sessionId: "SESSION-ACK",
            txnRef: "TXN-ACK",
            status: LocalCardPaymentAttemptStatus.OrderCompleted);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient();
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.None, result.Outcome);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal("SESSION-ACK", backend.AcknowledgedSessionId);
        Assert.NotNull(attempts.AcknowledgedAt);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
    }

    [Fact]
    public async Task RecoverLatestAsync_order_completed_ack_retry_failure_keeps_unacknowledged_attempt()
    {
        var attempt = CreateAttempt(
            sessionId: "SESSION-ACK",
            txnRef: "TXN-ACK",
            status: LocalCardPaymentAttemptStatus.OrderCompleted);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            AcknowledgeException = new InvalidOperationException("ack failed")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.None, result.Outcome);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal("SESSION-ACK", backend.AcknowledgedSessionId);
        Assert.Null(attempts.AcknowledgedAt);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("NotSubmitted")]
    public async Task RecoverActiveSessionAsync_without_local_attempt_recovers_and_acknowledges_failed_session(string finalStatus)
    {
        var attempts = new FakeCardPaymentAttemptRepository(null);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus("Pending", sessionId: "ACTIVE-SESSION", txnRef: "TXN-ACTIVE", responseCode: null, responseText: null),
            Status = CreateStatus(finalStatus, sessionId: "ACTIVE-SESSION", txnRef: "TXN-ACTIVE", responseCode: "05", responseText: finalStatus)
        };
        var cart = CreateCurrentCart();
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverActiveSessionAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.Equal(1, backend.ResumeCallCount);
        Assert.Equal("ACTIVE-SESSION", backend.ResumedSessionId);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal("ACTIVE-SESSION", backend.AcknowledgedSessionId);
        Assert.Single(cart.Lines);
        Assert.Equal("CURRENT-SKU", cart.Lines[0].ProductCode);
        Assert.Equal(0, orders.SaveCount);
        Assert.Contains("previous Linkly session", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoverActiveSessionAsync_without_local_attempt_completed_session_requires_supervisor_review()
    {
        var attempts = new FakeCardPaymentAttemptRepository(null);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus("Completed", sessionId: "ACTIVE-APPROVED", txnRef: "TXN-APPROVED", responseCode: "00", responseText: "APPROVED")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverActiveSessionAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(0, orders.SaveCount);
        Assert.Contains("supervisor", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TXN-APPROVED", result.DialogDetails?.TxnRef);
    }

    [Fact]
    public async Task RecoverLatestAsync_requires_review_returns_unknown_without_status_query_or_acknowledge()
    {
        var attempt = CreateAttempt(
            sessionId: "SESSION-REVIEW",
            txnRef: "TXN-REVIEW",
            status: LocalCardPaymentAttemptStatus.RequiresReview);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient();
        var cart = new PosCartService();
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Contains("does not match the order amount", result.Message);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.RequiresReview, attempts.Status);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task RecoverLatestAsync_approved_txn_ref_mismatch_returns_unknown_without_saving_or_acknowledging()
    {
        var attempt = CreateAttempt(sessionId: "SESSION-001", txnRef: "TXN-LOCAL");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus("Completed", sessionId: "SESSION-001", txnRef: "TXN-REMOTE", responseCode: "00", responseText: "APPROVED")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Null(attempts.AcknowledgedAt);
    }

    [Theory]
    [InlineData("Failed", "OPERATOR TIMEOUT", LocalCardPaymentAttemptStatus.TimedOut)]
    [InlineData("NotSubmitted", "Linkly Cloud returned HTTP 400.", LocalCardPaymentAttemptStatus.Failed)]
    public async Task RecoverLatestAsync_final_resumable_failure_restores_draft_and_acknowledges(
        string status,
        string responseText,
        LocalCardPaymentAttemptStatus expectedStatus)
    {
        var attempt = CreateAttempt(sessionId: null, txnRef: "TXN-FAILED");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus(status, sessionId: "SESSION-FAILED", txnRef: "TXN-FAILED", responseCode: "05", responseText: responseText)
        };
        var cart = new PosCartService();
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.Single(cart.Lines);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(expectedStatus, attempts.Status);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal("SESSION-FAILED", backend.AcknowledgedSessionId);
        Assert.NotNull(attempts.AcknowledgedAt);
    }

    [Fact]
    public async Task RecoverLatestAsync_pending_resumable_resumes_to_final_binds_session_and_completes_order()
    {
        var attempt = CreateAttempt(sessionId: null, txnRef: "TXN-PENDING");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus("Pending", sessionId: "SESSION-PENDING", txnRef: "TXN-PENDING", responseCode: null, responseText: "PRESENT CARD"),
            Status = CreateStatus("Completed", sessionId: "SESSION-PENDING", txnRef: "TXN-PENDING", responseCode: "00", responseText: "APPROVED")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.OrderCompleted, result.Outcome);
        Assert.Equal(1, backend.ResumeCallCount);
        Assert.Equal("SESSION-PENDING", backend.ResumedSessionId);
        Assert.Equal("SESSION-PENDING", attempts.SessionId);
        Assert.Equal("TXN-PENDING", attempts.TxnRef);
        Assert.Equal(1, orders.SaveCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Status);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal("SESSION-PENDING", backend.AcknowledgedSessionId);
    }

    [Fact]
    public async Task RecoverLatestAsync_pending_resumable_resumes_to_final_and_restores_draft_after_decline()
    {
        var attempt = CreateAttempt(sessionId: null, txnRef: "TXN-PENDING");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus("Pending", sessionId: "SESSION-PENDING", txnRef: "TXN-PENDING", responseCode: null, responseText: "PRESENT CARD"),
            Status = CreateStatus("Failed", sessionId: "SESSION-PENDING", txnRef: "TXN-PENDING", responseCode: "05", responseText: "DECLINED")
        };
        var cart = new PosCartService();
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.DraftRestored, result.Outcome);
        Assert.Equal(1, backend.ResumeCallCount);
        Assert.Equal("SESSION-PENDING", backend.ResumedSessionId);
        Assert.Equal("SESSION-PENDING", attempts.SessionId);
        Assert.Equal("TXN-PENDING", attempts.TxnRef);
        Assert.Single(cart.Lines);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Declined, attempts.Status);
        Assert.Equal(1, backend.AcknowledgeCallCount);
        Assert.Equal("SESSION-PENDING", backend.AcknowledgedSessionId);
    }

    [Fact]
    public async Task RecoverLatestAsync_resumable_without_local_session_or_txn_ref_fails_closed_without_saving()
    {
        var attempt = CreateAttempt(sessionId: null, txnRef: null);
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus("Pending", sessionId: "SESSION-UNKNOWN", txnRef: "TXN-UNKNOWN", responseCode: null, responseText: "PRESENT CARD"),
            Status = CreateStatus("Completed", sessionId: "SESSION-UNKNOWN", txnRef: "TXN-UNKNOWN", responseCode: "00", responseText: "APPROVED")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Contains("cannot be confirmed", result.Message);
        Assert.Equal(1, backend.ResumeCallCount);
        Assert.Null(attempts.SessionId);
        Assert.Null(attempts.TxnRef);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
    }

    [Fact]
    public async Task RecoverLatestAsync_status_query_failure_returns_unknown_without_saving_or_acknowledging()
    {
        var attempt = CreateAttempt(sessionId: "SESSION-001", txnRef: "TXN-001");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            StatusException = new InvalidOperationException("network down")
        };
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(new PosCartService(), Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Null(attempts.AcknowledgedAt);
    }

    [Fact]
    public async Task RecoverLatestAsync_approved_with_non_empty_current_cart_defers_without_saving_or_acknowledging()
    {
        var attempt = CreateAttempt(sessionId: "SESSION-001", txnRef: "TXN-001");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            Status = CreateStatus("Completed", sessionId: "SESSION-001", txnRef: "TXN-001", responseCode: "00", responseText: "APPROVED")
        };
        var cart = CreateCurrentCart();
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Single(cart.Lines);
        Assert.Equal("CURRENT-SKU", cart.Lines[0].ProductCode);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Null(attempts.AcknowledgedAt);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("NotSubmitted")]
    public async Task RecoverLatestAsync_failure_with_non_empty_current_cart_defers_without_restoring_or_acknowledging(string status)
    {
        var attempt = CreateAttempt(sessionId: null, txnRef: "TXN-FAILED");
        var attempts = new FakeCardPaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var backend = new FakeLinklyBackendTerminalClient
        {
            ResumableStatus = CreateStatus(status, sessionId: "SESSION-FAILED", txnRef: "TXN-FAILED", responseCode: "05", responseText: "OPERATOR TIMEOUT")
        };
        var cart = CreateCurrentCart();
        var service = CreateService(attempts, orders, backend);

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Single(cart.Lines);
        Assert.Equal("CURRENT-SKU", cart.Lines[0].ProductCode);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(0, backend.AcknowledgeCallCount);
        Assert.Equal(LocalCardPaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Null(attempts.AcknowledgedAt);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_verified_with_non_empty_current_cart_defers_without_saving_or_completing()
    {
        var attempt = CreateSquareAttempt(
            LocalSquarePaymentAttemptStatus.PaymentVerified,
            checkoutId: "CHECKOUT-001",
            paymentId: "PAYMENT-001",
            paymentStatus: "COMPLETED");
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var service = CreateSquareService(attempts, orders, new FakeSquareTerminalPaymentClient());
        var cart = CreateCurrentCart();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Contains("current cart already contains items", result.Message);
        Assert.Single(cart.Lines);
        Assert.Equal("CURRENT-SKU", cart.Lines[0].ProductCode);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Equal(0, attempts.MarkOrderCompletedCount);
        Assert.Equal(0, attempts.MarkFailedCount);
    }

    [Fact]
    public async Task RecoverLatestAsync_square_canceled_with_non_empty_current_cart_defers_without_restoring_or_marking_terminal()
    {
        var attempt = CreateSquareAttempt(LocalSquarePaymentAttemptStatus.CheckoutCreated, checkoutId: "CHECKOUT-001");
        var attempts = new FakeSquarePaymentAttemptRepository(attempt);
        var orders = new FakeLocalOrderRepository();
        var terminal = new FakeSquareTerminalPaymentClient
        {
            Checkout = new SquareCheckoutStatusResult("CHECKOUT-001", "CANCELED", 1000, "AUD", [], "OPERATOR TIMEOUT")
        };
        var service = CreateSquareService(attempts, orders, terminal);
        var cart = CreateCurrentCart();

        var result = await service.RecoverLatestAsync(cart, Session);

        Assert.Equal(CardPaymentRecoveryOutcome.Unknown, result.Outcome);
        Assert.Contains("current cart already contains items", result.Message);
        Assert.Single(cart.Lines);
        Assert.Equal("CURRENT-SKU", cart.Lines[0].ProductCode);
        Assert.Equal(0, orders.SaveCount);
        Assert.Equal(LocalSquarePaymentAttemptStatus.Recovering, attempts.Status);
        Assert.Equal(0, attempts.UpdateCheckoutStatusCount);
        Assert.Equal(0, attempts.MarkFailedCount);
    }

    private static CardPaymentRecoveryService CreateService(
        FakeCardPaymentAttemptRepository attempts,
        FakeLocalOrderRepository orders,
        FakeLinklyBackendTerminalClient backend)
    {
        return new CardPaymentRecoveryService(
            attempts,
            new FakeCardTerminalSettingsProvider(),
            backend,
            new CashCheckoutService(),
            orders,
            new FakeSyncQueueRepository());
    }

    private static SquarePaymentRecoveryService CreateSquareService(
        FakeSquarePaymentAttemptRepository attempts,
        FakeLocalOrderRepository orders,
        FakeSquareTerminalPaymentClient terminal)
    {
        return new SquarePaymentRecoveryService(
            attempts,
            new FakeSquareCardTerminalSettingsProvider(),
            terminal,
            new CashCheckoutService(),
            orders);
    }

    private static LocalCardPaymentAttempt CreateAttempt(
        string? sessionId,
        string? txnRef,
        LocalCardPaymentAttemptStatus status = LocalCardPaymentAttemptStatus.SessionStarted,
        DateTimeOffset? acknowledgedAt = null)
    {
        var now = DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");
        return new LocalCardPaymentAttempt(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            sessionId,
            txnRef,
            "Linkly",
            "Sandbox",
            "CloudBackendAsync",
            "P",
            10m,
            status,
            JsonSerializer.Serialize(CreateDraft(), JsonOptions),
            "S001",
            "POS-01",
            "C001",
            null,
            null,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            status == LocalCardPaymentAttemptStatus.OrderCompleted ? now.AddMinutes(-1) : null,
            acknowledgedAt);
    }

    private static LocalSquarePaymentAttempt CreateSquareAttempt(
        LocalSquarePaymentAttemptStatus status,
        string? checkoutId,
        string? paymentId = null,
        string? paymentStatus = null)
    {
        var now = DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");
        return new LocalSquarePaymentAttempt(
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            checkoutId,
            "idem-square-001",
            "DEVICE-001",
            "LOCATION-001",
            "Sandbox",
            10m,
            1000,
            "AUD",
            status,
            checkoutId is null ? null : "COMPLETED",
            null,
            JsonSerializer.Serialize(CreateDraft(), JsonOptions),
            "S001",
            "POS-01",
            "C001",
            paymentId,
            paymentStatus,
            null,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            paymentId is null ? null : now.AddMinutes(-1),
            null,
            null);
    }

    private static CardPaymentOrderDraft CreateDraft()
    {
        return new CardPaymentOrderDraft(
            CreateOrderGuid(),
            Session,
            new PosCartSnapshot(
            [
                new PosCartLineSnapshot(
                    "S001",
                    "SKU-10",
                    null,
                    "Test Item",
                    "930010",
                    "ITEM-10",
                    null,
                    1m,
                    10m,
                    0m,
                    null,
                    PriceSourceKind.StoreRetailPrice,
                    "Store price")
            ]),
            [],
            10m,
            10m,
            "P",
            null,
            DateTimeOffset.Parse("2026-06-05T10:00:00+10:00"));
    }

    private static PosCartService CreateCurrentCart()
    {
        var cart = new PosCartService();
        cart.AddItem(new SellableItemDto(
            "S001",
            "CURRENT-SKU",
            null,
            "Current Cart Item",
            "930020",
            "ITEM-CURRENT",
            "930020",
            2m,
            PriceSourceKind.StoreRetailPrice,
            "Store price",
            1m,
            DateTimeOffset.UtcNow,
            null));
        return cart;
    }

    private static Guid CreateOrderGuid()
    {
        return Guid.Parse("11111111-2222-3333-4444-555555555555");
    }

    private static LinklyCloudBackendSessionResponse CreateStatus(
        string status,
        string sessionId,
        string? txnRef,
        string? responseCode,
        string? responseText)
    {
        return new LinklyCloudBackendSessionResponse(
            "Sandbox",
            "S001",
            "POS-01",
            sessionId,
            status,
            txnRef,
            responseCode,
            responseText,
            null,
            responseText,
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            "RECEIPT",
            0,
            null,
            null,
            200,
            []);
    }

    private sealed class FakeCardPaymentAttemptRepository(LocalCardPaymentAttempt? attempt) : ILocalCardPaymentAttemptRepository
    {
        private LocalCardPaymentAttempt? _attempt = attempt;

        public LocalCardPaymentAttemptStatus Status => _attempt?.Status ?? LocalCardPaymentAttemptStatus.Failed;

        public string? SessionId => _attempt?.SessionId;

        public string? TxnRef => _attempt?.TxnRef;

        public DateTimeOffset? AcknowledgedAt => _attempt?.AcknowledgedAt;

        public Task CreateAsync(LocalCardPaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateSessionAsync(Guid attemptGuid, string sessionId, string? txnRef, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            _attempt = _attempt! with
            {
                SessionId = sessionId,
                TxnRef = txnRef ?? _attempt.TxnRef,
                Status = LocalCardPaymentAttemptStatus.SessionStarted,
                UpdatedAt = updatedAt
            };
            return Task.CompletedTask;
        }

        public Task UpdateOutcomeAsync(
            Guid attemptGuid,
            LocalCardPaymentAttemptStatus status,
            string? responseCode,
            string? responseText,
            string? paymentReference,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            _attempt = _attempt! with
            {
                Status = status,
                ResponseCode = responseCode,
                ResponseText = responseText,
                PaymentReference = paymentReference,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            };
            return Task.CompletedTask;
        }

        public Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            _attempt = _attempt! with
            {
                Status = LocalCardPaymentAttemptStatus.OrderCompleted,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            };
            return Task.CompletedTask;
        }

        public Task MarkAcknowledgedAsync(Guid attemptGuid, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default)
        {
            _attempt = _attempt! with
            {
                AcknowledgedAt = acknowledgedAt,
                UpdatedAt = acknowledgedAt
            };
            return Task.CompletedTask;
        }

        public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            _attempt = _attempt! with
            {
                Status = LocalCardPaymentAttemptStatus.Recovering,
                UpdatedAt = updatedAt
            };
            return Task.CompletedTask;
        }

        public Task<LocalCardPaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string cashierId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalCardPaymentAttempt?>(_attempt);
        }

        public Task<LocalCardPaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalCardPaymentAttempt?>(_attempt);
        }
    }

    private sealed class FakeCardTerminalSettingsProvider : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CardTerminalSettings(
                CardProcessorKind.Linkly,
                CardTerminalEnvironment.Sandbox,
                "127.0.0.1",
                2011,
                null,
                null,
                null,
                "https://connect.squareupsandbox.com",
                TimeSpan.FromSeconds(30),
                LinklyConnectionMode.CloudBackendAsync));
        }
    }

    private sealed class FakeSquareCardTerminalSettingsProvider : ICardTerminalSettingsProvider
    {
        public Task<CardTerminalSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CardTerminalSettings(
                CardProcessorKind.Square,
                CardTerminalEnvironment.Sandbox,
                "127.0.0.1",
                2011,
                "DEVICE-001",
                "LOCATION-001",
                "token",
                "https://connect.squareupsandbox.com",
                TimeSpan.FromSeconds(30),
                LinklyConnectionMode.LocalIp));
        }
    }

    private sealed class FakeSquarePaymentAttemptRepository(LocalSquarePaymentAttempt attempt) : ILocalSquarePaymentAttemptRepository
    {
        public LocalSquarePaymentAttemptStatus Status { get; private set; } = attempt.Status;

        public int MarkFailedCount { get; private set; }

        public int UpdateCheckoutStatusCount { get; private set; }

        public int MarkOrderCompletedCount { get; private set; }

        public Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task MarkCheckoutCreatedAsync(Guid attemptGuid, string checkoutId, string? checkoutStatus, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Status = LocalSquarePaymentAttemptStatus.CheckoutCreated;
            return Task.CompletedTask;
        }

        public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Status = LocalSquarePaymentAttemptStatus.Recovering;
            return Task.CompletedTask;
        }

        public Task UpdateCheckoutStatusAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? cancelReason,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            UpdateCheckoutStatusCount++;
            Status = status;
            return Task.CompletedTask;
        }

        public Task MarkPaymentVerifiedAsync(
            Guid attemptGuid,
            string paymentId,
            string paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            Status = LocalSquarePaymentAttemptStatus.PaymentVerified;
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            Guid attemptGuid,
            LocalSquarePaymentAttemptStatus status,
            string? checkoutStatus,
            string? paymentStatus,
            string? responseCode,
            string? responseText,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default)
        {
            MarkFailedCount++;
            Status = status;
            return Task.CompletedTask;
        }

        public Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            MarkOrderCompletedCount++;
            Status = LocalSquarePaymentAttemptStatus.OrderCompleted;
            return Task.CompletedTask;
        }

        public Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string cashierId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(attempt);
        }

        public Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(attempt);
        }
    }

    private sealed class FakeSquareTerminalPaymentClient : ISquareTerminalPaymentClient
    {
        public SquareCheckoutStatusResult Checkout { get; set; } =
            new("CHECKOUT-001", "COMPLETED", 1000, "AUD", ["PAYMENT-001"], null);

        public SquarePaymentStatusResult Payment { get; set; } =
            new("PAYMENT-001", "COMPLETED", 1000, "AUD");

        public Task<SquareCheckoutStatusResult> GetCheckoutAsync(
            CardTerminalSettings settings,
            string checkoutId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Checkout);
        }

        public Task<SquarePaymentStatusResult> GetPaymentAsync(
            CardTerminalSettings settings,
            string paymentId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Payment);
        }
    }

    private sealed class FakeLinklyBackendTerminalClient : ILinklyBackendTerminalClient
    {
        public LinklyCloudBackendSessionResponse? Status { get; set; }

        public LinklyCloudBackendSessionResponse? ResumableStatus { get; set; }

        public Exception? StatusException { get; set; }

        public int AcknowledgeCallCount { get; private set; }

        public string? AcknowledgedSessionId { get; private set; }

        public int ResumeCallCount { get; private set; }

        public string? ResumedSessionId { get; private set; }

        public Exception? AcknowledgeException { get; set; }

        public Task<LinklyConnectionTestResult> TestConnectionAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(true, "ok"));
        }

        public Task<LinklyConnectionTestResult> TestTransactionStatusAsync(CardTerminalEnvironment environment, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(true, "status ok"));
        }

        public Task<PaymentAuthorizationResult> PurchaseAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(false));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(decimal amount, PosSessionState session, CardTerminalSettings settings, string? originalReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(false));
        }

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(CardTerminalSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResumableStatus);
        }

        public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Status ?? ResumableStatus ?? throw new InvalidOperationException("Missing status."));
        }

        public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(CardTerminalSettings settings, LinklyCloudBackendSessionResponse activeStatus, CancellationToken cancellationToken = default)
        {
            ResumeCallCount++;
            ResumedSessionId = activeStatus.SessionId;
            return Task.FromResult(Status ?? ResumableStatus ?? activeStatus);
        }

        public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default)
        {
            if (StatusException is not null)
            {
                throw StatusException;
            }

            return Task.FromResult(Status ?? throw new InvalidOperationException("Missing status."));
        }

        public Task AcknowledgeSessionAsync(CardTerminalSettings settings, string sessionId, CancellationToken cancellationToken = default)
        {
            AcknowledgeCallCount++;
            AcknowledgedSessionId = sessionId;
            if (AcknowledgeException is not null)
            {
                throw AcknowledgeException;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeLocalOrderRepository : ILocalOrderRepository
    {
        private LocalOrder? _saved;

        public int SaveCount { get; private set; }

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _saved = order;
            return Task.CompletedTask;
        }

        public Task UpdatePaymentReferenceAsync(
            Guid paymentGuid,
            string? reference,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(LocalOrderHistoryQuery query, int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_saved is not null && _saved.OrderGuid == orderGuid ? _saved : null);
        }
    }

    private sealed class FakeSyncQueueRepository : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(0, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }
}
