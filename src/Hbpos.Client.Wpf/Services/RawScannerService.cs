using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Hbpos.Client.Wpf.Services;

public interface IRawScannerService : IDisposable
{
    bool IsActive { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler);

    void Unsubscribe(string pageId);

    void SetActivePage(string? pageId);

    void Start(IntPtr hwnd);

    void Stop();

    Task ResetBindingAsync(CancellationToken cancellationToken = default);

    IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled);
}

public sealed class RawBarcodeScannedEventArgs(string barcode, string devicePath, DateTimeOffset scannedAt) : EventArgs
{
    public string Barcode { get; } = barcode;

    public string DevicePath { get; } = devicePath;

    public DateTimeOffset ScannedAt { get; } = scannedAt;
}

public sealed class RawScannerService(
    IScannerBindingService bindingService,
    RawScannerInputProcessor inputProcessor) : IRawScannerService
{
    private const int RidInput = 0x10000003;
    private const int RidiDevicename = 0x20000007;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int WM_INPUT = 0x00FF;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int RIDEV_INPUTSINK = 0x00000100;

    private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private string? _activePageId;
    private string? _boundDevicePath;
    private string? _lastRejectedDevicePath;
    private Key? _lastUnmappedKey;
    private bool _isBinding;
    private bool _isInitialized;
    private bool _loggedEmptyDevicePath;

    public bool IsActive { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _boundDevicePath = await bindingService.GetBoundDevicePathAsync(cancellationToken);
        _isInitialized = true;
        if (string.IsNullOrWhiteSpace(_boundDevicePath))
        {
            ConsoleLog.Write("RawScanner", "no scanner device is bound; first valid POS scan will be learned");
        }
        else
        {
            ConsoleLog.Write("RawScanner", $"loaded bound scanner device path={_boundDevicePath}");
        }
    }

    public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
    {
        _handlers[pageId] = handler;
        ConsoleLog.Write("RawScanner", $"handler subscribed page={pageId}");
    }

    public void Unsubscribe(string pageId)
    {
        _handlers.Remove(pageId);
        ConsoleLog.Write("RawScanner", $"handler unsubscribed page={pageId}");
    }

    public void SetActivePage(string? pageId)
    {
        _activePageId = pageId;
        ConsoleLog.Write("RawScanner", $"active page set page={pageId ?? "<none>"}");
    }

    public void Start(IntPtr hwnd)
    {
        if (IsActive || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_isInitialized)
        {
            ConsoleLog.Write("RawScanner", "scanner service started before binding initialization completed");
        }

        var devices = new RAWINPUTDEVICE[]
        {
            new()
            {
                usUsagePage = 0x01,
                usUsage = 0x06,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            ConsoleLog.Write("RawScanner", $"RegisterRawInputDevices failed error={Marshal.GetLastWin32Error()}");
            return;
        }

        _flushTimer.Tick += OnFlushTimerTick;
        _flushTimer.Start();
        IsActive = true;
        ConsoleLog.Write("RawScanner", "raw input scanner service started");
    }

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlushTimerTick;
        inputProcessor.Clear();
        IsActive = false;
        ConsoleLog.Write("RawScanner", "raw input scanner service stopped");
    }

    public async Task ResetBindingAsync(CancellationToken cancellationToken = default)
    {
        inputProcessor.Clear();
        _boundDevicePath = null;
        await bindingService.ClearBoundDevicePathAsync(cancellationToken);
        ConsoleLog.Write("RawScanner", "scanner device binding cleared; next valid POS scan will be learned");
    }

    public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!IsActive || msg != WM_INPUT)
        {
            return IntPtr.Zero;
        }

        ProcessRawInput(lParam);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        var size = 0u;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        _ = GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (raw.header.dwType != RIM_TYPEKEYBOARD ||
                raw.keyboard.Message is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            {
                return;
            }

            var devicePath = GetDevicePath(raw.header.hDevice);
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                LogEmptyDevicePath();
                return;
            }

            var timestamp = DateTimeOffset.Now;
            var key = KeyInterop.KeyFromVirtualKey(raw.keyboard.VKey);
            var result = ProcessScannerKey(devicePath, key, timestamp);

            if (result is not null)
            {
                DispatchResult(result);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal RawScannerInputResult? ProcessScannerKeyForDiagnostics(string devicePath, Key key, DateTimeOffset timestamp)
    {
        return ProcessScannerKey(devicePath, key, timestamp);
    }

    internal void DispatchResultForDiagnostics(RawScannerInputResult result)
    {
        DispatchResult(result);
    }

    private RawScannerInputResult? ProcessScannerKey(string devicePath, Key key, DateTimeOffset timestamp)
    {
        if (!RawScannerInputProcessor.CanAcceptDevice(devicePath, _boundDevicePath))
        {
            LogRejectedDevice(devicePath);
            return null;
        }

        _lastRejectedDevicePath = null;
        if (key == Key.Enter)
        {
            return inputProcessor.ProcessEnter(devicePath, timestamp, _boundDevicePath);
        }

        if (TryMapCharacter(key, out var character))
        {
            _lastUnmappedKey = null;
            return inputProcessor.ProcessCharacter(devicePath, character, timestamp, _boundDevicePath);
        }

        LogUnmappedKey(key, devicePath);
        return null;
    }

    private void OnFlushTimerTick(object? sender, EventArgs e)
    {
        foreach (var result in inputProcessor.FlushExpired(DateTimeOffset.Now, _boundDevicePath))
        {
            DispatchResult(result);
        }
    }

    private void DispatchResult(RawScannerInputResult result)
    {
        if (_activePageId is null || !_handlers.TryGetValue(_activePageId, out var handler))
        {
            ConsoleLog.Write("RawScanner", $"scan ignored because no active handler page={_activePageId ?? "<none>"} barcode={result.Barcode}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_boundDevicePath))
        {
            _boundDevicePath = result.DevicePath;
            _ = PersistBoundDevicePathAsync(result.DevicePath);
        }

        var dispatchAt = DateTimeOffset.Now;
        var completedAt = result.CompletedAt == default ? dispatchAt : result.CompletedAt;
        var dispatchDelayMs = Math.Max(0, (dispatchAt - completedAt).TotalMilliseconds);
        ConsoleLog.Write(
            "RawScanner",
            $"scan accepted barcode={result.Barcode} completion={result.CompletionKind} activePage={_activePageId} dispatchDelayMs={dispatchDelayMs:0.###}");
        handler(new RawBarcodeScannedEventArgs(result.Barcode, result.DevicePath, completedAt));
    }

    private void LogEmptyDevicePath()
    {
        if (_loggedEmptyDevicePath)
        {
            return;
        }

        _loggedEmptyDevicePath = true;
        ConsoleLog.Write("RawScanner", "raw input ignored because device path is empty");
    }

    private void LogRejectedDevice(string devicePath)
    {
        if (string.Equals(_lastRejectedDevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastRejectedDevicePath = devicePath;
        ConsoleLog.Write(
            "RawScanner",
            $"raw input ignored because scanner device does not match binding currentPath={devicePath} boundPath={_boundDevicePath ?? "<none>"}; use Reset scanner binding and scan once to learn the scanner");
    }

    private void LogUnmappedKey(Key key, string devicePath)
    {
        if (_lastUnmappedKey == key)
        {
            return;
        }

        _lastUnmappedKey = key;
        ConsoleLog.Write("RawScanner", $"raw input key ignored because it cannot be mapped key={key} devicePath={devicePath}");
    }

    private async Task PersistBoundDevicePathAsync(string devicePath)
    {
        if (_isBinding)
        {
            return;
        }

        _isBinding = true;
        try
        {
            await bindingService.SetBoundDevicePathAsync(devicePath);
            ConsoleLog.Write("RawScanner", $"scanner device learned path={devicePath}");
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("RawScanner", $"scanner device binding failed error={ex.Message}");
        }
        finally
        {
            _isBinding = false;
        }
    }

    private static string? GetDevicePath(IntPtr deviceHandle)
    {
        var size = 0u;
        _ = GetRawInputDeviceInfo(deviceHandle, RidiDevicename, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            return GetRawInputDeviceInfo(deviceHandle, RidiDevicename, buffer, ref size) == uint.MaxValue
                ? null
                : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public int dwType;
        public int dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        int uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        int uiCommand,
        IntPtr pData,
        ref uint pcbSize);
}
