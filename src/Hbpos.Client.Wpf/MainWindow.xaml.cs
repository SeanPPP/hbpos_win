using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppStartupOptions _startupOptions;
    private readonly IRawScannerService _rawScannerService;
    private HwndSource? _hwndSource;
    private Task? _startupInitializationTask;
    private bool _postShowStartupStarted;

    public event EventHandler? StartupCompleted;

    public MainWindow(
        MainViewModel viewModel,
        AppStartupOptions startupOptions,
        IRawScannerService rawScannerService)
    {
        _viewModel = viewModel;
        _startupOptions = startupOptions;
        _rawScannerService = rawScannerService;
        DataContext = _viewModel;
        InitializeComponent();
        SourceInitialized += MainWindowSourceInitialized;
        Loaded += MainWindowLoaded;
        Closed += MainWindowClosed;
    }

    private async void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindowLoaded;
        await InitializeForStartupAsync();
    }

    public Task InitializeForStartupAsync()
    {
        _startupInitializationTask ??= InitializeForStartupCoreAsync();
        return _startupInitializationTask;
    }

    private async Task InitializeForStartupCoreAsync()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        await _rawScannerService.InitializeAsync();
        _rawScannerService.Start(hwnd);
        await _viewModel.InitializeAsync(_startupOptions);
        StartupCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void ContinueStartupAfterShown()
    {
        if (_postShowStartupStarted)
        {
            return;
        }

        _postShowStartupStarted = true;
        _ = ContinueStartupAfterShownCoreAsync();
    }

    private async Task ContinueStartupAfterShownCoreAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(300);
            await _viewModel.ContinueStartupAfterShownAsync(_startupOptions);
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = ex.Message;
        }
    }

    private void MainWindowSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        _hwndSource?.AddHook(_rawScannerService.ProcessWindowMessage);
    }

    private void MainWindowClosed(object? sender, EventArgs e)
    {
        _hwndSource?.RemoveHook(_rawScannerService.ProcessWindowMessage);
        _rawScannerService.Stop();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
