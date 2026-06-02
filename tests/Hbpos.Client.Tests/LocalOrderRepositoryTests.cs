using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalOrderRepositoryTests
{
    [Fact]
    public async Task SavePendingOrderAsync_persists_voucher_refund_idempotency_key_and_updates_reference_idempotently()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var repository = new LocalOrderRepository(store);
            var order = CreateOrder();
            var payment = Assert.Single(order.Payments);

            await schema.InitializeAsync();
            await repository.SavePendingOrderAsync(order);
            await repository.UpdatePaymentReferenceAsync(payment.PaymentGuid, "VOUCHER_REFUND:RF123");
            await repository.UpdatePaymentReferenceAsync(payment.PaymentGuid, "VOUCHER_REFUND:RF123");

            var saved = await repository.GetOrderAsync(order.OrderGuid);

            Assert.NotNull(saved);
            var savedPayment = Assert.Single(saved.Payments);
            Assert.Equal("VOUCHER_REFUND:RF123", savedPayment.Reference);
            Assert.Equal(payment.IdempotencyKey, savedPayment.IdempotencyKey);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static LocalOrder CreateOrder()
    {
        return new LocalOrder(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.Parse("2026-06-02T10:30:00+10:00"),
            6m,
            0m,
            -6m,
            [
                new LocalOrderLine(
                    Guid.NewGuid(),
                    "SKU-VR-LOCAL",
                    null,
                    "Voucher Refund Local",
                    "930600",
                    "ITEM-VR-LOCAL",
                    1m,
                    6m,
                    0m,
                    -6m,
                    PriceSourceKind.StoreRetailPrice,
                    OrderLineKind.Return,
                    "RETURN:LOCAL-VR",
                    Guid.NewGuid(),
                    Guid.NewGuid())
            ],
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Voucher,
                    -6m,
                    "VOUCHER_REFUND_PENDING",
                    IdempotencyKey: "refund-key-001")
            ]);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-local-order-repo-{Guid.NewGuid():N}.db");
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
}
