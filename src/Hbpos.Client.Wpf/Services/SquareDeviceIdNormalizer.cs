namespace Hbpos.Client.Wpf.Services;

internal static class SquareDeviceIdNormalizer
{
    private const string DevicesApiPrefix = "device:";

    public static string? NormalizeForTerminalCheckout(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var trimmed = deviceId.Trim();
        return trimmed.StartsWith(DevicesApiPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[DevicesApiPrefix.Length..]
            : trimmed;
    }

    public static bool AreEquivalent(string? left, string? right)
    {
        return string.Equals(
            NormalizeForTerminalCheckout(left),
            NormalizeForTerminalCheckout(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
