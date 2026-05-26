using Hbpos.Api.Data;
using Hbpos.Contracts.Square;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface ISquareTokenService
{
    Task<SquareTokenResponse?> GetActiveTokenAsync(
        string environment,
        CancellationToken cancellationToken);
}

public sealed class SquareTokenService(ISquareTokenRepository repository) : ISquareTokenService
{
    public async Task<SquareTokenResponse?> GetActiveTokenAsync(
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        if (normalizedEnvironment is null)
        {
            return null;
        }

        var token = await repository.GetActiveTokenAsync(normalizedEnvironment, cancellationToken);
        return token is null
            ? null
            : new SquareTokenResponse(
                normalizedEnvironment,
                token.AccessToken ?? string.Empty,
                new DateTimeOffset(DateTime.SpecifyKind(token.UpdatedAt ?? DateTime.UtcNow, DateTimeKind.Utc)));
    }

    internal static string? NormalizeEnvironment(string? environment)
    {
        return (environment ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "PRODUCTION" => "Production",
            "SANDBOX" or "TEST" => "Sandbox",
            _ => null
        };
    }
}

public interface ISquareTokenRepository
{
    Task<SquareTokenRecord?> GetActiveTokenAsync(
        string environment,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarSquareTokenRepository(HbposSqlSugarContext dbContext) : ISquareTokenRepository
{
    public async Task<SquareTokenRecord?> GetActiveTokenAsync(
        string environment,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id],
                [Environment],
                [AccessToken],
                [IsEnabled],
                [UpdatedAt],
                [UpdatedBy]
            FROM [dbo].[POSM_SquareToken]
            WHERE [Environment] = @Environment
              AND [IsEnabled] = 1
              AND NULLIF(LTRIM(RTRIM([AccessToken])), '') IS NOT NULL
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<SquareTokenRecord>(
            sql,
            new SugarParameter("@Environment", environment));
    }
}

public sealed class SquareTokenRecord
{
    public long Id { get; set; }

    public string? Environment { get; set; }

    public string? AccessToken { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}
