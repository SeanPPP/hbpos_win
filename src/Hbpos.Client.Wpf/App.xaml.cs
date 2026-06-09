using System.Windows;
using System.Windows.Threading;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf;

public partial class App : Application
{
    private const int SplashShownPercent = 10;
    private const int HostBuiltPercent = 30;
    private const int HostStartedPercent = 50;
    private const int MainWindowPreparingPercent = 65;
    private const int MainWindowInitializedPercent = 85;
    private const int StartupCompletedPercent = 100;

    private IHost? _host;
    private SingleInstanceStartupLease? _startupLease;
    private StartupSplashWindow? _startupSplashWindow;
    private StartupProgressState? _startupProgressState;

    protected override async void OnStartup(StartupEventArgs e)
    {
        WindowsShellIdentityService.ApplyProcessIdentity();

        var startupOptions = AppStartupOptions.FromArgs(e.Args);
        var startupGuard = new SingleInstanceStartupGuard();
        var startupResult = startupGuard.TryAcquire(startupOptions.PreviewMode);
        if (!startupResult.CanStart)
        {
            Shutdown();
            return;
        }

        _startupLease = startupResult.Lease;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!startupOptions.PreviewMode)
        {
            _startupProgressState = new StartupProgressState();
            _startupProgressState.SetStage(SplashShownPercent, StartupText("startup.stage.starting"));
            _startupSplashWindow = new StartupSplashWindow(_startupProgressState);
            _startupSplashWindow.Show();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }

        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices(services =>
                {
                    services.AddHbposClientServices(startupOptions);
                })
                .Build();
            _startupProgressState?.SetStage(HostBuiltPercent, StartupText("startup.stage.initializingServices"));

            await _host.StartAsync();
            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            LocalizationResourceProvider.Instance.Configure(localization);
            _startupProgressState?.SetStage(HostStartedPercent, localization.T("startup.stage.startingLocalComponents"));

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _startupProgressState?.SetStage(MainWindowPreparingPercent, localization.T("startup.stage.loadingProducts"));
            await mainWindow.InitializeForStartupAsync();
            _startupProgressState?.SetStage(MainWindowInitializedPercent, localization.T("startup.stage.preparingMainWindow"));
            MainWindow = mainWindow;
            FinishStartupExperience();
            mainWindow.Show();
            // 主窗口句柄会在 Show 前为扫码初始化提前创建；Show 后再刷新一次，确保任务栏按钮拿到正确图标。
            WindowsShellIdentityService.ApplyWindowIdentity(mainWindow);
            WindowsShellIdentityService.ApplyWindowIcon(mainWindow);
            mainWindow.ActivateForScannerInput();
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            mainWindow.ContinueStartupAfterShown();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _ = ReleaseStartupGateAfterClickGuardDelayAsync();

            base.OnStartup(e);
        }
        catch
        {
            FinishStartupExperience();
            _startupLease?.Dispose();
            _startupLease = null;
            throw;
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        FinishStartupExperience();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        _startupLease?.Dispose();
        _startupLease = null;

        base.OnExit(e);
    }

    private async Task ReleaseStartupGateAfterClickGuardDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _startupLease?.ReleaseStartupGate();
    }

    private void FinishStartupExperience()
    {
        if (_startupSplashWindow is null)
        {
            return;
        }

        _startupProgressState?.SetStage(StartupCompletedPercent, StartupText("startup.stage.completed"));
        _startupSplashWindow.Close();
        _startupSplashWindow = null;
        _startupProgressState = null;
    }

    private static string StartupText(string key) => LocalizationResourceProvider.Instance[key];
}
