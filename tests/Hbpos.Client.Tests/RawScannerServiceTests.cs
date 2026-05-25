using System.Windows.Input;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class RawScannerServiceTests
{
    [Fact]
    public void DispatchResultForDiagnostics_DispatchesToActiveHandler()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        RawBarcodeScannedEventArgs? received = null;
        service.Subscribe("pos", args => received = args);
        service.SetActivePage("pos");

        service.DispatchResultForDiagnostics(new RawScannerInputResult("930110", "scanner-device", RawScannerCompletionKind.Enter));

        Assert.NotNull(received);
        Assert.Equal("930110", received.Barcode);
        Assert.Contains(logs.Lines, line => line.Contains("scan accepted barcode=930110", StringComparison.Ordinal));
    }

    [Fact]
    public void DispatchResultForDiagnostics_LogsWhenNoActiveHandler()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());
        var called = false;
        service.Subscribe("pos", _ => called = true);

        service.DispatchResultForDiagnostics(new RawScannerInputResult("930111", "scanner-device", RawScannerCompletionKind.Enter));

        Assert.False(called);
        Assert.Contains(logs.Lines, line => line.Contains("scan ignored because no active handler page=<none> barcode=930111", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessScannerKeyForDiagnostics_LogsBoundDeviceMismatchOnce()
    {
        using var logs = new ConsoleLogCapture();
        var binding = new FakeScannerBindingService { BoundDevicePath = "bound-scanner" };
        var service = new RawScannerService(binding, new RawScannerInputProcessor());
        await service.InitializeAsync();

        var first = service.ProcessScannerKeyForDiagnostics("other-keyboard", Key.D9, DateTimeOffset.UtcNow);
        var second = service.ProcessScannerKeyForDiagnostics("other-keyboard", Key.D3, DateTimeOffset.UtcNow);

        Assert.Null(first);
        Assert.Null(second);
        var mismatchLogs = logs.Lines
            .Where(line => line.Contains("scanner device does not match binding currentPath=other-keyboard boundPath=bound-scanner", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(mismatchLogs);
    }

    [Fact]
    public void ProcessScannerKeyForDiagnostics_LogsUnmappedKey()
    {
        using var logs = new ConsoleLogCapture();
        var service = new RawScannerService(new FakeScannerBindingService(), new RawScannerInputProcessor());

        var result = service.ProcessScannerKeyForDiagnostics("scanner-device", Key.LeftCtrl, DateTimeOffset.UtcNow);

        Assert.Null(result);
        Assert.Contains(logs.Lines, line => line.Contains("raw input key ignored because it cannot be mapped key=LeftCtrl", StringComparison.Ordinal));
    }

    private sealed class FakeScannerBindingService : IScannerBindingService
    {
        public string? BoundDevicePath { get; set; }

        public Task<string?> GetBoundDevicePathAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BoundDevicePath);
        }

        public Task SetBoundDevicePathAsync(string devicePath, CancellationToken cancellationToken = default)
        {
            BoundDevicePath = devicePath;
            return Task.CompletedTask;
        }

        public Task ClearBoundDevicePathAsync(CancellationToken cancellationToken = default)
        {
            BoundDevicePath = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
