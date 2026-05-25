namespace Hbpos.Client.Wpf.Services;

public enum UserFeedbackCue
{
    ScanAdded,
    ScanMultipleMatches,
    ScanNoMatch,
    OperationError
}

public interface IUserFeedbackService
{
    void Play(UserFeedbackCue cue);
}

public sealed class NoopUserFeedbackService : IUserFeedbackService
{
    public static NoopUserFeedbackService Instance { get; } = new();

    private NoopUserFeedbackService()
    {
    }

    public void Play(UserFeedbackCue cue)
    {
    }
}
