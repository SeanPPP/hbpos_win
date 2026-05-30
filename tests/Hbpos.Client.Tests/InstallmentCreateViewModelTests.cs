using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class InstallmentCreateViewModelTests
{
    [Fact]
    public void Prepare_sets_cart_details_and_default_down_payment()
    {
        var service = new FakeInstallmentOrderService();
        var viewModel = new InstallmentCreateViewModel(
            service,
            CreateSession(),
            _ => Task.CompletedTask,
            () => { });

        viewModel.Prepare(CreateSession(), CreateCartSnapshot());

        Assert.Equal("创建分期", viewModel.PageTitleText);
        Assert.Single(viewModel.CartLines);
        Assert.Equal(130m, viewModel.GoodsAmount);
        Assert.Equal(10m, viewModel.DiscountAmount);
        Assert.Equal(120m, viewModel.TotalAmount);
        Assert.Equal(20m, viewModel.DownPaymentAmount);
        Assert.Equal(100m, viewModel.FinancedAmount);
        Assert.Equal("请完善客户、首付和分期信息。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SubmitCommand_requires_voucher_reference_and_token_then_submits_request()
    {
        var service = new FakeInstallmentOrderService();
        InstallmentOrderSummary? createdOrder = null;
        var viewModel = new InstallmentCreateViewModel(
            service,
            CreateSession(),
            order =>
            {
                createdOrder = order;
                return Task.CompletedTask;
            },
            () => { });

        viewModel.Prepare(CreateSession(), CreateCartSnapshot());
        viewModel.CustomerName = "张三";
        viewModel.CustomerPhone = "0400111222";
        viewModel.DownPaymentAmount = 30m;
        viewModel.DownPaymentMethod = PaymentMethodKind.Voucher;
        viewModel.DownPaymentReference = "VIP001";

        Assert.False(viewModel.SubmitCommand.CanExecute(null));

        viewModel.VoucherReservationToken = "LOCK-001";

        Assert.True(viewModel.SubmitCommand.CanExecute(null));

        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastCreateRequest);
        Assert.Equal(PaymentMethodKind.Voucher, service.LastCreateRequest!.DownPayment.Method);
        Assert.Equal("VIP001", service.LastCreateRequest.DownPayment.Reference);
        Assert.Equal("LOCK-001", service.LastCreateRequest.DownPayment.ReservationToken);
        Assert.NotNull(createdOrder);
        Assert.Equal("已创建分期单。", viewModel.StatusMessage);
    }

    private static PosSessionState CreateSession()
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
                new PosCartLineServiceSnapshot("SKU-001", null, "Premium Rice Cooker", "690001", "ITEM-001", 1m, 130m, 10m, 120m)
            ]);
    }

    private sealed class FakeInstallmentOrderService : IInstallmentOrderService
    {
        public InstallmentOrderCreateRequest? LastCreateRequest { get; private set; }

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<InstallmentOrderSummary>>([]);
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
            LastCreateRequest = request;
            return Task.FromResult(new InstallmentOrderCreateResult(
                true,
                "已创建分期单。",
                new InstallmentOrderSummary(
                    Guid.NewGuid(),
                    "IO-001",
                    "张三",
                    "0400111222",
                    120m,
                    30m,
                    30m,
                    90m,
                    0,
                    true,
                    false,
                    true,
                    true,
                    "待补款",
                    "POS-01",
                    DateTimeOffset.Now)));
        }

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
