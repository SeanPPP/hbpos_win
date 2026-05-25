namespace Hbpos.Client.Wpf.Services;

public enum RawScannerCompletionKind
{
    Enter,
    Timeout
}

public sealed record RawScannerInputResult(
    string Barcode,
    string DevicePath,
    RawScannerCompletionKind CompletionKind,
    DateTimeOffset CompletedAt = default);

public sealed class RawScannerInputProcessor
{
    public static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromMilliseconds(120);
    public const int DefaultMinBarcodeLength = 3;

    private readonly TimeSpan _scanTimeout;
    private readonly int _minBarcodeLength;
    private readonly Dictionary<string, ScannerBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    public RawScannerInputProcessor()
        : this(DefaultScanTimeout, DefaultMinBarcodeLength)
    {
    }

    public RawScannerInputProcessor(TimeSpan scanTimeout, int minBarcodeLength)
    {
        _scanTimeout = scanTimeout;
        _minBarcodeLength = minBarcodeLength;
    }

    public RawScannerInputResult? ProcessCharacter(
        string devicePath,
        char character,
        DateTimeOffset timestamp,
        string? boundDevicePath)
    {
        if (!CanAcceptDevice(devicePath, boundDevicePath))
        {
            return null;
        }

        var buffer = GetBuffer(devicePath);
        if (buffer.Text.Length > 0 && timestamp - buffer.LastInputAt > _scanTimeout)
        {
            buffer.Text.Clear();
        }

        buffer.Text.Append(character);
        buffer.LastInputAt = timestamp;
        return null;
    }

    public RawScannerInputResult? ProcessEnter(
        string devicePath,
        DateTimeOffset timestamp,
        string? boundDevicePath)
    {
        if (!CanAcceptDevice(devicePath, boundDevicePath))
        {
            return null;
        }

        if (!_buffers.TryGetValue(devicePath, out var buffer))
        {
            return null;
        }

        return CompleteBuffer(devicePath, buffer, RawScannerCompletionKind.Enter, timestamp);
    }

    public IReadOnlyList<RawScannerInputResult> FlushExpired(
        DateTimeOffset timestamp,
        string? boundDevicePath)
    {
        var results = new List<RawScannerInputResult>();
        foreach (var pair in _buffers.ToArray())
        {
            if (!CanAcceptDevice(pair.Key, boundDevicePath))
            {
                pair.Value.Text.Clear();
                continue;
            }

            if (pair.Value.Text.Length > 0 && timestamp - pair.Value.LastInputAt >= _scanTimeout)
            {
                var result = CompleteBuffer(pair.Key, pair.Value, RawScannerCompletionKind.Timeout, timestamp);
                if (result is not null)
                {
                    results.Add(result);
                }
            }
        }

        return results;
    }

    public void Clear()
    {
        foreach (var buffer in _buffers.Values)
        {
            buffer.Text.Clear();
        }
    }

    public static bool CanAcceptDevice(string devicePath, string? boundDevicePath)
    {
        return !string.IsNullOrWhiteSpace(devicePath) &&
            (string.IsNullOrWhiteSpace(boundDevicePath) ||
                string.Equals(devicePath, boundDevicePath, StringComparison.OrdinalIgnoreCase));
    }

    private ScannerBuffer GetBuffer(string devicePath)
    {
        if (!_buffers.TryGetValue(devicePath, out var buffer))
        {
            buffer = new ScannerBuffer();
            _buffers[devicePath] = buffer;
        }

        return buffer;
    }

    private RawScannerInputResult? CompleteBuffer(
        string devicePath,
        ScannerBuffer buffer,
        RawScannerCompletionKind completionKind,
        DateTimeOffset completedAt)
    {
        var barcode = buffer.Text.ToString();
        buffer.Text.Clear();

        return barcode.Length >= _minBarcodeLength
            ? new RawScannerInputResult(barcode, devicePath, completionKind, completedAt)
            : null;
    }

    private sealed class ScannerBuffer
    {
        public System.Text.StringBuilder Text { get; } = new();

        public DateTimeOffset LastInputAt { get; set; } = DateTimeOffset.MinValue;
    }
}
