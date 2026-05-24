using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.Services;

public interface IDeviceApiClient
{
    Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default);

    Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken = default);

    Task<DeviceVerifyResponse> VerifyAsync(DeviceVerifyRequest request, CancellationToken cancellationToken = default);

    Task<DeviceReregisterResponse> ReregisterAsync(DeviceReregisterRequest request, CancellationToken cancellationToken = default);
}

public sealed class DeviceApiClient(HttpClient httpClient) : IDeviceApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/catalog/stores", cancellationToken);
        var stores = await ReadApiResultAsync<IReadOnlyList<StoreDto>>(response, cancellationToken);
        return stores
            .Where(x => x.IsActive)
            .OrderBy(x => x.StoreName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.StoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoreSelectionItem(x.StoreCode, x.StoreName, x.IsActive))
            .ToArray();
    }

    public async Task<DeviceRegisterResponse> RegisterAsync(
        DeviceRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/devices/register",
            request,
            JsonOptions,
            cancellationToken);
        return await ReadApiResultAsync<DeviceRegisterResponse>(response, cancellationToken);
    }

    public async Task<DeviceVerifyResponse> VerifyAsync(
        DeviceVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/devices/verify",
            request,
            JsonOptions,
            cancellationToken);
        return await ReadApiResultAsync<DeviceVerifyResponse>(response, cancellationToken);
    }

    public async Task<DeviceReregisterResponse> ReregisterAsync(
        DeviceReregisterRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/devices/reregister",
            request,
            JsonOptions,
            cancellationToken);
        return await ReadApiResultAsync<DeviceReregisterResponse>(response, cancellationToken);
    }

    private static async Task<T> ReadApiResultAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<T>? result = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                result?.Message ?? $"Device API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            throw new CatalogApiException("Device API returned an empty response.", response.StatusCode);
        }

        if (result.Data is null)
        {
            throw new CatalogApiException("Device API returned no data.", response.StatusCode, result.ErrorCode);
        }

        return result.Data;
    }
}
