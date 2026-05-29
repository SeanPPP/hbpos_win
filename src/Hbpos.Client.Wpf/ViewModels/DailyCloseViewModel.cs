using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class DailyCloseViewModel : ObservableObject
{
    private readonly IDailyCloseService _dailyCloseService;
    private readonly IDailyClosePrintService _dailyClosePrintService;
    private readonly ILocalizationService? _localization;
    private readonly Action? _returnToPos;
    private DailyCloseReport? _currentReport;
    private int _archivePreviewVersion;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private DateTime? _selectedDate = DateTime.Today;

    [ObservableProperty]
    private string _keypadBuffer = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private decimal _expectedCashAmount;

    [ObservableProperty]
    private decimal _grossAmount;

    [ObservableProperty]
    private decimal _netAmount;

    [ObservableProperty]
    private decimal _refundAmount;

    [ObservableProperty]
    private decimal _returnQuantity;

    [ObservableProperty]
    private int _transactionCount;

    private DailyCloseArchiveListItemViewModel? _selectedArchive;

    public DailyCloseViewModel(
        IDailyCloseService dailyCloseService,
        IDailyClosePrintService dailyClosePrintService,
        PosSessionState session,
        ILocalizationService? localization = null,
        Action? returnToPos = null)
    {
        _dailyCloseService = dailyCloseService;
        _dailyClosePrintService = dailyClosePrintService;
        _session = session;
        _localization = localization;
        _returnToPos = returnToPos;

        foreach (var denomination in _dailyCloseService.Denominations)
        {
            var entry = new CashDenominationEntryViewModel(denomination.Value, denomination.Label, denomination.Kind);
            entry.PropertyChanged += OnDenominationChanged;
            Denominations.Add(entry);
        }

        RefreshSummaryCommand = new AsyncRelayCommand(RefreshSummaryAsync, () => !IsBusy);
        SaveAndPrintCommand = new AsyncRelayCommand(SaveAndPrintAsync, CanSaveAndPrint);
        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, () => !IsBusy);
        ReprintSelectedArchiveCommand = new AsyncRelayCommand(ReprintSelectedArchiveAsync, CanReprintSelectedArchive);
        KeypadInputCommand = new RelayCommand<string>(AppendKeypadInput, _ => !IsBusy);
        KeypadBackspaceCommand = new RelayCommand(BackspaceKeypad, () => !IsBusy && KeypadBuffer.Length > 0);
        KeypadClearCommand = new RelayCommand(ClearKeypad, () => !IsBusy && KeypadBuffer.Length > 0);
        ApplyDenominationCommand = new RelayCommand<CashDenominationEntryViewModel>(ApplyDenominationCount, CanApplyDenominationCount);
        ReturnToPosCommand = new RelayCommand(() => _returnToPos?.Invoke(), () => _returnToPos is not null);
        StatusMessage = T("dailyClose.status.ready", "Select a business date and refresh the summary.");
    }

    public ObservableCollection<CashDenominationEntryViewModel> Denominations { get; } = [];

    public ObservableCollection<DailyClosePaymentSummaryItemViewModel> PaymentSummaries { get; } = [];

    public ObservableCollection<DailyCloseArchiveListItemViewModel> Archives { get; } = [];

    public ObservableCollection<ReceiptPreviewRow> ArchivePreviewRows { get; } = [];

    public ObservableCollection<CashDenominationCount> SelectedArchiveNoteCounts { get; } = [];

    public ObservableCollection<CashDenominationCount> SelectedArchiveCoinCounts { get; } = [];

    public IAsyncRelayCommand RefreshSummaryCommand { get; }

    public IAsyncRelayCommand SaveAndPrintCommand { get; }

    public IAsyncRelayCommand LoadHistoryCommand { get; }

    public IAsyncRelayCommand ReprintSelectedArchiveCommand { get; }

    public IRelayCommand<string> KeypadInputCommand { get; }

    public IRelayCommand KeypadBackspaceCommand { get; }

    public IRelayCommand KeypadClearCommand { get; }

    public IRelayCommand<CashDenominationEntryViewModel> ApplyDenominationCommand { get; }

    public IRelayCommand ReturnToPosCommand { get; }

    public IEnumerable<CashDenominationEntryViewModel> NoteDenominations => Denominations.Where(item => item.Kind == CashDenominationKind.Note);

    public IEnumerable<CashDenominationEntryViewModel> CoinDenominations => Denominations.Where(item => item.Kind == CashDenominationKind.Coin);

    public decimal NoteSubtotal => NoteDenominations.Sum(item => item.Subtotal);

    public decimal CoinSubtotal => CoinDenominations.Sum(item => item.Subtotal);

    public decimal CountedCashAmount => NoteSubtotal + CoinSubtotal;

    public decimal CashDifference => CountedCashAmount - ExpectedCashAmount;

    public string BusinessDateText => BusinessDate.ToString("ddd, dd MMM yyyy", CultureInfo.CurrentCulture);

    public DailyCloseArchiveListItemViewModel? SelectedArchive
    {
        get => _selectedArchive;
        set
        {
            if (SetProperty(ref _selectedArchive, value))
            {
                ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
                _ = ApplySelectedArchiveAsync(value, CancellationToken.None);
            }
        }
    }

    private DateTime BusinessDate => (SelectedDate ?? DateTime.Today).Date;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return RefreshSummaryAsync(cancellationToken);
    }

    public async Task RefreshSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.refreshing", "Refreshing daily close summary...");

        try
        {
            var report = await _dailyCloseService.LoadReportAsync(Session, BusinessDate, cancellationToken);
            ApplyReport(report);
            await RefreshArchivesAsync(cancellationToken);
            StatusMessage = T("dailyClose.status.refreshed", "Daily close summary refreshed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveAndPrintAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSaveAndPrint())
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.saving", "Saving and printing daily close...");

        try
        {
            var archive = await _dailyCloseService.SaveAsync(Session, BusinessDate, BuildCashCounts(), cancellationToken);
            _currentReport = archive.Report;
            var printResult = await _dailyClosePrintService.PrintAsync(archive, ReceiptPrintReason.Manual, cancellationToken);
            await RefreshArchivesAsync(cancellationToken);
            StatusMessage = printResult.Succeeded
                ? T("dailyClose.status.savedPrinted", "Daily close saved and sent to printer.")
                : Format(
                    "dailyClose.status.savedPrintFailed",
                    "Daily close saved, but printing failed: {0}",
                    printResult.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.historyLoading", "Loading daily close history...");

        try
        {
            await RefreshArchivesAsync(cancellationToken);
            StatusMessage = Archives.Count == 0
                ? T("dailyClose.status.historyEmpty", "No daily close archives found for this business date.")
                : Format(
                    "dailyClose.status.historyLoaded",
                    "Loaded {0} daily close archive(s).",
                    Archives.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectArchiveAsync(
        DailyCloseArchiveListItemViewModel? archive,
        CancellationToken cancellationToken = default)
    {
        if (SetProperty(ref _selectedArchive, archive, nameof(SelectedArchive)))
        {
            ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
        }

        await ApplySelectedArchiveAsync(archive, cancellationToken);
    }

    private async Task ReprintSelectedArchiveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanReprintSelectedArchive())
        {
            return;
        }

        IsBusy = true;
        StatusMessage = T("dailyClose.status.reprinting", "Reprinting daily close archive...");

        try
        {
            var result = await _dailyClosePrintService.PrintAsync(SelectedArchive!.Archive, ReceiptPrintReason.Reprint, cancellationToken);
            StatusMessage = result.Succeeded
                ? T("dailyClose.status.reprintPrinted", "Daily close archive sent to printer.")
                : Format(
                    "dailyClose.status.reprintFailed",
                    "Daily close reprint failed: {0}",
                    result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedDateChanged(DateTime? value)
    {
        _currentReport = null;
        PaymentSummaries.Clear();
        Archives.Clear();
        SelectedArchive = null;
        ArchivePreviewRows.Clear();
        SelectedArchiveNoteCounts.Clear();
        SelectedArchiveCoinCounts.Clear();
        ClearCashCounts();
        ExpectedCashAmount = 0m;
        GrossAmount = 0m;
        NetAmount = 0m;
        RefundAmount = 0m;
        ReturnQuantity = 0m;
        TransactionCount = 0;
        OnPropertyChanged(nameof(BusinessDateText));
        StatusMessage = Format(
            "dailyClose.status.dateChanged",
            "Switched to {0:yyyy-MM-dd}. Refresh the summary.",
            BusinessDate);
        SaveAndPrintCommand.NotifyCanExecuteChanged();
    }

    partial void OnKeypadBufferChanged(string value)
    {
        KeypadBackspaceCommand.NotifyCanExecuteChanged();
        KeypadClearCommand.NotifyCanExecuteChanged();
        ApplyDenominationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshSummaryCommand.NotifyCanExecuteChanged();
        SaveAndPrintCommand.NotifyCanExecuteChanged();
        LoadHistoryCommand.NotifyCanExecuteChanged();
        ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
        KeypadInputCommand.NotifyCanExecuteChanged();
        KeypadBackspaceCommand.NotifyCanExecuteChanged();
        KeypadClearCommand.NotifyCanExecuteChanged();
        ApplyDenominationCommand.NotifyCanExecuteChanged();
    }

    partial void OnExpectedCashAmountChanged(decimal value)
    {
        OnPropertyChanged(nameof(CashDifference));
    }

    private void ApplyReport(DailyCloseReport report)
    {
        _currentReport = report;
        PaymentSummaries.ReplaceWith(report.PaymentSummaries.Select(summary => new DailyClosePaymentSummaryItemViewModel(
            summary.MethodLabel,
            summary.SalesAmount,
            summary.RefundAmount,
            summary.NetAmount,
            summary.TransactionCount)));
        ExpectedCashAmount = report.SystemCashAmount;
        GrossAmount = report.SalesAmount;
        NetAmount = report.NetAmount;
        RefundAmount = report.RefundAmount;
        ReturnQuantity = report.ReturnQuantity;
        TransactionCount = report.OrderCount;
        RaiseCashTotalsChanged();
    }

    private async Task RefreshArchivesAsync(CancellationToken cancellationToken)
    {
        var selectedArchiveGuid = SelectedArchive?.DailyCloseGuid;
        var archives = await _dailyCloseService.GetArchivesAsync(Session, BusinessDate, cancellationToken);
        var items = archives.Select(archive => new DailyCloseArchiveListItemViewModel(archive)).ToList();
        Archives.ReplaceWith(items);

        var selected = items.FirstOrDefault(item => item.DailyCloseGuid == selectedArchiveGuid) ?? items.FirstOrDefault();
        await SelectArchiveAsync(selected, cancellationToken);
    }

    private bool CanSaveAndPrint()
    {
        return !IsBusy && _currentReport is not null;
    }

    private bool CanReprintSelectedArchive()
    {
        return !IsBusy && SelectedArchive is not null;
    }

    private async Task ApplySelectedArchiveAsync(
        DailyCloseArchiveListItemViewModel? selectedArchive,
        CancellationToken cancellationToken)
    {
        var previewVersion = Interlocked.Increment(ref _archivePreviewVersion);
        ArchivePreviewRows.Clear();
        SelectedArchiveNoteCounts.Clear();
        SelectedArchiveCoinCounts.Clear();

        if (selectedArchive is null)
        {
            ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
            return;
        }

        var normalizedCounts = NormalizeCashCounts(selectedArchive.Archive.CashCounts);
        SelectedArchiveNoteCounts.ReplaceWith(normalizedCounts.Where(count => count.Kind == CashDenominationKind.Note));
        SelectedArchiveCoinCounts.ReplaceWith(normalizedCounts.Where(count => count.Kind == CashDenominationKind.Coin));

        try
        {
            var document = await _dailyClosePrintService.BuildDocumentAsync(selectedArchive.Archive, ReceiptPrintReason.Reprint, cancellationToken);
            if (previewVersion != _archivePreviewVersion)
            {
                return;
            }

            ArchivePreviewRows.ReplaceWith(document.PreviewRows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (previewVersion == _archivePreviewVersion)
            {
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            ReprintSelectedArchiveCommand.NotifyCanExecuteChanged();
        }
    }

    private void AppendKeypadInput(string? input)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(input) || !input.All(char.IsDigit))
        {
            return;
        }

        KeypadBuffer += input;
    }

    private void BackspaceKeypad()
    {
        if (KeypadBuffer.Length > 0)
        {
            KeypadBuffer = KeypadBuffer[..^1];
        }
    }

    private void ClearKeypad()
    {
        KeypadBuffer = string.Empty;
    }

    private bool CanApplyDenominationCount(CashDenominationEntryViewModel? denomination)
    {
        return !IsBusy && denomination is not null && !string.IsNullOrWhiteSpace(KeypadBuffer);
    }

    private void ApplyDenominationCount(CashDenominationEntryViewModel? denomination)
    {
        if (denomination is null || !int.TryParse(KeypadBuffer, out var count) || count < 0)
        {
            return;
        }

        denomination.Count = count;
        KeypadBuffer = string.Empty;
        RaiseCashTotalsChanged();
    }

    private IReadOnlyList<CashDenominationCount> BuildCashCounts()
    {
        return Denominations
            .Select(item => new CashDenominationCount(item.Value, item.Label, item.Kind, item.Count))
            .ToList();
    }

    private static IReadOnlyList<CashDenominationCount> NormalizeCashCounts(IReadOnlyList<CashDenominationCount> cashCounts)
    {
        return DailyCloseService.AustralianDenominations
            .Select(denomination =>
            {
                var count = cashCounts.FirstOrDefault(item => item.Kind == denomination.Kind && item.Value == denomination.Value);
                return count ?? new CashDenominationCount(denomination.Value, denomination.Label, denomination.Kind, 0);
            })
            .ToList();
    }

    private void ClearCashCounts()
    {
        foreach (var denomination in Denominations)
        {
            denomination.Count = 0;
        }

        KeypadBuffer = string.Empty;
        RaiseCashTotalsChanged();
    }

    private void OnDenominationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CashDenominationEntryViewModel.Subtotal))
        {
            RaiseCashTotalsChanged();
        }
    }

    private void RaiseCashTotalsChanged()
    {
        OnPropertyChanged(nameof(NoteSubtotal));
        OnPropertyChanged(nameof(CoinSubtotal));
        OnPropertyChanged(nameof(CountedCashAmount));
        OnPropertyChanged(nameof(CashDifference));
        SaveAndPrintCommand.NotifyCanExecuteChanged();
    }

    private string T(string key, string fallback)
    {
        return _localization?.T(key) ?? fallback;
    }

    private string Format(string key, string fallback, params object[] args)
    {
        return string.Format(
            _localization?.CurrentCulture ?? CultureInfo.CurrentCulture,
            _localization?.T(key) ?? fallback,
            args);
    }
}

public sealed partial class CashDenominationEntryViewModel : ObservableObject
{
    public CashDenominationEntryViewModel(decimal value, string label, CashDenominationKind kind)
    {
        Value = value;
        Label = label;
        Kind = kind;
    }

    [ObservableProperty]
    private int _count;

    public decimal Value { get; }

    public string Label { get; }

    public CashDenominationKind Kind { get; }

    public bool IsCoin => Kind == CashDenominationKind.Coin;

    public decimal Amount => Value;

    public decimal Subtotal => decimal.Round(Value * Count, 2, MidpointRounding.AwayFromZero);

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(Subtotal));
    }
}

public sealed record DailyClosePaymentSummaryItemViewModel(
    string Label,
    decimal SalesAmount,
    decimal RefundAmount,
    decimal NetAmount,
    int TransactionCount);

public sealed record DailyCloseArchiveListItemViewModel(DailyCloseArchive Archive)
{
    public Guid DailyCloseGuid => Archive.DailyCloseGuid;

    public DateTimeOffset SavedAt => Archive.SavedAt;

    public string OperatorName => Archive.Report.CashierName;

    public decimal CountedCashAmount => Archive.CountedCashAmount;

    public decimal CashDifference => Archive.CashDifference;

    public string ClosedAtDisplay => SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}
