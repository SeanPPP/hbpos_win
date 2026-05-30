using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalInstallmentOrderRepositoryTests
{
    [Fact]
    public async Task Local_schema_service_creates_local_order_installments_table_and_indexes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalOrderInstallments';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'UX_LocalOrderInstallments_InstallmentGuid';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalOrderInstallments_Store_Status_CreatedAt';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'UX_LocalOrderInstallments_InstallmentNumber';"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertAsync_saves_and_reads_installment_snapshot()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var expected = CreateLocalInstallmentOrder();

            await schema.InitializeAsync();
            await repository.UpsertAsync(expected);

            var saved = await repository.GetAsync(expected.InstallmentGuid);

            Assert.NotNull(saved);
            Assert.Equal(expected.OrderGuid, saved.OrderGuid);
            Assert.Equal(expected.InstallmentNumber, saved.InstallmentNumber);
            Assert.Equal(expected.CustomerName, saved.CustomerName);
            Assert.Equal(expected.CustomerPhone, saved.CustomerPhone);
            Assert.Equal(expected.MinimumDownPayment, saved.MinimumDownPayment);
            Assert.Equal(expected.DownPaymentAmount, saved.DownPaymentAmount);
            Assert.Equal(expected.PaidAmount, saved.PaidAmount);
            Assert.Equal(expected.BalanceAmount, saved.BalanceAmount);
            Assert.Equal(expected.Status, saved.Status);
            Assert.Equal(expected.Note, saved.Note);
            Assert.NotNull(saved.PickupInfo);
            Assert.Equal(expected.PickupInfo!.PickedUpBy, saved.PickupInfo!.PickedUpBy);
            Assert.Equal(expected.Lines.Count, saved.Lines.Count);
            Assert.Equal(expected.Payments.Count, saved.Payments.Count);
            Assert.Equal(PaymentMethodKind.Card, saved.Payments[1].Method);
            Assert.Single(saved.Payments[1].CardTransactions!);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertAsync_updates_existing_snapshot_and_keeps_installment_fields_out_of_local_orders()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var original = CreateLocalInstallmentOrder();
            var updated = original with
            {
                InstallmentNumber = "IO-20260530-0009",
                UpdatedAt = original.UpdatedAt.AddMinutes(15),
                PaidAmount = 70m,
                BalanceAmount = 50m,
                Status = InstallmentStatus.PaidOff,
                Payments =
                [
                    .. original.Payments,
                    new InstallmentPaymentDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Cash,
                        40m,
                        null,
                        InstallmentPaymentStatus.Recorded,
                        original.UpdatedAt.AddMinutes(15),
                        "C001",
                        "POS-01")
                ]
            };

            await schema.InitializeAsync();
            await repository.UpsertAsync(original);
            await repository.UpsertAsync(updated);

            var saved = await repository.GetAsync(original.InstallmentGuid);

            Assert.NotNull(saved);
            Assert.Equal(updated.InstallmentNumber, saved.InstallmentNumber);
            Assert.Equal(updated.PaidAmount, saved.PaidAmount);
            Assert.Equal(updated.BalanceAmount, saved.BalanceAmount);
            Assert.Equal(updated.Status, saved.Status);
            Assert.Equal(3, saved.Payments.Count);

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalOrderInstallments;"));
            Assert.Equal(0, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM LocalOrders;"));
            Assert.Equal(0, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM SyncQueue;"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetRecentByStoreAsync_returns_requested_store_only_in_descending_created_order()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalInstallmentOrderRepository(store);
            var earliest = CreateLocalInstallmentOrder(
                installmentGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"),
                orderGuid: Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555"),
                installmentNumber: "IO-20260530-0001",
                storeCode: "S001",
                createdAt: DateTimeOffset.Parse("2026-05-30T10:00:00+10:00"));
            var latest = CreateLocalInstallmentOrder(
                installmentGuid: Guid.Parse("22222222-3333-4444-5555-666666666666"),
                orderGuid: Guid.Parse("bbbbbbbb-3333-4444-5555-666666666666"),
                installmentNumber: "IO-20260530-0002",
                storeCode: "S001",
                createdAt: DateTimeOffset.Parse("2026-05-30T11:00:00+10:00"));
            var otherStore = CreateLocalInstallmentOrder(
                installmentGuid: Guid.Parse("33333333-4444-5555-6666-777777777777"),
                orderGuid: Guid.Parse("cccccccc-4444-5555-6666-777777777777"),
                installmentNumber: "IO-20260530-0003",
                storeCode: "S002",
                createdAt: DateTimeOffset.Parse("2026-05-30T12:00:00+10:00"));

            await schema.InitializeAsync();
            await repository.UpsertAsync(earliest);
            await repository.UpsertAsync(latest);
            await repository.UpsertAsync(otherStore);

            var orders = await repository.GetRecentByStoreAsync("S001");

            Assert.Collection(
                orders,
                order =>
                {
                    Assert.Equal(latest.InstallmentGuid, order.InstallmentGuid);
                    Assert.Equal(latest.OrderGuid, order.OrderGuid);
                },
                order =>
                {
                    Assert.Equal(earliest.InstallmentGuid, order.InstallmentGuid);
                    Assert.Equal(earliest.OrderGuid, order.OrderGuid);
                });
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static LocalInstallmentOrder CreateLocalInstallmentOrder(
        Guid? installmentGuid = null,
        Guid? orderGuid = null,
        string installmentNumber = "IO-20260530-0001",
        string storeCode = "S001",
        DateTimeOffset? createdAt = null)
    {
        var effectiveCreatedAt = createdAt ?? DateTimeOffset.Parse("2026-05-30T10:00:00+10:00");
        var effectiveInstallmentGuid = installmentGuid ?? Guid.Parse("11111111-2222-3333-4444-555555555555");
        // 这里刻意把 OrderGuid 和 InstallmentGuid 拆开，防止仓储读写时把两个字段混掉。
        var effectiveOrderGuid = orderGuid ?? Guid.Parse("99999999-8888-7777-6666-555555555555");

        return new LocalInstallmentOrder(
            effectiveOrderGuid,
            effectiveInstallmentGuid,
            installmentNumber,
            storeCode,
            "POS-01",
            "C001",
            "Alice",
            "Alice Zhang",
            "0400111222",
            effectiveCreatedAt,
            effectiveCreatedAt.AddMinutes(5),
            120m,
            30m,
            30m,
            30m,
            90m,
            InstallmentStatus.Active,
            [
                new InstallmentLineDto(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    "SKU-001",
                    null,
                    "Premium Rice Cooker",
                    "690001",
                    1m,
                    120m,
                    0m,
                    120m,
                    "ITEM-001")
            ],
            [
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-1111-2222-3333-444444444444"),
                    PaymentMethodKind.Cash,
                    10m,
                    null,
                    InstallmentPaymentStatus.Recorded,
                    effectiveCreatedAt,
                    "C001",
                    "POS-01"),
                new InstallmentPaymentDto(
                    Guid.Parse("12345678-5555-6666-7777-888888888888"),
                    PaymentMethodKind.Card,
                    20m,
                    "ANZ:TXN-INS-1",
                    InstallmentPaymentStatus.Recorded,
                    effectiveCreatedAt.AddMinutes(1),
                    "C001",
                    "POS-01",
                    [
                        new CardTransactionDto(
                            "ANZ",
                            "TXN-INS-1",
                            "AUTH-001",
                            "VISA",
                            4,
                            "****1234",
                            "MID-001",
                            "00",
                            "APPROVED",
                            "001122",
                            effectiveCreatedAt.AddMinutes(1),
                            20m,
                            "merchant receipt")
                    ])
            ],
            new InstallmentPickupInfoDto(effectiveCreatedAt.AddDays(3), "Bob", "confirmed by phone"),
            "weekend pickup");
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-installment-repo-{Guid.NewGuid():N}.db");
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
