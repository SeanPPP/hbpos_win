using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface IRemoteOrderHistoryService
{
    Task<RemoteOrderHistoryResult> QueryAsync(
        RemoteOrderHistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<ReceiptDetails?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RemoteOrderHistoryQuery(
    string StoreCode,
    DateTimeOffset? SoldFrom,
    DateTimeOffset? SoldTo,
    string? DeviceCode,
    string? Keyword,
    int Take);

public sealed record RemoteOrderHistoryResult(IReadOnlyList<RemoteOrderHistorySummary> Orders);

public sealed record RemoteOrderHistorySummary(
    Guid OrderGuid,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    DateTimeOffset SoldAt,
    decimal TotalAmount,
    decimal DiscountAmount,
    decimal ActualAmount,
    int LineCount,
    string PaymentSummary,
    string StatusLabel);

public sealed class RemoteOrderHistoryService(IOrderHistoryApiClient apiClient) : IRemoteOrderHistoryService
{
    public async Task<RemoteOrderHistoryResult> QueryAsync(
        RemoteOrderHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var response = await apiClient.QueryAsync(new OrderHistoryQueryRequest(
            query.StoreCode,
            query.DeviceCode,
            query.SoldFrom,
            query.SoldTo,
            query.Keyword,
            Math.Clamp(query.Take, 1, 200)), cancellationToken);

        return new RemoteOrderHistoryResult(response.Orders.Select(order => new RemoteOrderHistorySummary(
            order.OrderGuid,
            order.StoreCode,
            order.DeviceCode,
            order.CashierName,
            order.SoldAt,
            order.TotalAmount,
            order.DiscountAmount,
            order.ActualAmount,
            order.LineCount,
            order.PaymentSummary,
            order.StatusLabel)).ToList());
    }

    public async Task<ReceiptDetails?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        var details = await apiClient.GetDetailsAsync(orderGuid, cancellationToken);
        return details is null
            ? null
            : new ReceiptDetails(
                details.OrderGuid,
                details.StoreCode,
                details.DeviceCode,
                details.CashierName,
                details.SoldAt,
                details.TotalAmount,
                details.DiscountAmount,
                details.ActualAmount,
                details.Lines.Select(line => new ReceiptPreviewLine(
                    line.DisplayName,
                    line.LookupCode,
                    line.Quantity,
                    line.UnitPrice,
                    line.DiscountAmount,
                    line.ActualAmount)).ToList(),
                details.Payments.Select(payment => new ReceiptPaymentLine(
                    payment.Method,
                    payment.Amount,
                    payment.Reference)).ToList());
    }

    public Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        return apiClient.GetReturnContextAsync(orderGuid, cancellationToken);
    }

    public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return apiClient.CreateReturnRecordsAsync(request, cancellationToken);
    }
}

public interface IOrderHistoryApiClient
{
    Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default);

    Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OrderHistoryApiClient(HttpClient httpClient) : IOrderHistoryApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OrderHistoryQueryResponse> QueryAsync(
        OrderHistoryQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/orders/history",
            ("storeCode", request.StoreCode),
            ("deviceCode", request.DeviceCode),
            ("soldFrom", request.SoldFrom?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("soldTo", request.SoldTo?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("keyword", request.Keyword),
            ("take", request.Take.ToString(CultureInfo.InvariantCulture)));

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        return await ReadApiResultAsync<OrderHistoryQueryResponse>(response, cancellationToken);
    }

    public async Task<OrderHistoryDetailsDto?> GetDetailsAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/orders/history/{Uri.EscapeDataString(orderGuid.ToString("D"))}",
            cancellationToken);

        return await ReadApiResultAsync<OrderHistoryDetailsDto?>(response, cancellationToken);
    }

    public async Task<OrderReturnContextDto?> GetReturnContextAsync(
        Guid orderGuid,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/orders/history/{Uri.EscapeDataString(orderGuid.ToString("D"))}/return-context",
            cancellationToken);

        return await ReadApiResultAsync<OrderReturnContextDto?>(response, cancellationToken);
    }

    public async Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
        OrderReturnRecordCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/v1/orders/returns", request, JsonOptions, cancellationToken);
        return await ReadApiResultAsync<OrderReturnRecordCreateResponse>(response, cancellationToken);
    }

    private static async Task<T> ReadApiResultAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<T>? result = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new CatalogApiException(
                    "Order history API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                result?.Message ?? $"Order history API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            throw new CatalogApiException(
                "Order history API returned an empty response.",
                response.StatusCode);
        }

        if (!result.Success)
        {
            throw new CatalogApiException(
                result.Message ?? "Order history API returned a failure response.",
                response.StatusCode,
                result.ErrorCode);
        }

        return result.Data!;
    }

    private static string BuildUri(string path, params (string Name, string? Value)[] query)
    {
        var queryString = string.Join(
            "&",
            query
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Name)}={Uri.EscapeDataString(x.Value!)}"));

        return string.IsNullOrEmpty(queryString)
            ? path
            : $"{path}?{queryString}";
    }
}
