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
    private IHost? _host;
    private SingleInstanceStartupLease? _startupLease;
    private StartupSplashWindow? _startupSplashWindow;

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
            _startupSplashWindow = new StartupSplashWindow();
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

            await _host.StartAsync();
            LocalizationResourceProvider.Instance.Configure(_host.Services.GetRequiredService<ILocalizationService>());

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            await mainWindow.InitializeForStartupAsync();
            FinishStartupExperience();
            MainWindow = mainWindow;
            mainWindow.Show();
            mainWindow.Activate();
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

        _startupSplashWindow.Close();
        _startupSplashWindow = null;
    }

}
