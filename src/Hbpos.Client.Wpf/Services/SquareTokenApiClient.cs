using System.Net.Http;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Wpf.Services;

public interface ISquareTokenApiClient
{
    Task<SquareTokenResponse> GetTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);
}

public sealed class SquareTokenApiClient(HttpClient httpClient) : ISquareTokenApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SquareTokenResponse> GetTokenAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/square/token?environment={Uri.EscapeDataString(environment.ToString())}",
            cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<SquareTokenResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<SquareTokenResponse>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new CatalogApiException(
                    "Square token API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                $"Square token API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            throw new CatalogApiException("Square token API returned an empty response.", response.StatusCode);
        }

        if (!result.Success)
        {
            throw new CatalogApiException(
                "Square token API returned a failure response.",
                response.StatusCode,
                result.ErrorCode);
        }

        if (result.Data is null || string.IsNullOrWhiteSpace(result.Data.AccessToken))
        {
            throw new CatalogApiException(
                "Square token API returned no token.",
                response.StatusCode,
                result.ErrorCode);
        }

        return result.Data;
    }
}
