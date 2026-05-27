using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class TransactionHistoryViewModelTests
{
    [Fact]
    public void Constructor_initializes_readonly_store_and_terminal_dropdown()
    {
        var session = CreateSession(deviceCode: "POS-09");
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            session);

        Assert.Equal("Main Store (S001)", viewModel.StoreFilterText);
        Assert.Collection(
            viewModel.TerminalOptions,
            option =>
            {
                Assert.Null(option.DeviceCode);
                Assert.Equal("All Terminals", option.Label);
            },
            option =>
            {
                Assert.Equal("POS-09", option.DeviceCode);
                Assert.Equal("POS-09", option.Label);
            });
        Assert.Equal("POS-09", viewModel.SelectedTerminalOption?.DeviceCode);
        Assert.Equal("POS-09", viewModel.TerminalFilterText);
        Assert.True(viewModel.IsLocalSourceSelected);
        Assert.False(viewModel.IsOnlineSourceSelected);
    }

    [Fact]
    public void Session_change_preserves_all_terminal_selection_and_refreshes_current_terminal_option()
    {
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(deviceCode: "POS-01"));
        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode is null);

        viewModel.Session = CreateSession(deviceCode: "POS-02", storeCode: "S002", storeName: "Second Store");

        Assert.Equal("Second Store (S002)", viewModel.StoreFilterText);
        Assert.Null(viewModel.SelectedTerminalOption?.DeviceCode);
        Assert.Equal("All Terminals", viewModel.TerminalFilterText);
        Assert.Contains(viewModel.TerminalOptions, option => option.DeviceCode == "POS-02");
    }

    [Fact]
    public async Task LoadAsync_passes_selected_terminal_filter_to_all_history_sources()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var suspendedOrders = new CapturingSuspendedOrderService();
        var remoteOrders = new CapturingRemoteOrderHistoryService();
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            suspendedOrders,
            remoteOrders,
            CreateSession(deviceCode: "POS-04"));

        await viewModel.LoadAsync();
        Assert.Equal("POS-04", receiptQuery.LastQuery?.DeviceCode);
        Assert.Equal("POS-04", suspendedOrders.LastDeviceCode);

        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode is null);
        await viewModel.LoadAsync();
        Assert.Null(receiptQuery.LastQuery?.DeviceCode);
        Assert.Null(suspendedOrders.LastDeviceCode);

        viewModel.IsOnlineSourceSelected = true;
        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode == "POS-04");
        await viewModel.LoadAsync();
        Assert.Equal("POS-04", remoteOrders.LastQuery?.DeviceCode);

        viewModel.SelectedTerminalOption = viewModel.TerminalOptions.Single(option => option.DeviceCode is null);
        await viewModel.LoadAsync();
        Assert.Null(remoteOrders.LastQuery?.DeviceCode);
    }

    [Fact]
    public async Task Local_history_merges_local_and_suspended_orders_and_sorts_descending()
    {
        var localOrderGuid = Guid.NewGuid();
        var suspendedOrderGuid = Guid.NewGuid();
        var receiptQuery = new CapturingReceiptQueryService
        {
            Orders =
            [
                new LocalOrderSummary(
                    localOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero),
                    16m,
                    1m,
                    15m,
                    "Synced",
                    2,
                    "Cash")
            ]
        };
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-02",
                    "Bob",
                    new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero),
                    12m,
                    0m,
                    12m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession())
        {
            DateFrom = new DateTime(2026, 5, 10),
            DateTo = new DateTime(2026, 5, 10)
        };

        await viewModel.LoadAsync();

        Assert.Collection(
            viewModel.Orders,
            order =>
            {
                Assert.Equal(suspendedOrderGuid, order.OrderGuid);
                Assert.True(order.IsSuspendedOrder);
                Assert.True(order.CanRecall);
                Assert.Equal("挂单", order.PaymentSummary);
                Assert.Equal("待取回", order.StatusLabel);
            },
            order =>
            {
                Assert.Equal(localOrderGuid, order.OrderGuid);
                Assert.False(order.IsSuspendedOrder);
                Assert.False(order.CanRecall);
                Assert.Equal("Cash", order.PaymentSummary);
            });
    }

    [Fact]
    public async Task LoadAsync_applies_date_range_to_local_and_suspended_orders()
    {
        var receiptQuery = new CapturingReceiptQueryService();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    Guid.NewGuid(),
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero),
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending),
                new SuspendedOrderSummary(
                    Guid.NewGuid(),
                    "S001",
                    "POS-01",
                    "Alice",
                    new DateTimeOffset(2026, 5, 7, 9, 0, 0, TimeSpan.Zero),
                    20m,
                    0m,
                    20m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            receiptQuery,
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession())
        {
            DateFrom = new DateTime(2026, 5, 5),
            DateTo = new DateTime(2026, 5, 6)
        };

        await viewModel.LoadAsync();

        Assert.Equal(new DateTime(2026, 5, 5), receiptQuery.LastQuery?.SoldFrom?.Date);
        Assert.Equal(new DateTime(2026, 5, 6), receiptQuery.LastQuery?.SoldTo?.Date);
        Assert.Empty(viewModel.Orders);
    }

    [Fact]
    public async Task Recall_order_command_uses_row_parameter_refreshes_list_and_invokes_callback()
    {
        var recalled = false;
        var suspendedOrderGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.Now,
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            () =>
            {
                recalled = true;
                return Task.CompletedTask;
            });

        await viewModel.LoadAsync();
        var suspendedOrder = Assert.Single(viewModel.Orders);

        Assert.True(viewModel.RecallOrderCommand.CanExecute(suspendedOrder));

        await viewModel.RecallOrderCommand.ExecuteAsync(suspendedOrder);

        Assert.True(recalled);
        Assert.Equal(suspendedOrderGuid, suspendedOrders.RecalledOrderGuid);
        Assert.Empty(viewModel.Orders);
    }

    [Fact]
    public async Task Selected_suspended_order_shows_recall_action_and_hides_reprint()
    {
        var suspendedOrderGuid = Guid.NewGuid();
        var suspendedOrders = new CapturingSuspendedOrderService
        {
            PendingOrders =
            [
                new SuspendedOrderSummary(
                    suspendedOrderGuid,
                    "S001",
                    "POS-01",
                    "Alice",
                    DateTimeOffset.Now,
                    10m,
                    0m,
                    10m,
                    1,
                    SuspendedOrderStatus.Pending)
            ]
        };
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            suspendedOrders,
            new CapturingRemoteOrderHistoryService(),
            CreateSession());

        await viewModel.ShowSuspendedOrdersAsync();

        Assert.True(viewModel.IsRecallVisible);
        Assert.False(viewModel.IsReprintVisible);
        Assert.True(viewModel.RecallSelectedCommand.CanExecute(null));
        Assert.Equal(suspendedOrderGuid, viewModel.SelectedOrder?.OrderGuid);
        Assert.True(viewModel.SelectedOrder?.CanRecall);
    }

    [Fact]
    public async Task Remote_history_shows_reprint_hidden_and_hides_recall()
    {
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService
            {
                QueryResult = new RemoteOrderHistoryResult(
                [
                    new RemoteOrderHistorySummary(
                        Guid.NewGuid(),
                        "S001",
                        "POS-01",
                        "Alice",
                        DateTimeOffset.Now,
                        12m,
                        0m,
                        12m,
                        1,
                        "Cash",
                        "Synced")
                ])
            },
            CreateSession());

        viewModel.IsOnlineSourceSelected = true;
        await viewModel.LoadAsync();

        Assert.False(viewModel.IsRecallVisible);
        Assert.False(viewModel.IsReprintVisible);
        Assert.False(viewModel.RecallSelectedCommand.CanExecute(null));
        Assert.False(viewModel.ReprintCommand.CanExecute(null));
    }

    [Fact]
    public void Return_to_pos_command_invokes_callback()
    {
        var returned = false;
        var viewModel = new TransactionHistoryViewModel(
            new CapturingReceiptQueryService(),
            new CapturingSuspendedOrderService(),
            new CapturingRemoteOrderHistoryService(),
            CreateSession(),
            returnToPos: () => returned = true);

        Assert.True(viewModel.ReturnToPosCommand.CanExecute(null));

        viewModel.ReturnToPosCommand.Execute(null);

        Assert.True(returned);
    }

    private static PosSessionState CreateSession(
        string storeCode = "S001",
        string storeName = "Main Store",
        string deviceCode = "POS-01")
    {
        return new PosSessionState("HB POS", storeCode, storeName, deviceCode, "C001", "Alice", true, 0);
    }

    private sealed class CapturingReceiptQueryService : IReceiptQueryService
    {
        public IReadOnlyList<LocalOrderSummary> Orders { get; set; } = [];

        public LocalOrderHistoryQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(new LocalOrderHistoryQuery(), take, cancellationToken);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(Orders);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }
    }

    private sealed class CapturingSuspendedOrderService : ISuspendedOrderService
    {
        public IReadOnlyList<SuspendedOrderSummary> PendingOrders { get; set; } = [];

        public string? LastDeviceCode { get; private set; }

        public Guid? RecalledOrderGuid { get; private set; }

        public Task<SuspendedOrder> SuspendCurrentOrderAsync(PosSessionState session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingOrdersAsync(
            string storeCode,
            string? deviceCode = null,
            string? keyword = null,
            int take = 100,
            CancellationToken cancellationToken = default)
        {
            LastDeviceCode = deviceCode;
            return Task.FromResult(PendingOrders);
        }

        public Task<SuspendedOrder?> GetOrderAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SuspendedOrder?>(null);
        }

        public Task<SuspendedOrder> RecallOrderAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default)
        {
            RecalledOrderGuid = suspendedOrderGuid;
            PendingOrders = [];
            return Task.FromResult(new SuspendedOrder(
                suspendedOrderGuid,
                "S001",
                "POS-01",
                "C001",
                "Alice",
                DateTimeOffset.Now,
                10m,
                0m,
                10m,
                SuspendedOrderStatus.Recalled,
                []));
        }
    }

    private sealed class CapturingRemoteOrderHistoryService : IRemoteOrderHistoryService
    {
        public RemoteOrderHistoryResult QueryResult { get; init; } = new([]);

        public RemoteOrderHistoryQuery? LastQuery { get; private set; }

        public Task<RemoteOrderHistoryResult> QueryAsync(RemoteOrderHistoryQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(QueryResult);
        }

        public Task<ReceiptDetails?> GetDetailsAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(null);
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrderReturnContextDto?>(null);
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
            OrderReturnRecordCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OrderReturnRecordCreateResponse(request.ReturnOrderGuid, []));
        }
    }
}
