using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public sealed record ShellSyncCenterSnapshot(
    SyncQueueOverview Overview,
    IReadOnlyList<SyncQueueListItem> ActiveItems);

public interface IShellSyncCenterService
{
    Task<ShellSyncCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class ShellSyncCenterService(ISyncQueueRepository syncQueueRepository) : IShellSyncCenterService
{
    public async Task<ShellSyncCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var overview = await syncQueueRepository.GetOverviewAsync(cancellationToken);
        var activeItems = await syncQueueRepository.GetActiveItemsAsync(cancellationToken: cancellationToken);
        return new ShellSyncCenterSnapshot(overview, activeItems);
    }
}
