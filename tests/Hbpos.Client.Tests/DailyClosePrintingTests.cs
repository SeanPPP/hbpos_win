using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class DailyClosePrintingTests
{
    [Fact]
    public async Task Daily_close_print_service_prints_required_daily_close_sections()
    {
        var settingsStore = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with
            {
                PrinterPort = "COM5",
                BrandName = "HotBargain",
                StoreName = "Sunnybank"
            }
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new DailyClosePrintService(settingsStore, driver);

        var result = await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Manual);

        Assert.True(result.Succeeded);
        Assert.Equal("COM5", driver.LastSettings?.PrinterPort);
        Assert.NotNull(driver.LastDocument);
        Assert.Contains(driver.LastDocument!.PreviewRows, row => row.Text == "==== DAILY CLOSE ====" && row.IsCentered && row.IsEmphasized);
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text == "Daily Close Date: 2026-05-27");
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text == "Store: S001");
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text == "Terminal: POS-01");
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text == "Cashier: Alice");
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("Cash Counted", StringComparison.Ordinal) && row.Text.Contains("$287.00", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("Cash Difference", StringComparison.Ordinal) && row.Text.Contains("+$7.00", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("Refund Amount", StringComparison.Ordinal) && row.Text.Contains("$25.50", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("Return Qty", StringComparison.Ordinal) && row.Text.Contains("3", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("Cash", StringComparison.Ordinal) && row.Text.Contains("$300.00", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("Notes Total", StringComparison.Ordinal) && row.Text.Contains("$260.00", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("Coins Total", StringComparison.Ordinal) && row.Text.Contains("$27.00", StringComparison.Ordinal));
        AssertAllDenominationsArePrinted(driver.LastDocument);
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("$5", StringComparison.Ordinal) && row.Text.Contains("x0", StringComparison.Ordinal) && row.Text.Contains("$0.00", StringComparison.Ordinal));
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text.Contains("5c", StringComparison.Ordinal) && row.Text.Contains("x0", StringComparison.Ordinal) && row.Text.Contains("$0.00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Daily_close_print_service_returns_driver_failure_message()
    {
        var driver = new RecordingReceiptPrinterDriver
        {
            PrintResult = new ReceiptPrinterDriverResult(false, "printer offline")
        };
        var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), driver);

        var result = await service.PrintAsync(CreateArchive(), ReceiptPrintReason.Reprint);

        Assert.False(result.Succeeded);
        Assert.Equal("printer offline", result.Message);
        Assert.NotNull(driver.LastDocument);
        Assert.Contains(driver.LastDocument!.PreviewRows, row => row.Text == "==== DAILY CLOSE REPRINT ====");
    }

    [Fact]
    public async Task BuildDocumentAsync_builds_reprint_preview_without_printing()
    {
        var driver = new RecordingReceiptPrinterDriver();
        var service = new DailyClosePrintService(new FakeReceiptPrinterSettingsStore(), driver);

        var document = await service.BuildDocumentAsync(CreateArchive(), ReceiptPrintReason.Reprint);

        Assert.Null(driver.LastDocument);
        Assert.Contains(document.PreviewRows, row => row.Text == "==== DAILY CLOSE REPRINT ====");
        AssertAllDenominationsArePrinted(document);
    }

    private static DailyCloseArchive CreateArchive()
    {
        var report = new DailyCloseReport(
            new DateTime(2026, 5, 27),
            new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            12,
            [
                new DailyClosePaymentSummary(PaymentMethodKind.Cash, 300m, 20m, 280m, 8),
                new DailyClosePaymentSummary(PaymentMethodKind.Card, 520.50m, 0m, 520.50m, 3),
                new DailyClosePaymentSummary(PaymentMethodKind.Voucher, 40m, 5.50m, 34.50m, 1)
            ],
            25.50m,
            3m);
        var counts = new[]
        {
            Count(100m, 1),
            Count(50m, 1),
            Count(20m, 5),
            Count(10m, 1),
            Count(2m, 5),
            Count(1m, 10),
            Count(0.50m, 2),
            Count(0.20m, 5),
            Count(0.10m, 50)
        };

        return new DailyCloseArchive(
            Guid.NewGuid(),
            report,
            counts,
            new DateTimeOffset(2026, 5, 27, 18, 30, 0, TimeSpan.Zero),
            260m,
            27m,
            287m,
            7m);
    }

    private static CashDenominationCount Count(decimal value, int quantity)
    {
        var denomination = DailyCloseService.AustralianDenominations.Single(item => item.Value == value);
        return new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, quantity);
    }

    private static void AssertAllDenominationsArePrinted(ReceiptPrintDocument document)
    {
        foreach (var denomination in DailyCloseService.AustralianDenominations)
        {
            Assert.Contains(document.PreviewRows, row => row.Text.Contains(denomination.Label, StringComparison.Ordinal));
        }
    }

    private sealed class FakeReceiptPrinterSettingsStore : IReceiptPrinterSettingsStore
    {
        public ReceiptPrinterSettings Settings { get; init; } = ReceiptPrinterSettings.Default;

        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReceiptPrinterDriver : IReceiptPrinterDriver
    {
        public ReceiptPrintDocument? LastDocument { get; private set; }

        public ReceiptPrinterSettings? LastSettings { get; private set; }

        public ReceiptPrinterDriverResult PrintResult { get; init; } = new(true, "printed");

        public Task<ReceiptPrinterDriverResult> PrintAsync(
            ReceiptPrintDocument document,
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastDocument = document;
            LastSettings = settings;
            return Task.FromResult(PrintResult);
        }

        public Task<ReceiptPrinterDriverResult> TestAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "tested"));
        }

        public Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "drawer opened"));
        }
    }
}
