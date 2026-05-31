using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Views.Screens;
using Hbpos.Contracts.Advertisements;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayViewModelTests
{
    [Fact]
    public void LoadLines_calculates_item_quantity_and_sku_count()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadLines(
            [
                new CustomerDisplayLine("Milk", "SKU-001", 2m, 3m, 6m),
                new CustomerDisplayLine("Bread", "SKU-002", 1.5m, 4m, 6m)
            ],
            subtotal: 12m,
            taxAmount: 0m,
            savingsAmount: 1m);

        Assert.Equal(3.5m, viewModel.TotalItemQuantity);
        Assert.Equal(2, viewModel.SkuCount);
        Assert.Equal(11m, viewModel.TotalToPay);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1279, true)]
    [InlineData(1280, false)]
    [InlineData(1920, false)]
    public void CustomerDisplayView_uses_banner_promotion_layout_on_narrow_fullscreen_widths(
        double width,
        bool expectedCompact)
    {
        Assert.Equal(expectedCompact, CustomerDisplayView.UsesCompactPromotionLayout(width));
    }

    [Fact]
    public void LoadAdvertisements_filters_unplayable_items_and_marks_idle_visible_when_cart_is_empty()
    {
        var viewModel = new CustomerDisplayViewModel();

        viewModel.LoadAdvertisements(
            [
                CreateAdvertisement("ad-image", "image", "https://cdn.example.com/ad-image.png"),
                CreateAdvertisement("ad-video", "video", "https://cdn.example.com/ad-video.mp4"),
                CreateAdvertisement("ad-empty", "image", string.Empty),
                CreateAdvertisement("ad-audio", "audio", "https://cdn.example.com/ad-audio.mp3")
            ]);

        Assert.True(viewModel.IsAdvertisementAvailable);
        Assert.True(viewModel.IsIdleAdvertisementVisible);
        Assert.Equal("ad-image", viewModel.CurrentAdvertisement?.Id);
    }

    [Fact]
    public void AdvanceAdvertisement_with_single_item_raises_change_notifications_for_restart()
    {
        var viewModel = new CustomerDisplayViewModel();
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.PropertyName))
            {
                changedProperties.Add(e.PropertyName);
            }
        };
        viewModel.LoadAdvertisements([CreateAdvertisement("ad-image", "image", "https://cdn.example.com/ad-image.png")]);
        changedProperties.Clear();

        viewModel.AdvanceAdvertisement();

        Assert.Equal("ad-image", viewModel.CurrentAdvertisement?.Id);
        Assert.Equal(2, changedProperties.Count(name => name == nameof(CustomerDisplayViewModel.CurrentAdvertisement)));
    }

    [Fact]
    public void SkipCurrentAdvertisement_removes_failed_item_and_falls_back_when_last_item_is_skipped()
    {
        var viewModel = new CustomerDisplayViewModel();
        viewModel.LoadAdvertisements([CreateAdvertisement("ad-image", "image", "https://cdn.example.com/ad-image.png")]);

        viewModel.SkipCurrentAdvertisement();

        Assert.False(viewModel.IsAdvertisementAvailable);
        Assert.Null(viewModel.CurrentAdvertisement);
        Assert.False(viewModel.IsIdleAdvertisementVisible);
    }

    private static AdvertisementPlaybackItemDto CreateAdvertisement(string id, string mediaType, string mediaUrl)
    {
        return new AdvertisementPlaybackItemDto(
            id,
            $"Ad {id}",
            $"Description {id}",
            mediaType,
            mediaUrl,
            null,
            $"object/{id}",
            $"{id}.dat",
            "application/octet-stream",
            1024,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(5),
            1);
    }
}
