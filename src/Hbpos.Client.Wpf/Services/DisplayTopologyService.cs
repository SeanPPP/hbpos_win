using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace Hbpos.Client.Wpf.Services;

public sealed record DisplayBounds(IntPtr Handle, int Left, int Top, int Width, int Height);

public interface IDisplayTopologyService
{
    IReadOnlyList<DisplayBounds> GetDisplays();

    DisplayBounds? FindDisplayAwayFrom(Window owner);

    void AttachWorkAreaConstraint(Window window);

    void FitToDisplayWorkArea(Window window, Window coordinateSource, DisplayBounds display);
}

public sealed class DisplayTopologyService : IDisplayTopologyService
{
    private const uint MonitorDefaultToNearest = 2;
    private const int WmGetMinMaxInfo = 0x0024;

    public IReadOnlyList<DisplayBounds> GetDisplays()
    {
        return EnumerateDisplays();
    }

    public DisplayBounds? FindDisplayAwayFrom(Window owner)
    {
        var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        var ownerMonitor = MonitorFromWindow(ownerHandle, MonitorDefaultToNearest);

        return EnumerateDisplays()
            .FirstOrDefault(display => display.Handle != ownerMonitor);
    }

    public void AttachWorkAreaConstraint(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource existingSource)
        {
            AttachHook(window, existingSource);
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                AttachHook(window, source);
            }
        };
    }

    public void FitToDisplayWorkArea(Window window, Window coordinateSource, DisplayBounds display)
    {
        var topLeft = FromDevice(coordinateSource, display.Left, display.Top);
        var bottomRight = FromDevice(coordinateSource, display.Left + display.Width, display.Top + display.Height);

        window.Left = topLeft.X;
        window.Top = topLeft.Y;
        window.Width = Math.Max(window.MinWidth, bottomRight.X - topLeft.X);
        window.Height = Math.Max(window.MinHeight, bottomRight.Y - topLeft.Y);
        window.MaxWidth = window.Width;
        window.MaxHeight = window.Height;
    }

    private static IReadOnlyList<DisplayBounds> EnumerateDisplays()
    {
        var displays = new List<DisplayBounds>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var workArea = monitorInfo.WorkArea;
                    displays.Add(new DisplayBounds(
                        monitor,
                        workArea.Left,
                        workArea.Top,
                        workArea.Right - workArea.Left,
                        workArea.Bottom - workArea.Top));
                }

                return true;
            },
            IntPtr.Zero);

        return displays;
    }

    private static void AttachHook(Window window, HwndSource source)
    {
        source.AddHook(WindowMessageHook);
        ApplyCurrentWorkAreaLimit(window);
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitorArea = monitorInfo.Monitor;
        var workArea = monitorInfo.WorkArea;

        minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
        minMaxInfo.MaxTrackSize.X = minMaxInfo.MaxSize.X;
        minMaxInfo.MaxTrackSize.Y = minMaxInfo.MaxSize.Y;

        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private static void ApplyCurrentWorkAreaLimit(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var workArea = monitorInfo.WorkArea;
        var topLeft = FromDevice(window, workArea.Left, workArea.Top);
        var bottomRight = FromDevice(window, workArea.Right, workArea.Bottom);
        var maxWidth = Math.Max(window.MinWidth, bottomRight.X - topLeft.X);
        var maxHeight = Math.Max(window.MinHeight, bottomRight.Y - topLeft.Y);

        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;
        if (window.Width > maxWidth)
        {
            window.Width = maxWidth;
        }

        if (window.Height > maxHeight)
        {
            window.Height = maxHeight;
        }
    }

    private static Point FromDevice(Window source, int x, int y)
    {
        var transform = PresentationSource.FromVisual(source)?.CompositionTarget?.TransformFromDevice;
        return transform?.Transform(new Point(x, y)) ?? new Point(x, y);
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointL Reserved;
        public PointL MaxSize;
        public PointL MaxPosition;
        public PointL MinTrackSize;
        public PointL MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
