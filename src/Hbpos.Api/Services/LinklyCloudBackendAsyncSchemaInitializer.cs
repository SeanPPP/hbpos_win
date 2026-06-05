using Hbpos.Api.Data;

namespace Hbpos.Api.Services;

public interface ILinklyCloudBackendAsyncSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ILinklyCloudBackendAsyncSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarLinklyCloudBackendAsyncSchemaInitializer(
    ILinklyCloudBackendAsyncSchemaSqlExecutor sqlExecutor) : ILinklyCloudBackendAsyncSchemaInitializer
{
    // 异步交易状态和通知都按环境、门店、设备、会话四段 scope 隔离。
    internal const string EnsureTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudBackendSession] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudBackendSession] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [SessionId] NVARCHAR(64) NOT NULL,
                [Status] NVARCHAR(32) NOT NULL,
                [TxnRef] NVARCHAR(16) NULL,
                [ResponseCode] NVARCHAR(32) NULL,
                [ResponseText] NVARCHAR(512) NULL,
                [RecoveryAction] NVARCHAR(64) NULL,
                [DisplayText] NVARCHAR(512) NULL,
                [DisplayLines] NVARCHAR(MAX) NULL,
                [CancelKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CancelKeyFlag] DEFAULT (0),
                [OKKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OKKeyFlag] DEFAULT (0),
                [AcceptYesKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AcceptYesKeyFlag] DEFAULT (0),
                [DeclineNoKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_DeclineNoKeyFlag] DEFAULT (0),
                [AuthoriseKeyFlag] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AuthoriseKeyFlag] DEFAULT (0),
                [InputType] NVARCHAR(64) NULL,
                [GraphicCode] NVARCHAR(64) NULL,
                [ReceiptText] NVARCHAR(MAX) NULL,
                [RecoveryCount] INT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_RecoveryCount] DEFAULT (0),
                [ReceiptPrintedAt] DATETIME2(7) NULL,
                [ClientAcknowledgedAt] DATETIME2(7) NULL,
                [LastHttpStatus] INT NULL,
                [IsActive] BIT NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_IsActive] DEFAULT (0),
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendSession_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_LinklyCloudBackendSession_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [UX_POSM_LinklyCloudBackendSession_Scope] UNIQUE ([Environment], [StoreCode], [DeviceCode], [SessionId])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayText') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [DisplayText] NVARCHAR(512) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DisplayLines') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [DisplayLines] NVARCHAR(MAX) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'CancelKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [CancelKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_CancelKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'OKKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [OKKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_OKKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AcceptYesKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [AcceptYesKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AcceptYesKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'DeclineNoKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [DeclineNoKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_DeclineNoKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'AuthoriseKeyFlag') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [AuthoriseKeyFlag] BIT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_AuthoriseKeyFlag_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'InputType') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [InputType] NVARCHAR(64) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'GraphicCode') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [GraphicCode] NVARCHAR(64) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptText') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [ReceiptText] NVARCHAR(MAX) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'RecoveryCount') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [RecoveryCount] INT NOT NULL
                        CONSTRAINT [DF_POSM_LinklyCloudBackendSession_RecoveryCount_Upgrade] DEFAULT (0) WITH VALUES;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ReceiptPrintedAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [ReceiptPrintedAt] DATETIME2(7) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'ClientAcknowledgedAt') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [ClientAcknowledgedAt] DATETIME2(7) NULL;
            END;

            IF COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'LastHttpStatus') IS NULL
            BEGIN
                ALTER TABLE [dbo].[POSM_LinklyCloudBackendSession]
                    ADD [LastHttpStatus] INT NULL;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendSession_ActiveTerminal')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendSession_ActiveTerminal]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [StoreCode], [DeviceCode])
                    WHERE [IsActive] = 1;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendSession]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendSession_TxnRef')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendSession_TxnRef]
                    ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [StoreCode], [TxnRef])
                    WHERE [TxnRef] IS NOT NULL;
            END;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudBackendTerminal] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudBackendTerminal] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [Secret] NVARCHAR(512) NOT NULL,
                [PosId] NVARCHAR(64) NOT NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendTerminal_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                [UpdatedBy] NVARCHAR(128) NULL,
                CONSTRAINT [CK_POSM_LinklyCloudBackendTerminal_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox')),
                CONSTRAINT [UX_POSM_LinklyCloudBackendTerminal_Scope] UNIQUE ([Environment], [StoreCode], [DeviceCode])
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U')
                  AND [name] = N'UX_POSM_LinklyCloudBackendTerminal_Scope')
            BEGIN
                CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendTerminal_Scope]
                    ON [dbo].[POSM_LinklyCloudBackendTerminal] ([Environment], [StoreCode], [DeviceCode]);
            END;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_LinklyCloudBackendNotification] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_LinklyCloudBackendNotification] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [StoreCode] NVARCHAR(32) NOT NULL,
                [DeviceCode] NVARCHAR(64) NOT NULL,
                [SessionId] NVARCHAR(64) NOT NULL,
                [Type] NVARCHAR(64) NOT NULL,
                [PayloadJson] NVARCHAR(MAX) NOT NULL,
                [ReceivedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_LinklyCloudBackendNotification_ReceivedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_LinklyCloudBackendNotification_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;

        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U') IS NOT NULL
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendNotification]', N'U')
                  AND [name] = N'IX_POSM_LinklyCloudBackendNotification_Scope')
            BEGIN
                CREATE INDEX [IX_POSM_LinklyCloudBackendNotification_Scope]
                    ON [dbo].[POSM_LinklyCloudBackendNotification] ([Environment], [StoreCode], [DeviceCode], [SessionId], [ReceivedAt]);
            END;
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure start");
        try
        {
            await sqlExecutor.ExecuteAsync(EnsureTableSql, cancellationToken);
            Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure succeeded");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure canceled");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} backend async schema ensure failed error={ex.GetType().Name}");
            throw;
        }
    }
}

public sealed class SqlSugarLinklyCloudBackendAsyncSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : ILinklyCloudBackendAsyncSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}
