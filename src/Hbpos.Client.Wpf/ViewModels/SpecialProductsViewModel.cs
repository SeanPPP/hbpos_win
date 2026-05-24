using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class SpecialProductsViewModel : ObservableObject
{
    private readonly LocalSellableItemIndex _priceIndex;
    private readonly PosCartService _cart;
    private readonly ILocalCatalogRepository _catalogRepository;
    private readonly ISpecialProductService _specialProductService;
    private readonly ILocalizationService _localization;
    private readonly Action _onBack;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public SpecialProductsViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        ILocalCatalogRepository catalogRepository,
        ISpecialProductService specialProductService,
        PosSessionState session,
        ILocalizationService localization,
        Action onBack)
    {
        _priceIndex = priceIndex;
        _cart = cart;
        _catalogRepository = catalogRepository;
        _specialProductService = specialProductService;
        _session = session;
        _localization = localization;
        _onBack = onBack;

        BackCommand = new RelayCommand(_onBack);
        SearchCommand = new RelayCommand(SearchCatalog);
        ClearSearchCommand = new RelayCommand(ClearSearch, () => !string.IsNullOrWhiteSpace(SearchText));
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        AddToCartCommand = new RelayCommand<SellableItemDto>(AddToCart);
        AddSpecialProductCommand = new AsyncRelayCommand<SellableItemDto>(AddSpecialProductAsync, CanMutateItem);
        RemoveSpecialProductCommand = new AsyncRelayCommand<SellableItemDto>(RemoveSpecialProductAsync, CanMutateItem);
        MoveUpCommand = new AsyncRelayCommand<SellableItemDto>(item => MoveSpecialProductAsync(item, -1), CanMoveUp);
        MoveDownCommand = new AsyncRelayCommand<SellableItemDto>(item => MoveSpecialProductAsync(item, 1), CanMoveDown);

        _localization.CultureChanged += (_, _) => RaiseLocalizedProperties();
        StatusMessage = T("specialProducts.status.ready");
    }

    public ObservableCollection<SellableItemDto> SpecialItems { get; } = [];

    public ObservableCollection<SellableItemDto> SearchResults { get; } = [];

    public IRelayCommand BackCommand { get; }

    public IRelayCommand SearchCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand<SellableItemDto> AddToCartCommand { get; }

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

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var specialItems = await _catalogRepository.LoadSpecialProductItemsAsync(Session.StoreCode, cancellationToken);
            SpecialItems.ReplaceWith(specialItems);
            OnPropertyChanged(nameof(IsSpecialListEmpty));
            SetStatus("specialProducts.status.loaded", SpecialItems.Count);
            RefreshCommandStates();
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
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommandStates();
    }

    private void SearchCatalog()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchResults.Clear();
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }

        var results = _priceIndex.Search(SearchText, 80)
            .Where(item =>
                string.Equals(item.StoreCode, Session.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                !item.IsSpecialProduct)
            .GroupBy(item => NormalizeProductCode(item.ProductCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(PreferredLookupRank)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.LookupCode, StringComparer.OrdinalIgnoreCase)
                .First())
            .Take(12)
            .ToArray();

        SearchResults.ReplaceWith(results);
        OnPropertyChanged(nameof(HasSearchResults));
        SetStatus(results.Length == 0
            ? "specialProducts.status.noSearchResults"
            : "specialProducts.status.searchResults",
            results.Length);
    }

    private void ClearSearch()
    {
        SearchText = string.Empty;
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private void AddToCart(SellableItemDto? item)
    {
        if (item is null)
        {
            return;
        }

        _cart.AddItem(item);
        SetStatus("specialProducts.status.addedToCart", item.DisplayName);
    }

    private async Task AddSpecialProductAsync(SellableItemDto? item, CancellationToken cancellationToken)
    {
        if (item is null || !EnsureOnlineMutationAllowed())
        {
            return;
        }

        await MarkSpecialProductAsync(item, true, cancellationToken);
        SearchCatalog();
    }

    private async Task RemoveSpecialProductAsync(SellableItemDto? item, CancellationToken cancellationToken)
    {
        if (item is null || !EnsureOnlineMutationAllowed())
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
        try
        {
            await _specialProductService.MarkSpecialProductAsync(
                Session.StoreCode,
                item.ProductCode,
                isSpecialProduct,
                cancellationToken);
            var catalogItems = await _catalogRepository.LoadSellableItemsAsync(cancellationToken);
            _priceIndex.ReplaceAll(catalogItems);
            var specialItems = await _catalogRepository.LoadSpecialProductItemsAsync(Session.StoreCode, cancellationToken);
            SpecialItems.ReplaceWith(specialItems);
            OnPropertyChanged(nameof(IsSpecialListEmpty));
            SetStatus(
                isSpecialProduct ? "specialProducts.status.marked" : "specialProducts.status.unmarked",
                item.DisplayName);
        }
        catch (Exception ex)
        {
            SetStatusText(string.Format(_localization.CurrentCulture, T("specialProducts.status.markFailed"), ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MoveSpecialProductAsync(SellableItemDto? item, int delta)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        var currentIndex = SpecialItems.IndexOf(item);
        var nextIndex = currentIndex + delta;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= SpecialItems.Count)
        {
            return;
        }

        SpecialItems.Move(currentIndex, nextIndex);
        await _catalogRepository.SaveSpecialProductOrderAsync(
            Session.StoreCode,
            SpecialItems.Select(x => x.ProductCode),
            CancellationToken.None);
        SetStatus("specialProducts.status.orderSaved");
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
        return item is not null && Session.IsOnline && !IsBusy;
    }

    private bool CanMoveUp(SellableItemDto? item)
    {
        return item is not null && !IsBusy && SpecialItems.IndexOf(item) > 0;
    }

    private bool CanMoveDown(SellableItemDto? item)
    {
        var index = item is null ? -1 : SpecialItems.IndexOf(item);
        return index >= 0 && !IsBusy && index < SpecialItems.Count - 1;
    }

    private void RefreshCommandStates()
    {
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

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(ClearSearchText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(MoveUpText));
        OnPropertyChanged(nameof(MoveDownText));
        OnPropertyChanged(nameof(TapToAddText));
        OnPropertyChanged(nameof(SearchResultsText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(NoSearchResultsText));
        OnPropertyChanged(nameof(OnlineStateText));
    }

    private string T(string key)
    {
        return _localization.T(key);
    }

    private static int PreferredLookupRank(SellableItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.Barcode) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.Barcode), StringComparison.Ordinal))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(item.ItemNumber) &&
            string.Equals(NormalizeLookupCode(item.LookupCode), NormalizeLookupCode(item.ItemNumber), StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }

    private static string NormalizeLookupCode(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}
