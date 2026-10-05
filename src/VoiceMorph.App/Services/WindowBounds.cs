using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VoiceMorph.App.Services;

/// <summary>Keep custom-chrome maximized windows inside the current monitor's work area.</summary>
internal static class WindowBounds
{
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook((nint handle, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message != 0x24) return nint.Zero; // WM_GETMINMAXINFO
                var monitor = MonitorFromWindow(handle, 2);
                var area = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref area)) return nint.Zero;
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MaxPosition = new PointI(area.Work.Left - area.Monitor.Left, area.Work.Top - area.Monitor.Top);
                limits.MaxSize = new PointI(area.Work.Right - area.Work.Left, area.Work.Bottom - area.Work.Top);
                var scale = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
                limits.MinTrackSize = new PointI((int)Math.Ceiling(window.MinWidth * scale.M11),
                    (int)Math.Ceiling(window.MinHeight * scale.M22));
                Marshal.StructureToPtr(limits, lParam, false);
                handled = true;
                return nint.Zero;
            });
        };
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointI(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public PointI Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
