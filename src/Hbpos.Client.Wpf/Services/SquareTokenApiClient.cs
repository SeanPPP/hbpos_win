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
        LogSquareToken($"token request start environment={environment}");
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
                LogSquareToken($"token request invalid json environment={environment} http={(int)response.StatusCode}");
                throw new CatalogApiException(
                    "Square token API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            LogSquareToken($"token request failed environment={environment} http={(int)response.StatusCode} errorCode={LogValue(result?.ErrorCode)}");
            throw new CatalogApiException(
                $"Square token API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            LogSquareToken($"token request failed environment={environment} reason=empty-response");
            throw new CatalogApiException("Square token API returned an empty response.", response.StatusCode);
        }

        if (!result.Success)
        {
            LogSquareToken($"token request failed environment={environment} reason=api-failure errorCode={LogValue(result.ErrorCode)}");
            throw new CatalogApiException(
                "Square token API returned a failure response.",
                response.StatusCode,
                result.ErrorCode);
        }

        if (result.Data is null || string.IsNullOrWhiteSpace(result.Data.AccessToken))
        {
            LogSquareToken($"token request failed environment={environment} reason=missing-token errorCode={LogValue(result.ErrorCode)}");
            throw new CatalogApiException(
                "Square token API returned no token.",
                response.StatusCode,
                result.ErrorCode);
        }

        LogSquareToken($"token request succeeded environment={environment}");
        return result.Data;
    }

    private static void LogSquareToken(string message)
    {
        ConsoleLog.Write("Square", message);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }
}
