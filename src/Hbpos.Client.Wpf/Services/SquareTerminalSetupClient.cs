using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

public sealed record SquareLocationOption(string Id, string Name);

public sealed record SquareDeviceOption(string Id, string Name, string? Status);

public sealed record SquareDeviceCodeOption(
    string Id,
    string Name,
    string Code,
    string Status,
    string? LocationId,
    string? DeviceId,
    DateTimeOffset? PairBy,
    DateTimeOffset? CreatedAt);

public interface ISquareTerminalSetupClient
{
    Task<IReadOnlyList<SquareLocationOption>> ListLocationsAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDeviceOption>> ListDevicesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDeviceCodeOption>> ListDeviceCodesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> CreateDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> GetDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
        CancellationToken cancellationToken = default);
}

public sealed class SquareTerminalSetupClient(HttpClient httpClient) : ISquareTerminalSetupClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SquareLocationOption>> ListLocationsAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessToken(accessToken);

        using var response = await SendAsync(
            accessToken,
            environment,
            HttpMethod.Get,
            "locations",
            body: null,
            cancellationToken);

        using var document = await ReadSuccessDocumentAsync(response, "locations", cancellationToken);
        return ReadLocations(document.RootElement);
    }

    public async Task<IReadOnlyList<SquareDeviceOption>> ListDevicesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessToken(accessToken);
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new ArgumentException("Location id is required.", nameof(locationId));
        }

        var relativeUrl = $"devices?location_id={Uri.EscapeDataString(locationId.Trim())}";
        using var response = await SendAsync(
            accessToken,
            environment,
            HttpMethod.Get,
            relativeUrl,
            body: null,
            cancellationToken);

        using var document = await ReadSuccessDocumentAsync(response, "devices", cancellationToken);
        return ReadDevices(document.RootElement);
    }

    public async Task<IReadOnlyList<SquareDeviceCodeOption>> ListDeviceCodesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessToken(accessToken);
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new ArgumentException("Location id is required.", nameof(locationId));
        }

        var relativeUrl = $"devices/codes?location_id={Uri.EscapeDataString(locationId.Trim())}&product_type=TERMINAL_API";
        using var response = await SendAsync(
            accessToken,
            environment,
            HttpMethod.Get,
            relativeUrl,
            body: null,
            cancellationToken);

        using var document = await ReadSuccessDocumentAsync(response, "device codes", cancellationToken);
        return ReadDeviceCodes(document.RootElement);
    }

    public async Task<SquareDeviceCodeOption> CreateDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessToken(accessToken);
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new ArgumentException("Location id is required.", nameof(locationId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Device code name is required.", nameof(name));
        }

        using var response = await SendAsync(
            accessToken,
            environment,
            HttpMethod.Post,
            "devices/codes",
            new
            {
                idempotency_key = Guid.NewGuid().ToString("N"),
                device_code = new
                {
                    name = name.Trim(),
                    location_id = locationId.Trim(),
                    product_type = "TERMINAL_API"
                }
            },
            cancellationToken);

        using var document = await ReadSuccessDocumentAsync(response, "create device code", cancellationToken);
        return ReadSingleDeviceCode(document.RootElement);
    }

    public async Task<SquareDeviceCodeOption> GetDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessToken(accessToken);
        if (string.IsNullOrWhiteSpace(deviceCodeId))
        {
            throw new ArgumentException("Device code id is required.", nameof(deviceCodeId));
        }

        using var response = await SendAsync(
            accessToken,
            environment,
            HttpMethod.Get,
            $"devices/codes/{Uri.EscapeDataString(deviceCodeId.Trim())}",
            body: null,
            cancellationToken);

        using var document = await ReadSuccessDocumentAsync(response, "device code", cancellationToken);
        return ReadSingleDeviceCode(document.RootElement);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(CardTerminalSettings.GetSquareApiBaseUrl(environment), UriKind.Absolute);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.Add("Square-Version", CardTerminalSettings.SquareVersion);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonDocument> ReadSuccessDocumentAsync(
        HttpResponseMessage response,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new SquareApiException(
                $"Square {operationName} request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<SquareLocationOption> ReadLocations(JsonElement root)
    {
        if (!root.TryGetProperty("locations", out var locationsElement) ||
            locationsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var locations = new List<SquareLocationOption>(locationsElement.GetArrayLength());
        foreach (var locationElement in locationsElement.EnumerateArray())
        {
            var id = ReadRequiredString(locationElement, "id");
            var name = ReadRequiredString(locationElement, "name");
            locations.Add(new SquareLocationOption(id, name));
        }

        return locations;
    }

    private static IReadOnlyList<SquareDeviceOption> ReadDevices(JsonElement root)
    {
        if (!root.TryGetProperty("devices", out var devicesElement) ||
            devicesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var devices = new List<SquareDeviceOption>(devicesElement.GetArrayLength());
        foreach (var deviceElement in devicesElement.EnumerateArray())
        {
            var id = ReadRequiredString(deviceElement, "id");
            var name = deviceElement.TryGetProperty("attributes", out var attributesElement)
                ? ReadOptionalString(attributesElement, "name")
                : null;
            var status = deviceElement.TryGetProperty("status", out var statusElement)
                ? ReadOptionalString(statusElement, "category")
                : null;

            devices.Add(new SquareDeviceOption(id, name ?? id, status));
        }

        return devices;
    }

    private static IReadOnlyList<SquareDeviceCodeOption> ReadDeviceCodes(JsonElement root)
    {
        if (!root.TryGetProperty("device_codes", out var deviceCodesElement) ||
            deviceCodesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var deviceCodes = new List<SquareDeviceCodeOption>(deviceCodesElement.GetArrayLength());
        foreach (var deviceCodeElement in deviceCodesElement.EnumerateArray())
        {
            deviceCodes.Add(ReadDeviceCode(deviceCodeElement));
        }

        return deviceCodes;
    }

    private static SquareDeviceCodeOption ReadSingleDeviceCode(JsonElement root)
    {
        if (!root.TryGetProperty("device_code", out var deviceCodeElement) ||
            deviceCodeElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Square response is missing required property 'device_code'.");
        }

        return ReadDeviceCode(deviceCodeElement);
    }

    private static SquareDeviceCodeOption ReadDeviceCode(JsonElement element)
    {
        var id = ReadRequiredString(element, "id");
        var name = ReadRequiredString(element, "name");
        var code = ReadRequiredString(element, "code");
        var status = ReadRequiredString(element, "status");
        var locationId = ReadOptionalString(element, "location_id");
        var deviceId = ReadOptionalString(element, "device_id");
        var pairBy = ReadOptionalDateTimeOffset(element, "pair_by");
        var createdAt = ReadOptionalDateTimeOffset(element, "created_at");

        return new SquareDeviceCodeOption(id, name, code, status, locationId, deviceId, pairBy, createdAt);
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Square response is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String
                ? propertyElement.GetString()
                : null;
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(JsonElement element, string propertyName)
    {
        var text = ReadOptionalString(element, propertyName);
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }

    private static void ValidateAccessToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required.", nameof(accessToken));
        }
    }
}

public sealed class SquareApiException : InvalidOperationException
{
    public SquareApiException(string message, System.Net.HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode StatusCode { get; }

    public bool IsAuthenticationFailure =>
        StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
}
