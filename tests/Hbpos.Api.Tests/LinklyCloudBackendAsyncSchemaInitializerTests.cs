using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudBackendAsyncSchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_executes_idempotent_backend_async_session_and_notification_ddl()
    {
        var executor = new CapturingLinklyCloudBackendAsyncSchemaSqlExecutor();
        var initializer = new SqlSugarLinklyCloudBackendAsyncSchemaInitializer(executor);

        await initializer.InitializeAsync();

        Assert.Single(executor.SqlStatements);
        var sql = executor.SqlStatements[0];
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NULL", sql);
        Assert.Contains("[Environment] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[StoreCode] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[DeviceCode] NVARCHAR(64) NOT NULL", sql);
        Assert.Contains("[SessionId] NVARCHAR(64) NOT NULL", sql);
        Assert.Contains("[TxnRef] NVARCHAR(16) NULL", sql);
        Assert.Contains("[DisplayText] NVARCHAR(512) NULL", sql);
        Assert.Contains("[DisplayLines] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[CancelKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[OKKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[AcceptYesKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[DeclineNoKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[AuthoriseKeyFlag] BIT NOT NULL", sql);
        Assert.Contains("[InputType] NVARCHAR(64) NULL", sql);
        Assert.Contains("[GraphicCode] NVARCHAR(64) NULL", sql);
        Assert.Contains("[ReceiptText] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[RecoveryCount] INT NOT NULL", sql);
        Assert.Contains("[ReceiptPrintedAt] DATETIME2(7) NULL", sql);
        Assert.Contains("[LastHttpStatus] INT NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayText') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayLines') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'CancelKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OKKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AcceptYesKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DeclineNoKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AuthoriseKeyFlag') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'InputType') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'GraphicCode') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptText') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RecoveryCount') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptPrintedAt') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LastHttpStatus') IS NULL", sql);
        Assert.Contains("UNIQUE ([Environment], [StoreCode], [DeviceCode], [SessionId])", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendSession_ActiveTerminal", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendSession_TxnRef", sql);
        Assert.Contains("[Environment], [StoreCode], [TxnRef]", sql);
        Assert.Contains("WHERE [IsActive] = 1", sql);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U') IS NULL", sql);
        Assert.Contains("[PayloadJson] NVARCHAR(MAX) NOT NULL", sql);
        Assert.Contains("IX_POSM_LinklyCloudBackendNotification_Scope", sql);
        Assert.Contains("[Environment], [StoreCode], [DeviceCode], [SessionId]", sql);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U') IS NULL", sql);
        Assert.Contains("[Secret] NVARCHAR(512) NOT NULL", sql);
        Assert.Contains("[PosId] NVARCHAR(64) NOT NULL", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendTerminal_Scope", sql);
        Assert.Contains("UNIQUE ([Environment], [StoreCode], [DeviceCode])", sql);
    }

    private sealed class CapturingLinklyCloudBackendAsyncSchemaSqlExecutor : ILinklyCloudBackendAsyncSchemaSqlExecutor
    {
        public List<string> SqlStatements { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            SqlStatements.Add(sql);
            return Task.CompletedTask;
        }
    }
}
