using System.Windows;
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

    public bool IsOpen => _window is not null;

    public CustomerDisplayWindowMode Mode => _mode;

    public event EventHandler? Closed;

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

    private CustomerDisplayWindow EnsureWindow(CustomerDisplayViewModel viewModel, Window owner)
    {
        if (_window is not null)
        {
            _window.Owner ??= owner;
            _window.DataContext = viewModel;
            return _window;
        }

        _window = new CustomerDisplayWindow
        {
            Owner = owner,
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal
        };
        _displayTopology.AttachWorkAreaConstraint(_window);
        _window.Closed += OnWindowClosed;
        return _window;
    }

    private void ApplyMode(CustomerDisplayWindow window, Window owner, DisplayBounds targetDisplay, CustomerDisplayWindowMode mode)
    {
        window.WindowState = WindowState.Normal;
        window.SetTitleBarVisible(mode == CustomerDisplayWindowMode.Normal);
        _displayTopology.FitToDisplayWorkArea(window, owner, targetDisplay);

        if (mode == CustomerDisplayWindowMode.Normal)
        {
            CenterNormalWindow(window);
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (mode == CustomerDisplayWindowMode.Fullscreen)
        {
            window.WindowState = WindowState.Maximized;
        }
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
