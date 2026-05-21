namespace Hbpos.Client.Wpf.Models;

public sealed record SyncQueueOverview(
    int PendingCount,
    int FailedCount,
    int SyncingCount,
    string? LastError);

public sealed record SyncQueueListItem(
    Guid EntityId,
    string EntityType,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastTriedAt,
    string? ErrorMessage,
    decimal? Amount)
{
    public string ShortEntityId => EntityId.ToString("N")[..10].ToUpperInvariant();

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string LastTriedAtDisplay => LastTriedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    public string AmountDisplay => Amount.HasValue ? Amount.Value.ToString("C2") : "-";
}
