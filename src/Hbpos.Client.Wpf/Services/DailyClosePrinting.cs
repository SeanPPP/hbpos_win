using System.Globalization;
using System.Text;

namespace Hbpos.Client.Wpf.Services;

public interface IDailyClosePrintService
{
    Task<ReceiptPrintDocument> BuildDocumentAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> PrintAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default);
}

public sealed class DailyClosePrintService(
    IReceiptPrinterSettingsStore settingsStore,
    IReceiptPrinterDriver driver) : IDailyClosePrintService, IDisposable
{
    // 打印机驱动只能串行写入，日结打印和普通小票保持同样的互斥策略。
    private readonly SemaphoreSlim _printLock = new(1, 1);

    public async Task<ReceiptPrintDocument> BuildDocumentAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        var (_, document) = await BuildDocumentWithSettingsAsync(archive, reason, cancellationToken);
        return document;
    }

    public async Task<ReceiptPrintResult> PrintAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        await _printLock.WaitAsync(cancellationToken);
        try
        {
            var (settings, document) = await BuildDocumentWithSettingsAsync(archive, reason, cancellationToken);
            var result = await driver.PrintAsync(document, settings, cancellationToken);
            return new ReceiptPrintResult(
                result.Succeeded,
                result.Succeeded ? "Daily close report printed." : result.Message);
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

    private async Task<(ReceiptPrinterSettings Settings, ReceiptPrintDocument Document)> BuildDocumentWithSettingsAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        return (settings, DailyCloseTextFormatter.Build(archive, settings, reason));
    }

    public void Dispose()
    {
        _printLock.Dispose();
    }
}

public sealed class NoopDailyClosePrintService : IDailyClosePrintService
{
    public static NoopDailyClosePrintService Instance { get; } = new();

    private NoopDailyClosePrintService()
    {
    }

    public Task<ReceiptPrintDocument> BuildDocumentAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(DailyCloseTextFormatter.Build(archive, ReceiptPrinterSettings.Default, reason));
    }

    public Task<ReceiptPrintResult> PrintAsync(
        DailyCloseArchive archive,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReceiptPrintResult(false, "Daily close printer is not configured."));
    }
}

public static class DailyCloseTextFormatter
{
    private const int LineWidth = 42;
    private const int PaymentLabelWidth = 14;
    private const int AmountColumnWidth = 9;

    public static ReceiptPrintDocument Build(
        DailyCloseArchive archive,
        ReceiptPrinterSettings settings,
        ReceiptPrintReason reason = ReceiptPrintReason.Manual,
        DateTimeOffset? printTime = null)
    {
        var report = archive.Report;
        var printedAt = (printTime ?? DateTimeOffset.Now).ToLocalTime();
        var builder = new DailyCloseDocumentBuilder();
        var headerName = FirstNonBlank(settings.BrandName, settings.StoreName, report.StoreCode);

        builder.Text(headerName, ReceiptPrintAlignment.Center, isEmphasized: true);
        if (!string.IsNullOrWhiteSpace(settings.StoreName) &&
            !string.Equals(settings.StoreName.Trim(), headerName, StringComparison.OrdinalIgnoreCase))
        {
            builder.Text(settings.StoreName.Trim(), ReceiptPrintAlignment.Center);
        }

        foreach (var addressLine in WrapByWord(settings.StoreAddress, 35))
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
        builder.Text(GetTitle(reason), ReceiptPrintAlignment.Center, isEmphasized: true);
        builder.Blank();
        builder.Text($"Daily Close Date: {report.BusinessDate:yyyy-MM-dd}");
        builder.Text($"Archive: {archive.ShortArchiveId}");
        builder.Text($"Store: {Fallback(report.StoreCode)}");
        builder.Text($"Terminal: {Fallback(report.DeviceCode)}");
        builder.Text($"Cashier: {Fallback(report.CashierName)}");
        builder.Text($"Saved: {archive.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.Text($"Print Time: {printedAt:yyyy-MM-dd HH:mm:ss}");
        builder.Separator();
        builder.Text(FitPaymentHeader());
        builder.Separator();

        foreach (var payment in report.PaymentSummaries)
        {
            builder.Text(FitPaymentColumns(
                payment.MethodLabel,
                payment.SalesAmount,
                payment.RefundAmount,
                payment.NetAmount));
        }

        builder.Separator();
        builder.Text(FitTwoColumns("Orders", report.OrderCount.ToString(CultureInfo.InvariantCulture)));
        builder.Text(FitTwoColumns("Sales Amount", Money(report.SalesAmount)));
        builder.Text(FitTwoColumns("Refund Amount", Money(report.RefundAmount)));
        builder.Text(FitTwoColumns("Return Qty", report.ReturnQuantity.ToString("0.##", CultureInfo.InvariantCulture)));
        builder.Text(FitTwoColumns("Net Amount", Money(report.NetAmount)), isEmphasized: true);
        builder.Separator();
        builder.Text(FitTwoColumns("Cash Expected", Money(report.SystemCashAmount)));
        builder.Text(FitTwoColumns("Cash Counted", Money(archive.CountedCashAmount)), isEmphasized: true);
        builder.Text(FitTwoColumns("Cash Difference", SignedMoney(archive.CashDifference)), isEmphasized: true);

        AppendCashGroup(builder, "Notes", NormalizeCashGroup(archive.CashCounts, CashDenominationKind.Note), archive.NoteSubtotal);
        AppendCashGroup(builder, "Coins", NormalizeCashGroup(archive.CashCounts, CashDenominationKind.Coin), archive.CoinSubtotal);

        builder.Separator();
        builder.Text("END OF REPORT", ReceiptPrintAlignment.Center);
        builder.Blank();

        return builder.Build();
    }

    private static void AppendCashGroup(
        DailyCloseDocumentBuilder builder,
        string title,
        IReadOnlyList<CashDenominationCount> counts,
        decimal subtotal)
    {
        builder.Separator();
        builder.Text(title, ReceiptPrintAlignment.Center, isEmphasized: true);
        foreach (var count in counts.OrderByDescending(count => count.Value))
        {
            builder.Text(FitDenominationColumns(count.Label, count.Quantity, count.Amount));
        }

        builder.Text(FitTwoColumns($"{title} Total", Money(subtotal)));
    }

    private static IReadOnlyList<CashDenominationCount> NormalizeCashGroup(
        IReadOnlyList<CashDenominationCount> counts,
        CashDenominationKind kind)
    {
        return DailyCloseService.AustralianDenominations
            .Where(denomination => denomination.Kind == kind)
            .Select(denomination =>
            {
                var count = counts.FirstOrDefault(item => item.Kind == denomination.Kind && item.Value == denomination.Value);
                return count ?? new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, 0);
            })
            .ToList();
    }

    private static string GetTitle(ReceiptPrintReason reason)
    {
        return reason switch
        {
            ReceiptPrintReason.Reprint => "==== DAILY CLOSE REPRINT ====",
            ReceiptPrintReason.Test => "==== DAILY CLOSE TEST ====",
            _ => "==== DAILY CLOSE ===="
        };
    }

    private static string FitPaymentHeader()
    {
        return FitColumns("Payment", "Sales", "Refund", "Net");
    }

    private static string FitPaymentColumns(string method, decimal sales, decimal refund, decimal net)
    {
        return FitColumns(method, Money(sales), Money(refund), Money(net));
    }

    private static string FitColumns(string first, string second, string third, string fourth)
    {
        return TrimTo(first, PaymentLabelWidth).PadRight(PaymentLabelWidth) +
            TrimTo(second, AmountColumnWidth).PadLeft(AmountColumnWidth) +
            TrimTo(third, AmountColumnWidth).PadLeft(AmountColumnWidth) +
            TrimTo(fourth, AmountColumnWidth + 1).PadLeft(AmountColumnWidth + 1);
    }

    private static string FitDenominationColumns(string label, int quantity, decimal amount)
    {
        var quantityText = $"x{quantity.ToString(CultureInfo.InvariantCulture)}";
        return TrimTo(label, 18).PadRight(18) +
            TrimTo(quantityText, 6).PadLeft(6) +
            Money(amount).PadLeft(18);
    }

    private static string FitTwoColumns(string left, string right)
    {
        left = TrimTo(left, 18);
        right = TrimTo(right, 20);
        return left + new string(' ', Math.Max(1, LineWidth - left.Length - right.Length)) + right;
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string Fallback(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static string Money(decimal amount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${amount:0.00}");
    }

    private static string SignedMoney(decimal amount)
    {
        var sign = amount >= 0m ? "+" : "-";
        return sign + Money(Math.Abs(amount));
    }

    private static string TrimTo(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return maxChars <= 3 ? value[..maxChars] : value[..(maxChars - 3)] + "...";
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

    private sealed class DailyCloseDocumentBuilder
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

        public ReceiptPrintDocument Build()
        {
            return new ReceiptPrintDocument(_elements, _previewRows);
        }
    }
}
