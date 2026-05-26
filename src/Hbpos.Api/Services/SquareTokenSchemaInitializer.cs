using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface ISquareTokenSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ISquareTokenSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarSquareTokenSchemaInitializer(
    ISquareTokenSchemaSqlExecutor sqlExecutor) : ISquareTokenSchemaInitializer
{
    internal const string EnsureTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareToken]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareToken] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_SquareToken] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [AccessToken] NVARCHAR(2048) NOT NULL,
                [IsEnabled] BIT NOT NULL CONSTRAINT [DF_POSM_SquareToken_IsEnabled] DEFAULT (0),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareToken_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_SquareToken_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;
        """;

    internal const string EnsureEnabledTokenIndexSql = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = N'UX_POSM_SquareToken_Environment_Enabled'
              AND object_id = OBJECT_ID(N'[dbo].[POSM_SquareToken]')
        )
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_SquareToken_Environment_Enabled]
            ON [dbo].[POSM_SquareToken]([Environment])
            WHERE [IsEnabled] = 1;
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureEnabledTokenIndexSql, cancellationToken);
    }
}

public sealed class SqlSugarSquareTokenSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : ISquareTokenSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
