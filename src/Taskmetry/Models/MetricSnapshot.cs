namespace Taskmetry.Models;

public readonly record struct MetricSnapshot(double CpuPercent, double MemoryPercent, ulong UsedMemoryBytes, ulong TotalMemoryBytes);
