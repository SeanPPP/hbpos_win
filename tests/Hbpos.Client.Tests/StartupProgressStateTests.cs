using Hbpos.Client.Wpf;

namespace Hbpos.Client.Tests;

public sealed class StartupProgressStateTests
{
    [Fact]
    public void SetStage_updates_percent_and_progress_text()
    {
        var state = new StartupProgressState();
        var changedProperties = new List<string>();
        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changedProperties.Add(args.PropertyName);
            }
        };

        state.SetStage(50);

        Assert.Equal(50, state.StagePercent);
        Assert.Equal("50%", state.ProgressText);
        Assert.Contains(nameof(StartupProgressState.StagePercent), changedProperties);
        Assert.Contains(nameof(StartupProgressState.ProgressText), changedProperties);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(110, 100)]
    public void SetStage_clamps_percent_into_valid_range(int input, int expected)
    {
        var state = new StartupProgressState();

        state.SetStage(input);

        Assert.Equal(expected, state.StagePercent);
    }
}
