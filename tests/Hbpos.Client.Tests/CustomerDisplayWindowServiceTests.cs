using System.Windows;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayWindowServiceTests
{
    [Fact]
    public void Fullscreen_layout_plan_matches_maximize_then_hide_titlebar_flow()
    {
        var plan = CustomerDisplayWindowService.GetLayoutPlan(CustomerDisplayWindowMode.Fullscreen);

        Assert.True(plan.TitleBarVisibleDuringPlacement);
        Assert.False(plan.CenterAfterPlacement);
        Assert.True(plan.UseFullDisplayBoundsForPlacement);
        Assert.Equal(WindowState.Maximized, plan.FinalWindowState);
        Assert.False(plan.TitleBarVisibleAfterStateChange);
    }

    [Fact]
    public void Normal_layout_plan_keeps_titlebar_visible_and_centered()
    {
        var plan = CustomerDisplayWindowService.GetLayoutPlan(CustomerDisplayWindowMode.Normal);

        Assert.True(plan.TitleBarVisibleDuringPlacement);
        Assert.True(plan.CenterAfterPlacement);
        Assert.False(plan.UseFullDisplayBoundsForPlacement);
        Assert.Equal(WindowState.Normal, plan.FinalWindowState);
        Assert.True(plan.TitleBarVisibleAfterStateChange);
    }

    [Fact]
    public void Prewarm_loads_cart_into_view_model_and_calls_window_service_once()
    {
        var windowService = new FakeCustomerDisplayWindowService();
        var orchestrator = new CustomerDisplayOrchestrator(windowService);
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-APPLE", "Apple", "PLU001", 3.50m));
        cart.AddItem(CreateItem("SKU-APPLE", "Apple", "PLU001", 3.50m));

        orchestrator.Prewarm(customerDisplay, session, cart);

        Assert.Equal(1, windowService.PrewarmCallCount);
        Assert.Same(customerDisplay, windowService.LastPrewarmedViewModel);
        Assert.Equal("POS-1001", customerDisplay.TerminalName);
        Assert.Single(customerDisplay.Lines);
        Assert.Equal("Apple", customerDisplay.Lines[0].DisplayName);
        Assert.Equal("PLU001", customerDisplay.Lines[0].LookupCode);
        Assert.Equal(2, customerDisplay.TotalItemQuantity);
        Assert.Equal(7.00m, customerDisplay.TotalToPay);
    }

    [Fact]
    public void SetMode_after_prewarm_preserves_no_second_display_result()
    {
        var expected = new CustomerDisplayWindowResult(
            CustomerDisplayWindowMode.Closed,
            CustomerDisplayWindowService.NoSecondDisplayStatusKey);
        var windowService = new FakeCustomerDisplayWindowService
        {
            NextSetModeResult = expected
        };
        var orchestrator = new CustomerDisplayOrchestrator(windowService);
        var customerDisplay = new CustomerDisplayViewModel();
        var session = CreateSession();
        var cart = new PosCartService();
        cart.AddItem(CreateItem("SKU-APPLE", "Apple", "PLU001", 4.20m));

        orchestrator.Prewarm(customerDisplay, session, cart);
        var result = orchestrator.SetMode(
            CustomerDisplayWindowMode.Fullscreen,
            customerDisplay,
            session,
            cart,
            owner: null);

        Assert.Equal(1, windowService.PrewarmCallCount);
        Assert.Equal(1, windowService.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, windowService.LastRequestedMode);
        Assert.Equal(expected, result);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            SystemName: "HB POS",
            StoreCode: "S001",
            StoreName: "Main Store",
            DeviceCode: "POS-1001",
            CashierId: "C001",
            CashierName: "Alice",
            IsOnline: false,
            PendingSyncCount: 0);
    }

    private static SellableItemDto CreateItem(string productCode, string displayName, string lookupCode, decimal price)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: "StoreRetailPrice",
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: null);
    }

    private sealed class FakeCustomerDisplayWindowService : ICustomerDisplayWindowService
    {
        public bool IsOpen => Mode != CustomerDisplayWindowMode.Closed;

        public CustomerDisplayWindowMode Mode { get; private set; }

        public int PrewarmCallCount { get; private set; }

        public int SetModeCallCount { get; private set; }

        public CustomerDisplayViewModel? LastPrewarmedViewModel { get; private set; }

        public CustomerDisplayWindowMode LastRequestedMode { get; private set; }

        public CustomerDisplayWindowResult NextSetModeResult { get; init; } = new(
            CustomerDisplayWindowMode.Fullscreen,
            CustomerDisplayWindowService.OpenedFullscreenStatusKey);

        public event EventHandler? Closed
        {
            add { }
            remove { }
        }

        public void Prewarm(CustomerDisplayViewModel viewModel)
        {
            PrewarmCallCount++;
            LastPrewarmedViewModel = viewModel;
        }

        public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
        {
            return SetMode(CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
        }

        public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
        {
            var nextMode = Mode == CustomerDisplayWindowMode.Closed
                ? CustomerDisplayWindowMode.Fullscreen
                : CustomerDisplayWindowMode.Closed;
            return SetMode(nextMode, viewModel, owner);
        }

        public CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner)
        {
            SetModeCallCount++;
            LastRequestedMode = mode;
            Mode = NextSetModeResult.Mode;
            return NextSetModeResult;
        }
    }
}
