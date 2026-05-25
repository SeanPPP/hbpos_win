using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class SpecialProductsViewModel : ObservableObject, IScannerInputTarget, IDisposable
{
    public const string PageId = "SpecialProducts";

    public string ScannerPageId => PageId;

    private const int PageSize = 20;

    private readonly ISpecialProductsWorkflowService _workflowService;
    private readonly ILocalizationService _localization;
    private readonly Action _onBack;
    private readonly Action<CartLine>? _onCartLineAdded;
    private readonly IRawScannerService? _rawScannerService;
    private readonly object _specialItemsGate = new();

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isDownloadProgressVisible;

    [ObservableProperty]
    private bool _isDownloadProgressFailed;

    [ObservableProperty]
    private double _downloadProgressValue;

    [ObservableProperty]
    private string _downloadProgressText = string.Empty;

    [ObservableProperty]
    private string _downloadProgressDetailText = string.Empty;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private SellableItemDto? _selectedSpecialItem;

    [ObservableProperty]
    private SellableItemDto? _selectedSearchResult;

    public SpecialProductsViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        ILocalCatalogRepository catalogRepository,
        ISpecialProductService specialProductService,
        PosSessionState session,
        ILocalizationService localization,
        Action onBack,
        Action<CartLine>? onCartLineAdded = null,
        ISpecialProductsWorkflowService? workflowService = null,
        IRawScannerService? rawScannerService = null)
    {
        _workflowService = workflowService ?? new SpecialProductsWorkflowService(
            priceIndex,
            cart,
            catalogRepository,
            specialProductService);
        _session = session;
        _localization = localization;
        _onBack = onBack;
        _onCartLineAdded = onCartLineAdded;
        _rawScannerService = rawScannerService;

        BackCommand = new RelayCommand(_onBack);
        SearchCommand = new RelayCommand(SearchCatalog);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => !string.IsNullOrWhiteSpace(SearchText));
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        DownloadCommand = new AsyncRelayCommand(DownloadSpecialProductsAsync, CanDownloadSpecialProducts);
        ToggleEditModeCommand = new RelayCommand(ToggleEditMode, () => !IsBusy);
        PreviousPageCommand = new RelayCommand(ShowPreviousPage, CanShowPreviousPage);
        NextPageCommand = new RelayCommand(ShowNextPage, CanShowNextPage);
        AddToCartCommand = new RelayCommand<SellableItemDto>(AddToCart);
        SpecialItemCardCommand = new RelayCommand<SellableItemDto>(HandleSpecialItemCard);
        AddSpecialProductCommand = new AsyncRelayCommand<SellableItemDto>(AddSpecialProductAsync, CanMutateItem);
        RemoveSpecialProductCommand = new AsyncRelayCommand<SellableItemDto>(RemoveSpecialProductAsync, CanMutateItem);
        MoveUpCommand = new AsyncRelayCommand<SellableItemDto>(item => MoveSpecialProductAsync(item, -1), CanMoveUp);
        MoveDownCommand = new AsyncRelayCommand<SellableItemDto>(item => MoveSpecialProductAsync(item, 1), CanMoveDown);

        _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);
        StatusMessage = T("specialProducts.status.ready");
    }

    public ObservableCollection<SellableItemDto> SpecialItems { get; } = [];

    public ObservableCollection<SellableItemDto> PagedSpecialItems { get; } = [];

    public ObservableCollection<SellableItemDto> SearchResults { get; } = [];

    public IRelayCommand BackCommand { get; }

    public IRelayCommand SearchCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand DownloadCommand { get; }

    public IRelayCommand ToggleEditModeCommand { get; }

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    public IRelayCommand<SellableItemDto> AddToCartCommand { get; }

    public IRelayCommand<SellableItemDto> SpecialItemCardCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> AddSpecialProductCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> RemoveSpecialProductCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> MoveUpCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> MoveDownCommand { get; }

    public string TitleText => T("specialProducts.title");

    public string SubtitleText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.subtitle"),
        Session.StoreName,
        Session.StoreCode);

    public string BackText => T("specialProducts.back");

    public string SearchPlaceholderText => T("specialProducts.search.placeholder");

    public string SearchButtonText => T("specialProducts.search.action");

    public string ClearSearchText => T("Clear");

    public string RefreshText => T("specialProducts.refresh");

    public string DownloadText => T("specialProducts.download");

    public string EditModeText => T(IsEditMode ? "specialProducts.done" : "specialProducts.edit");

    public string PreviousPageText => T("specialProducts.previousPage");

    public string NextPageText => T("specialProducts.nextPage");

    public string AddText => T("specialProducts.add");

    public string RemoveText => T("specialProducts.remove");

    public string MoveUpText => T("specialProducts.moveUp");

    public string MoveDownText => T("specialProducts.moveDown");

    public string TapToAddText => T("specialProducts.tapToAdd");

    public string SearchResultsText => T("specialProducts.search.results");

    public string EmptyText => T("specialProducts.empty");

    public string NoSearchResultsText => T("specialProducts.search.empty");

    public string OnlineStateText => T(Session.IsOnline ? "pos.status.online" : "pos.status.offline");

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool IsSpecialListEmpty => SpecialItems.Count == 0;

    public string SelectedSpecialItemText => SelectedSpecialItem?.DisplayName ?? T("specialProducts.edit.noSelection");

    public int TotalPages => Math.Max(1, (SpecialItems.Count + PageSize - 1) / PageSize);

    public string PageStatusText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.pageStatus"),
        CurrentPage,
        TotalPages,
        SpecialItems.Count);

    public Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        return PreloadCoreAsync(cancellationToken);
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        var shouldResetBusy = !IsBusy;
        if (shouldResetBusy)
        {
            IsBusy = true;
        }

        try
        {
            var result = await _workflowService.EnsureLoadedAsync(Session.StoreCode, cancellationToken);
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: SpecialItems.Count == 0);
        }
        catch (Exception ex)
        {
            SetStatusText(string.Format(_localization.CurrentCulture, T("specialProducts.status.loadFailed"), ex.Message));
        }
        finally
        {
            if (shouldResetBusy)
            {
                IsBusy = false;
            }
        }
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadAsyncCore(cancellationToken);
    }

    public void ActivateForEntry()
    {
        IsEditMode = false;
        ClearSearch();
        SelectedSpecialItem = null;
        RefreshPagedSpecialItems(resetToFirstPage: true);
    }

    private async Task PreloadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _workflowService.PreloadAsync(Session.StoreCode, cancellationToken);
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: true);
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("SpecialProducts", $"preload failed store={Session.StoreCode} error={ex.Message}");
        }
    }

    private async Task LoadAsyncCore(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            Log($"load skipped store={Session.StoreCode} reason=busy forceReload=true");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _workflowService.LoadAsync(Session.StoreCode, cancellationToken);
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: true);
            SetStatus("specialProducts.status.loaded", SpecialItems.Count);
        }
        catch (Exception ex)
        {
            SetStatusText(string.Format(_localization.CurrentCulture, T("specialProducts.status.loadFailed"), ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(OnlineStateText));
        RefreshCommandStates();
    }

    partial void OnSearchTextChanged(string value)
    {
        ClearSearchCommand.NotifyCanExecuteChanged();
        if (string.IsNullOrWhiteSpace(value))
        {
            SearchResults.Clear();
            SelectedSearchResult = null;
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommandStates();
    }

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(EditModeText));
        if (!value)
        {
            ClearSearch();
            SelectedSpecialItem = null;
        }

        RefreshCommandStates();
    }

    partial void OnSelectedSpecialItemChanged(SellableItemDto? value)
    {
        OnPropertyChanged(nameof(SelectedSpecialItemText));
        RefreshCommandStates();
    }

    public bool ProcessScannerBarcode(string barcode, string devicePath, string source)
    {
        var normalizedBarcode = barcode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBarcode))
        {
            Log($"operation=scanner store={Session.StoreCode} source={source} device={devicePath} success=false reason=empty-barcode");
            return true;
        }

        if (!IsEditMode)
        {
            SetStatus("specialProducts.status.scanRequiresEdit");
            Log($"operation=scanner store={Session.StoreCode} source={source} device={devicePath} barcode={normalizedBarcode} consumed=true searched=false reason=edit-mode-off");
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        SearchText = normalizedBarcode;
        SearchCatalog();
        stopwatch.Stop();

        var selected = SelectedSearchResult;
        if (selected is not null)
        {
            SetStatus("specialProducts.status.scanConfirm", selected.DisplayName);
        }

        Log($"operation=scanner store={Session.StoreCode} source={source} device={devicePath} barcode={normalizedBarcode} consumed=true searched=true results={SearchResults.Count} selectedProductCode={selected?.ProductCode ?? "<none>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return true;
    }

    private void SearchCatalog()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchResults.Clear();
            SelectedSearchResult = null;
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }

        var results = _workflowService.Search(Session.StoreCode, SearchText);
        SearchResults.ReplaceWith(results.Items);
        SelectedSearchResult = SearchResults.FirstOrDefault();
        OnPropertyChanged(nameof(HasSearchResults));
        SetStatus(results.Items.Count == 0
            ? "specialProducts.status.noSearchResults"
            : "specialProducts.status.searchResults",
            results.Items.Count);
    }

    private void ClearSearch()
    {
        SearchText = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private void HandleSpecialItemCard(SellableItemDto? item)
    {
        if (item is null)
        {
            return;
        }

        if (IsEditMode)
        {
            SelectedSpecialItem = item;
            SetStatus("specialProducts.status.selectedForEdit", item.DisplayName);
            return;
        }

        AddToCart(item);
    }

    private void AddToCart(SellableItemDto? item)
    {
        if (item is null)
        {
            Log($"operation=add-to-cart store={Session.StoreCode} success=false reason=null-item totalElapsedMs=0");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = _workflowService.AddToCart(item);
            SetStatus("specialProducts.status.addedToCart", item.DisplayName);
            _onBack();
            _onCartLineAdded?.Invoke(result.Line);
            stopwatch.Stop();
            Log($"operation=add-to-cart store={Session.StoreCode} productCode={item.ProductCode} lookupCode={item.LookupCode} success=true revealRequested={_onCartLineAdded is not null} cartLines={result.CartLineCount} totalElapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"operation=add-to-cart store={Session.StoreCode} productCode={item.ProductCode} lookupCode={item.LookupCode} success=false totalElapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    private async Task DownloadSpecialProductsAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || !EnsureOnlineMutationAllowed())
        {
            return;
        }

        IsBusy = true;
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            Log($"operation=download store={Session.StoreCode} stage=start");
            ApplyDownloadProgress(new SpecialProductDownloadProgress(
                Session.StoreCode,
                SpecialProductDownloadProgressStage.Preparing,
                0,
                0,
                0,
                0,
                0,
                0,
                0));

            var progress = new Progress<SpecialProductDownloadProgress>(ApplyDownloadProgress);
            var serviceStopwatch = Stopwatch.StartNew();
            var result = await _workflowService.DownloadAsync(
                Session.StoreCode,
                cancellationToken,
                progress);
            serviceStopwatch.Stop();

            var replaceStopwatch = Stopwatch.StartNew();
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: true);
            replaceStopwatch.Stop();

            var searchStopwatch = Stopwatch.StartNew();
            SearchCatalog();
            searchStopwatch.Stop();
            ApplyDownloadProgress(new SpecialProductDownloadProgress(
                result.DownloadResult.StoreCode,
                SpecialProductDownloadProgressStage.Completed,
                result.DownloadResult.TotalCount,
                result.DownloadResult.DownloadedCount,
                100,
                result.DownloadResult.PageCount,
                result.DownloadResult.UpsertedCount,
                result.DownloadResult.UnmarkedCount,
                0));
            SetStatus(
                "specialProducts.status.downloadCompleted",
                result.DownloadResult.DownloadedCount,
                result.DownloadResult.UnmarkedCount);
            totalStopwatch.Stop();
            Log($"operation=download store={Session.StoreCode} stage=completed pages={result.DownloadResult.PageCount} downloaded={result.DownloadResult.DownloadedCount} upserted={result.DownloadResult.UpsertedCount} unmarked={result.DownloadResult.UnmarkedCount} serviceElapsedMs={serviceStopwatch.ElapsedMilliseconds} replaceElapsedMs={replaceStopwatch.ElapsedMilliseconds} searchElapsedMs={searchStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"operation=download store={Session.StoreCode} stage=failed totalElapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            SetStatusText(string.Format(
                _localization.CurrentCulture,
                T("specialProducts.status.downloadFailed"),
                ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddSpecialProductAsync(SellableItemDto? item, CancellationToken cancellationToken)
    {
        if (item is null || !IsEditMode || !EnsureOnlineMutationAllowed())
        {
            return;
        }

        await MarkSpecialProductAsync(item, true, cancellationToken);
        SearchCatalog();
    }

    private async Task RemoveSpecialProductAsync(SellableItemDto? item, CancellationToken cancellationToken)
    {
        if (item is null || !IsEditMode || !EnsureOnlineMutationAllowed())
        {
            return;
        }

        await MarkSpecialProductAsync(item, false, cancellationToken);
    }

    private async Task MarkSpecialProductAsync(
        SellableItemDto item,
        bool isSpecialProduct,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            Log($"operation=mark store={Session.StoreCode} productCode={item.ProductCode} isSpecialProduct={isSpecialProduct} stage=start");
            var serviceStopwatch = Stopwatch.StartNew();
            var result = await _workflowService.MarkSpecialProductAsync(
                Session.StoreCode,
                item.ProductCode,
                isSpecialProduct,
                cancellationToken);
            serviceStopwatch.Stop();

            var replaceStopwatch = Stopwatch.StartNew();
            ApplySpecialItems(
                result.SpecialItems,
                resetToFirstPage: false,
                focusProductCode: isSpecialProduct ? item.ProductCode : null);
            replaceStopwatch.Stop();
            SetStatus(
                isSpecialProduct ? "specialProducts.status.marked" : "specialProducts.status.unmarked",
                item.DisplayName);
            totalStopwatch.Stop();
            Log($"operation=mark store={Session.StoreCode} productCode={item.ProductCode} isSpecialProduct={isSpecialProduct} stage=completed items={result.SpecialItems.Count} serviceElapsedMs={serviceStopwatch.ElapsedMilliseconds} replaceElapsedMs={replaceStopwatch.ElapsedMilliseconds} pageRefreshElapsedMs=0 totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"operation=mark store={Session.StoreCode} productCode={item.ProductCode} isSpecialProduct={isSpecialProduct} stage=failed totalElapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            SetStatusText(string.Format(_localization.CurrentCulture, T("specialProducts.status.markFailed"), ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MoveSpecialProductAsync(SellableItemDto? item, int delta)
    {
        if (item is null || IsBusy || !IsEditMode)
        {
            return;
        }

        var currentIndex = SpecialItems.IndexOf(item);
        var nextIndex = currentIndex + delta;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= SpecialItems.Count)
        {
            return;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var saveStopwatch = Stopwatch.StartNew();
        var result = await _workflowService.ReorderAsync(
            Session.StoreCode,
            SpecialItems.ToArray(),
            item.ProductCode,
            delta,
            CancellationToken.None);
        saveStopwatch.Stop();
        if (result is null)
        {
            return;
        }

        var pageStopwatch = Stopwatch.StartNew();
        ApplySpecialItems(result.SpecialItems, resetToFirstPage: false, focusProductCode: result.FocusProductCode);
        pageStopwatch.Stop();
        SetStatus("specialProducts.status.orderSaved");
        RefreshCommandStates();
        totalStopwatch.Stop();
        Log($"operation=move store={Session.StoreCode} productCode={item.ProductCode} fromIndex={currentIndex} toIndex={nextIndex} saveElapsedMs={saveStopwatch.ElapsedMilliseconds} pageRefreshElapsedMs={pageStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
    }

    private void ToggleEditMode()
    {
        IsEditMode = !IsEditMode;
    }

    private void ShowPreviousPage()
    {
        if (!CanShowPreviousPage())
        {
            return;
        }

        CurrentPage--;
        RefreshPagedSpecialItems();
    }

    private void ShowNextPage()
    {
        if (!CanShowNextPage())
        {
            return;
        }

        CurrentPage++;
        RefreshPagedSpecialItems();
    }

    private void RefreshPagedSpecialItems(string? focusProductCode = null, bool resetToFirstPage = false)
    {
        if (resetToFirstPage)
        {
            CurrentPage = 1;
        }
        else if (!string.IsNullOrWhiteSpace(focusProductCode))
        {
            var focusIndex = SpecialItems
                .Select((item, index) => new { item.ProductCode, Index = index })
                .FirstOrDefault(x => string.Equals(
                    NormalizeProductCode(x.ProductCode),
                    NormalizeProductCode(focusProductCode),
                    StringComparison.OrdinalIgnoreCase))
                ?.Index;

            if (focusIndex.HasValue)
            {
                CurrentPage = focusIndex.Value / PageSize + 1;
            }
        }

        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
        PagedSpecialItems.ReplaceWith(SpecialItems
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize));
        OnPropertyChanged(nameof(IsSpecialListEmpty));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageStatusText));
        RefreshCommandStates();
    }

    private bool EnsureOnlineMutationAllowed()
    {
        if (Session.IsOnline)
        {
            return true;
        }

        SetStatus("specialProducts.status.onlineRequired");
        return false;
    }

    private bool CanMutateItem(SellableItemDto? item)
    {
        return item is not null && IsEditMode && Session.IsOnline && !IsBusy;
    }

    private bool CanDownloadSpecialProducts()
    {
        return Session.IsOnline && !IsBusy;
    }

    private bool CanMoveUp(SellableItemDto? item)
    {
        return item is not null && IsEditMode && !IsBusy && SpecialItems.IndexOf(item) > 0;
    }

    private bool CanMoveDown(SellableItemDto? item)
    {
        var index = item is null ? -1 : SpecialItems.IndexOf(item);
        return index >= 0 && IsEditMode && !IsBusy && index < SpecialItems.Count - 1;
    }

    private bool CanShowPreviousPage()
    {
        return !IsBusy && CurrentPage > 1;
    }

    private bool CanShowNextPage()
    {
        return !IsBusy && CurrentPage < TotalPages;
    }

    private void RefreshCommandStates()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        ToggleEditModeCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        AddSpecialProductCommand.NotifyCanExecuteChanged();
        RemoveSpecialProductCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string key, params object[] args)
    {
        StatusMessage = args.Length == 0
            ? T(key)
            : string.Format(_localization.CurrentCulture, T(key), args);
    }

    private void SetStatusText(string message)
    {
        StatusMessage = message;
    }

    private void ApplySpecialItems(
        IReadOnlyList<SellableItemDto> specialItems,
        bool resetToFirstPage,
        string? focusProductCode = null)
    {
        lock (_specialItemsGate)
        {
            var selectedProductCode = SelectedSpecialItem?.ProductCode;
            SpecialItems.ReplaceWith(specialItems);
            RefreshPagedSpecialItems(focusProductCode, resetToFirstPage);
            SelectedSpecialItem = string.IsNullOrWhiteSpace(selectedProductCode)
                ? null
                : SpecialItems.FirstOrDefault(item => string.Equals(
                    NormalizeProductCode(item.ProductCode),
                    NormalizeProductCode(selectedProductCode),
                    StringComparison.OrdinalIgnoreCase));
            RefreshCommandStates();
        }
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(ClearSearchText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(EditModeText));
        OnPropertyChanged(nameof(PreviousPageText));
        OnPropertyChanged(nameof(NextPageText));
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(MoveUpText));
        OnPropertyChanged(nameof(MoveDownText));
        OnPropertyChanged(nameof(TapToAddText));
        OnPropertyChanged(nameof(SearchResultsText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(NoSearchResultsText));
        OnPropertyChanged(nameof(OnlineStateText));
        OnPropertyChanged(nameof(SelectedSpecialItemText));
    }

    private string T(string key)
    {
        return _localization.T(key);
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("SpecialProducts", message);
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs args)
    {
        ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw");
    }

    public void Dispose()
    {
        _rawScannerService?.Unsubscribe(PageId);
    }

    private void ApplyDownloadProgress(SpecialProductDownloadProgress progress)
    {
        Log($"operation=download-progress store={progress.StoreCode} stage={progress.Stage} percent={progress.Percent} downloaded={progress.DownloadedCount} total={progress.TotalCount} pages={progress.PageCount} elapsedMs={progress.ElapsedMilliseconds}");
        IsDownloadProgressVisible = true;
        IsDownloadProgressFailed = progress.Stage == SpecialProductDownloadProgressStage.Failed;
        DownloadProgressValue = progress.Percent;

        var titleKey = progress.Stage switch
        {
            SpecialProductDownloadProgressStage.Completed => "specialProducts.download.completed",
            SpecialProductDownloadProgressStage.Failed => "specialProducts.download.failed",
            _ => "specialProducts.download.downloading"
        };
        DownloadProgressText = string.Format(
            _localization.CurrentCulture,
            T(titleKey),
            progress.Percent);

        DownloadProgressDetailText = progress.Stage == SpecialProductDownloadProgressStage.Failed
            ? (progress.ErrorMessage ?? string.Empty)
            : string.Format(
                _localization.CurrentCulture,
                T("specialProducts.download.detail"),
                progress.DownloadedCount,
                progress.TotalCount,
                progress.PageCount,
                progress.UpsertedCount,
                progress.UnmarkedCount,
                FormatElapsed(progress.ElapsedMilliseconds));
    }

    private string FormatElapsed(long elapsedMilliseconds)
    {
        return string.Format(
            _localization.CurrentCulture,
            T("shell.catalogDownload.elapsedSeconds"),
            elapsedMilliseconds / 1000d);
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}
