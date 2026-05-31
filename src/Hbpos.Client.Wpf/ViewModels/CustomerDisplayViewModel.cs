using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Advertisements;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class CustomerDisplayViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _taxAmount;

    [ObservableProperty]
    private decimal _savingsAmount;

    [ObservableProperty]
    private decimal _totalToPay;

    [ObservableProperty]
    private decimal _totalItemQuantity;

    [ObservableProperty]
    private int _skuCount;

    [ObservableProperty]
    private string _terminalName = "Terminal 01";

    [ObservableProperty]
    private string _promotionTitle = "customer.promotionTitle";

    [ObservableProperty]
    private string _promotionSubtitle = "customer.promotionSubtitle";

    [ObservableProperty]
    private string _promotionBody = "customer.promotionBody";

    [ObservableProperty]
    private bool _isReadyForPayment;

    [ObservableProperty]
    private AdvertisementPlaybackItemDto? _currentAdvertisement;

    [ObservableProperty]
    private bool _isAdvertisementAvailable;

    [ObservableProperty]
    private bool _isIdleAdvertisementVisible;

    private readonly List<AdvertisementPlaybackItemDto> _advertisements = [];
    private int _currentAdvertisementIndex = -1;

    public ObservableCollection<CustomerDisplayLine> Lines { get; } = [];

    public string TotalToPayLabel => "customer.totalToPay";

    public string ReadyForPaymentLabel => "customer.readyForPayment";

    public string InsertOrTapLabel => "customer.insertOrTap";

    public string SubtotalLabel => "Subtotal";

    public string TaxLabel => "Tax";

    public string SavingsLabel => "Savings";

    public string CurrentAdvertisementTitle => CurrentAdvertisement?.Title ?? string.Empty;

    public string CurrentAdvertisementDescription => CurrentAdvertisement?.Description ?? string.Empty;

    public string? CurrentAdvertisementMediaUrl => CurrentAdvertisement?.MediaUrl;

    public bool IsCurrentAdvertisementImage =>
        CurrentAdvertisement is not null
        && string.Equals(CurrentAdvertisement.MediaType, "image", StringComparison.OrdinalIgnoreCase);

    public bool IsCurrentAdvertisementVideo =>
        CurrentAdvertisement is not null
        && string.Equals(CurrentAdvertisement.MediaType, "video", StringComparison.OrdinalIgnoreCase);

    public void LoadLines(IEnumerable<CustomerDisplayLine> lines, decimal subtotal, decimal taxAmount, decimal savingsAmount)
    {
        var materialized = lines.ToList();
        Lines.ReplaceWith(materialized);
        Subtotal = subtotal;
        TaxAmount = taxAmount;
        SavingsAmount = savingsAmount;
        TotalToPay = subtotal + taxAmount - savingsAmount;
        TotalItemQuantity = materialized.Sum(line => line.Quantity);
        SkuCount = materialized.Count;
        IsReadyForPayment = TotalToPay > 0m;
        RefreshIdleAdvertisementVisibility();
    }

    public void LoadAdvertisements(IEnumerable<AdvertisementPlaybackItemDto> advertisements)
    {
        _advertisements.Clear();
        _advertisements.AddRange(advertisements.Where(IsPlayableAdvertisement));
        IsAdvertisementAvailable = _advertisements.Count > 0;
        _currentAdvertisementIndex = IsAdvertisementAvailable ? 0 : -1;
        CurrentAdvertisement = IsAdvertisementAvailable ? _advertisements[0] : null;
        RefreshIdleAdvertisementVisibility();
    }

    public void AdvanceAdvertisement()
    {
        if (_advertisements.Count == 0)
        {
            CurrentAdvertisement = null;
            IsAdvertisementAvailable = false;
            RefreshIdleAdvertisementVisibility();
            return;
        }

        _currentAdvertisementIndex = (_currentAdvertisementIndex + 1) % _advertisements.Count;
        var nextAdvertisement = _advertisements[_currentAdvertisementIndex];

        // 只有一条广告时也要触发属性变更，让播放层重新开始下一轮。
        if (EqualityComparer<AdvertisementPlaybackItemDto?>.Default.Equals(CurrentAdvertisement, nextAdvertisement))
        {
            CurrentAdvertisement = null;
        }

        CurrentAdvertisement = nextAdvertisement;
    }

    public void SkipCurrentAdvertisement()
    {
        if (_advertisements.Count == 0)
        {
            CurrentAdvertisement = null;
            IsAdvertisementAvailable = false;
            RefreshIdleAdvertisementVisibility();
            return;
        }

        var currentIndex = _currentAdvertisementIndex;
        if (currentIndex < 0 || currentIndex >= _advertisements.Count)
        {
            currentIndex = 0;
        }

        // 播放失败的素材直接移出当前轮播，避免客显在坏素材上反复打转。
        _advertisements.RemoveAt(currentIndex);
        if (_advertisements.Count == 0)
        {
            _currentAdvertisementIndex = -1;
            CurrentAdvertisement = null;
            IsAdvertisementAvailable = false;
            RefreshIdleAdvertisementVisibility();
            return;
        }

        _currentAdvertisementIndex = currentIndex - 1;
        IsAdvertisementAvailable = true;
        AdvanceAdvertisement();
    }

    partial void OnCurrentAdvertisementChanged(AdvertisementPlaybackItemDto? value)
    {
        OnPropertyChanged(nameof(CurrentAdvertisementTitle));
        OnPropertyChanged(nameof(CurrentAdvertisementDescription));
        OnPropertyChanged(nameof(CurrentAdvertisementMediaUrl));
        OnPropertyChanged(nameof(IsCurrentAdvertisementImage));
        OnPropertyChanged(nameof(IsCurrentAdvertisementVideo));
    }

    partial void OnIsAdvertisementAvailableChanged(bool value)
    {
        RefreshIdleAdvertisementVisibility();
    }

    private void RefreshIdleAdvertisementVisibility()
    {
        IsIdleAdvertisementVisible = IsAdvertisementAvailable && Lines.Count == 0;
    }

    private static bool IsPlayableAdvertisement(AdvertisementPlaybackItemDto advertisement)
    {
        return !string.IsNullOrWhiteSpace(advertisement.MediaUrl)
            && (string.Equals(advertisement.MediaType, "image", StringComparison.OrdinalIgnoreCase)
                || string.Equals(advertisement.MediaType, "video", StringComparison.OrdinalIgnoreCase));
    }
}
