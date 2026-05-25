using CommunityToolkit.Mvvm.ComponentModel;

namespace Hbpos.Client.Wpf;

public sealed partial class StartupProgressState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int stagePercent;

    public string ProgressText => $"{StagePercent}%";

    public void SetStage(int percent)
    {
        StagePercent = Math.Clamp(percent, 0, 100);
    }
}
