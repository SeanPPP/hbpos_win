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
    // 旧表升级时补齐环境维度，并保持脚本可重复执行。
    internal const string EnsureTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudCredential] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudCredential] PRIMARY KEY,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [Environment] NVARCHAR(32) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment] DEFAULT (N'Production'),
                [Username] NVARCHAR(256) NOT NULL,
                [Password] NVARCHAR(256) NOT NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudCredential_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_LinklyCloudCredential_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment] UNIQUE ([StoreCode], [Environment])
            );
        END;
        """;

    // Environment 列新增后需要进入下一批次再引用，避免 SQL Server 编译旧表结构时报“列名无效”。
    internal const string EnsureEnvironmentColumnSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudCredential', N'Environment') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ADD [Environment] NVARCHAR(32) NOT NULL
                    CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment] DEFAULT (N'Production') WITH VALUES;
            END
        END;
        """;

    // 这一段只在补列批次完成后执行，保证下面的 Environment 引用一定能被 SQL Server 解析。
    internal const string NormalizeEnvironmentColumnSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_LinklyCloudCredential', N'Environment') IS NOT NULL
        BEGIN
            UPDATE [dbo].[POSM_LinklyCloudCredential]
            SET [Environment] = N'Production'
            WHERE NULLIF(LTRIM(RTRIM([Environment])), N'') IS NULL;

            IF EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U')
                  AND [name] = N'Environment'
                  AND [is_nullable] = 1)
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ALTER COLUMN [Environment] NVARCHAR(32) NOT NULL;
            END;
        END;
        """;

    internal const string EnsureConstraintsSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NOT NULL
        BEGIN
            IF OBJECT_ID(N'[dbo].[DF_POSM_LinklyCloudCredential_Environment]', N'D') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ADD CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment]
                    DEFAULT (N'Production') FOR [Environment];
            END;

            IF OBJECT_ID(N'[dbo].[UX_POSM_LinklyCloudCredential_StoreCode]', N'UQ') IS NOT NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    DROP CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode];
            END;

            IF OBJECT_ID(N'[dbo].[CK_POSM_LinklyCloudCredential_Environment]', N'C') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential] WITH CHECK
                    ADD CONSTRAINT [CK_POSM_LinklyCloudCredential_Environment]
                    CHECK ([Environment] IN (N'Production', N'Sandbox'));
            END;

            IF OBJECT_ID(N'[dbo].[UX_POSM_LinklyCloudCredential_StoreCode_Environment]', N'UQ') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudCredential]
                    ADD CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment]
                    UNIQUE ([StoreCode], [Environment]);
            END;
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloud] {DateTimeOffset.Now:O} credential schema ensure start table=POSM_LinklyCloudCredential");
        try
        {
            await sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
            await sqlExecutor.ExecuteAsync(EnsureEnvironmentColumnSql, cancellationToken);
            await sqlExecutor.ExecuteAsync(NormalizeEnvironmentColumnSql, cancellationToken);
            await sqlExecutor.ExecuteAsync(EnsureConstraintsSql, cancellationToken);
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
