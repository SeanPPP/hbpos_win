using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class SquareTokenSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_table_and_index_ddl()
    {
        var executor = new CapturingSquareTokenSchemaSqlExecutor();
        var initializer = new SqlSugarSquareTokenSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(2, executor.SqlStatements.Count);
        var combinedSql = string.Join(Environment.NewLine, executor.SqlStatements);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_SquareToken]', N'U') IS NULL", combinedSql);
        Assert.Contains("[Id] BIGINT IDENTITY(1,1)", combinedSql);
        Assert.Contains("[Environment] NVARCHAR(32) NOT NULL", combinedSql);
        Assert.Contains("[AccessToken] NVARCHAR(2048) NOT NULL", combinedSql);
        Assert.Contains("[IsEnabled] BIT NOT NULL", combinedSql);
        Assert.Contains("[UpdatedAt] DATETIME2(7) NOT NULL", combinedSql);
        Assert.Contains("[UpdatedBy] NVARCHAR(128) NULL", combinedSql);
        Assert.Contains("CHECK ([Environment] IN (N'Production', N'Sandbox'))", combinedSql);
        Assert.Contains("UX_POSM_SquareToken_Environment_Enabled", combinedSql);
        Assert.Contains("WHERE [IsEnabled] = 1", combinedSql);
    }

    private sealed class CapturingSquareTokenSchemaSqlExecutor : ISquareTokenSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}
