using System.Diagnostics;
using System.IO;
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
        WriteFileLog(line);
    }

    private static void WriteFileLog(string line)
    {
        var logPath = Environment.GetEnvironmentVariable("HBPOS_CLIENT_LOG_FILE");
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // Startup diagnostics should never block the POS app.
        }
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
