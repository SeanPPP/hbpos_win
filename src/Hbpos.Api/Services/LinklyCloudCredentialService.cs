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

    Task<LinklyCloudCredentialUpsertResponse> UpsertAsync(
        string storeCode,
        LinklyCloudCredentialUpsertRequest request,
        string? updatedBy,
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

    public async Task<LinklyCloudCredentialUpsertResponse> UpsertAsync(
        string storeCode,
        LinklyCloudCredentialUpsertRequest request,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedEnvironment = NormalizeEnvironment(request.Environment)
            ?? throw new LinklyCloudCredentialValidationException("environment must be Production or Sandbox");
        var username = NormalizeRequired(request.Username, "username");
        var password = NormalizeRequired(request.Password, "password");
        var now = DateTime.UtcNow;

        var credential = await repository.UpsertAsync(
            normalizedStoreCode,
            normalizedEnvironment,
            username,
            password,
            now,
            NormalizeUpdatedBy(updatedBy),
            cancellationToken);

        return new LinklyCloudCredentialUpsertResponse(
            credential.StoreCode ?? normalizedStoreCode,
            credential.Environment ?? normalizedEnvironment,
            credential.Username ?? username,
            !string.IsNullOrWhiteSpace(credential.Password),
            new DateTimeOffset(DateTime.SpecifyKind(credential.UpdatedAt ?? now, DateTimeKind.Utc)));
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

    private static string NormalizeRequired(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new LinklyCloudCredentialValidationException($"{fieldName} is required.")
            : value.Trim();
    }

    private static string? NormalizeUpdatedBy(string? updatedBy)
    {
        return string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();
    }
}

public sealed class LinklyCloudCredentialValidationException(string message) : Exception(message);

public interface ILinklyCloudCredentialRepository
{
    Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
        string storeCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudCredentialRecord> UpsertAsync(
        string storeCode,
        string environment,
        string username,
        string password,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarLinklyCloudCredentialRepository(
    HbposSqlSugarContext dbContext) : ILinklyCloudCredentialRepository
{
    internal const string UpsertSql = """
        MERGE [dbo].[POSM_LinklyCloudCredential] WITH (HOLDLOCK) AS target
        USING (SELECT @StoreCode AS [StoreCode], @Environment AS [Environment]) AS source
        ON target.[StoreCode] = source.[StoreCode]
           AND target.[Environment] = source.[Environment]
        WHEN MATCHED THEN
            UPDATE SET
                [Username] = @Username,
                [Password] = @Password,
                [UpdatedAt] = @UpdatedAt,
                [UpdatedBy] = @UpdatedBy
        WHEN NOT MATCHED THEN
            INSERT ([StoreCode], [Environment], [Username], [Password], [UpdatedAt], [UpdatedBy])
            VALUES (@StoreCode, @Environment, @Username, @Password, @UpdatedAt, @UpdatedBy);
        """;

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

    public async Task<LinklyCloudCredentialRecord> UpsertAsync(
        string storeCode,
        string environment,
        string username,
        string password,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        // 密码只写入数据库参数，响应映射只暴露 HasPassword，不回传明文。
        await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            UpsertSql,
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@Environment", environment),
            new SugarParameter("@Username", username),
            new SugarParameter("@Password", password),
            new SugarParameter("@UpdatedAt", updatedAt),
            new SugarParameter("@UpdatedBy", updatedBy));

        return await GetByStoreCodeAsync(storeCode, environment, cancellationToken)
            ?? new LinklyCloudCredentialRecord
            {
                StoreCode = storeCode,
                Environment = environment,
                Username = username,
                Password = password,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
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
