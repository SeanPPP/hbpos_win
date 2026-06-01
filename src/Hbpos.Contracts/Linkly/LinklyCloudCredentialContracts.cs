namespace Hbpos.Contracts.Linkly;

public sealed record LinklyCloudCredentialResponse(
    string StoreCode,
    string Environment,
    string Username,
    string Password,
    DateTimeOffset UpdatedAt);
