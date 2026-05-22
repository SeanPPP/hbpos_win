using Hbpos.Client.Wpf.Services;
using System.Threading;

namespace Hbpos.Client.Tests;

public sealed class SingleInstanceStartupGuardTests
{
    [Fact]
    public void Selector_only_returns_same_executable_path_and_excludes_current_process()
    {
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", []);
        var currentProcess = new FakeRunningProcess(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
        var sameExecutable = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
        var differentExecutable = new FakeRunningProcess(12, @"C:\Other\Hbpos.Client.Wpf.exe");
        var inaccessibleExecutable = new FakeRunningProcess(13, null);

        var processes = new IRunningProcess[]
        {
            currentProcess,
            sameExecutable,
            differentExecutable,
            inaccessibleExecutable
        };

        var result = SingleInstanceProcessSelector.FindReplaceableProcesses(provider, processes);

        var process = Assert.Single(result);
        Assert.Equal(11, process.Id);
    }

    [Fact]
    public void TryAcquire_returns_startup_in_progress_when_startup_gate_is_held()
    {
        var options = CreateOptions();
        using var startupGate = new Semaphore(1, 1, options.StartupGateName);
        Assert.True(startupGate.WaitOne(TimeSpan.Zero));
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe");
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        try
        {
            var result = guard.TryAcquire(previewMode: false);

            Assert.False(result.CanStart);
            Assert.Equal(SingleInstanceStartupStatus.AnotherStartupInProgress, result.Status);
            Assert.Equal(0, provider.GetSiblingProcessesCallCount);
            Assert.Equal(0, process.CloseMainWindowCallCount);
            Assert.False(process.KillCalled);
        }
        finally
        {
            startupGate.Release();
        }
    }

    [Fact]
    public void TryAcquire_kills_existing_process_when_graceful_close_does_not_exit()
    {
        var options = CreateOptions();
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe")
        {
            CloseMainWindowResult = false,
            WaitForExitResult = false
        };
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        using var lease = guard.TryAcquire(previewMode: false).Lease;

        Assert.NotNull(lease);
        Assert.Equal(1, process.CloseMainWindowCallCount);
        Assert.True(process.KillCalled);
        Assert.True(process.KillEntireProcessTree);
    }

    [Fact]
    public void TryAcquire_does_not_kill_when_graceful_close_exits()
    {
        var options = CreateOptions();
        var process = new FakeRunningProcess(11, @"C:\HBPOS\Hbpos.Client.Wpf.exe")
        {
            CloseMainWindowResult = true,
            WaitForExitResult = true
        };
        var provider = new FakeProcessProvider(10, @"C:\HBPOS\Hbpos.Client.Wpf.exe", [process]);
        var guard = new SingleInstanceStartupGuard(provider, options);

        using var lease = guard.TryAcquire(previewMode: false).Lease;

        Assert.NotNull(lease);
        Assert.Equal(1, process.CloseMainWindowCallCount);
        Assert.False(process.KillCalled);
    }

    private static SingleInstanceStartupGuardOptions CreateOptions()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new SingleInstanceStartupGuardOptions(
            $@"Local\Hbpos.Client.Wpf.Test.StartupGate.{suffix}",
            $@"Local\Hbpos.Client.Wpf.Test.SingleInstance.{suffix}",
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
    }

    private sealed class FakeProcessProvider : IRunningProcessProvider
    {
        private readonly IReadOnlyList<IRunningProcess> _processes;

        public FakeProcessProvider(int currentProcessId, string? currentExecutablePath, IReadOnlyList<IRunningProcess> processes)
        {
            CurrentProcessId = currentProcessId;
            CurrentExecutablePath = currentExecutablePath;
            _processes = processes;
        }

        public int CurrentProcessId { get; }

        public string? CurrentExecutablePath { get; }

        public int GetSiblingProcessesCallCount { get; private set; }

        public ProcessSnapshot GetSiblingProcesses()
        {
            GetSiblingProcessesCallCount++;
            return new ProcessSnapshot(_processes);
        }
    }

    private sealed class FakeRunningProcess : IRunningProcess
    {
        public FakeRunningProcess(int id, string? executablePath)
        {
            Id = id;
            ExecutablePath = executablePath;
        }

        public int Id { get; }

        public string? ExecutablePath { get; }

        public bool HasExited { get; set; }

        public bool CloseMainWindowResult { get; init; }

        public bool WaitForExitResult { get; init; }

        public int CloseMainWindowCallCount { get; private set; }

        public bool KillCalled { get; private set; }

        public bool KillEntireProcessTree { get; private set; }

        public bool CloseMainWindow()
        {
            CloseMainWindowCallCount++;
            return CloseMainWindowResult;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            HasExited = WaitForExitResult;
            return WaitForExitResult;
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KillEntireProcessTree = entireProcessTree;
            HasExited = true;
        }

        public void Dispose()
        {
        }
    }
}
