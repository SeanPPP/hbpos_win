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

        Assert.Single(executor.SqlStatements);
        var sql = executor.SqlStatements[0];
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NULL", sql);
        Assert.Contains("[StoreCode] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[Username] NVARCHAR(256) NOT NULL", sql);
        Assert.Contains("[Password] NVARCHAR(256) NOT NULL", sql);
        Assert.Contains("[UpdatedAt] DATETIME2(7) NOT NULL", sql);
        Assert.Contains("[UpdatedBy] NVARCHAR(128) NULL", sql);
        Assert.Contains("CONSTRAINT [UX_POSM_LinklyCloudCredential_StoreCode] UNIQUE ([StoreCode])", sql);
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
