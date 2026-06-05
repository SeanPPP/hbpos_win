using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalCardPaymentAttemptRepositoryTests
{
    [Fact]
    public async Task Local_schema_service_creates_local_card_payment_attempts_table_and_indexes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalCardPaymentAttempts';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalCardPaymentAttempts_RecoverLatest';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalSquarePaymentAttempts';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalSquarePaymentAttempts_RecoverLatest';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalSquarePaymentAttempts_CheckoutId';"));
            Assert.Equal(
                [
                    "Pending",
                    "SessionStarted",
                    "Recovering",
                    "Approved",
                    "Declined",
                    "TimedOut",
                    "Cancelled",
                    "Failed",
                    "OrderCompleted",
                    "Abandoned"
                ],
                Enum.GetNames<LocalCardPaymentAttemptStatus>());
            Assert.Equal(
                [
                    "Pending",
                    "CheckoutCreated",
                    "Recovering",
                    "CheckoutCompleted",
                    "PaymentVerified",
                    "Canceled",
                    "TimedOut",
                    "Failed",
                    "Unknown",
                    "OrderCompleted",
                    "Abandoned"
                ],
                Enum.GetNames<LocalSquarePaymentAttemptStatus>());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_attempt_repository_persists_checkout_payment_and_order_completion()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var attempt = CreateSquareAttempt();
            var checkoutAt = attempt.CreatedAt.AddMinutes(1);
            var paymentAt = attempt.CreatedAt.AddMinutes(2);
            var completedAt = attempt.CreatedAt.AddMinutes(3);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);
            await repository.MarkCheckoutCreatedAsync(attempt.AttemptGuid, "checkout-1", "PENDING", checkoutAt);
            await repository.MarkPaymentVerifiedAsync(
                attempt.AttemptGuid,
                "payment-1",
                "COMPLETED",
                null,
                "Payment verified.",
                paymentAt);
            await repository.MarkOrderCompletedAsync(attempt.AttemptGuid, completedAt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.NotNull(saved);
            Assert.Equal("checkout-1", saved.CheckoutId);
            Assert.Equal("payment-1", saved.PaymentId);
            Assert.Equal("COMPLETED", saved.PaymentStatus);
            Assert.Equal(LocalSquarePaymentAttemptStatus.OrderCompleted, saved.Status);
            Assert.Equal(paymentAt, saved.CompletedAt);
            Assert.Equal(completedAt, saved.OrderCompletedAt);
            Assert.Equal(completedAt, saved.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Square_attempt_repository_gets_latest_open_attempt_with_scope_filter()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalSquarePaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-05T09:00:00+10:00");
            var olderOpen = CreateSquareAttempt(
                attemptGuid: Guid.Parse("11111111-aaaa-1111-1111-111111111111"),
                status: LocalSquarePaymentAttemptStatus.Pending,
                updatedAt: baseTime);
            var latestOpen = CreateSquareAttempt(
                attemptGuid: Guid.Parse("22222222-aaaa-2222-2222-222222222222"),
                status: LocalSquarePaymentAttemptStatus.PaymentVerified,
                updatedAt: baseTime.AddMinutes(1));
            var terminal = CreateSquareAttempt(
                attemptGuid: Guid.Parse("33333333-aaaa-3333-3333-333333333333"),
                status: LocalSquarePaymentAttemptStatus.Failed,
                updatedAt: baseTime.AddMinutes(2));
            var otherCashier = CreateSquareAttempt(
                attemptGuid: Guid.Parse("44444444-aaaa-4444-4444-444444444444"),
                cashierId: "C002",
                status: LocalSquarePaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(3));

            await schema.InitializeAsync();
            await repository.CreateAsync(olderOpen);
            await repository.CreateAsync(latestOpen);
            await repository.CreateAsync(terminal);
            await repository.CreateAsync(otherCashier);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", "C001", "Production");

            Assert.NotNull(saved);
            Assert.Equal(latestOpen.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(LocalSquarePaymentAttemptStatus.PaymentVerified, saved.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task CreateAsync_saves_and_reads_card_payment_attempt()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var expected = CreateAttempt(orderDraftJson: """{"orderGuid":"ORDER-1","lines":[]}""");

            await schema.InitializeAsync();
            await repository.CreateAsync(expected);

            var saved = await repository.GetAttemptAsync(expected.AttemptGuid);

            Assert.NotNull(saved);
            Assert.Equal(expected.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(expected.SessionId, saved.SessionId);
            Assert.Equal(expected.TxnRef, saved.TxnRef);
            Assert.Equal(expected.Processor, saved.Processor);
            Assert.Equal(expected.Environment, saved.Environment);
            Assert.Equal(expected.ConnectionMode, saved.ConnectionMode);
            Assert.Equal(expected.TxnType, saved.TxnType);
            Assert.Equal(expected.Amount, saved.Amount);
            Assert.Equal(expected.Status, saved.Status);
            Assert.Equal(expected.OrderDraftJson, saved.OrderDraftJson);
            Assert.Equal(expected.StoreCode, saved.StoreCode);
            Assert.Equal(expected.DeviceCode, saved.DeviceCode);
            Assert.Equal(expected.CashierId, saved.CashierId);
            Assert.Equal(expected.ResponseCode, saved.ResponseCode);
            Assert.Equal(expected.ResponseText, saved.ResponseText);
            Assert.Equal(expected.PaymentReference, saved.PaymentReference);
            Assert.Equal(expected.CreatedAt, saved.CreatedAt);
            Assert.Equal(expected.UpdatedAt, saved.UpdatedAt);
            Assert.Equal(expected.CompletedAt, saved.CompletedAt);
            Assert.Equal(expected.AcknowledgedAt, saved.AcknowledgedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Status_update_methods_persist_session_outcome_completion_and_acknowledgement()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var attempt = CreateAttempt();
            var sessionAt = attempt.CreatedAt.AddMinutes(1);
            var approvedAt = attempt.CreatedAt.AddMinutes(2);
            var completedAt = attempt.CreatedAt.AddMinutes(3);
            var acknowledgedAt = attempt.CreatedAt.AddMinutes(4);

            await schema.InitializeAsync();
            await repository.CreateAsync(attempt);
            await repository.UpdateSessionAsync(attempt.AttemptGuid, "SESSION-001", "TXN-001", sessionAt);
            await repository.UpdateOutcomeAsync(
                attempt.AttemptGuid,
                LocalCardPaymentAttemptStatus.Approved,
                "00",
                "APPROVED",
                "PAYMENT-001",
                approvedAt);
            await repository.MarkOrderCompletedAsync(attempt.AttemptGuid, completedAt);
            await repository.MarkAcknowledgedAsync(attempt.AttemptGuid, acknowledgedAt);

            var saved = await repository.GetAttemptAsync(attempt.AttemptGuid);

            Assert.NotNull(saved);
            Assert.Equal("SESSION-001", saved.SessionId);
            Assert.Equal("TXN-001", saved.TxnRef);
            Assert.Equal(LocalCardPaymentAttemptStatus.OrderCompleted, saved.Status);
            Assert.Equal("00", saved.ResponseCode);
            Assert.Equal("APPROVED", saved.ResponseText);
            Assert.Equal("PAYMENT-001", saved.PaymentReference);
            Assert.Equal(approvedAt, saved.CompletedAt);
            Assert.Equal(acknowledgedAt, saved.AcknowledgedAt);
            Assert.Equal(acknowledgedAt, saved.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetLatestOpenAttemptAsync_filters_scope_and_ignores_terminal_statuses()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalCardPaymentAttemptRepository(store);
            var baseTime = DateTimeOffset.Parse("2026-06-05T09:00:00+10:00");
            var olderOpen = CreateAttempt(
                attemptGuid: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                status: LocalCardPaymentAttemptStatus.Pending,
                updatedAt: baseTime);
            var latestOpen = CreateAttempt(
                attemptGuid: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                status: LocalCardPaymentAttemptStatus.Approved,
                updatedAt: baseTime.AddMinutes(1));
            var terminal = CreateAttempt(
                attemptGuid: Guid.Parse("33333333-3333-3333-3333-333333333333"),
                status: LocalCardPaymentAttemptStatus.Declined,
                updatedAt: baseTime.AddMinutes(2));
            var otherCashier = CreateAttempt(
                attemptGuid: Guid.Parse("44444444-4444-4444-4444-444444444444"),
                cashierId: "C002",
                status: LocalCardPaymentAttemptStatus.Recovering,
                updatedAt: baseTime.AddMinutes(3));
            var otherEnvironment = CreateAttempt(
                attemptGuid: Guid.Parse("55555555-5555-5555-5555-555555555555"),
                environment: "prod",
                status: LocalCardPaymentAttemptStatus.SessionStarted,
                updatedAt: baseTime.AddMinutes(4));

            await schema.InitializeAsync();
            await repository.CreateAsync(olderOpen);
            await repository.CreateAsync(latestOpen);
            await repository.CreateAsync(terminal);
            await repository.CreateAsync(otherCashier);
            await repository.CreateAsync(otherEnvironment);

            var saved = await repository.GetLatestOpenAttemptAsync("S001", "POS-01", "C001", "sandbox");

            Assert.NotNull(saved);
            Assert.Equal(latestOpen.AttemptGuid, saved.AttemptGuid);
            Assert.Equal(LocalCardPaymentAttemptStatus.Approved, saved.Status);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static LocalCardPaymentAttempt CreateAttempt(
        Guid? attemptGuid = null,
        string storeCode = "S001",
        string deviceCode = "POS-01",
        string cashierId = "C001",
        string environment = "sandbox",
        LocalCardPaymentAttemptStatus status = LocalCardPaymentAttemptStatus.Pending,
        DateTimeOffset? updatedAt = null,
        string orderDraftJson = "{}")
    {
        var effectiveUpdatedAt = updatedAt ?? DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");

        return new LocalCardPaymentAttempt(
            attemptGuid ?? Guid.Parse("99999999-8888-7777-6666-555555555555"),
            null,
            null,
            "Linkly",
            environment,
            "Cloud",
            "Purchase",
            12.34m,
            status,
            orderDraftJson,
            storeCode,
            deviceCode,
            cashierId,
            null,
            null,
            null,
            effectiveUpdatedAt.AddMinutes(-1),
            effectiveUpdatedAt,
            null,
            null);
    }

    private static LocalSquarePaymentAttempt CreateSquareAttempt(
        Guid? attemptGuid = null,
        string storeCode = "S001",
        string deviceCode = "POS-01",
        string cashierId = "C001",
        string environment = "Production",
        LocalSquarePaymentAttemptStatus status = LocalSquarePaymentAttemptStatus.Pending,
        DateTimeOffset? updatedAt = null,
        string orderDraftJson = "{}")
    {
        var effectiveUpdatedAt = updatedAt ?? DateTimeOffset.Parse("2026-06-05T10:00:00+10:00");

        return new LocalSquarePaymentAttempt(
            attemptGuid ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            null,
            "idem-1",
            "DEV-1",
            "LOC-1",
            environment,
            12.34m,
            1234,
            "AUD",
            status,
            null,
            null,
            orderDraftJson,
            storeCode,
            deviceCode,
            cashierId,
            null,
            null,
            null,
            null,
            effectiveUpdatedAt.AddMinutes(-1),
            effectiveUpdatedAt,
            null,
            null,
            null);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-card-attempt-repo-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<int> ReadScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
