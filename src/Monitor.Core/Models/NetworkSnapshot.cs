namespace Monitor.Core.Models;

public readonly record struct NetworkInterfaceSnapshot(
    string Name,
    string Description,
    ulong LinkSpeedBitsPerSec,
    double ReceiveBytesPerSec,
    double SendBytesPerSec,
    bool IsUp)
{
    public static NetworkInterfaceSnapshot Empty { get; } = new(
        Name: string.Empty,
        Description: string.Empty,
        LinkSpeedBitsPerSec: 0,
        ReceiveBytesPerSec: 0,
        SendBytesPerSec: 0,
        IsUp: false);
}

public readonly record struct NetworkSnapshot(
    IReadOnlyList<NetworkInterfaceSnapshot> Interfaces,
    double TotalReceiveBytesPerSec,
    double TotalSendBytesPerSec)
{
    public static NetworkSnapshot Empty { get; } = new(
        Interfaces: Array.Empty<NetworkInterfaceSnapshot>(),
        TotalReceiveBytesPerSec: 0,
        TotalSendBytesPerSec: 0);
}
