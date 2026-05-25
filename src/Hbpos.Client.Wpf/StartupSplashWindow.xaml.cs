using System.Windows;

namespace Hbpos.Client.Wpf;

public partial class StartupSplashWindow : Window
{
    public StartupSplashWindow(StartupProgressState progressState)
    {
        InitializeComponent();
        DataContext = progressState;
    }
}
