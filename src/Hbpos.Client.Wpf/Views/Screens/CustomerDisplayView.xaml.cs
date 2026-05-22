using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class CustomerDisplayView : UserControl
{
    private CustomerDisplayViewModel? _viewModel;

    public CustomerDisplayView()
    {
        InitializeComponent();
        Loaded += CustomerDisplayViewLoaded;
        DataContextChanged += CustomerDisplayViewDataContextChanged;
        Unloaded += CustomerDisplayViewUnloaded;
    }

    private void CustomerDisplayViewLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        SubscribeToViewModel(DataContext as CustomerDisplayViewModel);
    }

    private void CustomerDisplayViewDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromLines();
        SubscribeToViewModel(e.NewValue as CustomerDisplayViewModel);
    }

    private void SubscribeToViewModel(CustomerDisplayViewModel? viewModel)
    {
        if (_viewModel is not null || viewModel is null)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.Lines.CollectionChanged += LinesCollectionChanged;
        ScrollLatestLineIntoView();
    }

    private void CustomerDisplayViewUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UnsubscribeFromLines();
    }

    private void LinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollLatestLineIntoView();
    }

    private void ScrollLatestLineIntoView()
    {
        var latestLine = _viewModel?.Lines.LastOrDefault();
        if (latestLine is null)
        {
            return;
        }

        LineDataGrid.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                LineDataGrid.UpdateLayout();
                LineDataGrid.ScrollIntoView(latestLine);
            }),
            DispatcherPriority.Background);
    }

    private void UnsubscribeFromLines()
    {
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged -= LinesCollectionChanged;
            _viewModel = null;
        }
    }
}
