using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Hbpos.Client.Wpf.Services;

internal enum SingleInstanceStartupStatus
{
    Acquired,
    PreviewMode,
    AnotherStartupInProgress,
    ExistingInstanceCouldNotBeStopped
}

internal sealed class SingleInstanceStartupResult
{
    private SingleInstanceStartupResult(SingleInstanceStartupStatus status, SingleInstanceStartupLease? lease)
    {
        Status = status;
        Lease = lease;
    }

    public SingleInstanceStartupStatus Status { get; }

    public SingleInstanceStartupLease? Lease { get; }

    public bool CanStart => Status is SingleInstanceStartupStatus.Acquired or SingleInstanceStartupStatus.PreviewMode;

    public static SingleInstanceStartupResult PreviewMode()
    {
        return new SingleInstanceStartupResult(SingleInstanceStartupStatus.PreviewMode, null);
    }

    public static SingleInstanceStartupResult AnotherStartupInProgress()
    {
        return new SingleInstanceStartupResult(SingleInstanceStartupStatus.AnotherStartupInProgress, null);
    }

    public static SingleInstanceStartupResult ExistingInstanceCouldNotBeStopped()
    {
        return new SingleInstanceStartupResult(SingleInstanceStartupStatus.ExistingInstanceCouldNotBeStopped, null);
    }

    public static SingleInstanceStartupResult Acquired(SingleInstanceStartupLease lease)
    {
        return new SingleInstanceStartupResult(SingleInstanceStartupStatus.Acquired, lease);
    }
}

internal sealed class SingleInstanceStartupLease : IDisposable
{
    private Semaphore? _startupGate;
    private Mutex? _runningInstance;
    private bool _startupGateHeld;
    private bool _runningInstanceHeld;

    public SingleInstanceStartupLease(Semaphore startupGate, Mutex runningInstance)
    {
        _startupGate = startupGate;
        _runningInstance = runningInstance;
        _startupGateHeld = true;
        _runningInstanceHeld = true;
    }

    public void ReleaseStartupGate()
    {
        if (!_startupGateHeld || _startupGate is null)
        {
            return;
        }

        _startupGate.Release();
        ConsoleLog.Write("startup-guard", "startup gate released");
        _startupGateHeld = false;
        _startupGate.Dispose();
        _startupGate = null;
    }

    public void Dispose()
    {
        ReleaseStartupGate();

        if (_runningInstanceHeld && _runningInstance is not null)
        {
            _runningInstance.ReleaseMutex();
            _runningInstanceHeld = false;
        }

        _runningInstance?.Dispose();
        _runningInstance = null;
    }
}

internal sealed record SingleInstanceStartupGuardOptions(
    string StartupGateName,
    string RunningInstanceMutexName,
    TimeSpan GracefulCloseTimeout,
    TimeSpan KillWaitTimeout,
    TimeSpan RunningInstanceWaitTimeout)
{
    public static SingleInstanceStartupGuardOptions Default { get; } = new(
        @"Global\Hbpos.Client.Wpf.StartupGate",
        @"Global\Hbpos.Client.Wpf.SingleInstance",
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5));
}

internal sealed class SingleInstanceStartupGuard
{
    private readonly IRunningProcessProvider _processProvider;
    private readonly SingleInstanceStartupGuardOptions _options;

    public SingleInstanceStartupGuard()
        : this(new SystemRunningProcessProvider(), SingleInstanceStartupGuardOptions.Default)
    {
    }

    public SingleInstanceStartupGuard(
        IRunningProcessProvider processProvider,
        SingleInstanceStartupGuardOptions options)
    {
        _processProvider = processProvider;
        _options = options;
    }

    public SingleInstanceStartupResult TryAcquire(bool previewMode)
    {
        if (previewMode)
        {
            return SingleInstanceStartupResult.PreviewMode();
        }

        var startupGate = new Semaphore(1, 1, _options.StartupGateName);
        if (!startupGate.WaitOne(TimeSpan.Zero))
        {
            ConsoleLog.Write("startup-guard", "another startup is already preparing the app");
            startupGate.Dispose();
            return SingleInstanceStartupResult.AnotherStartupInProgress();
        }

        try
        {
            using var processes = _processProvider.GetSiblingProcesses();
            var replaceableProcesses = SingleInstanceProcessSelector.FindReplaceableProcesses(_processProvider, processes.Items);
            ConsoleLog.Write("startup-guard", $"replaceable process count={replaceableProcesses.Count}");
            StopExistingInstances(replaceableProcesses);

            var runningInstance = new Mutex(false, _options.RunningInstanceMutexName);
            if (!runningInstance.WaitOne(_options.RunningInstanceWaitTimeout))
            {
                ConsoleLog.Write("startup-guard", "existing instance did not release the running mutex before timeout");
                runningInstance.Dispose();
                startupGate.Release();
                startupGate.Dispose();
                return SingleInstanceStartupResult.ExistingInstanceCouldNotBeStopped();
            }

            return SingleInstanceStartupResult.Acquired(new SingleInstanceStartupLease(startupGate, runningInstance));
        }
        catch
        {
            startupGate.Release();
            startupGate.Dispose();
            throw;
        }
    }

    private void StopExistingInstances(IReadOnlyList<IRunningProcess> processes)
    {
        foreach (var process in processes)
        {
            if (process.HasExited)
            {
                continue;
            }

            ConsoleLog.Write("startup-guard", $"requesting close for pid={process.Id}");
            var closeRequested = process.CloseMainWindow();
            if (closeRequested && process.WaitForExit(_options.GracefulCloseTimeout))
            {
                ConsoleLog.Write("startup-guard", $"pid={process.Id} exited after close request");
                continue;
            }

            if (process.HasExited)
            {
                ConsoleLog.Write("startup-guard", $"pid={process.Id} exited before kill request");
                continue;
            }

            ConsoleLog.Write("startup-guard", $"killing pid={process.Id}");
            process.Kill(entireProcessTree: true);
            process.WaitForExit(_options.KillWaitTimeout);
        }
    }
}

internal static class SingleInstanceProcessSelector
{
    public static IReadOnlyList<IRunningProcess> FindReplaceableProcesses(
        IRunningProcessProvider processProvider,
        IReadOnlyList<IRunningProcess> processes)
    {
        var currentExecutablePath = NormalizeExecutablePath(processProvider.CurrentExecutablePath);
        if (currentExecutablePath is null)
        {
            return [];
        }

        return processes
            .Where(process =>
                process.Id != processProvider.CurrentProcessId &&
                !process.HasExited &&
                string.Equals(
                    NormalizeExecutablePath(process.ExecutablePath),
                    currentExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal interface IRunningProcessProvider
{
    int CurrentProcessId { get; }

    string? CurrentExecutablePath { get; }

    ProcessSnapshot GetSiblingProcesses();
}

internal sealed class ProcessSnapshot : IDisposable
{
    public ProcessSnapshot(IReadOnlyList<IRunningProcess> items)
    {
        Items = items;
    }

    public IReadOnlyList<IRunningProcess> Items { get; }

    public void Dispose()
    {
        foreach (var process in Items)
        {
            process.Dispose();
        }
    }
}

internal interface IRunningProcess : IDisposable
{
    int Id { get; }

    string? ExecutablePath { get; }

    bool HasExited { get; }

    bool CloseMainWindow();

    bool WaitForExit(TimeSpan timeout);

    void Kill(bool entireProcessTree);
}

internal sealed class SystemRunningProcessProvider : IRunningProcessProvider
{
    public int CurrentProcessId => Environment.ProcessId;

    public string? CurrentExecutablePath => Environment.ProcessPath ?? TryReadProcessPath(Process.GetCurrentProcess());

    public ProcessSnapshot GetSiblingProcesses()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(currentProcess.ProcessName)
            .Select(process => (IRunningProcess)new SystemRunningProcess(process))
            .ToArray();

        return new ProcessSnapshot(processes);
    }

    private static string? TryReadProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed class SystemRunningProcess : IRunningProcess
{
    private readonly Process _process;

    public SystemRunningProcess(Process process)
    {
        _process = process;
    }

    public int Id => _process.Id;

    public string? ExecutablePath
    {
        get
        {
            try
            {
                return _process.MainModule?.FileName;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public bool CloseMainWindow()
    {
        try
        {
            return _process.CloseMainWindow();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            return _process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
        }
        catch (Exception)
        {
            return true;
        }
    }

    public void Kill(bool entireProcessTree)
    {
        try
        {
            _process.Kill(entireProcessTree);
        }
        catch (Exception)
        {
            // The process may exit between the check and the kill request.
        }
    }

    public void Dispose()
    {
        _process.Dispose();
    }
}
