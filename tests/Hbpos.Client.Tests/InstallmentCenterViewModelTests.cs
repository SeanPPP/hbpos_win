using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class InstallmentCenterViewModelTests
{
    [Fact]
    public async Task SearchAsync_filters_orders_by_keyword()
    {
        var service = new FakeInstallmentOrderService
        {
            Orders =
            [
                CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true, canCancelWithRefund: true, canVoidCancel: true),
                CreateOrder("IO-002", "李四", "0400222333", "待提货", canConfirmPickup: true)
            ]
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        viewModel.SearchText = "李四";
        await viewModel.SearchAsync();

        Assert.Single(viewModel.Orders);
        Assert.Equal("IO-002", viewModel.Orders[0].OrderNumber);
        Assert.Equal("李四", viewModel.SelectedOrder!.CustomerName);

        viewModel.SearchText = "0400111222";
        await viewModel.SearchAsync();

        Assert.Single(viewModel.Orders);
        Assert.Equal("IO-001", viewModel.Orders[0].OrderNumber);
    }

    [Fact]
    public async Task AddRepaymentCommand_requires_voucher_inputs_and_invokes_service()
    {
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true, canCancelWithRefund: true, canVoidCancel: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            AddRepaymentResult = new InstallmentOrderActionResult(true, "补款完成")
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;

        Assert.Contains(viewModel.PaymentMethodOptions, option => option.Method == PaymentMethodKind.Cash);
        Assert.Contains(viewModel.PaymentMethodOptions, option => option.Method == PaymentMethodKind.Card);
        Assert.Contains(viewModel.PaymentMethodOptions, option => option.Method == PaymentMethodKind.Voucher);

        viewModel.RepaymentMethod = PaymentMethodKind.Card;
        viewModel.RepaymentReference = string.Empty;

        Assert.False(viewModel.AddRepaymentCommand.CanExecute(null));

        viewModel.RepaymentMethod = PaymentMethodKind.Voucher;
        viewModel.RepaymentReference = "VIP001";

        Assert.False(viewModel.AddRepaymentCommand.CanExecute(null));

        viewModel.RepaymentVoucherToken = "LOCK-001";

        Assert.True(viewModel.AddRepaymentCommand.CanExecute(null));

        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRepaymentRequest);
        Assert.Equal(targetOrder.OrderId, service.LastRepaymentRequest!.InstallmentGuid);
        Assert.Equal(40m, service.LastRepaymentRequest.Payment.Amount);
        Assert.Equal(PaymentMethodKind.Voucher, service.LastRepaymentRequest.Payment.Method);
        Assert.Equal("VIP001", service.LastRepaymentRequest.Payment.Reference);
        Assert.Equal("LOCK-001", service.LastRepaymentRequest.Payment.ReservationToken);
        Assert.Equal("补款完成", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AddRepaymentCommand_maps_service_exception_to_status_message()
    {
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            ThrowOnRepayment = true
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;
        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.Equal("API refused repayment", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AddRepaymentCommand_authorizes_card_repayment_before_service_call()
    {
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true);
        var cardTransactions = new[]
        {
            new CardTransactionDto("Square", "TXN-001", "AUTH-001", "VISA", 4, "1234", "MID-1", "00", "APPROVED", "RRN-1", DateTimeOffset.Now, 40m, "receipt")
        };
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            AddRepaymentResult = new InstallmentOrderActionResult(true, "补款完成")
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { },
            cardTerminalClient: new ApprovedCardTerminalClient("CARD-001", cardTransactions));

        await viewModel.LoadAsync();
        viewModel.RepaymentAmount = 40m;
        viewModel.RepaymentMethod = PaymentMethodKind.Card;

        Assert.True(viewModel.AddRepaymentCommand.CanExecute(null));

        await viewModel.AddRepaymentCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastRepaymentRequest);
        Assert.Equal(PaymentMethodKind.Card, service.LastRepaymentRequest!.Payment.Method);
        Assert.Equal("CARD-001", service.LastRepaymentRequest.Payment.Reference);
        Assert.Same(cardTransactions, service.LastRepaymentRequest.Payment.CardTransactions);
    }

    [Fact]
    public async Task Cancel_refund_and_void_commands_follow_button_state_and_invoke_service()
    {
        var targetOrder = CreateOrder("IO-001", "张三", "0400111222", "待补款", canAddRepayment: true, canCancelWithRefund: true, canVoidCancel: true);
        var service = new FakeInstallmentOrderService
        {
            Orders = [targetOrder],
            CancelWithRefundResult = new InstallmentOrderActionResult(true, "已取消退款"),
            VoidCancelResult = new InstallmentOrderActionResult(true, "已作废")
        };
        var viewModel = new InstallmentCenterViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        await viewModel.LoadAsync();

        Assert.True(viewModel.CancelWithRefundCommand.CanExecute(null));
        Assert.True(viewModel.VoidCancelCommand.CanExecute(null));

        viewModel.VoidReason = "客户改主意";
        await viewModel.CancelWithRefundCommand.ExecuteAsync(null);
        await viewModel.VoidCancelCommand.ExecuteAsync(null);

        Assert.Equal(targetOrder.OrderId, service.LastCancelOrderId);
        Assert.Equal(targetOrder.OrderId, service.LastVoidOrderId);
        Assert.Equal("客户改主意", service.LastVoidReason);
    }

    private static InstallmentOrderSummary CreateOrder(
        string orderNumber,
        string customerName,
        string phone,
        string status,
        bool canAddRepayment = false,
        bool canConfirmPickup = false,
        bool canCancelWithRefund = false,
        bool canVoidCancel = false)
    {
        return new InstallmentOrderSummary(
            Guid.NewGuid(),
            orderNumber,
            customerName,
            phone,
            120m,
            30m,
            canConfirmPickup ? 120m : 30m,
            canConfirmPickup ? 0m : 90m,
            0,
            canAddRepayment,
            canConfirmPickup,
            canCancelWithRefund,
            canVoidCancel,
            status,
            "POS-01",
            DateTimeOffset.Now);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private sealed class FakeInstallmentOrderService : IInstallmentOrderService
    {
        public IReadOnlyList<InstallmentOrderSummary> Orders { get; init; } = [];

        public InstallmentOrderActionResult AddRepaymentResult { get; init; } = new(false, "未配置");

        public InstallmentOrderActionResult CancelWithRefundResult { get; init; } = new(false, "未配置");

        public InstallmentOrderActionResult VoidCancelResult { get; init; } = new(false, "未配置");

        public bool ThrowOnRepayment { get; init; }

        public InstallmentOrderRepaymentRequest? LastRepaymentRequest { get; private set; }

        public Guid LastCancelOrderId { get; private set; }

        public Guid LastVoidOrderId { get; private set; }

        public string? LastVoidReason { get; private set; }

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Orders);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
        {
            var filtered = string.IsNullOrWhiteSpace(keyword)
                ? Orders
                : Orders.Where(order =>
                    order.OrderNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.CustomerPhone.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.Status.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    order.DeviceCode.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>(filtered.ToList());
        }

        public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalInstallmentOrder?>(null);
        }

        public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRepayment)
            {
                throw new InvalidOperationException("API refused repayment");
            }

            LastRepaymentRequest = request;
            return Task.FromResult(AddRepaymentResult);
        }

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            LastCancelOrderId = orderId;
            return Task.FromResult(CancelWithRefundResult);
        }

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
        {
            LastVoidOrderId = orderId;
            LastVoidReason = reason;
            return Task.FromResult(VoidCancelResult);
        }

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new InstallmentOrderActionResult(true, "已确认提货"));
        }
    }

    private sealed class ApprovedCardTerminalClient(
        string reference,
        IReadOnlyList<CardTransactionDto>? cardTransactions = null) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, reference, AuthorizedAmount: amount, CardTransactions: cardTransactions));
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
}
