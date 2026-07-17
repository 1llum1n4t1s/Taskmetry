using System.ComponentModel;
using System.Runtime.InteropServices;
using Taskmetry.Models;

namespace Taskmetry.Services;

public sealed class SystemMetricsService
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasPreviousSample;

    public MetricSnapshot Read()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var idleValue = idle.ToUInt64();
        var kernelValue = kernel.ToUInt64();
        var userValue = user.ToUInt64();
        var cpu = 0d;

        if (_hasPreviousSample)
        {
            cpu = CalculateCpuPercent(
                idleValue - _previousIdle,
                kernelValue - _previousKernel,
                userValue - _previousUser);
        }

        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;
        _hasPreviousSample = true;

        var memory = new NativeMethods.MemoryStatusEx();
        if (!NativeMethods.GlobalMemoryStatusEx(ref memory))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var usedMemory = memory.TotalPhysical - memory.AvailablePhysical;
        var memoryPercent = memory.TotalPhysical == 0 ? 0 : usedMemory * 100d / memory.TotalPhysical;
        return new MetricSnapshot(cpu, memoryPercent, usedMemory, memory.TotalPhysical);
    }

    internal static double CalculateCpuPercent(ulong idleDelta, ulong kernelDelta, ulong userDelta)
    {
        var total = kernelDelta + userDelta;
        if (total == 0 || idleDelta > total)
        {
            return 0;
        }

        return Math.Clamp((total - idleDelta) * 100d / total, 0, 100);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct FileTime
        {
            private readonly uint _low;
            private readonly uint _high;

            internal ulong ToUInt64() => ((ulong)_high << 32) | _low;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatusEx
        {
            internal uint Length;
            internal uint MemoryLoad;
            internal ulong TotalPhysical;
            internal ulong AvailablePhysical;
            internal ulong TotalPageFile;
            internal ulong AvailablePageFile;
            internal ulong TotalVirtual;
            internal ulong AvailableVirtual;
            internal ulong AvailableExtendedVirtual;

            public MemoryStatusEx()
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            }
        }
    }
}
