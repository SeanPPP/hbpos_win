using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hbpos.Client.Wpf.Services;

internal static class WindowsShellIdentityService
{
    internal const string AppUserModelId = "Hbpos.Client.Wpf";
    private const string ApplicationExecutableName = "Hbpos.Client.Wpf.exe";
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int WmSetIcon = 0x0080;
    private static readonly Guid PropertyStoreInterfaceId = new("00000138-0000-0000-C000-000000000046");
    private static readonly PropertyKey AppUserModelIdPropertyKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PropertyKey RelaunchCommandPropertyKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);
    private static readonly PropertyKey RelaunchIconResourcePropertyKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);

    public static void ApplyProcessIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 任务栏在第一个窗口创建前读取应用标识；这里固定标识，避免启动方式不同导致图标分组或图标缓存错乱。
        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    public static void ApplyWindowIdentity(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var executablePath = GetApplicationExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        _ = TryApplyWindowIdentity(hwnd, executablePath);
    }

    public static void ApplyWindowIcon(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var executablePath = GetApplicationExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        if (ExtractIconEx(executablePath, 0, largeIcons, smallIcons, 1) <= 0)
        {
            return;
        }

        // 无边框主窗口创建句柄后再显式推送大小图标，确保任务栏和 Alt+Tab 都使用 exe 内嵌 HB POS 图标。
        if (largeIcons[0] != IntPtr.Zero)
        {
            SendMessage(hwnd, WmSetIcon, new IntPtr(IconBig), largeIcons[0]);
        }

        if (smallIcons[0] != IntPtr.Zero)
        {
            SendMessage(hwnd, WmSetIcon, new IntPtr(IconSmall), smallIcons[0]);
        }

        window.Closed += (_, _) =>
        {
            DestroyIconHandle(largeIcons[0]);
            DestroyIconHandle(smallIcons[0]);
        };
    }

    internal static string? GetApplicationExecutablePath() =>
        ResolveApplicationExecutablePath(
            Environment.ProcessPath,
            Assembly.GetEntryAssembly()?.Location,
            AppContext.BaseDirectory);

    internal static string? ResolveApplicationExecutablePath(
        string? processPath,
        string? entryAssemblyLocation,
        string baseDirectory)
    {
        var assemblyExePath = GetAssemblyExePath(entryAssemblyLocation);
        if (File.Exists(assemblyExePath))
        {
            return assemblyExePath;
        }

        var appExePath = Path.Combine(baseDirectory, ApplicationExecutableName);
        if (File.Exists(appExePath))
        {
            return appExePath;
        }

        return processPath;
    }

    internal static string BuildRelaunchCommand(string executablePath) => $"\"{executablePath}\"";

    internal static string BuildRelaunchIconResource(string executablePath) => $"{executablePath},0";

    private static string? GetAssemblyExePath(string? entryAssemblyLocation)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyLocation))
        {
            return null;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(entryAssemblyLocation);
        if (!string.Equals(assemblyName, "Hbpos.Client.Wpf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Equals(Path.GetExtension(entryAssemblyLocation), ".exe", StringComparison.OrdinalIgnoreCase)
            ? entryAssemblyLocation
            : Path.ChangeExtension(entryAssemblyLocation, ".exe");
    }

    private static bool TryApplyWindowIdentity(IntPtr hwnd, string executablePath)
    {
        var propertyStoreInterfaceId = PropertyStoreInterfaceId;
        if (SHGetPropertyStoreForWindow(hwnd, ref propertyStoreInterfaceId, out var propertyStore) != 0)
        {
            return false;
        }

        try
        {
            // Shell 的任务栏按钮有时从窗口属性而不是 WM_SETICON 取图；显式写入 exe 图标资源让自定义窗口也能稳定显示。
            return SetStringValue(propertyStore, AppUserModelIdPropertyKey, AppUserModelId) &&
                SetStringValue(propertyStore, RelaunchCommandPropertyKey, BuildRelaunchCommand(executablePath)) &&
                SetStringValue(propertyStore, RelaunchIconResourcePropertyKey, BuildRelaunchIconResource(executablePath)) &&
                propertyStore.Commit() == 0;
        }
        finally
        {
            Marshal.ReleaseComObject(propertyStore);
        }
    }

    private static bool SetStringValue(IPropertyStore propertyStore, PropertyKey key, string value)
    {
        using var propertyValue = PropVariant.FromString(value);
        return propertyStore.SetValue(ref key, propertyValue) == 0;
    }

    private static void DestroyIconHandle(IntPtr iconHandle)
    {
        if (iconHandle != IntPtr.Zero)
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appID);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[] phiconLarge,
        IntPtr[] phiconSmall,
        uint nIcons);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear([In, Out] PropVariant propVariant);

    [ComImport]
    [Guid("00000138-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint propertyCount);

        int GetAt(uint propertyIndex, out PropertyKey key);

        int GetValue(ref PropertyKey key, out PropVariant value);

        int SetValue(ref PropertyKey key, PropVariant value);

        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey
    {
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        public Guid FormatId { get; }

        public uint PropertyId { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class PropVariant : IDisposable
    {
        private const ushort VariantTypeString = 31;

        private ushort _variantType;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private IntPtr _value;

        private PropVariant(string value)
        {
            _variantType = VariantTypeString;
            _value = Marshal.StringToCoTaskMemUni(value);
        }

        public static PropVariant FromString(string value) => new(value);

        public void Dispose()
        {
            PropVariantClear(this);
            GC.SuppressFinalize(this);
        }
    }
}
