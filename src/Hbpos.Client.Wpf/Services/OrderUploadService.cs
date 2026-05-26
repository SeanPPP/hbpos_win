using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Orders;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface IOrderUploadService
{
    Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default);
}

public interface IOrderUploadExecutionService
{
    Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default);
}

public sealed class OrderUploadService(
    ILocalOrderRepository orderRepository,
    IOrderSyncApiClient apiClient,
    ILocalOrderUploadRepository uploadRepository) : IOrderUploadService
{
    public async Task UploadOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        var order = await orderRepository.GetOrderAsync(orderGuid, cancellationToken)
            ?? throw new InvalidOperationException("Order was not found for upload.");
        try
        {
            await uploadRepository.MarkSyncingAsync(orderGuid, cancellationToken);
            var response = await apiClient.SyncAsync(ToRequest(order), cancellationToken);
            if (!response.Accepted)
            {
                throw new InvalidOperationException(response.Message ?? "Order sync was not accepted.");
            }

            await uploadRepository.MarkSyncedAsync(orderGuid, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await uploadRepository.MarkFailedAsync(orderGuid, ex.Message, cancellationToken);
            throw;
        }
    }

    private static OrderSyncRequest ToRequest(LocalOrder order)
    {
        return new OrderSyncRequest(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierId,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.Lines.Select(line => new OrderLineSyncDto(
                line.OrderLineGuid,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.ActualAmount,
                line.PriceSource,
                line.ItemNumber)).ToList(),
            order.Payments.Select(ToPaymentSyncDto).ToList());
    }

    private static PaymentSyncDto ToPaymentSyncDto(LocalPayment payment)
    {
        if (payment.Method == PaymentMethodKind.Voucher)
        {
            var (voucherCode, reservationToken) = ParseVoucherReference(payment.Reference);
            return new PaymentSyncDto(
                payment.PaymentGuid,
                payment.Method,
                payment.Amount,
                voucherCode,
                reservationToken,
                payment.CardTransactions);
        }

        return new PaymentSyncDto(
            payment.PaymentGuid,
            payment.Method,
            payment.Amount,
            payment.Reference,
            CardTransactions: payment.CardTransactions);
    }

    private static (string VoucherCode, string ReservationToken) ParseVoucherReference(string? reference)
    {
        var parts = (reference ?? string.Empty).Split(':', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 && parts[0].Equals("VOUCHER", StringComparison.OrdinalIgnoreCase)
            ? (parts[1], parts[2])
            : (reference ?? string.Empty, string.Empty);
    }
}

public sealed class OrderUploadExecutionService(
    IOrderUploadService uploadService,
    ILocalOrderUploadRepository uploadRepository) : IOrderUploadExecutionService
{
    public async Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        var orderGuids = await uploadRepository.GetPendingOrderGuidsAsync(batchSize, cancellationToken);
        var uploadedCount = 0;
        var failedCount = 0;

        foreach (var orderGuid in orderGuids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await uploadService.UploadOrderAsync(orderGuid, cancellationToken);
                uploadedCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failedCount++;
            }
        }

        return new OrderUploadExecutionResult(orderGuids.Count, uploadedCount, failedCount);
    }
}

public interface IOrderSyncApiClient
{
    Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default);
}

public sealed class OrderSyncApiClient(HttpClient httpClient) : IOrderSyncApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/v1/orders/sync", request, JsonOptions, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<OrderSyncResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            result = JsonSerializer.Deserialize<ApiResult<OrderSyncResponse>>(content, JsonOptions);
        }

        if (!response.IsSuccessStatusCode || result is null || !result.Success || result.Data is null)
        {
            throw new CatalogApiException(
                result?.Message ?? $"Order sync failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        return result.Data;
    }
}

public interface ILocalOrderUploadRepository
{
    Task<IReadOnlyList<Guid>> GetPendingOrderGuidsAsync(int take = 20, CancellationToken cancellationToken = default);

    Task MarkSyncingAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task MarkSyncedAsync(Guid orderGuid, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default);
}

public sealed class LocalOrderUploadRepository(LocalSqliteStore store) : ILocalOrderUploadRepository
{
    public async Task<IReadOnlyList<Guid>> GetPendingOrderGuidsAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT EntityId
            FROM SyncQueue
            WHERE EntityType = 'Order'
              AND Status IN ('Pending', 'Failed')
            ORDER BY CreatedAt
            LIMIT $Take;
            """;
        command.Parameters.AddWithValue("$Take", Math.Clamp(take, 1, 100));

        var orderGuids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var orderGuid))
            {
                orderGuids.Add(orderGuid);
            }
        }

        return orderGuids;
    }

    public Task MarkSyncingAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Syncing", null, cancellationToken);
    }

    public Task MarkSyncedAsync(Guid orderGuid, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Synced", null, cancellationToken);
    }

    public Task MarkFailedAsync(Guid orderGuid, string errorMessage, CancellationToken cancellationToken = default)
    {
        return UpdateStatusAsync(orderGuid, "Failed", errorMessage, cancellationToken);
    }

    private async Task UpdateStatusAsync(
        Guid orderGuid,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var orderCommand = connection.CreateCommand())
        {
            orderCommand.Transaction = transaction;
            orderCommand.CommandText = "UPDATE LocalOrders SET SyncStatus = $Status WHERE OrderGuid = $OrderGuid;";
            orderCommand.Parameters.AddWithValue("$Status", status);
            orderCommand.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
            await orderCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var queueCommand = connection.CreateCommand())
        {
            queueCommand.Transaction = transaction;
            queueCommand.CommandText = """
                UPDATE SyncQueue
                SET Status = $Status,
                    LastTriedAt = $LastTriedAt,
                    ErrorMessage = $ErrorMessage
                WHERE EntityId = $OrderGuid AND EntityType = 'Order';
                """;
            queueCommand.Parameters.AddWithValue("$Status", status == "Synced" ? "Synced" : status);
            queueCommand.Parameters.AddWithValue("$LastTriedAt", DateTimeOffset.Now.ToString("O"));
            queueCommand.Parameters.AddWithValue("$ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            queueCommand.Parameters.AddWithValue("$OrderGuid", orderGuid.ToString());
            await queueCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed record OrderUploadExecutionResult(
    int AttemptedCount,
    int UploadedCount,
    int FailedCount);
