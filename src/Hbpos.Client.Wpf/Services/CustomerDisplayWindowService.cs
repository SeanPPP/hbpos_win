using System.Windows;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Windows;

namespace Hbpos.Client.Wpf.Services;

public sealed record CustomerDisplayWindowResult(bool IsOpen, string? StatusMessageKey);

public interface ICustomerDisplayWindowService
{
    bool IsOpen { get; }

    event EventHandler? Closed;

    CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner);

    CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner);
}

public sealed class CustomerDisplayWindowService : ICustomerDisplayWindowService
{
    public const string OpenedStatusKey = "customerDisplay.window.opened";
    public const string NoSecondDisplayStatusKey = "customerDisplay.window.noSecondDisplay";

    private readonly IDisplayTopologyService _displayTopology;
    private CustomerDisplayWindow? _window;

    public CustomerDisplayWindowService(IDisplayTopologyService displayTopology)
    {
        _displayTopology = displayTopology;
    }

    public bool IsOpen => _window is not null;

    public event EventHandler? Closed;

    public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
    {
        if (_window is not null)
        {
            return new CustomerDisplayWindowResult(true, null);
        }

        if (owner is null)
        {
            return new CustomerDisplayWindowResult(false, NoSecondDisplayStatusKey);
        }

        var targetDisplay = _displayTopology.FindDisplayAwayFrom(owner);
        if (targetDisplay is null)
        {
            return new CustomerDisplayWindowResult(false, NoSecondDisplayStatusKey);
        }

        _window = new CustomerDisplayWindow
        {
            Owner = owner,
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal
        };
        _displayTopology.AttachWorkAreaConstraint(_window);
        _displayTopology.FitToDisplayWorkArea(_window, owner, targetDisplay);
        _window.Closed += OnWindowClosed;
        _window.Show();
        _window.WindowState = WindowState.Maximized;

        return new CustomerDisplayWindowResult(true, OpenedStatusKey);
    }

    public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
    {
        if (_window is not null)
        {
            _window.Close();
            return new CustomerDisplayWindowResult(false, null);
        }

        return Open(viewModel, owner);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window = null;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }
}
