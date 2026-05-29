using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public enum ReceiptPrintReason
{
    Manual,
    LastReceipt,
    Reprint,
    CardAuto,
    Test
}

public enum ReceiptPrintElementKind
{
    Text,
    Separator,
    Barcode,
    QrCode
}

public enum ReceiptPrintAlignment
{
    Left = 0,
    Center = 1,
    Right = 2
}

public enum ReceiptPreviewRowKind
{
    Text,
    Separator,
    Barcode,
    QrCode
}

public sealed record ReceiptPrinterSettings(
    string PrinterPort,
    string BrandName,
    string StoreName,
    string StoreAddress,
    string StorePhone,
    string Abn,
    string ReturnPolicy,
    int CutDistance)
{
    public const string DefaultPrinterPort = "USB,";

    public static ReceiptPrinterSettings Default { get; } = new(
        DefaultPrinterPort,
        "HotBargain",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        60);
}

public sealed record ReceiptPrintElement(
    ReceiptPrintElementKind Kind,
    string Text,
    ReceiptPrintAlignment Alignment = ReceiptPrintAlignment.Left,
    bool IsEmphasized = false);

public sealed record ReceiptPreviewRow(
    ReceiptPreviewRowKind Kind,
    string Text,
    ReceiptPrintAlignment Alignment = ReceiptPrintAlignment.Left,
    bool IsEmphasized = false)
{
    public bool IsSeparator => Kind == ReceiptPreviewRowKind.Separator;

    public bool IsBarcode => Kind == ReceiptPreviewRowKind.Barcode;

    public bool IsQrCode => Kind == ReceiptPreviewRowKind.QrCode;

    public bool IsCentered => Alignment == ReceiptPrintAlignment.Center;

    public bool IsRightAligned => Alignment == ReceiptPrintAlignment.Right;

    public bool IsMachineCode => IsBarcode || IsQrCode;
}

public sealed record ReceiptPrintDocument(
    IReadOnlyList<ReceiptPrintElement> Elements,
    IReadOnlyList<ReceiptPreviewRow> PreviewRows)
{
    public string PlainText => string.Join(
        Environment.NewLine,
        Elements
            .Where(element => element.Kind is ReceiptPrintElementKind.Text or ReceiptPrintElementKind.Separator)
            .Select(element => element.Text));
}

public sealed record ReceiptPrinterDriverResult(bool Succeeded, string Message);

public sealed record ReceiptPrintResult(bool Succeeded, string Message, Guid? OrderGuid = null);

public interface IReceiptPrinterSettingsStore
{
    Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default);
}

public interface IReceiptTextFormatter
{
    ReceiptPrintDocument Build(
        ReceiptDetails receipt,
        ReceiptPrinterSettings settings,
        DateTimeOffset? printTime = null);
}

public interface IReceiptPrinterDriver
{
    Task<ReceiptPrinterDriverResult> PrintAsync(
        ReceiptPrintDocument document,
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrinterDriverResult> TestAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default);
}

public interface IReceiptPrintService
{
    Task<ReceiptPrintResult> PrintLatestReceiptAsync(
        ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> PrintReceiptAsync(
        Guid orderGuid,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> PrintReceiptAsync(
        ReceiptDetails receipt,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default);
}

public interface ICashDrawerService
{
    Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed class ReceiptPrinterSettingsStore(ILocalAppSettingsRepository settingsRepository) : IReceiptPrinterSettingsStore
{
    private const string Prefix = "ReceiptPrinter:";
    private const string PrinterPortKey = Prefix + "Port";
    private const string BrandNameKey = Prefix + "BrandName";
    private const string StoreNameKey = Prefix + "StoreName";
    private const string StoreAddressKey = Prefix + "StoreAddress";
    private const string StorePhoneKey = Prefix + "StorePhone";
    private const string AbnKey = Prefix + "Abn";
    private const string ReturnPolicyKey = Prefix + "ReturnPolicy";
    private const string CutDistanceKey = Prefix + "CutDistance";

    public async Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var fallback = ReceiptPrinterSettings.Default;
        return new ReceiptPrinterSettings(
            NormalizePort(await settingsRepository.GetValueAsync(PrinterPortKey, cancellationToken)),
            NormalizeText(await settingsRepository.GetValueAsync(BrandNameKey, cancellationToken), fallback.BrandName),
            NormalizeText(await settingsRepository.GetValueAsync(StoreNameKey, cancellationToken), fallback.StoreName),
            NormalizeText(await settingsRepository.GetValueAsync(StoreAddressKey, cancellationToken), fallback.StoreAddress),
            NormalizeText(await settingsRepository.GetValueAsync(StorePhoneKey, cancellationToken), fallback.StorePhone),
            NormalizeText(await settingsRepository.GetValueAsync(AbnKey, cancellationToken), fallback.Abn),
            NormalizeText(await settingsRepository.GetValueAsync(ReturnPolicyKey, cancellationToken), fallback.ReturnPolicy),
            NormalizeCutDistance(await settingsRepository.GetValueAsync(CutDistanceKey, cancellationToken), fallback.CutDistance));
    }

    public async Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
    {
        await settingsRepository.SetValueAsync(PrinterPortKey, NormalizePort(settings.PrinterPort), cancellationToken);
        await settingsRepository.SetValueAsync(BrandNameKey, NormalizeText(settings.BrandName, string.Empty), cancellationToken);
        await settingsRepository.SetValueAsync(StoreNameKey, NormalizeText(settings.StoreName, string.Empty), cancellationToken);
        await settingsRepository.SetValueAsync(StoreAddressKey, NormalizeText(settings.StoreAddress, string.Empty), cancellationToken);
        await settingsRepository.SetValueAsync(StorePhoneKey, NormalizeText(settings.StorePhone, string.Empty), cancellationToken);
        await settingsRepository.SetValueAsync(AbnKey, NormalizeText(settings.Abn, string.Empty), cancellationToken);
        await settingsRepository.SetValueAsync(ReturnPolicyKey, NormalizeText(settings.ReturnPolicy, string.Empty), cancellationToken);
        await settingsRepository.SetValueAsync(CutDistanceKey, Math.Max(1, settings.CutDistance).ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    private static string NormalizePort(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? ReceiptPrinterSettings.DefaultPrinterPort : value.Trim();
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int NormalizeCutDistance(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var distance) && distance > 0
            ? distance
            : fallback;
    }
}

public sealed class ReceiptTextFormatter : IReceiptTextFormatter
{
    private const int LineWidth = 42;
    private const int AddressLineWidth = 35;

    public ReceiptPrintDocument Build(
        ReceiptDetails receipt,
        ReceiptPrinterSettings settings,
        DateTimeOffset? printTime = null)
    {
        var builder = new ReceiptDocumentBuilder();
        var printedAt = printTime ?? DateTimeOffset.Now;
        var orderId = receipt.OrderGuid.ToString();

        var brandName = FirstNonBlank(settings.BrandName, receipt.StoreCode);
        builder.Text(brandName, ReceiptPrintAlignment.Center, isEmphasized: true);
        if (!string.IsNullOrWhiteSpace(settings.StoreName) &&
            !string.Equals(settings.StoreName.Trim(), brandName, StringComparison.OrdinalIgnoreCase))
        {
            builder.Text(settings.StoreName.Trim(), ReceiptPrintAlignment.Center);
        }

        foreach (var addressLine in WrapByWord(settings.StoreAddress, AddressLineWidth))
        {
            builder.Text(addressLine, ReceiptPrintAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(settings.StorePhone))
        {
            builder.Text($"Tel: {settings.StorePhone.Trim()}", ReceiptPrintAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(settings.Abn))
        {
            builder.Text($"ABN: {settings.Abn.Trim()}", ReceiptPrintAlignment.Center);
        }

        builder.Blank();
        builder.Text("===== TAX INVOICE =====", ReceiptPrintAlignment.Center);
        builder.Blank();
        builder.Text("*** Paid ***", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Blank();
        builder.Text($"Order: {orderId}");
        builder.Text($"Date: {receipt.SoldAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.Text($"Cashier: {receipt.CashierName}");
        builder.Text($"Store: {receipt.StoreCode}");
        builder.Text($"Device: {receipt.DeviceCode}");
        builder.Separator();
        builder.Text(FitColumns("ITEM", "QTY", "PRICE", 25, 5, 12));
        builder.Separator();

        foreach (var line in receipt.Lines)
        {
            foreach (var nameLine in WrapByWord(line.DisplayName, LineWidth))
            {
                builder.Text(nameLine);
            }

            builder.Text(FitColumns(
                TrimTo(line.LookupCode, 25),
                line.QuantityDisplay,
                Money(line.ActualAmount),
                25,
                5,
                12));

            if (line.DiscountAmount != 0m)
            {
                builder.Text(FitTwoColumns("Dis", $"-{Money(line.DiscountAmount)}"));
            }
        }

        builder.Separator();
        builder.Text(FitTwoColumns("Subtotal", Money(receipt.TotalAmount)));
        if (receipt.DiscountAmount != 0m)
        {
            builder.Text(FitTwoColumns("Discount", $"-{Money(receipt.DiscountAmount)}"));
        }

        var gst = decimal.Round(receipt.ActualAmount / 11m, 2, MidpointRounding.AwayFromZero);
        builder.Text(FitTwoColumns("GST", Money(gst)));
        builder.Text(FitTwoColumns("Total(inc GST)", Money(receipt.ActualAmount)), isEmphasized: true);
        builder.Separator();
        builder.Text("Payment:");

        foreach (var payment in receipt.Payments)
        {
            builder.Text(FitTwoColumns(payment.MethodLabel, Money(payment.Amount)));
            if (!string.IsNullOrWhiteSpace(payment.DisplayReference))
            {
                builder.Text($"  {payment.DisplayReference}");
            }

            if (!string.IsNullOrWhiteSpace(payment.CardSummary))
            {
                builder.Text($"  {payment.CardSummary}");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ReturnPolicy))
        {
            builder.Separator();
            builder.Text("Refunds and returns", ReceiptPrintAlignment.Center, isEmphasized: true);
            foreach (var line in WrapByWord(settings.ReturnPolicy, LineWidth))
            {
                builder.Text(line, ReceiptPrintAlignment.Center);
            }
        }

        var receiptTexts = receipt.Payments
            .Select(payment => payment.ReceiptText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Trim())
            .ToList();
        if (receiptTexts.Count > 0)
        {
            builder.Separator();
            foreach (var receiptText in receiptTexts)
            {
                foreach (var line in receiptText.Replace("\r\n", "\n").Split('\n'))
                {
                    builder.Text(line);
                }
                builder.Blank();
            }
        }

        builder.Separator();
        builder.Barcode(orderId);
        builder.QrCode(orderId);
        builder.Text($"Print Time: {printedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.Text($"Device: {receipt.DeviceCode}");
        builder.Blank();
        builder.Text("Thank you for your purchase!", ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Blank();

        return builder.Build();
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static IReadOnlyList<string> WrapByWord(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = new StringBuilder();
            foreach (var word in words)
            {
                if (word.Length > maxChars)
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    for (var index = 0; index < word.Length; index += maxChars)
                    {
                        lines.Add(word.Substring(index, Math.Min(maxChars, word.Length - index)));
                    }

                    continue;
                }

                var nextLength = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
                if (nextLength > maxChars)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        return lines;
    }

    private static string Money(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }

    private static string FitColumns(string left, string middle, string right, int leftWidth, int middleWidth, int rightWidth)
    {
        return TrimTo(left, leftWidth).PadRight(leftWidth) +
            TrimTo(middle, middleWidth).PadLeft(middleWidth) +
            TrimTo(right, rightWidth).PadLeft(rightWidth);
    }

    private static string FitTwoColumns(string left, string right)
    {
        left = TrimTo(left, 24);
        right = TrimTo(right, 16);
        return left + new string(' ', Math.Max(1, LineWidth - left.Length - right.Length)) + right;
    }

    private static string TrimTo(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return maxChars <= 3 ? value[..maxChars] : value[..(maxChars - 3)] + "...";
    }

    private sealed class ReceiptDocumentBuilder
    {
        private readonly List<ReceiptPrintElement> _elements = [];
        private readonly List<ReceiptPreviewRow> _previewRows = [];

        public void Text(
            string text,
            ReceiptPrintAlignment alignment = ReceiptPrintAlignment.Left,
            bool isEmphasized = false)
        {
            var normalized = text ?? string.Empty;
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Text, normalized, alignment, isEmphasized));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Text, normalized, alignment, isEmphasized));
        }

        public void Blank()
        {
            Text(string.Empty);
        }

        public void Separator()
        {
            var text = new string('-', LineWidth);
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Separator, text));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Separator, text));
        }

        public void Barcode(string text)
        {
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.Barcode, text, ReceiptPrintAlignment.Center));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.Barcode, $"BARCODE {text}", ReceiptPrintAlignment.Center));
        }

        public void QrCode(string text)
        {
            _elements.Add(new ReceiptPrintElement(ReceiptPrintElementKind.QrCode, text, ReceiptPrintAlignment.Center));
            _previewRows.Add(new ReceiptPreviewRow(ReceiptPreviewRowKind.QrCode, $"QR {text}", ReceiptPrintAlignment.Center));
        }

        public ReceiptPrintDocument Build()
        {
            return new ReceiptPrintDocument(_elements, _previewRows);
        }
    }
}

public sealed class ReceiptPrintService(
    IReceiptQueryService receiptQueryService,
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptTextFormatter formatter,
    IReceiptPrinterDriver driver,
    ILocalizationService? localization = null) : IReceiptPrintService, IDisposable
{
    private readonly SemaphoreSlim _printLock = new(1, 1);

    public async Task<ReceiptPrintResult> PrintLatestReceiptAsync(
        ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
        CancellationToken cancellationToken = default)
    {
        var receipt = await receiptQueryService.GetLatestReceiptAsync(cancellationToken);
        return receipt is null
            ? new ReceiptPrintResult(false, T("receipt.print.noReceiptFound", "No receipt found."))
            : await PrintReceiptAsync(receipt, reason, cancellationToken);
    }

    public async Task<ReceiptPrintResult> PrintReceiptAsync(
        Guid orderGuid,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        var receipt = await receiptQueryService.GetReceiptAsync(orderGuid, cancellationToken);
        return receipt is null
            ? new ReceiptPrintResult(false, T("receipt.print.noReceiptFound", "No receipt found."), orderGuid)
            : await PrintReceiptAsync(receipt, reason, cancellationToken);
    }

    public async Task<ReceiptPrintResult> PrintReceiptAsync(
        ReceiptDetails receipt,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        await _printLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            var document = formatter.Build(receipt, settings, receipt.SoldAt);
            var result = await driver.PrintAsync(document, settings, cancellationToken);
            return new ReceiptPrintResult(
                result.Succeeded,
                result.Succeeded ? T("receipt.print.success", "Receipt printed.") : result.Message,
                receipt.OrderGuid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message, receipt.OrderGuid);
        }
        finally
        {
            _printLock.Release();
        }
    }

    public async Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
    {
        await _printLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            var result = await driver.TestAsync(settings, cancellationToken);
            return new ReceiptPrintResult(result.Succeeded, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
        finally
        {
            _printLock.Release();
        }
    }

    public void Dispose()
    {
        _printLock.Dispose();
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}

public sealed class CashDrawerService(
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptPrinterDriver driver,
    ILocalizationService? localization = null) : ICashDrawerService
{
    public async Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            var result = await driver.OpenCashDrawerAsync(settings, cancellationToken);
            return new ReceiptPrintResult(result.Succeeded, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReceiptPrintResult(false, ex.Message);
        }
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }
}

public sealed class NoopCashDrawerService(ILocalizationService? localization = null) : ICashDrawerService
{
    public Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.drawer.notConfigured") ?? "Cash drawer is not configured."));
    }
}

public sealed class NoopReceiptPrintService(ILocalizationService? localization = null) : IReceiptPrintService
{
    public Task<ReceiptPrintResult> PrintLatestReceiptAsync(
        ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured."));
    }

    public Task<ReceiptPrintResult> PrintReceiptAsync(
        Guid orderGuid,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured.", orderGuid));
    }

    public Task<ReceiptPrintResult> PrintReceiptAsync(
        ReceiptDetails receipt,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured.", receipt.OrderGuid));
    }

    public Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, localization?.T("receipt.printer.notConfigured") ?? "Receipt printer is not configured."));
    }
}

public sealed class XpReceiptPrinterDriver(ILocalizationService? localization = null) : IReceiptPrinterDriver, IDisposable
{
    private const int CashDrawerPinMode = 0;
    private const int CashDrawerOnTime = 25;
    private const int CashDrawerOffTime = 250;

    private readonly SemaphoreSlim _printerLock = new(1, 1);
    private bool _disposed;

    public async Task<ReceiptPrinterDriverResult> PrintAsync(
        ReceiptPrintDocument document,
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _printerLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => PrintCore(document, settings), cancellationToken);
        }
        finally
        {
            _printerLock.Release();
        }
    }

    public async Task<ReceiptPrinterDriverResult> TestAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default)
    {
        var document = new ReceiptPrintDocument(
            [
                new ReceiptPrintElement(ReceiptPrintElementKind.Text, "HBPOS Printer Test", ReceiptPrintAlignment.Center, true),
                new ReceiptPrintElement(ReceiptPrintElementKind.Text, $"Port: {settings.PrinterPort}"),
                new ReceiptPrintElement(ReceiptPrintElementKind.Text, $"Print Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}")
            ],
            []);
        return await PrintAsync(document, settings, cancellationToken);
    }

    public async Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
        ReceiptPrinterSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _printerLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => OpenCashDrawerCore(settings), cancellationToken);
        }
        finally
        {
            _printerLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _printerLock.Dispose();
        _disposed = true;
    }

    private ReceiptPrinterDriverResult PrintCore(ReceiptPrintDocument document, ReceiptPrinterSettings settings)
    {
        var printer = InitPrinter(string.Empty);
        if (printer == IntPtr.Zero)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.initFailed", "Printer could not be initialized."));
        }

        var opened = false;
        try
        {
            var openResult = OpenPort(printer, string.IsNullOrWhiteSpace(settings.PrinterPort)
                ? ReceiptPrinterSettings.DefaultPrinterPort
                : settings.PrinterPort.Trim());
            if (openResult != 0)
            {
                return new ReceiptPrinterDriverResult(false, T("receipt.printer.portOpenFailed", "Printer port could not be opened."));
            }

            opened = true;
            var initializeResult = GetSdkFailure(PrinterInitialize(printer), T("receipt.printer.initFailed", "Printer could not be initialized."));
            if (initializeResult is not null)
            {
                return initializeResult;
            }

            var lineSpaceResult = GetSdkFailure(SetTextLineSpace(printer, 30), T("receipt.printer.lineSpacingFailed", "Printer line spacing could not be set."));
            if (lineSpaceResult is not null)
            {
                return lineSpaceResult;
            }

            var statusResult = GetPrinterNotReadyResult(printer);
            if (statusResult is not null)
            {
                return statusResult;
            }

            foreach (var element in document.Elements)
            {
                switch (element.Kind)
                {
                    case ReceiptPrintElementKind.Barcode:
                        var barcodeResult = GetSdkFailure(
                            PrintBarCode(printer, 8, element.Text, 2, 100, (int)ReceiptPrintAlignment.Center, 2),
                            T("receipt.printer.barcodeFailed", "Printer barcode could not be printed."));
                        if (barcodeResult is not null)
                        {
                            return barcodeResult;
                        }

                        break;
                    case ReceiptPrintElementKind.QrCode:
                        var qrResult = GetSdkFailure(
                            PrintSymbol(printer, 49, element.Text, 48, 7, 7, (int)ReceiptPrintAlignment.Center),
                            T("receipt.printer.qrCodeFailed", "Printer QR code could not be printed."));
                        if (qrResult is not null)
                        {
                            return qrResult;
                        }

                        break;
                    default:
                        var textResult = GetSdkFailure(
                            PrintText(printer, element.Text + "\r\n", (int)element.Alignment, element.IsEmphasized ? 1 : 0),
                            T("receipt.printer.textFailed", "Printer text could not be printed."));
                        if (textResult is not null)
                        {
                            return textResult;
                        }

                        break;
                }
            }

            var cutResult = GetSdkFailure(
                CutPaperWithDistance(printer, Math.Max(1, settings.CutDistance)),
                T("receipt.printer.cutPaperFailed", "Printer paper could not be cut."));
            if (cutResult is not null)
            {
                return cutResult;
            }

            return new ReceiptPrinterDriverResult(true, T("receipt.print.success", "Receipt printed."));
        }
        finally
        {
            if (opened)
            {
                ClosePort(printer);
            }

            ReleasePrinter(printer);
        }
    }

    private ReceiptPrinterDriverResult OpenCashDrawerCore(ReceiptPrinterSettings settings)
    {
        var printer = InitPrinter(string.Empty);
        if (printer == IntPtr.Zero)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.initFailed", "Printer could not be initialized."));
        }

        var opened = false;
        try
        {
            var openResult = OpenPort(printer, string.IsNullOrWhiteSpace(settings.PrinterPort)
                ? ReceiptPrinterSettings.DefaultPrinterPort
                : settings.PrinterPort.Trim());
            if (openResult != 0)
            {
                return new ReceiptPrinterDriverResult(false, T("receipt.printer.portOpenFailed", "Printer port could not be opened."));
            }

            opened = true;
            var initializeResult = GetSdkFailure(PrinterInitialize(printer), T("receipt.printer.initFailed", "Printer could not be initialized."));
            if (initializeResult is not null)
            {
                return initializeResult;
            }

            var statusResult = GetPrinterNotReadyResult(printer);
            if (statusResult is not null)
            {
                return statusResult;
            }

            // 通过打印机 DK 钱箱口发送脉冲，不打印小票。
            var drawerResult = GetSdkFailure(
                OpenCashDrawer(printer, CashDrawerPinMode, CashDrawerOnTime, CashDrawerOffTime),
                T("receipt.drawer.openFailed", "Cash drawer could not be opened."));
            if (drawerResult is not null)
            {
                return drawerResult;
            }

            return new ReceiptPrinterDriverResult(true, T("cashDrawer.opened", "Cash drawer opened."));
        }
        finally
        {
            if (opened)
            {
                ClosePort(printer);
            }

            ReleasePrinter(printer);
        }
    }

    private ReceiptPrinterDriverResult? GetSdkFailure(int result, string message)
    {
        return result == 0 ? null : new ReceiptPrinterDriverResult(false, $"{message} SDK result: {result}.");
    }

    private ReceiptPrinterDriverResult? GetPrinterNotReadyResult(IntPtr printer)
    {
        var status = 2;
        var result = GetPrinterState(printer, ref status);
        if (result != 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.statusReadFailed", "Printer status could not be read."));
        }

        if (status == 0x12)
        {
            return null;
        }

        if ((status & 0b100) > 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.coverOpen", "Printer cover is open."));
        }

        if ((status & 0b100000) > 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.outOfPaper", "Printer is out of paper."));
        }

        if ((status & 0b1000000) > 0)
        {
            return new ReceiptPrinterDriverResult(false, T("receipt.printer.error", "Printer is reporting an error."));
        }

        return new ReceiptPrinterDriverResult(
            false,
            string.Format(
                localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
                T("receipt.printer.notReady", "Printer is not ready. Status: {0}."),
                status));
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern IntPtr InitPrinter(string model);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern int ReleasePrinter(IntPtr intPtr);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern int OpenPort(IntPtr intPtr, string port);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern int ClosePort(IntPtr intPtr);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrinterInitialize(IntPtr intPtr);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int SetTextLineSpace(IntPtr intPtr, int lineSpace);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int GetPrinterState(IntPtr intPtr, ref int printerStatus);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrintText(IntPtr intPtr, string data, int alignment, int textSize);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrintBarCode(IntPtr intPtr, int bcType, string bcData, int width, int height, int alignment, int hriPosition);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int PrintSymbol(IntPtr intPtr, int type, string data, int errLevel, int width, int height, int alignment);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int CutPaperWithDistance(IntPtr intPtr, int distance);

    [DllImport("printer.sdk.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int OpenCashDrawer(IntPtr intPtr, int pinMode, int onTime, int offTime);
}
