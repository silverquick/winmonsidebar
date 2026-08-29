namespace Monitor.Core.Models;

public sealed record ProcessInfo(
    int Pid,
    string Name,
    double CpuPercent,
    ulong WorkingSetBytes);

public readonly record struct ProcessSnapshot(IReadOnlyList<ProcessInfo> Processes)
{
    public static ProcessSnapshot Empty { get; } = new(
        Processes: Array.Empty<ProcessInfo>());
}
