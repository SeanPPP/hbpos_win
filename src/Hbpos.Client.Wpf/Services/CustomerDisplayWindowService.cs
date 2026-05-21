using System.Windows;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Windows;

namespace Hbpos.Client.Wpf.Services;

public interface ICustomerDisplayWindowService
{
    bool IsOpen { get; }

    event EventHandler? Closed;

    void Toggle(CustomerDisplayViewModel viewModel, Window owner);
}

public sealed class CustomerDisplayWindowService : ICustomerDisplayWindowService
{
    private CustomerDisplayWindow? _window;

    public bool IsOpen => _window is not null;

    public event EventHandler? Closed;

    public void Toggle(CustomerDisplayViewModel viewModel, Window owner)
    {
        if (_window is not null)
        {
            _window.Close();
            return;
        }

        _window = new CustomerDisplayWindow
        {
            Owner = owner,
            DataContext = viewModel
        };
        _window.Closed += OnWindowClosed;
        _window.Show();
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
