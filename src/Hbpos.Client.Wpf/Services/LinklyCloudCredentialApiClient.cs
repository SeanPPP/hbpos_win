using System.Net.Http;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyCloudCredentialApiClient
{
    Task<LinklyCloudCredentialResponse> GetCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);
}

public sealed class LinklyCloudCredentialApiClient(HttpClient httpClient) : ILinklyCloudCredentialApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<LinklyCloudCredentialResponse> GetCredentialAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        Log($"backend credential request start environment={environment}");
        using var response = await httpClient.GetAsync(
            $"api/v1/linkly/cloud-credential?environment={Uri.EscapeDataString(environment.ToString())}",
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        Log($"backend credential response environment={environment} http={(int)response.StatusCode}");
        ApiResult<LinklyCloudCredentialResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<LinklyCloudCredentialResponse>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                Log($"backend credential response invalid-json environment={environment} http={(int)response.StatusCode}");
                throw new CatalogApiException(
                    "Linkly credential API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            Log($"backend credential request failed environment={environment} http={(int)response.StatusCode} errorCode={LogValue(result?.ErrorCode)}");
            throw new CatalogApiException(
                $"Linkly credential API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            Log($"backend credential request failed environment={environment} http={(int)response.StatusCode} reason=empty-response");
            throw new CatalogApiException("Linkly credential API returned an empty response.", response.StatusCode);
        }

        if (!result.Success || result.Data is null)
        {
            Log($"backend credential request failed environment={environment} http={(int)response.StatusCode} errorCode={LogValue(result.ErrorCode)}");
            throw new CatalogApiException(
                "Linkly credential API returned a failure response.",
                response.StatusCode,
                result.ErrorCode);
        }

        if (string.IsNullOrWhiteSpace(result.Data.Username) ||
            string.IsNullOrWhiteSpace(result.Data.Password))
        {
            Log($"backend credential request failed environment={environment} http={(int)response.StatusCode} reason=incomplete-credentials store={LogValue(result.Data.StoreCode)}");
            throw new CatalogApiException("Linkly credential API returned incomplete credentials.", response.StatusCode);
        }

        Log($"backend credential request succeeded environment={environment} store={LogValue(result.Data.StoreCode)} credentialEnvironment={LogValue(result.Data.Environment)} updatedAt={result.Data.UpdatedAt:O}");
        return result.Data;
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("LinklyCloud", message);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }
}
