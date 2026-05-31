using System.Net.Http;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Advertisements;

namespace Hbpos.Client.Tests;

public sealed class CustomerDisplayOrchestratorTests
{
    [Fact]
    public async Task RefreshAdvertisementsAsync_loads_snapshot_from_api()
    {
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => Task.FromResult(CreateResponse(CreateImageAdvertisement("ad-1")))));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");

        Assert.True(customerDisplay.IsAdvertisementAvailable);
        Assert.Equal("ad-1", customerDisplay.CurrentAdvertisement?.Id);
        Assert.True(customerDisplay.IsIdleAdvertisementVisible);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_keeps_last_snapshot_when_api_fails()
    {
        var responses = new Queue<Func<Task<AdvertisementPlaybackResponse>>>(new[]
        {
            () => Task.FromResult(CreateResponse(CreateImageAdvertisement("ad-1"))),
            () => throw new HttpRequestException("boom")
        });
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => responses.Dequeue().Invoke()));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");
        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S002");

        Assert.True(customerDisplay.IsAdvertisementAvailable);
        Assert.Equal("ad-1", customerDisplay.CurrentAdvertisement?.Id);
    }

    [Fact]
    public async Task RefreshAdvertisementsAsync_first_failure_keeps_static_promotion()
    {
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ => throw new HttpRequestException("boom")));
        var customerDisplay = new CustomerDisplayViewModel();

        await orchestrator.RefreshAdvertisementsAsync(customerDisplay, "S001");

        Assert.False(customerDisplay.IsAdvertisementAvailable);
        Assert.Null(customerDisplay.CurrentAdvertisement);
        Assert.False(customerDisplay.IsIdleAdvertisementVisible);
    }

    [Fact]
    public async Task LoadFromCart_starts_periodic_advertisement_refresh()
    {
        var callCount = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new CustomerDisplayOrchestrator(
            new FakeCustomerDisplayWindowService(),
            new FakeAdvertisementApiClient(_ =>
            {
                if (Interlocked.Increment(ref callCount) >= 2)
                {
                    secondRefresh.TrySetResult();
                }

                return Task.FromResult(CreateResponse(CreateImageAdvertisement($"ad-{callCount}")));
            }),
            TimeSpan.FromMilliseconds(25));
        var customerDisplay = new CustomerDisplayViewModel();

        orchestrator.LoadFromCart(customerDisplay, CreateSession(), new PosCartService());

        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(callCount >= 2);
    }

    private static AdvertisementPlaybackResponse CreateResponse(params AdvertisementPlaybackItemDto[] items)
    {
        return new AdvertisementPlaybackResponse("S001", DateTimeOffset.UtcNow, items);
    }

    private static PosSessionState CreateSession()
    {
        return new PosSessionState(
            SystemName: "HB POS",
            StoreCode: "S001",
            StoreName: "Main Store",
            DeviceCode: "POS-01",
            CashierId: "C001",
            CashierName: "Alice",
            IsOnline: true,
            PendingSyncCount: 0);
    }

    private static AdvertisementPlaybackItemDto CreateImageAdvertisement(string id)
    {
        return new AdvertisementPlaybackItemDto(
            id,
            $"Ad {id}",
            $"Description {id}",
            "image",
            $"https://cdn.example.com/{id}.png",
            null,
            $"object/{id}",
            $"{id}.png",
            "image/png",
            1024,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(5),
            1);
    }

    private sealed class FakeAdvertisementApiClient(
        Func<string, Task<AdvertisementPlaybackResponse>> handler) : IAdvertisementApiClient
    {
        public Task<AdvertisementPlaybackResponse> GetActiveAsync(
            string storeCode,
            int take = 20,
            CancellationToken cancellationToken = default)
        {
            return handler(storeCode);
        }
    }

    private sealed class FakeCustomerDisplayWindowService : ICustomerDisplayWindowService
    {
        public bool IsOpen => false;

        public CustomerDisplayWindowMode Mode => CustomerDisplayWindowMode.Closed;

        public event EventHandler? Closed
        {
            add { }
            remove { }
        }

        public void Prewarm(CustomerDisplayViewModel viewModel)
        {
        }

        public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, System.Windows.Window? owner)
        {
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, null);
        }

        public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, System.Windows.Window? owner)
        {
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, null);
        }

        public CustomerDisplayWindowResult SetMode(
            CustomerDisplayWindowMode mode,
            CustomerDisplayViewModel viewModel,
            System.Windows.Window? owner)
        {
            return new CustomerDisplayWindowResult(CustomerDisplayWindowMode.Closed, null);
        }
    }
}
