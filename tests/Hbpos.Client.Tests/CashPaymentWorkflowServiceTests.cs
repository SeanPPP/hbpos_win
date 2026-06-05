using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using System.Text.Json;

namespace Hbpos.Client.Tests;

public sealed class CashPaymentWorkflowServiceTests
{
    [Fact]
    public void Cash_payment_workflow_rounds_cash_due_and_change_for_7_82()
    {
        var workflow = CreateWorkflow();

        var parsed = workflow.TryParseTenderedAmount("10", out var tenderedAmount);
        var remaining = CashRoundingPolicy.GetCashPayableAmount(7.82m, []);
        var change = workflow.CalculateChange("10", 7.82m);

        Assert.True(parsed);
        Assert.Equal(10m, tenderedAmount);
        Assert.Equal(7.80m, remaining);
        Assert.Equal(2.20m, change);
    }

    [Fact]
    public void Cash_payment_workflow_rounds_cash_due_and_change_for_7_83()
    {
        var workflow = CreateWorkflow();

        var remaining = CashRoundingPolicy.GetCashPayableAmount(7.83m, []);
        var change = workflow.CalculateChange("10", 7.83m);

        Assert.Equal(7.85m, remaining);
        Assert.Equal(2.15m, change);
    }

    [Fact]
    public void Cash_payment_workflow_rejects_invalid_tendered_amount()
    {
        var workflow = CreateWorkflow();

        var parsed = workflow.TryParseTenderedAmount("cash", out var tenderedAmount);
        var change = workflow.CalculateChange("cash", 7.81m);

        Assert.False(parsed);
        Assert.Equal(0m, tenderedAmount);
        Assert.Equal(0m, change);
    }

    [Fact]
    public async Task Cash_payment_workflow_persists_order_clears_cart_and_refreshes_pending_sync()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-301", "Workflow Tea", "930301", 4.4m));
        var orders = new RecordingOrderRepository();
        var syncQueue = new StubSyncQueueRepository(pendingCount: 3);
        var workflow = new CashPaymentWorkflowService(new CashCheckoutService(), orders, syncQueue);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompleteAsync(cart, session, "5");

        var savedOrder = Assert.Single(orders.SavedOrders);
        Assert.Same(savedOrder, result.Order);
        Assert.Equal(4.4m, savedOrder.ActualAmount);
        Assert.Equal(5m, result.TenderedAmount);
        Assert.Equal(0.6m, result.ChangeAmount);
        Assert.Empty(cart.Lines);
        Assert.Equal(3, result.PendingSyncCount);
        Assert.Equal(3, result.UpdatedSession.PendingSyncCount);
        Assert.Equal(savedOrder.OrderGuid, result.Order.OrderGuid);
    }

    [Fact]
    public async Task Cash_payment_workflow_persists_rounded_cash_order_without_overstating_local_payment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-302", "Workflow Soda", "930302", 7.82m));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 2));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompleteAsync(cart, session, "10");

        var savedOrder = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(savedOrder.Payments);
        Assert.Equal(10m, result.TenderedAmount);
        Assert.Equal(2.20m, result.ChangeAmount);
        Assert.Equal(PaymentMethodKind.Cash, payment.Method);
        Assert.Equal(7.82m, payment.Amount);
    }

    [Fact]
    public async Task Card_tender_creates_local_attempt_before_terminal_request_and_marks_order_completed_after_save()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-399", "Recoverable Card Tea", "930399", 10m));
        var orders = new RecordingOrderRepository();
        var attempts = new RecordingCardPaymentAttemptRepository();
        var terminal = new ObservingCardTerminalClient(() =>
        {
            var attempt = Assert.Single(attempts.Attempts);
            Assert.Equal(LocalCardPaymentAttemptStatus.Pending, attempt.Status);
            Assert.Contains("\"cardAmount\":10", attempt.OrderDraftJson, StringComparison.OrdinalIgnoreCase);
        });
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardPaymentAttemptRepository: attempts,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateBackendLinklySettings()));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());
        var completion = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tenderResult.Tender!],
            cashTenderedAmount: 0m);

        Assert.True(tenderResult.Succeeded);
        Assert.Equal("backend-session-1", attempts.Attempts.Single().SessionId);
        Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, attempts.Attempts.Single().Status);
        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
            attempts.Attempts.Single().OrderDraftJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(draft!.OrderGuid, completion.Order.OrderGuid);
        Assert.Equal("ANZBACKEND:TXN-1:session=backend-session-1:environment=Sandbox", completion.Order.Payments.Single().Reference);
    }

    [Fact]
    public async Task Square_card_tender_creates_dedicated_attempt_and_marks_order_completed_after_save()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-398", "Square Recoverable Tea", "930398", 10m));
        var orders = new RecordingOrderRepository();
        var squareAttempts = new RecordingSquarePaymentAttemptRepository();
        var squareContext = new SquarePaymentAttemptContextAccessor();
        var terminal = new ObservingCardTerminalClient(() =>
        {
            var attempt = Assert.Single(squareAttempts.Attempts);
            Assert.Equal(LocalSquarePaymentAttemptStatus.Pending, attempt.Status);
            Assert.Contains("\"cardAmount\":10", attempt.OrderDraftJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(attempt.AttemptGuid, squareContext.Current?.AttemptGuid);
            Assert.Equal(attempt.IdempotencyKey, squareContext.Current?.IdempotencyKey);
        }, new PaymentAuthorizationResult(
            true,
            "SQ:payment-1",
            "Square",
            10m,
            [new CardTransactionDto("Square", "payment-1", null, null, null, null, null, null, "COMPLETED", null, DateTimeOffset.UtcNow, 10m, null)]));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: terminal,
            cardTerminalSettingsProvider: new StaticCardTerminalSettingsProvider(CreateSquareSettings()),
            squarePaymentAttemptRepository: squareAttempts,
            squarePaymentAttemptContextAccessor: squareContext);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tenderResult = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            10m,
            [],
            "10.00",
            cancellationToken: CancellationToken.None,
            cartSnapshot: cart.CreateSnapshot());
        var completion = await workflow.CompletePaymentAsync(
            cart,
            session,
            [tenderResult.Tender!],
            cashTenderedAmount: 0m);

        Assert.True(tenderResult.Succeeded);
        var savedAttempt = Assert.Single(squareAttempts.Attempts);
        Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, savedAttempt.Status);
        Assert.Equal("SQUARE_ATTEMPT:", tenderResult.Tender!.IdempotencyKey![..15]);
        var draft = JsonSerializer.Deserialize<CardPaymentOrderDraft>(
            savedAttempt.OrderDraftJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(draft!.OrderGuid, completion.Order.OrderGuid);
        Assert.Equal("SQ:payment-1", completion.Order.Payments.Single().Reference);
    }

    [Fact]
    public async Task Cash_payment_workflow_keeps_local_payment_total_aligned_when_cash_rounds_down()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-304", "Rounded Down Soda", "930304", 7.82m));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 2));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Cash, 7.80m)],
            cashTenderedAmount: 7.80m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(savedOrder.Payments);
        Assert.Equal(7.80m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
        Assert.Equal(7.82m, payment.Amount);
    }

    [Fact]
    public async Task Payment_workflow_allocates_cash_change_without_overstating_local_payments()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-303", "Workflow Soda", "930303", 7.83m));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 2),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-001"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [
                new PaymentTender(PaymentMethodKind.Card, 5m, "CARD-001"),
                new PaymentTender(PaymentMethodKind.Cash, 2.85m)
            ],
            cashTenderedAmount: 2.85m);

        var savedOrder = Assert.Single(orders.SavedOrders);
        Assert.Equal(7.85m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
        Assert.Collection(
            savedOrder.Payments,
            payment =>
            {
                Assert.Equal(PaymentMethodKind.Card, payment.Method);
                Assert.Equal(5m, payment.Amount);
            },
            payment =>
            {
                Assert.Equal(PaymentMethodKind.Cash, payment.Method);
                Assert.Equal(2.83m, payment.Amount);
            });
    }

    [Fact]
    public async Task Payment_workflow_add_tender_blocks_non_cash_over_remaining_and_accepts_voucher_code()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ApprovedVoucherTenderClient("VOUCHER-ABC"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var blocked = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: 10m,
            currentTenders: [new PaymentTender(PaymentMethodKind.Card, 8m, "CARD-001")],
            amountText: "3");
        var voucher = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "4",
            referenceText: "ABC123");

        Assert.False(blocked.Succeeded);
        Assert.Equal("payment.status.cardExceedsRemaining", blocked.StatusKey);
        Assert.True(voucher.Succeeded);
        Assert.NotNull(voucher.Tender);
        Assert.Equal(PaymentMethodKind.Voucher, voucher.Tender.Method);
        Assert.Equal(4m, voucher.Tender.Amount);
        Assert.Equal("VOUCHER-ABC", voucher.Tender.Reference);
    }

    [Fact]
    public async Task Payment_workflow_add_tender_normalizes_cash_input()
    {
        var workflow = CreateWorkflow();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var roundedDown = await workflow.AddTenderAsync(
            PaymentMethodKind.Cash,
            session,
            actualAmount: 20m,
            currentTenders: [],
            amountText: "10.02");
        var roundedUp = await workflow.AddTenderAsync(
            PaymentMethodKind.Cash,
            session,
            actualAmount: 20m,
            currentTenders: [],
            amountText: "10.03");

        Assert.True(roundedDown.Succeeded);
        Assert.NotNull(roundedDown.Tender);
        Assert.Equal(10.00m, roundedDown.Tender.Amount);
        Assert.True(roundedUp.Succeeded);
        Assert.NotNull(roundedUp.Tender);
        Assert.Equal(10.05m, roundedUp.Tender.Amount);
    }

    [Fact]
    public void Payment_workflow_uses_cash_rounding_after_non_cash_tender()
    {
        var workflow = CreateWorkflow();
        var remaining = CashRoundingPolicy.GetCashPayableAmount(
            7.83m,
            [new PaymentTender(PaymentMethodKind.Card, 5m, "CARD-001")]);

        Assert.Equal(2.85m, remaining);
    }

    [Fact]
    public async Task Payment_workflow_does_not_round_down_pure_non_cash_underpayment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-305", "Card Boundary Tea", "930305", 7.82m));
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-305"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var remaining = workflow.CalculateRemainingAmount(
            7.82m,
            [new PaymentTender(PaymentMethodKind.Card, 7.80m, "CARD-305")]);

        Assert.Equal(0.02m, remaining);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Card, 7.80m, "CARD-305")],
            cashTenderedAmount: 0m));
    }

    [Fact]
    public async Task Payment_workflow_uses_authorized_voucher_amount_for_partial_redemption()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ApprovedVoucherTenderClient("VOUCHER-PARTIAL", authorizedAmount: 3m));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var voucher = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: 10m,
            currentTenders: [],
            amountText: "5",
            referenceText: "ABC123");

        Assert.True(voucher.Succeeded);
        Assert.NotNull(voucher.Tender);
        Assert.Equal(3m, voucher.Tender.Amount);
        Assert.Equal("VOUCHER-PARTIAL", voucher.Tender.Reference);
    }

    [Fact]
    public async Task Payment_workflow_blocks_duplicate_voucher_code()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: new ApprovedVoucherTenderClient("VOUCHER:ABC123:token-2"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var duplicate = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: 10m,
            currentTenders: [new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-1")],
            amountText: "2",
            referenceText: "abc123");

        Assert.False(duplicate.Succeeded);
        Assert.Equal("payment.status.duplicateVoucher", duplicate.StatusKey);
    }

    [Fact]
    public async Task Payment_workflow_retries_failed_voucher_upload_without_saving_duplicate_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-303", "Voucher Retry Tea", "930303", 8m));
        var orders = new RecordingOrderRepository();
        var uploads = new FailingOnceOrderUploadService();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            orderUploadService: uploads);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tenders = new[]
        {
            new PaymentTender(PaymentMethodKind.Voucher, 3m, "VOUCHER:ABC123:token-1"),
            new PaymentTender(PaymentMethodKind.Cash, 5m)
        };

        var failed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            tenders,
            cashTenderedAmount: 5m));
        var result = await workflow.RetryVoucherUploadAsync(
            failed.OrderGuid,
            cart,
            session,
            failed.TenderedAmount,
            failed.ChangeAmount);

        Assert.Single(orders.SavedOrders);
        Assert.Equal(failed.OrderGuid, result.Order.OrderGuid);
        Assert.Equal([failed.OrderGuid, failed.OrderGuid], uploads.AttemptedOrderGuids);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task Payment_workflow_adds_negative_cash_tender_for_refund()
    {
        var workflow = CreateWorkflow();
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Cash,
            session,
            actualAmount: -7.82m,
            currentTenders: [],
            amountText: "7.82");

        Assert.True(tender.Succeeded);
        Assert.NotNull(tender.Tender);
        Assert.Equal(-7.80m, tender.Tender.Amount);
    }

    [Fact]
    public async Task Payment_workflow_adds_negative_card_tender_for_refund()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-REFUND"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -10m,
            currentTenders: [],
            amountText: "4",
            referenceText: "SQ:payment-1");

        Assert.True(tender.Succeeded);
        Assert.NotNull(tender.Tender);
        Assert.Equal(-4m, tender.Tender.Amount);
        Assert.True(CardRefundReference.TryGetOriginalReference(tender.Tender.Reference, out var originalReference));
        Assert.Equal("SQ:payment-1", originalReference);
        Assert.Equal("REFUND:SQ:payment-1", CardRefundReference.GetDisplayReference(tender.Tender.Reference));
    }

    [Fact]
    public async Task Payment_workflow_rejects_card_refund_without_original_reference()
    {
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-REFUND"));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Card,
            session,
            actualAmount: -10m,
            currentTenders: [],
            amountText: "4");

        Assert.False(tender.Succeeded);
        Assert.Equal("payment.status.cardDeclined", tender.StatusKey);
    }

    [Fact]
    public async Task Payment_workflow_adds_negative_voucher_tender_for_refund()
    {
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var tender = await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: -10m,
            currentTenders: [],
            amountText: "6");

        Assert.True(tender.Succeeded);
        Assert.NotNull(tender.Tender);
        Assert.Equal(-6m, tender.Tender.Amount);
        Assert.Equal("VOUCHER_REFUND_PENDING", tender.Tender.Reference);
        Assert.Equal(0, vouchers.IssueRefundCallCount);
    }

    [Fact]
    public void Payment_workflow_calculates_refund_remaining_and_change_without_over_refunding()
    {
        var workflow = CreateWorkflow();

        var remainingAfterCash = workflow.CalculateRemainingAmount(
            -7.82m,
            [new PaymentTender(PaymentMethodKind.Cash, -7.80m)]);
        var remainingAfterCard = workflow.CalculateRemainingAmount(
            -10m,
            [new PaymentTender(PaymentMethodKind.Card, -4m, "SQ:payment-1")]);
        var change = workflow.CalculateChange(
            [new PaymentTender(PaymentMethodKind.Cash, -7.80m)],
            -7.82m);

        Assert.Equal(0m, remainingAfterCash);
        Assert.Equal(-6m, remainingAfterCard);
        Assert.Equal(0m, change);
    }

    [Fact]
    public async Task Payment_workflow_completes_refund_order_with_negative_payments()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-RET",
            null,
            "Returned Tea",
            "930500",
            "ITEM-RET",
            null,
            1m,
            7.82m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-500",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1));
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Cash, -7.80m)],
            cashTenderedAmount: -7.80m);

        var payment = Assert.Single(result.Order.Payments);
        Assert.Equal(-7.80m, payment.Amount);
        Assert.Equal(-7.80m, result.TenderedAmount);
        Assert.Equal(0m, result.ChangeAmount);
    }

    [Fact]
    public async Task Payment_workflow_issues_refund_voucher_after_order_guid_exists()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR",
            null,
            "Voucher Refund Tea",
            "930501",
            "ITEM-VR",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-1",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var result = await workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING")],
            cashTenderedAmount: 0m);

        var saved = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(saved.Payments);
        Assert.Equal(result.Order.OrderGuid.ToString("D"), vouchers.LastOrderReference);
        Assert.Equal(saved.OrderGuid.ToString("D"), vouchers.LastOrderReference);
        Assert.False(string.IsNullOrWhiteSpace(vouchers.LastIdempotencyKey));
        Assert.Equal(-6m, payment.Amount);
        Assert.Equal("VOUCHER_REFUND:RF123", payment.Reference);
        Assert.Equal(1, vouchers.IssueRefundCallCount);
    }

    [Fact]
    public async Task Payment_workflow_does_not_issue_refund_voucher_before_local_save_succeeds()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR-RETRY",
            null,
            "Voucher Refund Retry",
            "930503",
            "ITEM-VR-RETRY",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-RETRY",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new FailingOnceOrderRepository();
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
        var tender = (await workflow.AddTenderAsync(
            PaymentMethodKind.Voucher,
            session,
            actualAmount: -6m,
            currentTenders: [],
            amountText: "6")).Tender!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [tender],
            cashTenderedAmount: 0m));
        Assert.Equal(0, vouchers.IssueRefundCallCount);

        await workflow.CompletePaymentAsync(
            cart,
            session,
            [tender],
            cashTenderedAmount: 0m);

        Assert.False(string.IsNullOrWhiteSpace(vouchers.LastIdempotencyKey));
        Assert.Equal(tender.IdempotencyKey, vouchers.LastIdempotencyKey);
        Assert.Equal(1, vouchers.IssueRefundCallCount);
        Assert.Single(orders.SavedOrders);
    }

    [Fact]
    public async Task Payment_workflow_persists_pending_voucher_refund_order_when_issue_fails()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR-FAIL",
            null,
            "Voucher Refund Fail",
            "930502",
            "ITEM-VR-FAIL",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-FAIL",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var vouchers = new ApprovedVoucherTenderClient("VOUCHER_REFUND:RF123", approveRefund: false);
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING")],
            cashTenderedAmount: 0m));

        var saved = Assert.Single(orders.SavedOrders);
        var payment = Assert.Single(saved.Payments);
        Assert.Equal("VOUCHER_REFUND_PENDING", payment.Reference);
        Assert.False(string.IsNullOrWhiteSpace(payment.IdempotencyKey));
        Assert.Equal(1, vouchers.IssueRefundCallCount);
    }

    [Fact]
    public async Task Payment_workflow_retry_reuses_pending_refund_voucher_idempotency_key_and_updates_local_reference()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "S001",
            "SKU-VR-RECOVER",
            null,
            "Voucher Refund Recover",
            "930504",
            "ITEM-VR-RECOVER",
            null,
            1m,
            6m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-VOUCHER-RECOVER",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var orders = new RecordingOrderRepository();
        var vouchers = new RetriableVoucherTenderClient("VOUCHER_REFUND:RF123");
        var workflow = new CashPaymentWorkflowService(
            new CashCheckoutService(),
            orders,
            new StubSyncQueueRepository(pendingCount: 1),
            voucherTenderClient: vouchers);
        var session = new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

        var failed = await Assert.ThrowsAsync<PaymentUploadFailedException>(() => workflow.CompletePaymentAsync(
            cart,
            session,
            [new PaymentTender(PaymentMethodKind.Voucher, -6m, "VOUCHER_REFUND_PENDING")],
            cashTenderedAmount: 0m));
        vouchers.FailIssueRefund = false;

        var savedBeforeRetry = Assert.Single(orders.SavedOrders);
        var pendingPayment = Assert.Single(savedBeforeRetry.Payments);
        var result = await workflow.RetryVoucherUploadAsync(
            savedBeforeRetry.OrderGuid,
            cart,
            session,
            tenderedAmount: 0m,
            changeAmount: 0m);

        Assert.Contains("issue failed", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, vouchers.IssueRefundCallCount);
        Assert.Equal(2, vouchers.IssueRefundIdempotencyKeys.Count);
        Assert.Equal(vouchers.IssueRefundIdempotencyKeys[0], vouchers.IssueRefundIdempotencyKeys[1]);
        Assert.Equal(pendingPayment.IdempotencyKey, vouchers.IssueRefundIdempotencyKeys[0]);
        Assert.Equal("VOUCHER_REFUND:RF123", Assert.Single(result.Order.Payments).Reference);
        Assert.Equal("VOUCHER_REFUND:RF123", Assert.Single(Assert.Single(orders.SavedOrders).Payments).Reference);
        Assert.Empty(cart.Lines);
    }

    private static ICashPaymentWorkflowService CreateWorkflow()
    {
        return new CashPaymentWorkflowService(
            new CashCheckoutService(),
            new RecordingOrderRepository(),
            new StubSyncQueueRepository(pendingCount: 1));
    }

    private static SellableItemDto CreateItem(string productCode, string name, string lookupCode, decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static CardTerminalSettings CreateBackendLinklySettings()
    {
        return new CardTerminalSettings(
            CardProcessorKind.Linkly,
            CardTerminalEnvironment.Sandbox,
            "127.0.0.1",
            2011,
            null,
            null,
            null,
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Sandbox),
            TimeSpan.FromSeconds(90),
            LinklyConnectionMode.CloudBackendAsync);
    }

    private static CardTerminalSettings CreateSquareSettings()
    {
        return new CardTerminalSettings(
            CardProcessorKind.Square,
            CardTerminalEnvironment.Production,
            "127.0.0.1",
            2011,
            "square-token",
            "LOC-1",
            "DEV-1",
            CardTerminalSettings.GetSquareApiBaseUrl(CardTerminalEnvironment.Production),
            TimeSpan.FromSeconds(90));
    }

    private sealed class RecordingOrderRepository : ILocalOrderRepository
    {
        public List<LocalOrder> SavedOrders { get; } = [];

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            SavedOrders.Add(order);
            return Task.CompletedTask;
        }

        public Task UpdatePaymentReferenceAsync(
            Guid paymentGuid,
            string? reference,
            CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < SavedOrders.Count; index++)
            {
                var order = SavedOrders[index];
                var paymentIndex = order.Payments
                    .ToList()
                    .FindIndex(payment => payment.PaymentGuid == paymentGuid);
                if (paymentIndex < 0 || paymentIndex >= order.Payments.Count)
                {
                    continue;
                }

                var updatedPayments = order.Payments.ToList();
                updatedPayments[paymentIndex] = updatedPayments[paymentIndex] with { Reference = reference };
                SavedOrders[index] = order with { Payments = updatedPayments };
                break;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(SavedOrders.LastOrDefault(order => order.OrderGuid == orderGuid));
        }
    }

    private sealed class FailingOnceOrderRepository : ILocalOrderRepository
    {
        private bool _hasFailed;

        public List<LocalOrder> SavedOrders { get; } = [];

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            if (!_hasFailed)
            {
                _hasFailed = true;
                throw new InvalidOperationException("local save failed");
            }

            SavedOrders.Add(order);
            return Task.CompletedTask;
        }

        public Task UpdatePaymentReferenceAsync(
            Guid paymentGuid,
            string? reference,
            CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < SavedOrders.Count; index++)
            {
                var order = SavedOrders[index];
                var paymentIndex = order.Payments
                    .ToList()
                    .FindIndex(payment => payment.PaymentGuid == paymentGuid);
                if (paymentIndex < 0 || paymentIndex >= order.Payments.Count)
                {
                    continue;
                }

                var updatedPayments = order.Payments.ToList();
                updatedPayments[paymentIndex] = updatedPayments[paymentIndex] with { Reference = reference };
                SavedOrders[index] = order with { Payments = updatedPayments };
                break;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(SavedOrders.LastOrDefault(order => order.OrderGuid == orderGuid));
        }
    }

    private sealed class StubSyncQueueRepository(int pendingCount) : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(pendingCount);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(pendingCount, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }

    private sealed class RecordingCardPaymentAttemptRepository : ILocalCardPaymentAttemptRepository
    {
        public List<LocalCardPaymentAttempt> Attempts { get; } = [];

        public Task CreateAsync(LocalCardPaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task UpdateSessionAsync(
            Guid attemptGuid,
            string sessionId,
            string? txnRef,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                SessionId = sessionId,
                TxnRef = txnRef,
                Status = LocalCardPaymentAttemptStatus.SessionStarted,
                UpdatedAt = updatedAt
            });
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
            Update(attemptGuid, attempt => attempt with
            {
                Status = status,
                ResponseCode = responseCode,
                ResponseText = responseText,
                PaymentReference = paymentReference,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkOrderCompletedAsync(
            Guid attemptGuid,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalCardPaymentAttemptStatus.OrderCompleted,
                CompletedAt = attempt.CompletedAt ?? completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkAcknowledgedAsync(
            Guid attemptGuid,
            DateTimeOffset acknowledgedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                AcknowledgedAt = acknowledgedAt,
                UpdatedAt = acknowledgedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkRecoveringAsync(
            Guid attemptGuid,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalCardPaymentAttemptStatus.Recovering,
                UpdatedAt = updatedAt
            });
            return Task.CompletedTask;
        }

        public Task<LocalCardPaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string cashierId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalCardPaymentAttempt?>(Attempts.LastOrDefault());
        }

        public Task<LocalCardPaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalCardPaymentAttempt?>(Attempts.SingleOrDefault(attempt => attempt.AttemptGuid == attemptGuid));
        }

        private void Update(Guid attemptGuid, Func<LocalCardPaymentAttempt, LocalCardPaymentAttempt> update)
        {
            var index = Attempts.FindIndex(attempt => attempt.AttemptGuid == attemptGuid);
            Assert.True(index >= 0);
            Attempts[index] = update(Attempts[index]);
        }
    }

    private sealed class RecordingSquarePaymentAttemptRepository : ILocalSquarePaymentAttemptRepository
    {
        public List<LocalSquarePaymentAttempt> Attempts { get; } = [];

        public Task CreateAsync(LocalSquarePaymentAttempt attempt, CancellationToken cancellationToken = default)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task MarkCheckoutCreatedAsync(
            Guid attemptGuid,
            string checkoutId,
            string? checkoutStatus,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                CheckoutId = checkoutId,
                CheckoutStatus = checkoutStatus,
                Status = LocalSquarePaymentAttemptStatus.CheckoutCreated,
                UpdatedAt = updatedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkRecoveringAsync(Guid attemptGuid, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.Recovering,
                UpdatedAt = updatedAt
            });
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
            Update(attemptGuid, attempt => attempt with
            {
                Status = status,
                CheckoutStatus = checkoutStatus ?? attempt.CheckoutStatus,
                CancelReason = cancelReason ?? attempt.CancelReason,
                UpdatedAt = updatedAt
            });
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
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.PaymentVerified,
                PaymentId = paymentId,
                PaymentStatus = paymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                CompletedAt = completedAt,
                UpdatedAt = completedAt
            });
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
            Update(attemptGuid, attempt => attempt with
            {
                Status = status,
                CheckoutStatus = checkoutStatus ?? attempt.CheckoutStatus,
                PaymentStatus = paymentStatus ?? attempt.PaymentStatus,
                ResponseCode = responseCode,
                ResponseText = responseText,
                ResolvedAt = resolvedAt,
                UpdatedAt = resolvedAt
            });
            return Task.CompletedTask;
        }

        public Task MarkOrderCompletedAsync(Guid attemptGuid, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            Update(attemptGuid, attempt => attempt with
            {
                Status = LocalSquarePaymentAttemptStatus.OrderCompleted,
                OrderCompletedAt = completedAt,
                UpdatedAt = completedAt
            });
            return Task.CompletedTask;
        }

        public Task<LocalSquarePaymentAttempt?> GetLatestOpenAttemptAsync(
            string storeCode,
            string deviceCode,
            string cashierId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(Attempts.LastOrDefault());
        }

        public Task<LocalSquarePaymentAttempt?> GetAttemptAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalSquarePaymentAttempt?>(Attempts.SingleOrDefault(attempt => attempt.AttemptGuid == attemptGuid));
        }

        private void Update(Guid attemptGuid, Func<LocalSquarePaymentAttempt, LocalSquarePaymentAttempt> update)
        {
            var index = Attempts.FindIndex(attempt => attempt.AttemptGuid == attemptGuid);
            Assert.True(index >= 0);
            Attempts[index] = update(Attempts[index]);
        }
    }

    private sealed class ObservingCardTerminalClient(
        Action beforeResult,
        PaymentAuthorizationResult? result = null) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            beforeResult();
            return Task.FromResult(result ?? new PaymentAuthorizationResult(
                true,
                "ANZBACKEND:TXN-1:session=backend-session-1:environment=Sandbox",
                "APPROVED",
                amount,
                [
                    new CardTransactionDto(
                        "ANZ",
                        "TXN-1",
                        null,
                        null,
                        null,
                        null,
                        null,
                        "00",
                        "APPROVED",
                        null,
                        DateTimeOffset.UtcNow,
                        amount,
                        null)
                ],
                "ANZ",
                "Sandbox",
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                "P",
                "backend-session-1",
                "TXN-1",
                "00",
                "APPROVED"));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ApprovedCardTerminalClient(string reference) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, reference));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", AuthorizedAmount: amount));
        }
    }

    private sealed class ApprovedVoucherTenderClient(
        string reference,
        decimal? authorizedAmount = null,
        bool approveRefund = true) : IVoucherTenderClient
    {
        public int IssueRefundCallCount { get; private set; }

        public string? LastOrderReference { get; private set; }

        public string? LastIdempotencyKey { get; private set; }

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("ABC123", voucherCode);
            return Task.FromResult(new PaymentAuthorizationResult(true, reference, AuthorizedAmount: authorizedAmount));
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            IssueRefundCallCount++;
            LastOrderReference = orderReference;
            LastIdempotencyKey = idempotencyKey;
            return Task.FromResult(approveRefund
                ? new PaymentAuthorizationResult(true, reference, AuthorizedAmount: authorizedAmount ?? amount)
                : new PaymentAuthorizationResult(false, null, "issue failed"));
        }
    }

    private sealed class RetriableVoucherTenderClient(string reference) : IVoucherTenderClient
    {
        public bool FailIssueRefund { get; set; } = true;

        public int IssueRefundCallCount { get; private set; }

        public List<string> IssueRefundIdempotencyKeys { get; } = [];

        public Task<PaymentAuthorizationResult> RedeemAsync(
            decimal amount,
            PosSessionState session,
            string? voucherCode,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PaymentAuthorizationResult> IssueRefundAsync(
            decimal amount,
            PosSessionState session,
            string orderReference,
            string idempotencyKey,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            IssueRefundCallCount++;
            IssueRefundIdempotencyKeys.Add(idempotencyKey);
            return Task.FromResult(FailIssueRefund
                ? new PaymentAuthorizationResult(false, null, "issue failed")
                : new PaymentAuthorizationResult(true, reference, AuthorizedAmount: amount));
        }
    }

    private sealed class FailingOnceOrderUploadService : IOrderUploadService
    {
        private bool _hasFailed;

        public List<Guid> AttemptedOrderGuids { get; } = [];

        public Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            AttemptedOrderGuids.Add(orderGuid);
            if (!_hasFailed)
            {
                _hasFailed = true;
                throw new InvalidOperationException("network unavailable");
            }

            return Task.CompletedTask;
        }
    }
}
