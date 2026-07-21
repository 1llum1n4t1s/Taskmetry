using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Taskmetry.Models;

namespace Taskmetry.Services;

public enum TaskbarEdge
{
    Top,
    Bottom,
    Left,
    Right,
}

public sealed class TaskbarPlacementService
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int MinimumRailLength = 260;
    private const int OuterMargin = 4;
    private static readonly nint HwndTopmost = new(-1);

    public void ConfigureWindow(Window window, bool layoutEditMode)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            return;
        }

        var currentStyle = NativeMethods.GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var style = currentStyle | WsExToolWindow | WsExNoActivate;
        style = layoutEditMode ? style & ~WsExTransparent : style | WsExTransparent;
        _ = NativeMethods.SetWindowLongPtr(handle, GwlExStyle, new nint(style));
    }

    public bool Place(
        Window window,
        int preferredLengthPixels,
        RailPlacementMode placementMode,
        int manualOffsetPixels,
        out PlacementResult placement)
    {
        if (!TryCalculateCurrentPlacement(preferredLengthPixels, placementMode, manualOffsetPixels, out placement))
        {
            return false;
        }

        var scale = Math.Max(window.RenderScaling, 0.5);
        window.Width = placement.Width / scale;
        window.Height = placement.Height / scale;
        window.Position = new PixelPoint(placement.X, placement.Y);

        var handle = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle != nint.Zero && !NativeMethods.SetWindowPos(
                handle,
                HwndTopmost,
                placement.X,
                placement.Y,
                placement.Width,
                placement.Height,
                SwpNoActivate | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return true;
    }

    public bool TryCalculateCurrentPlacement(
        int preferredLengthPixels,
        RailPlacementMode placementMode,
        int manualOffsetPixels,
        out PlacementResult placement)
    {
        var taskbarHandle = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == nint.Zero || !NativeMethods.GetWindowRect(taskbarHandle, out var taskbar))
        {
            placement = default;
            return false;
        }

        var monitorHandle = NativeMethods.MonitorFromWindow(taskbarHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitorHandle == nint.Zero || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            placement = default;
            return false;
        }

        var trayHandle = NativeMethods.FindWindowEx(taskbarHandle, nint.Zero, "TrayNotifyWnd", null);
        var rebarHandle = NativeMethods.FindWindowEx(taskbarHandle, nint.Zero, "ReBarWindow32", null);
        var tray = TryGetRect(trayHandle, out var trayRect) ? trayRect : default;
        var rebar = TryGetRect(rebarHandle, out var rebarRect) ? rebarRect : default;
        placement = CalculatePlacement(
            taskbar,
            tray,
            rebar,
            monitorInfo.Monitor,
            preferredLengthPixels,
            placementMode,
            manualOffsetPixels);
        return true;
    }

    internal static PlacementResult CalculatePlacement(
        Rectangle taskbar,
        Rectangle tray,
        Rectangle rebar,
        Rectangle monitor,
        int preferredLengthPixels,
        RailPlacementMode placementMode = RailPlacementMode.Auto,
        int manualOffsetPixels = 0)
    {
        var edge = DetectEdge(taskbar, monitor);
        var vertical = edge is TaskbarEdge.Left or TaskbarEdge.Right;
        var mainStart = vertical
            ? (rebar.IsValid ? rebar.Bottom + 6 : taskbar.Top + 8)
            : (rebar.IsValid ? rebar.Right + 6 : taskbar.Left + 8);
        var mainEnd = vertical
            ? (tray.IsValid ? tray.Top - 6 : taskbar.Bottom - 8)
            : (tray.IsValid ? tray.Left - 6 : taskbar.Right - 8);
        var gapLength = Math.Max(0, mainEnd - mainStart);
        // Explorer 内部の占有領域が片方でも不明なら、アイコンを覆わないよう外側へ退避する。
        var mayUseGap = rebar.IsValid && tray.IsValid && gapLength >= MinimumRailLength;
        var useOutside = placementMode == RailPlacementMode.OutsideTaskbar || !mayUseGap;

        if (!useOutside)
        {
            var length = Math.Clamp(preferredLengthPixels, MinimumRailLength, gapLength);
            var baseMain = mainEnd - length;
            var main = Math.Clamp(baseMain + manualOffsetPixels, mainStart, mainEnd - length);
            var thickness = Math.Max(1, (vertical ? taskbar.Width : taskbar.Height) - 8);
            return vertical
                ? new PlacementResult(taskbar.Left + 4, main, thickness, length, true, false, edge, baseMain)
                : new PlacementResult(main, taskbar.Top + 4, length, thickness, true, false, edge, baseMain);
        }

        var monitorMainStart = vertical ? monitor.Top + 8 : monitor.Left + 8;
        var monitorMainEnd = vertical ? monitor.Bottom - 8 : monitor.Right - 8;
        var monitorMainLength = Math.Max(MinimumRailLength, monitorMainEnd - monitorMainStart);
        var outerLength = Math.Clamp(preferredLengthPixels, MinimumRailLength, monitorMainLength);
        var outerBaseMain = vertical
            ? Math.Clamp((tray.IsValid ? tray.Top : taskbar.Bottom) - outerLength - 6, monitorMainStart, monitorMainEnd - outerLength)
            : Math.Clamp((tray.IsValid ? tray.Left : taskbar.Right) - outerLength - 6, monitorMainStart, monitorMainEnd - outerLength);
        var outerMain = Math.Clamp(outerBaseMain + manualOffsetPixels, monitorMainStart, monitorMainEnd - outerLength);
        var outerThickness = Math.Max(32, (vertical ? taskbar.Width : taskbar.Height) - 8);

        return edge switch
        {
            TaskbarEdge.Top => new PlacementResult(outerMain, taskbar.Bottom + OuterMargin, outerLength, outerThickness, false, true, edge, outerBaseMain),
            TaskbarEdge.Bottom => new PlacementResult(outerMain, taskbar.Top - outerThickness - OuterMargin, outerLength, outerThickness, false, true, edge, outerBaseMain),
            TaskbarEdge.Left => new PlacementResult(taskbar.Right + OuterMargin, outerMain, outerThickness, outerLength, false, true, edge, outerBaseMain),
            _ => new PlacementResult(taskbar.Left - outerThickness - OuterMargin, outerMain, outerThickness, outerLength, false, true, edge, outerBaseMain),
        };
    }

    internal static TaskbarEdge DetectEdge(Rectangle taskbar, Rectangle monitor)
    {
        if (taskbar.Width >= taskbar.Height)
        {
            var topDistance = Math.Abs(taskbar.Top - monitor.Top);
            var bottomDistance = Math.Abs(monitor.Bottom - taskbar.Bottom);
            return topDistance <= bottomDistance ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        var leftDistance = Math.Abs(taskbar.Left - monitor.Left);
        var rightDistance = Math.Abs(monitor.Right - taskbar.Right);
        return leftDistance <= rightDistance ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    private static bool TryGetRect(nint handle, out Rectangle rectangle)
    {
        if (handle != nint.Zero && NativeMethods.GetWindowRect(handle, out rectangle))
        {
            return true;
        }

        rectangle = default;
        return false;
    }

    public readonly record struct PlacementResult(
        int X,
        int Y,
        int Width,
        int Height,
        bool UsedBlankGap,
        bool IsOutside,
        TaskbarEdge Edge,
        int BaseMainPosition)
    {
        public bool IsVertical => Edge is TaskbarEdge.Left or TaskbarEdge.Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Rectangle(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool IsValid => Width > 0 && Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rectangle Monitor;
        internal Rectangle WorkArea;
        internal uint Flags;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint FindWindow(string? className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out Rectangle rectangle);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern nint SetWindowLongPtr(nint window, int index, nint value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
