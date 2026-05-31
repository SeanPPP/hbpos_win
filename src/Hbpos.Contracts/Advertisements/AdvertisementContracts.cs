namespace Hbpos.Contracts.Advertisements;

public sealed record AdvertisementPlaybackItemDto(
    string Id,
    string Title,
    string? Description,
    string MediaType,
    string MediaUrl,
    string? ThumbnailUrl,
    string ObjectKey,
    string OriginalFileName,
    string ContentType,
    long FileSize,
    DateTimeOffset EffectiveStart,
    DateTimeOffset EffectiveEnd,
    int SortOrder);

public sealed record AdvertisementPlaybackResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdvertisementPlaybackItemDto> Items);
