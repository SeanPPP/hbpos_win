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
            _startupProgressState.SetStage(SplashShownPercent, "正在启动...");
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
            _startupProgressState?.SetStage(HostBuiltPercent, "正在初始化服务...");

            await _host.StartAsync();
            LocalizationResourceProvider.Instance.Configure(_host.Services.GetRequiredService<ILocalizationService>());
            _startupProgressState?.SetStage(HostStartedPercent, "正在启动本地组件...");

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _startupProgressState?.SetStage(MainWindowPreparingPercent, "正在加载本地商品...");
            await mainWindow.InitializeForStartupAsync();
            _startupProgressState?.SetStage(MainWindowInitializedPercent, "正在准备主界面...");
            FinishStartupExperience();
            MainWindow = mainWindow;
            mainWindow.Show();
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

        _startupProgressState?.SetStage(StartupCompletedPercent, "启动完成");
        _startupSplashWindow.Close();
        _startupSplashWindow = null;
        _startupProgressState = null;
    }

}
