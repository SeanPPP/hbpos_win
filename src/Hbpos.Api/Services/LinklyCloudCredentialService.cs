using Hbpos.Api.Data;
using Hbpos.Contracts.Linkly;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface ILinklyCloudCredentialService
{
    Task<LinklyCloudCredentialResponse?> GetByStoreCodeAsync(
        string storeCode,
        string environment,
        CancellationToken cancellationToken);
}

public sealed class LinklyCloudCredentialService(
    ILinklyCloudCredentialRepository repository) : ILinklyCloudCredentialService
{
    public async Task<LinklyCloudCredentialResponse?> GetByStoreCodeAsync(
        string storeCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var normalizedEnvironment = NormalizeEnvironment(environment);
        if (string.IsNullOrWhiteSpace(normalizedStoreCode) || normalizedEnvironment is null)
        {
            return null;
        }

        var credential = await repository.GetByStoreCodeAsync(
            normalizedStoreCode,
            normalizedEnvironment,
            cancellationToken);
        if (credential is null
            || string.IsNullOrWhiteSpace(credential.Username)
            || string.IsNullOrWhiteSpace(credential.Password))
        {
            return null;
        }

        return new LinklyCloudCredentialResponse(
            credential.StoreCode ?? normalizedStoreCode,
            credential.Environment ?? normalizedEnvironment,
            credential.Username,
            credential.Password,
            new DateTimeOffset(DateTime.SpecifyKind(credential.UpdatedAt ?? DateTime.UtcNow, DateTimeKind.Utc)));
    }

    internal static string NormalizeStoreCode(string? storeCode)
    {
        return (storeCode ?? string.Empty).Trim();
    }

    internal static string? NormalizeEnvironment(string? environment)
    {
        return (environment ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "PRODUCTION" => "Production",
            "SANDBOX" => "Sandbox",
            _ => null
        };
    }
}

public interface ILinklyCloudCredentialRepository
{
    Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
        string storeCode,
        string environment,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarLinklyCloudCredentialRepository(
    HbposSqlSugarContext dbContext) : ILinklyCloudCredentialRepository
{
    public async Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
        string storeCode,
        string environment,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id],
                [StoreCode],
                [Environment],
                [Username],
                [Password],
                [UpdatedAt],
                [UpdatedBy]
            FROM [dbo].[POSM_LinklyCloudCredential]
            WHERE [StoreCode] = @StoreCode
              AND [Environment] = @Environment
              AND NULLIF(LTRIM(RTRIM([Username])), '') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM([Password])), '') IS NOT NULL
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudCredentialRecord>(
            sql,
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@Environment", environment));
    }
}

public sealed class LinklyCloudCredentialRecord
{
    public long Id { get; set; }

    public string? StoreCode { get; set; }

    public string? Environment { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}
