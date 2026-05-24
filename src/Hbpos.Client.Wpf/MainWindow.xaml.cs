using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly IDisplayTopologyService _displayTopologyService;
    private HwndSource? _hwndSource;
    private Task? _startupInitializationTask;
    private readonly KeyboardScannerFallbackBuffer _keyboardScannerFallback = new();
    private bool _postShowStartupStarted;

    public event EventHandler? StartupCompleted;

    public MainWindow(
        MainViewModel viewModel,
        AppStartupOptions startupOptions,
        IRawScannerService rawScannerService,
        IDisplayTopologyService displayTopologyService)
    {
        _viewModel = viewModel;
        _startupOptions = startupOptions;
        _rawScannerService = rawScannerService;
        _displayTopologyService = displayTopologyService;
        DataContext = _viewModel;
        InitializeComponent();
        SourceInitialized += MainWindowSourceInitialized;
        Loaded += MainWindowLoaded;
        PreviewKeyDown += MainWindowPreviewKeyDown;
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
            await _viewModel.ContinueStartupAfterShownAsync(_startupOptions, this);
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = ex.Message;
        }
    }

    private void MainWindowSourceInitialized(object? sender, EventArgs e)
    {
        _displayTopologyService.AttachWorkAreaConstraint(this);
        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        _hwndSource?.AddHook(_rawScannerService.ProcessWindowMessage);
    }

    private void MainWindowClosed(object? sender, EventArgs e)
    {
        PreviewKeyDown -= MainWindowPreviewKeyDown;
        _hwndSource?.RemoveHook(_rawScannerService.ProcessWindowMessage);
        _rawScannerService.Stop();
    }

    private void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextInputFocused())
        {
            _keyboardScannerFallback.Clear();
            return;
        }

        var result = _keyboardScannerFallback.Process(e.Key, DateTimeOffset.Now);
        if (result is null)
        {
            return;
        }

        if (_viewModel.TryProcessKeyboardScannerInput(result))
        {
            e.Handled = true;
        }
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox;
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

internal sealed class KeyboardScannerFallbackBuffer
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMilliseconds(120);
    private const int MinBarcodeLength = 3;
    private readonly System.Text.StringBuilder _buffer = new();
    private DateTimeOffset _lastInputAt = DateTimeOffset.MinValue;

    public string? Process(Key key, DateTimeOffset timestamp)
    {
        if (key == Key.Enter)
        {
            return Complete();
        }

        if (!TryMapCharacter(key, out var character))
        {
            Clear();
            return null;
        }

        if (_buffer.Length > 0 && timestamp - _lastInputAt > ScanTimeout)
        {
            _buffer.Clear();
        }

        _buffer.Append(character);
        _lastInputAt = timestamp;
        return null;
    }

    public void Clear()
    {
        _buffer.Clear();
        _lastInputAt = DateTimeOffset.MinValue;
    }

    private string? Complete()
    {
        var barcode = _buffer.ToString();
        Clear();
        return barcode.Length >= MinBarcodeLength ? barcode : null;
    }

    private static bool TryMapCharacter(Key key, out char character)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            character = (char)('0' + (key - Key.D0));
            return true;
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            character = (char)('0' + (key - Key.NumPad0));
            return true;
        }

        if (key >= Key.A && key <= Key.Z)
        {
            character = (char)('A' + (key - Key.A));
            return true;
        }

        character = key switch
        {
            Key.OemMinus or Key.Subtract => '-',
            Key.OemPlus or Key.Add => '+',
            Key.OemPeriod or Key.Decimal => '.',
            Key.OemComma => ',',
            Key.Space => ' ',
            _ => '\0'
        };

        return character != '\0';
    }
}
