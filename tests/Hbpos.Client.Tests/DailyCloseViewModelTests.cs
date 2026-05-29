using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class DailyCloseViewModelTests
{
    [Fact]
    public void Constructor_defaults_to_today_and_builds_all_denominations()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());

        Assert.Equal(DateTime.Today, viewModel.SelectedDate);
        Assert.Equal(11, viewModel.Denominations.Count);
        Assert.Equal("$100", viewModel.Denominations.First().Label);
        Assert.Equal("5c", viewModel.Denominations.Last().Label);
    }

    [Fact]
    public void ApplyDenominationCommand_replaces_count_and_clears_keypad_buffer()
    {
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), new FakeDailyClosePrintService(), CreateSession());
        var denomination = viewModel.Denominations.Single(item => item.Label == "$50");

        viewModel.KeypadInputCommand.Execute("1");
        viewModel.KeypadInputCommand.Execute("2");
        viewModel.ApplyDenominationCommand.Execute(denomination);

        Assert.Equal(12, denomination.Count);
        Assert.Equal(600m, denomination.Subtotal);
        Assert.Equal(string.Empty, viewModel.KeypadBuffer);
        Assert.Equal(600m, viewModel.NoteSubtotal);
        Assert.Equal(600m, viewModel.CountedCashAmount);
    }

    [Fact]
    public async Task RefreshSummaryCommand_loads_report_payments_and_archives()
    {
        var service = new FakeDailyCloseService();
        var viewModel = new DailyCloseViewModel(service, new FakeDailyClosePrintService(), CreateSession());

        await viewModel.RefreshSummaryCommand.ExecuteAsync(null);

        Assert.Equal(DateTime.Today, service.LastRequestedDate);
        Assert.Equal(145.35m, viewModel.ExpectedCashAmount);
        Assert.Equal(980.50m, viewModel.GrossAmount);
        Assert.Equal(955.20m, viewModel.NetAmount);
        Assert.Equal(18, viewModel.TransactionCount);
        Assert.Equal(25.30m, viewModel.RefundAmount);
        Assert.Equal(2m, viewModel.ReturnQuantity);
        Assert.Collection(
            viewModel.PaymentSummaries,
            item =>
            {
                Assert.Equal("Cash", item.Label);
                Assert.Equal(145.35m, item.NetAmount);
                Assert.Equal(6, item.TransactionCount);
            },
            item =>
            {
                Assert.Equal("Card", item.Label);
                Assert.Equal(809.85m, item.NetAmount);
                Assert.Equal(12, item.TransactionCount);
            },
            item =>
            {
                Assert.Equal("Voucher", item.Label);
                Assert.Equal(0m, item.NetAmount);
            });
        Assert.Single(viewModel.Archives);
        Assert.NotNull(viewModel.SelectedArchive);
    }

    [Fact]
    public async Task SaveAndPrintCommand_saves_prints_and_refreshes_archive_list()
    {
        var service = new FakeDailyCloseService();
        var printService = new FakeDailyClosePrintService();
        var viewModel = new DailyCloseViewModel(service, printService, CreateSession());
        var note = viewModel.Denominations.Single(item => item.Label == "$20");

        await viewModel.RefreshSummaryCommand.ExecuteAsync(null);
        viewModel.KeypadInputCommand.Execute("3");
        viewModel.ApplyDenominationCommand.Execute(note);

        await viewModel.SaveAndPrintCommand.ExecuteAsync(null);

        Assert.Equal(DateTime.Today, service.LastSavedDate);
        Assert.NotNull(service.LastSavedCashCounts);
        Assert.Contains(service.LastSavedCashCounts!, item => item.Label == "$20" && item.Quantity == 3);
        Assert.Equal(1, printService.PrintCallCount);
        Assert.Equal(2, viewModel.Archives.Count);
        Assert.Equal("Daily close saved and sent to printer.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task LoadHistoryCommand_selects_archive_and_builds_preview_cash_detail()
    {
        var printService = new FakeDailyClosePrintService();
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), printService, CreateSession());

        await viewModel.LoadHistoryCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Archives);
        Assert.NotNull(viewModel.SelectedArchive);
        Assert.Equal(5, viewModel.SelectedArchiveNoteCounts.Count);
        Assert.Equal(6, viewModel.SelectedArchiveCoinCounts.Count);
        Assert.Contains(viewModel.SelectedArchiveNoteCounts, count => count.Label == "$100" && count.Quantity == 0);
        Assert.Contains(viewModel.SelectedArchiveCoinCounts, count => count.Label == "5c" && count.Quantity == 0);
        Assert.Contains(viewModel.ArchivePreviewRows, row => row.Text == "==== DAILY CLOSE REPRINT ====");
        Assert.Equal(1, printService.BuildDocumentCallCount);
    }

    [Fact]
    public async Task ReprintSelectedArchiveCommand_prints_selected_archive_as_reprint()
    {
        var printService = new FakeDailyClosePrintService();
        var viewModel = new DailyCloseViewModel(new FakeDailyCloseService(), printService, CreateSession());

        await viewModel.LoadHistoryCommand.ExecuteAsync(null);
        await viewModel.ReprintSelectedArchiveCommand.ExecuteAsync(null);

        Assert.Equal(1, printService.PrintCallCount);
        Assert.Equal(ReceiptPrintReason.Reprint, printService.LastPrintReason);
        Assert.Equal(viewModel.SelectedArchive!.Archive, printService.LastPrintedArchive);
        Assert.Equal("Daily close archive sent to printer.", viewModel.StatusMessage);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);
    }

    private sealed class FakeDailyCloseService : IDailyCloseService
    {
        private readonly List<DailyCloseArchive> _archives = [];

        public FakeDailyCloseService()
        {
            _archives.Add(CreateArchive(140m, -1.25m, new DateTimeOffset(2026, 5, 27, 17, 35, 0, TimeSpan.Zero)));
        }

        public IReadOnlyList<CashDenomination> Denominations => DailyCloseService.AustralianDenominations;

        public DateTime LastRequestedDate { get; private set; }

        public DateTime LastSavedDate { get; private set; }

        public IReadOnlyList<CashDenominationCount>? LastSavedCashCounts { get; private set; }

        public Task<DailyCloseReport> LoadReportAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            LastRequestedDate = businessDate;
            return Task.FromResult(CreateReport());
        }

        public Task<DailyCloseArchive> SaveAsync(
            PosSessionState session,
            DateTime businessDate,
            IReadOnlyList<CashDenominationCount> cashCounts,
            CancellationToken cancellationToken = default)
        {
            LastSavedDate = businessDate;
            LastSavedCashCounts = cashCounts;
            var counted = cashCounts.Sum(count => count.Amount);
            var archive = CreateArchive(counted, counted - CreateReport().SystemCashAmount, new DateTimeOffset(2026, 5, 28, 18, 0, 0, TimeSpan.Zero));
            _archives.Insert(0, archive);
            return Task.FromResult(archive);
        }

        public Task<IReadOnlyList<DailyCloseArchive>> GetArchivesAsync(
            PosSessionState session,
            DateTime businessDate,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<DailyCloseArchive>>(_archives.ToArray());
        }

        private static DailyCloseReport CreateReport()
        {
            return new DailyCloseReport(
                DateTime.Today,
                new DateTimeOffset(DateTime.Today),
                new DateTimeOffset(DateTime.Today.AddDays(1)),
                "S001",
                "POS-01",
                "C001",
                "Alice",
                18,
                [
                    new DailyClosePaymentSummary(PaymentMethodKind.Cash, 170.65m, 25.30m, 145.35m, 6),
                    new DailyClosePaymentSummary(PaymentMethodKind.Card, 809.85m, 0m, 809.85m, 12),
                    new DailyClosePaymentSummary(PaymentMethodKind.Voucher, 0m, 0m, 0m, 0)
                ],
                25.30m,
                2m);
        }

        private static DailyCloseArchive CreateArchive(decimal countedCashAmount, decimal cashDifference, DateTimeOffset savedAt)
        {
            return new DailyCloseArchive(
                Guid.NewGuid(),
                CreateReport(),
                DailyCloseService.AustralianDenominations
                    .Select(denomination => new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, 0))
                    .ToArray(),
                savedAt,
                countedCashAmount,
                0m,
                countedCashAmount,
                cashDifference);
        }
    }

    private sealed class FakeDailyClosePrintService : IDailyClosePrintService
    {
        public int BuildDocumentCallCount { get; private set; }

        public int PrintCallCount { get; private set; }

        public DailyCloseArchive? LastPrintedArchive { get; private set; }

        public ReceiptPrintReason? LastPrintReason { get; private set; }

        public Task<ReceiptPrintDocument> BuildDocumentAsync(
            DailyCloseArchive archive,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            BuildDocumentCallCount++;
            return Task.FromResult(DailyCloseTextFormatter.Build(archive, ReceiptPrinterSettings.Default, reason));
        }

        public Task<ReceiptPrintResult> PrintAsync(
            DailyCloseArchive archive,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            PrintCallCount++;
            LastPrintedArchive = archive;
            LastPrintReason = reason;
            return Task.FromResult(new ReceiptPrintResult(true, "printed"));
        }
    }
}
