using System.Windows;

namespace Hbpos.Client.Wpf.Services;

public interface IApplicationExitService
{
    void Exit();
}

public interface IConfirmationDialogService
{
    bool ConfirmExitApplication();
}

public sealed class WpfApplicationExitService : IApplicationExitService
{
    public void Exit()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        if (application.MainWindow is { } mainWindow)
        {
            mainWindow.Close();
            return;
        }

        application.Shutdown();
    }
}

public sealed class WpfConfirmationDialogService : IConfirmationDialogService
{
    public bool ConfirmExitApplication()
    {
        var owner = Application.Current?.MainWindow;
        var result = MessageBox.Show(
            owner,
            "确定要退出收银软件吗？",
            "退出软件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }
}
