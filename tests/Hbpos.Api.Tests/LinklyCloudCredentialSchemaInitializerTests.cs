using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudCredentialSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_linkly_cloud_credential_ddl()
    {
        var executor = new CapturingLinklyCloudCredentialSchemaSqlExecutor();
        var initializer = new SqlSugarLinklyCloudCredentialSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(4, executor.SqlStatements.Count);
        var sql = string.Join(Environment.NewLine, executor.SqlStatements);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NULL", sql);
        Assert.Contains("[StoreCode] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[Environment] NVARCHAR(32) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudCredential_Environment] DEFAULT (N'Production')", sql);
        Assert.Contains("[Username] NVARCHAR(256) NOT NULL", sql);
        Assert.Contains("[Password] NVARCHAR(256) NOT NULL", sql);
        Assert.Contains("[UpdatedAt] DATETIME2(7) NOT NULL", sql);
        Assert.Contains("[UpdatedBy] NVARCHAR(128) NULL", sql);
        Assert.Contains("SET [Environment] = N'Production'", sql);
        Assert.Contains("CONSTRAINT [CK_POSM_LinklyCloudCredential_Environment]", sql);
        Assert.Contains("DROP CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode]", sql);
        Assert.Contains("CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment]", sql);
        Assert.Contains("UNIQUE ([StoreCode], [Environment])", sql);
    }

    [Fact]
    public async Task InitializeAsync_executes_environment_column_batch_before_constraint_batch()
    {
        var executor = new CapturingLinklyCloudCredentialSchemaSqlExecutor();
        var initializer = new SqlSugarLinklyCloudCredentialSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Equal(4, executor.SqlStatements.Count);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudCredential', N'Environment') IS NULL", executor.SqlStatements[1]);
        Assert.DoesNotContain("SET [Environment] = N'Production'", executor.SqlStatements[1]);
        Assert.Contains("SET [Environment] = N'Production'", executor.SqlStatements[2]);
        Assert.Contains("CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode_Environment]", executor.SqlStatements[3]);
    }

    private sealed class CapturingLinklyCloudCredentialSchemaSqlExecutor : ILinklyCloudCredentialSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}
