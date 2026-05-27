using CommunityToolkit.Mvvm.ComponentModel;

namespace Hbpos.Client.Wpf;

public sealed partial class StartupProgressState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int stagePercent;

    [ObservableProperty]
    private string statusText = string.Empty;

    public string ProgressText => $"{StagePercent}%";

    public void SetStage(int percent, string? status = null)
    {
        StagePercent = Math.Clamp(percent, 0, 100);
        if (status is not null)
        {
            StatusText = status;
        }
    }
}
