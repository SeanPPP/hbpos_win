namespace Hbpos.Contracts.Square;

public sealed record SquareTokenResponse(
    string Environment,
    string AccessToken,
    DateTimeOffset UpdatedAt);
