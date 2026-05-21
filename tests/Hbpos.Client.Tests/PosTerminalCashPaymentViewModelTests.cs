using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class PosTerminalCashPaymentViewModelTests
{
    [Fact]
    public void Pos_terminal_scans_exact_barcode_into_cart()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("SKU-101", "Sparkling Water", "930101", PriceSourceKind.StoreRetailPrice, 2.5m)]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null);

        viewModel.ScanText = "930101";
        viewModel.ScanCommand.Execute(null);

        Assert.Empty(viewModel.ScanText);
        Assert.Equal(2.5m, viewModel.ActualAmount);
        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Sparkling Water", line.DisplayName);
        Assert.Equal("StoreRetailPrice", line.PriceSourceLabel);
    }

    [Fact]
    public async Task Pos_terminal_keeps_local_add_when_remote_lookup_fails()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-104", "Local Coffee", "930104", PriceSourceKind.StoreRetailPrice, 6.5m);
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task);

        viewModel.ScanText = "930104";
        viewModel.ScanCommand.Execute(null);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Coffee", line.DisplayName);
        Assert.Equal(6.5m, viewModel.ActualAmount);

        remoteLookup.SetException(new InvalidOperationException("remote unavailable"));
        await WaitUntilAsync(() => viewModel.StatusMessage.Contains("Remote lookup failed", StringComparison.Ordinal));

        line = Assert.Single(viewModel.CartLines);
        Assert.Equal("Local Coffee", line.DisplayName);
        Assert.Equal(6.5m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Pos_terminal_remote_deleted_removes_matching_cart_line()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var item = CreateItem("SKU-105", "Retired Snack", "930105", PriceSourceKind.StoreRetailPrice, 4.2m);
        var remoteLookup = new TaskCompletionSource<RemoteLookupRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.ReplaceAll([item]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            remoteLookupRefreshAsync: (_, _, _) => remoteLookup.Task,
            reloadCatalogAsync: _ =>
            {
                index.ReplaceAll([]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
            });

        viewModel.ScanText = "930105";
        viewModel.ScanCommand.Execute(null);

        Assert.Single(viewModel.CartLines);

        remoteLookup.SetResult(new RemoteLookupRefreshResult("S001", "930105", Found: false, Item: null, DeletedCount: 1));
        await WaitUntilAsync(() => viewModel.CartLines.Count == 0);

        Assert.Empty(cart.Lines);
        Assert.Empty(viewModel.Matches);
    }

    [Fact]
    public async Task Pos_terminal_sync_command_refreshes_matches_and_index()
    {
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var oldItem = CreateItem("SKU-106", "Old Tea", "930106", PriceSourceKind.ProductBase, 3m);
        var syncedItem = CreateItem("SKU-107", "Synced Tea", "930107", PriceSourceKind.StoreRetailPrice, 3.5m);
        index.ReplaceAll([oldItem]);
        var viewModel = new PosTerminalViewModel(
            index,
            cart,
            Session,
            onOpenPayment: null,
            syncCatalogAsync: _ =>
            {
                index.ReplaceAll([syncedItem]);
                return Task.FromResult<IReadOnlyList<SellableItemDto>>([syncedItem]);
            });
        viewModel.LoadMatches([oldItem]);

        await viewModel.SyncCommand.ExecuteAsync(null);

        var indexedItem = Assert.Single(index.Search("930107"));
        Assert.Equal("Synced Tea", indexedItem.DisplayName);
        var match = Assert.Single(viewModel.Matches);
        Assert.Equal("Synced Tea", match.DisplayName);
        Assert.Contains("completed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cash_payment_recalculates_change_from_amount_tendered()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-102", "Orange Juice", "930102", PriceSourceKind.ProductBase, 7.8m));
        var viewModel = new CashPaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);

        viewModel.AmountTenderedText = "10";

        Assert.Equal(2.2m, viewModel.ChangeDue);
        Assert.True(viewModel.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public void Cash_payment_refresh_cart_recalculates_change_after_cart_update()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-108", "Rice", "930108", PriceSourceKind.ProductBase, 7m));
        var viewModel = new CashPaymentViewModel(
            cart,
            new CashCheckoutService(),
            new InMemoryOrderRepository(),
            new InMemorySyncQueueRepository(),
            Session);
        viewModel.AmountTenderedText = "10";

        cart.UpdateLineFromRemote(CreateItem("SKU-108", "Rice", "930108", PriceSourceKind.StoreRetailPrice, 8m));
        viewModel.RefreshCart();

        Assert.Equal(2m, viewModel.ChangeDue);
        Assert.Equal(8m, viewModel.ActualAmount);
    }

    [Fact]
    public async Task Cash_payment_confirmation_saves_order_snapshot_and_refreshes_pending_sync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-cash-vm-{Guid.NewGuid():N}.db");

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var orders = new LocalOrderRepository(store);
            var syncQueue = new SyncQueueRepository(store);
            var cart = new PosCartService();
            cart.AddItem(CreateItem("SKU-103", "Whole Milk", "930103", PriceSourceKind.StoreClearancePrice, 4.4m));
            var viewModel = new CashPaymentViewModel(cart, new CashCheckoutService(), orders, syncQueue, Session);
            PaymentCompletedEventArgs? completed = null;
            viewModel.PaymentCompleted += (_, args) => completed = args;

            await schema.InitializeAsync();
            viewModel.AmountTenderedText = "5";
            await viewModel.ConfirmPaymentCommand.ExecuteAsync(null);

            Assert.NotNull(completed);
            Assert.Equal(0.6m, completed.ChangeAmount);
            Assert.Equal(4.4m, completed.Order.ActualAmount);
            Assert.Empty(cart.Lines);
            Assert.Equal(1, viewModel.PendingSyncCount);
            Assert.Equal(1, await syncQueue.CountPendingAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private static SellableItemDto CreateItem(
        string productCode,
        string name,
        string barcode,
        PriceSourceKind priceSource,
        decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: name,
            LookupCode: barcode,
            ItemNumber: productCode,
            Barcode: barcode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
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

    private sealed class InMemoryOrderRepository : ILocalOrderRepository
    {
        public LocalOrder? LastOrder { get; private set; }

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            LastOrder = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(LastOrder?.OrderGuid == orderGuid ? LastOrder : null);
        }
    }

    private sealed class InMemorySyncQueueRepository : ISyncQueueRepository
    {
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }

        public Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SyncQueueOverview(1, 0, 0, null));
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SyncQueueListItem>>([]);
        }
    }
}
