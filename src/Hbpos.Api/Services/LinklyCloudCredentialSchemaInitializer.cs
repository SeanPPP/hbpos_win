using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface ILinklyCloudCredentialSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ILinklyCloudCredentialSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarLinklyCloudCredentialSchemaInitializer(
    ILinklyCloudCredentialSchemaSqlExecutor sqlExecutor) : ILinklyCloudCredentialSchemaInitializer
{
    internal const string EnsureTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudCredential] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudCredential] PRIMARY KEY,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [Username] NVARCHAR(256) NOT NULL,
                [Password] NVARCHAR(256) NOT NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudCredential_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode] UNIQUE ([StoreCode])
            );
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloud] {DateTimeOffset.Now:O} credential schema ensure start table=POSM_LinklyCloudCredential");
        try
        {
            await sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
            Console.WriteLine($"[HBPOS][Api][LinklyCloud] {DateTimeOffset.Now:O} credential schema ensure succeeded table=POSM_LinklyCloudCredential");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[HBPOS][Api][LinklyCloud] {DateTimeOffset.Now:O} credential schema ensure canceled");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HBPOS][Api][LinklyCloud] {DateTimeOffset.Now:O} credential schema ensure failed error={ex.GetType().Name}");
            throw;
        }
    }
}

public sealed class SqlSugarLinklyCloudCredentialSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : ILinklyCloudCredentialSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
