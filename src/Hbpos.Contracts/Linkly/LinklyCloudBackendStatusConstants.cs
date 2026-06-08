namespace Hbpos.Contracts.Linkly;

/// <summary>
/// Linkly Cloud Backend 会话状态与恢复动作常量
/// </summary>
public static class LinklyCloudBackendStatusConstants
{
    public const string StatusPending = "Pending";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";
    public const string StatusNotSubmitted = "NotSubmitted";
    public const string StatusTokenRefreshRequired = "TokenRefreshRequired";

    public const string RecoveryRetry = "Retry";
    public const string RecoveryRefreshToken = "RefreshToken";
}
