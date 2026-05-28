namespace Hbpos.Contracts.Linkly;

public sealed record LinklyCloudCredentialResponse(
    string StoreCode,
    string Username,
    string Password,
    DateTimeOffset UpdatedAt);
