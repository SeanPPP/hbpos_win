using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Contracts.Advertisements;
using Hbpos.Contracts.Common;

namespace Hbpos.Client.Wpf.Services;

public interface IAdvertisementApiClient
{
    Task<AdvertisementPlaybackResponse> GetActiveAsync(
        string storeCode,
        int take = 20,
        CancellationToken cancellationToken = default);
}

public sealed class AdvertisementApiClient(HttpClient httpClient) : IAdvertisementApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AdvertisementPlaybackResponse> GetActiveAsync(
        string storeCode,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var requestUri = BuildUri(
            "api/v1/advertisements/active",
            ("storeCode", normalizedStoreCode),
            ("take", take.ToString(CultureInfo.InvariantCulture)));

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        return await ReadApiResultAsync<AdvertisementPlaybackResponse>(response, cancellationToken);
    }

    private static string NormalizeStoreCode(string? storeCode)
    {
        return (storeCode ?? string.Empty).Trim();
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
                throw new AdvertisementApiException(
                    "广告接口返回了无效的 JSON。",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AdvertisementApiException(
                result?.Message ?? $"广告接口请求失败，HTTP 状态码 {(int)response.StatusCode}。",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            throw new AdvertisementApiException(
                "广告接口返回了空响应。",
                response.StatusCode);
        }

        if (!result.Success)
        {
            throw new AdvertisementApiException(
                result.Message ?? "广告接口返回失败结果。",
                response.StatusCode,
                result.ErrorCode);
        }

        if (result.Data is null)
        {
            throw new AdvertisementApiException(
                "广告接口没有返回广告数据。",
                response.StatusCode,
                result.ErrorCode);
        }

        return result.Data;
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

public sealed class AdvertisementApiException : Exception
{
    public AdvertisementApiException(
        string message,
        HttpStatusCode? statusCode = null,
        string? errorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ErrorCode { get; }
}
