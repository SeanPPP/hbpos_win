using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptReturnsViewModelTests
{
    [Fact]
    public async Task BackCommand_resets_pending_return_state_before_leaving()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            LookupResult = CreateLookupResult()
        };
        var backCalled = false;
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => backCalled = true);

        viewModel.ScanText = "ORDER-001";
        await viewModel.LookupCommand.ExecuteAsync(null);
        viewModel.AddReceiptLineCommand.Execute(viewModel.OrderLines.Single());
        viewModel.IsNoReceiptMode = true;

        viewModel.BackCommand.Execute(null);

        Assert.True(backCalled);
        AssertDefaultState(viewModel);
    }

    [Fact]
    public async Task ConfirmToCart_adds_confirmed_lines_then_resets_pending_return_state()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            LookupResult = CreateLookupResult()
        };
        var backCalled = false;
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => backCalled = true);

        viewModel.ScanText = "ORDER-001";
        await viewModel.LookupCommand.ExecuteAsync(null);
        viewModel.AddReceiptLineCommand.Execute(viewModel.OrderLines.Single());

        viewModel.ConfirmToCartCommand.Execute(null);

        Assert.True(backCalled);
        Assert.Single(workflow.AddedLines);
        AssertDefaultState(viewModel);
    }

    private static void AssertDefaultState(ReceiptReturnsViewModel viewModel)
    {
        Assert.Empty(viewModel.ScanText);
        Assert.False(viewModel.IsNoReceiptMode);
        Assert.Empty(viewModel.OrderLines);
        Assert.Empty(viewModel.PendingLines);
        Assert.False(viewModel.ReturnRecordsMayBeStale);
        Assert.Equal("No order loaded", viewModel.OrderSummaryText);
        Assert.Equal("Scan an order number to start a receipt return.", viewModel.StatusMessage);
    }

    private static ReceiptReturnLookupResult CreateLookupResult()
    {
        var orderGuid = Guid.NewGuid();
        var lineGuid = Guid.NewGuid();
        return new ReceiptReturnLookupResult(
            new ReceiptReturnOrder(
                orderGuid,
                "S001",
                "POS-01",
                "Alice",
                DateTimeOffset.UtcNow,
                10m,
                [
                    new ReceiptReturnOrderLine(
                        lineGuid,
                        "SKU-001",
                        "REF-001",
                        "Milk",
                        "690001",
                        "ITEM-001",
                        1m,
                        10m,
                        10m,
                        0m)
                ],
                [],
                []),
            false,
            true,
            "Loaded local order; return records may be stale.");
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C01", "Alice", true, 0);
    }

    private sealed class FakeReceiptReturnsWorkflowService : IReceiptReturnsWorkflowService
    {
        public ReceiptReturnLookupResult LookupResult { get; init; } = new(null, false, false, "");

        public List<PendingReturnLine> AddedLines { get; } = [];

        public Task<ReceiptReturnLookupResult> LookupOrderAsync(
            PosSessionState session,
            string orderQuery,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LookupResult);
        }

        public ReceiptReturnProductLookupResult LookupNoReceiptProduct(
            PosSessionState session,
            string productQuery)
        {
            return new ReceiptReturnProductLookupResult(null, "");
        }

        public IReadOnlyList<CartLine> AddReturnLinesToCart(
            IEnumerable<PendingReturnLine> lines,
            IReadOnlyList<OrderReturnPaymentCapacityDto>? paymentCapacities = null)
        {
            AddedLines.AddRange(lines);
            return [];
        }
    }
}
