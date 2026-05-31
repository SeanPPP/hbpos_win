using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Views.Screens;

public partial class CustomerDisplayView : UserControl
{
    private const double CompactPromotionWidthThreshold = 1280;
    private static readonly GridLength VisibleSummaryRowHeight = new(132);
    private static readonly GridLength HiddenSummaryRowHeight = new(0);
    private readonly DispatcherTimer _imageAdvanceTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _videoTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CustomerDisplayViewModel? _viewModel;

    public CustomerDisplayView()
    {
        InitializeComponent();
        _imageAdvanceTimer.Tick += (_, _) => AdvanceAdvertisementPlayback();
        _videoTimeoutTimer.Tick += (_, _) => SkipCurrentAdvertisementPlayback();
        Loaded += CustomerDisplayViewLoaded;
        DataContextChanged += CustomerDisplayViewDataContextChanged;
        Unloaded += CustomerDisplayViewUnloaded;
    }

    private void CustomerDisplayViewLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel(DataContext as CustomerDisplayViewModel);
        RefreshPromotionLayout();
        RefreshAdvertisementPlayback();
    }

    private void CustomerDisplayViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromViewModel();
        SubscribeToViewModel(e.NewValue as CustomerDisplayViewModel);
        RefreshPromotionLayout();
        RefreshAdvertisementPlayback();
    }

    private void SubscribeToViewModel(CustomerDisplayViewModel? viewModel)
    {
        if (_viewModel is not null || viewModel is null)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.Lines.CollectionChanged += LinesCollectionChanged;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        ScrollLatestLineIntoView();
    }

    private void CustomerDisplayViewUnloaded(object sender, RoutedEventArgs e)
    {
        StopAdvertisementPlayback();
        UnsubscribeFromViewModel();
    }

    private void LinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollLatestLineIntoView();
    }

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyPromotionLayout(e.NewSize.Width);
    }

    public void RefreshPromotionLayout()
    {
        ApplyPromotionLayout(ContentGrid.ActualWidth);
    }

    private void ApplyPromotionLayout(double width)
    {
        if (_viewModel?.IsIdleAdvertisementVisible == true)
        {
            // 空闲状态使用全屏广告布局。
            SummaryRow.Height = HiddenSummaryRowHeight;
            SummaryPanel.Visibility = Visibility.Collapsed;
            CartPanel.Visibility = Visibility.Collapsed;
            PromotionBannerRow.Height = new GridLength(0);
            Grid.SetRow(PromotionPanel, 0);
            Grid.SetColumn(PromotionPanel, 0);
            Grid.SetRowSpan(PromotionPanel, 2);
            Grid.SetColumnSpan(PromotionPanel, 2);
            PromotionPanel.Margin = new Thickness(0);
            PromotionTextPanel.Margin = new Thickness(48, 44, 48, 44);
            ApplyPromotionTypography(44, 20);
            return;
        }

        SummaryRow.Height = VisibleSummaryRowHeight;
        SummaryPanel.Visibility = Visibility.Visible;
        CartPanel.Visibility = Visibility.Visible;
        Grid.SetRowSpan(PromotionPanel, 1);

        if (UsesCompactPromotionLayout(width))
        {
            PromotionBannerRow.Height = new GridLength(154);
            Grid.SetRow(PromotionPanel, 0);
            Grid.SetColumn(PromotionPanel, 0);
            Grid.SetColumnSpan(PromotionPanel, 2);
            PromotionPanel.Margin = new Thickness(0, 0, 0, 16);
            Grid.SetColumnSpan(CartPanel, 2);
            PromotionTextPanel.Margin = new Thickness(28, 20, 160, 18);
            ApplyPromotionTypography(26, 14);
            return;
        }

        PromotionBannerRow.Height = new GridLength(0);
        Grid.SetRow(PromotionPanel, 1);
        Grid.SetColumn(PromotionPanel, 1);
        Grid.SetColumnSpan(PromotionPanel, 1);
        PromotionPanel.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumnSpan(CartPanel, 1);
        PromotionTextPanel.Margin = new Thickness(48, 44, 48, 44);
        ApplyPromotionTypography(34, 16);
    }

    public static bool UsesCompactPromotionLayout(double width)
    {
        return width > 0 && width < CompactPromotionWidthThreshold;
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomerDisplayViewModel.IsIdleAdvertisementVisible))
        {
            RefreshPromotionLayout();
        }

        if (e.PropertyName is nameof(CustomerDisplayViewModel.CurrentAdvertisement)
            or nameof(CustomerDisplayViewModel.IsAdvertisementAvailable))
        {
            RefreshAdvertisementPlayback();
        }
    }

    private void RefreshAdvertisementPlayback()
    {
        var hasAdvertisement = _viewModel?.IsAdvertisementAvailable == true;
        PromotionSubtitleText.Visibility = hasAdvertisement ? Visibility.Visible : Visibility.Collapsed;
        PromotionBodyText.Visibility = hasAdvertisement && !string.IsNullOrWhiteSpace(_viewModel?.CurrentAdvertisementDescription)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PromotionFallbackSubtitleText.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;
        PromotionFallbackBodyText.Visibility = hasAdvertisement ? Visibility.Collapsed : Visibility.Visible;

        if (_viewModel?.CurrentAdvertisementMediaUrl is not { Length: > 0 } mediaUrl)
        {
            StopAdvertisementPlayback();
            return;
        }

        if (_viewModel.IsCurrentAdvertisementImage)
        {
            ShowImageAdvertisement(mediaUrl);
            return;
        }

        if (_viewModel.IsCurrentAdvertisementVideo)
        {
            ShowVideoAdvertisement(mediaUrl);
            return;
        }

        StopAdvertisementPlayback();
    }

    private void ShowImageAdvertisement(string mediaUrl)
    {
        StopAdvertisementPlayback(clearImageSource: false);

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri))
        {
            SkipCurrentAdvertisementPlayback();
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = mediaUri;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();

            AdvertisementVideo.Visibility = Visibility.Collapsed;
            AdvertisementImage.Source = bitmap;
            AdvertisementImage.Visibility = Visibility.Visible;
            _imageAdvanceTimer.Start();
        }
        catch
        {
            SkipCurrentAdvertisementPlayback();
        }
    }

    private void ShowVideoAdvertisement(string mediaUrl)
    {
        StopAdvertisementPlayback();

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var mediaUri))
        {
            SkipCurrentAdvertisementPlayback();
            return;
        }

        try
        {
            AdvertisementImage.Visibility = Visibility.Collapsed;
            AdvertisementVideo.Source = mediaUri;
            AdvertisementVideo.Visibility = Visibility.Visible;
            // 视频始终静音，避免干扰收银。
            AdvertisementVideo.IsMuted = true;
            AdvertisementVideo.Volume = 0;
            AdvertisementVideo.Play();
            _videoTimeoutTimer.Start();
        }
        catch
        {
            SkipCurrentAdvertisementPlayback();
        }
    }

    private void StopAdvertisementPlayback(bool clearImageSource = true)
    {
        _imageAdvanceTimer.Stop();
        _videoTimeoutTimer.Stop();

        AdvertisementVideo.Stop();
        AdvertisementVideo.Visibility = Visibility.Collapsed;
        AdvertisementVideo.Source = null;

        AdvertisementImage.Visibility = Visibility.Collapsed;
        if (clearImageSource)
        {
            AdvertisementImage.Source = null;
        }
    }

    private void AdvanceAdvertisementPlayback()
    {
        _imageAdvanceTimer.Stop();
        _videoTimeoutTimer.Stop();
        _viewModel?.AdvanceAdvertisement();
    }

    private void SkipCurrentAdvertisementPlayback()
    {
        _imageAdvanceTimer.Stop();
        _videoTimeoutTimer.Stop();
        _viewModel?.SkipCurrentAdvertisement();
    }

    private void AdvertisementVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        AdvanceAdvertisementPlayback();
    }

    private void AdvertisementVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SkipCurrentAdvertisementPlayback();
    }

    private void AdvertisementImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SkipCurrentAdvertisementPlayback();
    }

    private void ApplyPromotionTypography(double subtitleFontSize, double bodyFontSize)
    {
        PromotionSubtitleText.FontSize = subtitleFontSize;
        PromotionBodyText.FontSize = bodyFontSize;
        PromotionFallbackSubtitleText.FontSize = subtitleFontSize;
        PromotionFallbackBodyText.FontSize = bodyFontSize;
    }

    private void ScrollLatestLineIntoView()
    {
        var latestLine = _viewModel?.Lines.LastOrDefault();
        if (latestLine is null)
        {
            return;
        }

        LineDataGrid.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                LineDataGrid.UpdateLayout();
                LineDataGrid.ScrollIntoView(latestLine);
            }),
            DispatcherPriority.Background);
    }

    private void UnsubscribeFromViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.Lines.CollectionChanged -= LinesCollectionChanged;
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            _viewModel = null;
        }
    }
}
