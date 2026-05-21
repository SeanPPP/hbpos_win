using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hbpos.Client.Wpf.Services;

internal static class ConsoleLog
{
    private const int AttachParentProcess = -1;
    private static int _attachAttempted;

    public static void Write(string category, string message)
    {
        EnsureConsoleAttached();
        var line = $"[HBPOS][Client][{category}] {DateTimeOffset.Now:O} {message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }

    private static void EnsureConsoleAttached()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.Exchange(ref _attachAttempted, 1) != 0)
        {
            return;
        }

        _ = AttachConsole(AttachParentProcess);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
