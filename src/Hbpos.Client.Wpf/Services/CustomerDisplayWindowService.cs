using System.Windows;
using System.Windows.Threading;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Windows;

namespace Hbpos.Client.Wpf.Services;

public enum CustomerDisplayWindowMode
{
    Closed,
    Normal,
    Fullscreen
}

public sealed record CustomerDisplayWindowResult(CustomerDisplayWindowMode Mode, string? StatusMessageKey)
{
    public CustomerDisplayWindowResult(bool isOpen, string? statusMessageKey)
        : this(isOpen ? CustomerDisplayWindowMode.Fullscreen : CustomerDisplayWindowMode.Closed, statusMessageKey)
    {
    }

    public bool IsOpen => Mode != CustomerDisplayWindowMode.Closed;
}

public interface ICustomerDisplayWindowService
{
    bool IsOpen { get; }

    CustomerDisplayWindowMode Mode { get; }

    event EventHandler? Closed;

    void Prewarm(CustomerDisplayViewModel viewModel)
    {
    }

    CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner);

    CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner);

    CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner);
}

public sealed class CustomerDisplayWindowService : ICustomerDisplayWindowService
{
    public const string OpenedStatusKey = OpenedFullscreenStatusKey;
    public const string OpenedNormalStatusKey = "customerDisplay.window.openedNormal";
    public const string OpenedFullscreenStatusKey = "customerDisplay.window.openedFullscreen";
    public const string ClosedStatusKey = "customerDisplay.window.closed";
    public const string NoSecondDisplayStatusKey = "customerDisplay.window.noSecondDisplay";

    private readonly IDisplayTopologyService _displayTopology;
    private CustomerDisplayWindow? _window;
    private CustomerDisplayWindowMode _mode = CustomerDisplayWindowMode.Closed;

    public CustomerDisplayWindowService(IDisplayTopologyService displayTopology)
    {
        _displayTopology = displayTopology;
    }

    public bool IsOpen => _window?.IsVisible == true && _mode != CustomerDisplayWindowMode.Closed;

    public CustomerDisplayWindowMode Mode => _mode;

    public event EventHandler? Closed;

    internal sealed record CustomerDisplayLayoutPlan(
        bool TitleBarVisibleDuringPlacement,
        bool CenterAfterPlacement,
        bool UseFullDisplayBoundsForPlacement,
        WindowState FinalWindowState,
        bool TitleBarVisibleAfterStateChange);

    public void Prewarm(CustomerDisplayViewModel viewModel)
    {
        EnsureWindow(viewModel, owner: null);
    }

    public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
    {
        return SetMode(CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
    }

    public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
    {
        return SetMode(IsOpen ? CustomerDisplayWindowMode.Closed : CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
    }

    public CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner)
    {
        if (mode == CustomerDisplayWindowMode.Closed)
        {
            CloseWindow();
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, ClosedStatusKey);
        }

        if (owner is null)
        {
            CloseWindow();
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, NoSecondDisplayStatusKey);
        }

        var targetDisplay = _displayTopology.FindDisplayAwayFrom(owner);
        if (targetDisplay is null)
        {
            CloseWindow();
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, NoSecondDisplayStatusKey);
        }

        var window = EnsureWindow(viewModel, owner);
        ApplyMode(window, owner, targetDisplay, mode);
        _mode = mode;

        return new CustomerDisplayWindowResult(mode, GetOpenedStatusKey(mode));
    }

    private CustomerDisplayWindow EnsureWindow(CustomerDisplayViewModel viewModel, Window? owner)
    {
        if (_window is not null)
        {
            if (owner is not null && _window.Owner is null && !_window.IsVisible)
            {
                _window.Owner = owner;
            }

            _window.DataContext = viewModel;
            return _window;
        }

        _window = new CustomerDisplayWindow
        {
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal
        };
        if (owner is not null)
        {
            _window.Owner = owner;
        }

        _displayTopology.AttachWorkAreaConstraint(_window);
        _window.Closed += OnWindowClosed;
        return _window;
    }

    private void ApplyMode(CustomerDisplayWindow window, Window owner, DisplayBounds targetDisplay, CustomerDisplayWindowMode mode)
    {
        var plan = GetLayoutPlan(mode);
        window.WindowState = WindowState.Normal;
        window.SetTitleBarVisible(plan.TitleBarVisibleDuringPlacement);

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (plan.UseFullDisplayBoundsForPlacement)
        {
            _displayTopology.FitToDisplayBounds(window, targetDisplay);
        }
        else
        {
            _displayTopology.FitToDisplayWorkArea(window, targetDisplay);
        }

        if (plan.CenterAfterPlacement)
        {
            CenterNormalWindow(window);
        }

        window.WindowState = plan.FinalWindowState;
        window.SetTitleBarVisible(plan.TitleBarVisibleAfterStateChange);
        window.RefreshContentLayout();
        RestoreOwnerActivation(owner);
    }

    internal static CustomerDisplayLayoutPlan GetLayoutPlan(CustomerDisplayWindowMode mode)
    {
        return mode switch
        {
            CustomerDisplayWindowMode.Normal => new CustomerDisplayLayoutPlan(
                TitleBarVisibleDuringPlacement: true,
                CenterAfterPlacement: true,
                UseFullDisplayBoundsForPlacement: false,
                FinalWindowState: WindowState.Normal,
                TitleBarVisibleAfterStateChange: true),
            CustomerDisplayWindowMode.Fullscreen => new CustomerDisplayLayoutPlan(
                TitleBarVisibleDuringPlacement: true,
                CenterAfterPlacement: false,
                UseFullDisplayBoundsForPlacement: true,
                FinalWindowState: WindowState.Maximized,
                TitleBarVisibleAfterStateChange: false),
            _ => new CustomerDisplayLayoutPlan(
                TitleBarVisibleDuringPlacement: false,
                CenterAfterPlacement: false,
                UseFullDisplayBoundsForPlacement: false,
                FinalWindowState: WindowState.Normal,
                TitleBarVisibleAfterStateChange: false)
        };
    }

    private static void RestoreOwnerActivation(Window owner)
    {
        if (!owner.IsVisible)
        {
            return;
        }

        owner.Dispatcher.BeginInvoke(() =>
        {
            if (!owner.IsVisible)
            {
                return;
            }

            if (owner.WindowState == WindowState.Minimized)
            {
                owner.WindowState = WindowState.Normal;
            }

            var wasTopmost = owner.Topmost;
            owner.Topmost = true;
            owner.Activate();
            owner.Focus();
            owner.Topmost = wasTopmost;
        }, DispatcherPriority.ApplicationIdle);
    }

    private static void CenterNormalWindow(Window window)
    {
        var fullWidth = window.Width;
        var fullHeight = window.Height;
        var width = Math.Max(window.MinWidth, fullWidth * 0.78);
        var height = Math.Max(window.MinHeight, fullHeight * 0.78);

        window.Left += Math.Max(0, (fullWidth - width) / 2);
        window.Top += Math.Max(0, (fullHeight - height) / 2);
        window.Width = Math.Min(fullWidth, width);
        window.Height = Math.Min(fullHeight, height);
    }

    private void CloseWindow()
    {
        if (_window is null)
        {
            _mode = CustomerDisplayWindowMode.Closed;
            return;
        }

        _window.Close();
    }

    private static string GetOpenedStatusKey(CustomerDisplayWindowMode mode)
    {
        return mode == CustomerDisplayWindowMode.Normal
            ? OpenedNormalStatusKey
            : OpenedFullscreenStatusKey;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window = null;
        }

        _mode = CustomerDisplayWindowMode.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
