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

    [Fact]
    public void OpenNoReceiptOpenItemDialogCommand_opens_only_in_no_receipt_mode()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        Assert.False(viewModel.OpenNoReceiptOpenItemDialogCommand.CanExecute(null));

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);

        Assert.True(viewModel.IsOpenItemDialogOpen);
        Assert.Equal("Open Item", viewModel.OpenItemDisplayName);
        Assert.Empty(viewModel.OpenItemUnitPriceText);
        Assert.Equal(OpenItemKeyboardTarget.Description, viewModel.OpenItemKeyboardTarget);
        Assert.True(viewModel.IsOpenItemDescriptionKeyboardVisible);
        Assert.False(viewModel.IsOpenItemAmountKeyboardVisible);
    }

    [Fact]
    public void OpenItemKeyboardInputCommand_updates_description_with_letters_space_and_edit_keys()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemKeyboardInputCommand.Execute("Clear");
        viewModel.OpenItemKeyboardInputCommand.Execute("M");
        viewModel.OpenItemKeyboardInputCommand.Execute("I");
        viewModel.OpenItemKeyboardInputCommand.Execute("L");
        viewModel.OpenItemKeyboardInputCommand.Execute("K");
        viewModel.OpenItemKeyboardInputCommand.Execute("Space");
        viewModel.OpenItemKeyboardInputCommand.Execute("A");
        viewModel.OpenItemKeyboardInputCommand.Execute("Back");

        Assert.Equal("MILK ", viewModel.OpenItemDisplayName);
    }

    [Fact]
    public void OpenItemKeyboardInputCommand_updates_amount_with_two_decimal_limit()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.SelectOpenItemAmountKeyboardCommand.Execute(null);
        viewModel.OpenItemKeyboardInputCommand.Execute("1");
        viewModel.OpenItemKeyboardInputCommand.Execute("2");
        viewModel.OpenItemKeyboardInputCommand.Execute(".");
        viewModel.OpenItemKeyboardInputCommand.Execute("3");
        viewModel.OpenItemKeyboardInputCommand.Execute("4");
        viewModel.OpenItemKeyboardInputCommand.Execute("5");

        Assert.Equal("12.34", viewModel.OpenItemUnitPriceText);
        Assert.True(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));

        viewModel.OpenItemKeyboardInputCommand.Execute("Back");
        Assert.Equal("12.3", viewModel.OpenItemUnitPriceText);

        viewModel.OpenItemKeyboardInputCommand.Execute("Clear");
        Assert.Empty(viewModel.OpenItemUnitPriceText);
        Assert.False(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));
    }

    [Fact]
    public void ConfirmNoReceiptOpenItemCommand_requires_positive_price()
    {
        var viewModel = new ReceiptReturnsViewModel(
            new FakeReceiptReturnsWorkflowService(),
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemDisplayName = "Manual Refund";
        viewModel.OpenItemUnitPriceText = "0";

        Assert.False(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));

        viewModel.OpenItemUnitPriceText = "12.34";

        Assert.True(viewModel.ConfirmNoReceiptOpenItemCommand.CanExecute(null));
    }

    [Fact]
    public void ConfirmNoReceiptOpenItemCommand_adds_pending_line_and_closes_dialog()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            OpenItemResult = new ReceiptReturnPendingLineResult(
                CreatePendingOpenItem("Manual Refund", 12.34m),
                "Added no-barcode return item: Manual Refund")
        };
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemDisplayName = "Manual Refund";
        viewModel.OpenItemUnitPriceText = "12.34";

        viewModel.ConfirmNoReceiptOpenItemCommand.Execute(null);

        var line = Assert.Single(viewModel.PendingLines);
        Assert.Equal("Manual Refund", line.DisplayName);
        Assert.Equal(12.34m, line.UnitPrice);
        Assert.Equal(-12.34m, viewModel.PendingTotal);
        Assert.False(viewModel.IsOpenItemDialogOpen);
        Assert.Equal("Added no-barcode return item: Manual Refund", viewModel.StatusMessage);
    }

    [Fact]
    public void ConfirmNoReceiptOpenItemCommand_keeps_dialog_open_when_workflow_rejects()
    {
        var workflow = new FakeReceiptReturnsWorkflowService
        {
            OpenItemResult = new ReceiptReturnPendingLineResult(null, "OPENITEM was not found in the local catalog.")
        };
        var viewModel = new ReceiptReturnsViewModel(
            workflow,
            CreateSession(),
            () => { });

        viewModel.IsNoReceiptMode = true;
        viewModel.OpenNoReceiptOpenItemDialogCommand.Execute(null);
        viewModel.OpenItemDisplayName = "Manual Refund";
        viewModel.OpenItemUnitPriceText = "12.34";

        viewModel.ConfirmNoReceiptOpenItemCommand.Execute(null);

        Assert.Empty(viewModel.PendingLines);
        Assert.True(viewModel.IsOpenItemDialogOpen);
        Assert.Equal("OPENITEM was not found in the local catalog.", viewModel.StatusMessage);
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

    private static PendingReturnLine CreatePendingOpenItem(string displayName, decimal unitPrice)
    {
        return new PendingReturnLine(
            "S001",
            "OPEN-SKU",
            "REF-OPEN",
            displayName,
            "OPENITEM",
            "OPENITEM",
            null,
            1m,
            unitPrice,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            $"noreceipt-open:S001:{Guid.NewGuid():N}",
            null,
            null);
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

        public ReceiptReturnPendingLineResult OpenItemResult { get; init; } = new(null, "");

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

        public ReceiptReturnPendingLineResult CreateNoReceiptOpenItem(
            PosSessionState session,
            string displayName,
            decimal unitPrice)
        {
            return OpenItemResult;
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
