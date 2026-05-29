using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptPrintingTests
{
    [Fact]
    public void Receipt_text_formatter_builds_print_commands_and_preview_from_same_document()
    {
        var orderGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var receipt = CreateReceipt(orderGuid);
        var settings = new ReceiptPrinterSettings(
            "USB,",
            "HotBargain",
            "Main Store",
            "1 Main Street Brisbane",
            "07 3000 0000",
            "12 345 678 901",
            "Keep receipt for refunds.",
            60);
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(
            receipt,
            settings,
            new DateTimeOffset(2026, 5, 27, 10, 30, 0, TimeSpan.Zero));

        Assert.Contains(document.PreviewRows, row => row.Text == "HotBargain" && row.IsEmphasized && row.IsCentered);
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("===== TAX INVOICE =====", StringComparison.Ordinal));
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("Organic Gala Apples", StringComparison.Ordinal));
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("GST", StringComparison.Ordinal));
        Assert.Contains(document.PreviewRows, row => row.Text.Contains("APPROVED CARD RECEIPT", StringComparison.Ordinal));
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.Barcode && element.Text == orderGuid.ToString());
        Assert.Contains(document.Elements, element => element.Kind == ReceiptPrintElementKind.QrCode && element.Text == orderGuid.ToString());
    }

    [Fact]
    public void Receipt_text_formatter_does_not_print_success_page_cash_change_preview_rows()
    {
        var receipt = CreateReceipt(Guid.NewGuid(), tenderedAmount: 10m, changeAmount: 1m);
        var formatter = new ReceiptTextFormatter();

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.DoesNotContain(document.PreviewRows, row => row.Text.Contains("Tendered", StringComparison.Ordinal));
        Assert.DoesNotContain(document.PreviewRows, row => row.Text.Contains("Change", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Elements, element => element.Text.Contains("Tendered", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Elements, element => element.Text.Contains("Change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Receipt_print_service_prints_latest_receipt_with_configured_settings()
    {
        var receipt = CreateReceipt(Guid.NewGuid());
        var query = new FakeReceiptQueryService { LatestReceipt = receipt };
        var settingsStore = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with
            {
                PrinterPort = "COM3",
                BrandName = "HotBargain",
                StoreName = "Main Store"
            }
        };
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(query, settingsStore, new ReceiptTextFormatter(), driver);

        var result = await service.PrintLatestReceiptAsync(ReceiptPrintReason.LastReceipt);

        Assert.True(result.Succeeded);
        Assert.Equal(receipt.OrderGuid, result.OrderGuid);
        Assert.NotNull(driver.LastDocument);
        Assert.Equal("COM3", driver.LastSettings?.PrinterPort);
        Assert.Contains(driver.LastDocument!.PreviewRows, row => row.Text == "Main Store");
        Assert.Contains(driver.LastDocument.PreviewRows, row => row.Text == $"Print Time: {receipt.SoldAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    [Fact]
    public async Task Receipt_print_service_returns_failure_when_latest_receipt_is_missing()
    {
        var driver = new RecordingReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            new FakeReceiptQueryService(),
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver);

        var result = await service.PrintLatestReceiptAsync(ReceiptPrintReason.LastReceipt);

        Assert.False(result.Succeeded);
        Assert.Null(result.OrderGuid);
        Assert.Null(driver.LastDocument);
    }

    [Fact]
    public async Task Receipt_print_service_returns_failure_when_driver_fails()
    {
        var receipt = CreateReceipt(Guid.NewGuid());
        var query = new FakeReceiptQueryService { LatestReceipt = receipt };
        var driver = new RecordingReceiptPrinterDriver
        {
            PrintResult = new ReceiptPrinterDriverResult(false, "paper out")
        };
        var service = new ReceiptPrintService(
            query,
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver);

        var result = await service.PrintReceiptAsync(receipt, ReceiptPrintReason.Manual);

        Assert.False(result.Succeeded);
        Assert.Equal("paper out", result.Message);
        Assert.Equal(receipt.OrderGuid, result.OrderGuid);
    }

    [Fact]
    public async Task Receipt_print_service_serializes_concurrent_prints()
    {
        var first = CreateReceipt(Guid.NewGuid());
        var second = CreateReceipt(Guid.NewGuid());
        var query = new FakeReceiptQueryService();
        query.Receipts[first.OrderGuid] = first;
        query.Receipts[second.OrderGuid] = second;
        var driver = new DelayedReceiptPrinterDriver();
        var service = new ReceiptPrintService(
            query,
            new FakeReceiptPrinterSettingsStore(),
            new ReceiptTextFormatter(),
            driver);

        await Task.WhenAll(
            service.PrintReceiptAsync(first.OrderGuid, ReceiptPrintReason.Manual),
            service.PrintReceiptAsync(second.OrderGuid, ReceiptPrintReason.Reprint));

        Assert.Equal(2, driver.PrintCount);
        Assert.Equal(1, driver.MaxConcurrentPrints);
    }

    [Fact]
    public async Task Cash_drawer_service_opens_with_configured_printer_settings()
    {
        var settingsStore = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with { PrinterPort = "COM5" }
        };
        var driver = new RecordingReceiptPrinterDriver
        {
            OpenCashDrawerResult = new ReceiptPrinterDriverResult(true, "Cash drawer opened.")
        };
        var service = new CashDrawerService(settingsStore, driver);

        var result = await service.OpenAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("Cash drawer opened.", result.Message);
        Assert.Equal("COM5", driver.LastCashDrawerSettings?.PrinterPort);
        Assert.Equal(1, driver.OpenCashDrawerCallCount);
    }

    [Fact]
    public async Task Cash_drawer_service_returns_failure_when_driver_fails()
    {
        var driver = new RecordingReceiptPrinterDriver
        {
            OpenCashDrawerResult = new ReceiptPrinterDriverResult(false, "drawer offline")
        };
        var service = new CashDrawerService(new FakeReceiptPrinterSettingsStore(), driver);

        var result = await service.OpenAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("drawer offline", result.Message);
    }

    [Fact]
    public async Task Cash_drawer_service_returns_failure_when_driver_throws()
    {
        var driver = new RecordingReceiptPrinterDriver
        {
            OpenCashDrawerException = new InvalidOperationException("sdk missing")
        };
        var service = new CashDrawerService(new FakeReceiptPrinterSettingsStore(), driver);

        var result = await service.OpenAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("sdk missing", result.Message);
    }

    [Fact]
    public async Task Receipt_printer_settings_store_persists_fields_and_defaults_port()
    {
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository);
        var settings = ReceiptPrinterSettings.Default with
        {
            PrinterPort = "   ",
            BrandName = "HB",
            StoreName = "Sunnybank",
            StoreAddress = "Shop 1",
            StorePhone = "07",
            Abn = "ABN",
            ReturnPolicy = "Return within 7 days",
            CutDistance = 80
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal("USB,", loaded.PrinterPort);
        Assert.Equal("HB", loaded.BrandName);
        Assert.Equal("Sunnybank", loaded.StoreName);
        Assert.Equal("Shop 1", loaded.StoreAddress);
        Assert.Equal("07", loaded.StorePhone);
        Assert.Equal("ABN", loaded.Abn);
        Assert.Equal("Return within 7 days", loaded.ReturnPolicy);
        Assert.Equal(80, loaded.CutDistance);
    }

    private static ReceiptDetails CreateReceipt(Guid orderGuid, decimal? tenderedAmount = null, decimal? changeAmount = null)
    {
        return new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero),
            9.20m,
            0.20m,
            9.00m,
            [
                new ReceiptPreviewLine("Organic Gala Apples", "690101", 2m, 2.50m, 0m, 5.00m),
                new ReceiptPreviewLine("Whole Grain Bread", "690102", 1m, 4.20m, 0.20m, 4.00m)
            ],
            [
                new ReceiptPaymentLine(
                    PaymentMethodKind.Card,
                    9.00m,
                    "ANZ:123",
                    [
                        new CardTransactionDto(
                            "Linkly",
                            "TXN-1",
                            "AUTH1",
                            "VISA",
                            411111,
                            "****1111",
                            "M1",
                            "00",
                            "APPROVED",
                            "123456",
                            new DateTimeOffset(2026, 5, 27, 9, 1, 0, TimeSpan.Zero),
                            9.00m,
                            "APPROVED CARD RECEIPT")
                    ])
            ],
            tenderedAmount,
            changeAmount);
    }

    private sealed class FakeReceiptQueryService : IReceiptQueryService
    {
        public ReceiptDetails? LatestReceipt { get; init; }

        public Dictionary<Guid, ReceiptDetails> Receipts { get; } = [];

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            if (Receipts.TryGetValue(orderGuid, out var receipt))
            {
                return Task.FromResult<ReceiptDetails?>(receipt);
            }

            return Task.FromResult(LatestReceipt?.OrderGuid == orderGuid ? LatestReceipt : null);
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LatestReceipt);
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

        public ReceiptPrinterSettings? LastCashDrawerSettings { get; private set; }

        public ReceiptPrinterDriverResult PrintResult { get; init; } = new(true, "printed");

        public ReceiptPrinterDriverResult OpenCashDrawerResult { get; init; } = new(true, "drawer opened");

        public Exception? OpenCashDrawerException { get; init; }

        public int OpenCashDrawerCallCount { get; private set; }

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
            OpenCashDrawerCallCount++;
            LastCashDrawerSettings = settings;
            if (OpenCashDrawerException is not null)
            {
                throw OpenCashDrawerException;
            }

            return Task.FromResult(OpenCashDrawerResult);
        }
    }

    private sealed class DelayedReceiptPrinterDriver : IReceiptPrinterDriver
    {
        private readonly object _gate = new();
        private int _activePrints;

        public int PrintCount { get; private set; }

        public int MaxConcurrentPrints { get; private set; }

        public async Task<ReceiptPrinterDriverResult> PrintAsync(
            ReceiptPrintDocument document,
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activePrints);
            lock (_gate)
            {
                PrintCount++;
                MaxConcurrentPrints = Math.Max(MaxConcurrentPrints, active);
            }

            try
            {
                await Task.Delay(40, cancellationToken);
                return new ReceiptPrinterDriverResult(true, "printed");
            }
            finally
            {
                Interlocked.Decrement(ref _activePrints);
            }
        }

        public Task<ReceiptPrinterDriverResult> TestAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "tested"));
        }

        public Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "drawer opened"));
        }
    }

    private sealed class InMemorySettingsRepository : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
