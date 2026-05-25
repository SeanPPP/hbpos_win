using System.Collections.Concurrent;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class PosTerminalWorkflowServiceTests
{
    [Fact]
    public void Process_scan_auto_adds_single_exact_match()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-201", "Workflow Water", "930201", PriceSourceKind.StoreRetailPrice, 2.5m);
        index.ReplaceAll([item]);
        var service = new PosTerminalWorkflowService(index, cart);

        var result = service.ProcessScan(Session, "930201", preferExactLookup: false, source: "manual");

        Assert.Equal("pos.status.added", result.StatusKey);
        Assert.True(result.ClearScanText);
        Assert.False(result.MatchesPopupOpen);
        Assert.Same(item, result.SelectedItem);
        var line = Assert.Single(cart.Lines);
        Assert.Same(line, result.SelectedCartLine);
        Assert.Equal("Workflow Water", line.DisplayName);
    }

    [Fact]
    public void Process_scan_keeps_duplicate_exact_matches_for_selection()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var first = CreateItem("SKU-211", "Workflow Apple Small", "930211", PriceSourceKind.StoreRetailPrice, 1.5m);
        var second = CreateItem("SKU-212", "Workflow Apple Large", "930211", PriceSourceKind.StoreRetailPrice, 2.5m);
        index.ReplaceAll([first, second]);
        var service = new PosTerminalWorkflowService(index, cart);

        var result = service.ProcessScan(Session, "930211", preferExactLookup: true, source: "raw");

        Assert.Equal("pos.status.multipleMatches", result.StatusKey);
        Assert.False(result.ClearScanText);
        Assert.True(result.MatchesPopupOpen);
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<SellableItemDto>>(result.Matches).Count);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public void Modify_selected_line_quantity_rejects_decimal_values()
    {
        var cart = new PosCartService();
        var item = CreateItem("SKU-221", "Workflow Tea", "930221", PriceSourceKind.StoreRetailPrice, 4m);
        var line = cart.AddItem(item);
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);

        var result = service.ModifySelectedLineQuantity(line, "1.5");

        Assert.Equal("cart.status.quantityMustBeInteger", result.StatusKey);
        Assert.False(result.ClearKeypadBuffer);
        Assert.Equal(1m, line.Quantity);
    }

    [Fact]
    public void Guard_payment_blocks_zero_price_and_non_integer_quantity()
    {
        var zeroPriceCart = new PosCartService();
        zeroPriceCart.AddItem(CreateItem("SKU-231", "Zero Tea", "930231", PriceSourceKind.StoreRetailPrice, 0m));
        var zeroPriceService = new PosTerminalWorkflowService(new LocalSellableItemIndex(), zeroPriceCart);

        var zeroPriceResult = zeroPriceService.GuardPayment();

        Assert.False(zeroPriceResult.PaymentAllowed);
        Assert.Equal("cart.status.zeroPriceItem", zeroPriceResult.StatusKey);

        var invalidQuantityCart = new PosCartService();
        var invalidQuantityLine = invalidQuantityCart.AddItem(CreateItem("SKU-232", "Fraction Tea", "930232", PriceSourceKind.StoreRetailPrice, 5m));
        SetUnsafeQuantity(invalidQuantityLine, 1.5m);
        var invalidQuantityService = new PosTerminalWorkflowService(new LocalSellableItemIndex(), invalidQuantityCart);

        var invalidQuantityResult = invalidQuantityService.GuardPayment();

        Assert.False(invalidQuantityResult.PaymentAllowed);
        Assert.Equal("cart.status.quantityMustBeInteger", invalidQuantityResult.StatusKey);
    }

    [Fact]
    public async Task Add_selected_item_remote_delete_keeps_local_cart_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-241", "Retired Workflow Snack", "930241", PriceSourceKind.StoreRetailPrice, 4.2m);
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogReloaded = new TaskCompletionSource<IReadOnlyList<SellableItemDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.ReplaceAll([item]);
        var service = new PosTerminalWorkflowService(
            index,
            cart,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task,
            reloadCatalogAsync: _ =>
            {
                index.ReplaceAll([]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
            });
        service.CatalogReloaded += (_, args) => catalogReloaded.TrySetResult(args.CatalogItems);

        service.AddSelectedItem(Session, item, clearScanText: true, closeMatchesPopup: false, operation: "manual-add-selected");
        Assert.Single(cart.Lines);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930241", Found: false, Item: null, DeletedCount: 1));
        var catalogItems = await catalogReloaded.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(catalogItems);
        var line = Assert.Single(cart.Lines);
        Assert.Equal("Retired Workflow Snack", line.DisplayName);
    }

    [Fact]
    public async Task Add_selected_item_remote_timeout_keeps_cart_unchanged()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-251", "Timeout Workflow Tea", "930251", PriceSourceKind.StoreRetailPrice, 5.5m);
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([item]);
        var service = new PosTerminalWorkflowService(
            index,
            cart,
            remoteLookupRefreshAsync: async (_, _, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return new RemoteLookupRefreshResult("S001", "930251", Found: false, Item: null, DeletedCount: 1);
            });

        using var logCapture = CaptureClientLog(logs);

        service.AddSelectedItem(Session, item, clearScanText: true, closeMatchesPopup: false, operation: "manual-add-selected");

        await WaitUntilAsync(() => HasLog(logs, "remote lookup timeout"));

        var line = Assert.Single(cart.Lines);
        Assert.Equal("Timeout Workflow Tea", line.DisplayName);
        Assert.Equal(5.5m, line.UnitPrice);
    }

    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string barcode,
        PriceSourceKind priceSource,
        decimal price,
        string? referenceCode = null,
        string? itemNumber = null,
        string? productBarcode = null,
        string? productImage = null)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: referenceCode,
            DisplayName: name,
            LookupCode: barcode,
            ItemNumber: itemNumber ?? productCode,
            Barcode: productBarcode ?? barcode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage);
    }

    private static void SetUnsafeQuantity(CartLine line, decimal quantity)
    {
        var field = typeof(CartLine).GetField("_quantity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(line, quantity);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static IDisposable CaptureClientLog(ConcurrentQueue<string> lines)
    {
        void Capture(string line)
        {
            lines.Enqueue(line);
        }

        ConsoleLog.LineWritten += Capture;
        return new DisposableAction(() => ConsoleLog.LineWritten -= Capture);
    }

    private static bool HasLog(ConcurrentQueue<string> lines, string text)
    {
        return lines.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }
}
