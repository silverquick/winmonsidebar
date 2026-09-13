using Monitor.Core.Abstractions;
using Monitor.Core.Models;
using Monitor.Windows.Native;

namespace Monitor.Windows.Providers;

/// <summary>
/// IP Helper API (GetIfTable2) を使ってネットワーク インターフェースごとの送受信スループットを計測する。
/// PDH ではなくこちらを使うのは、NIC の表示名 (Alias) が素直に取れるため。
/// </summary>
public sealed class NetworkProvider : IMetricProvider<NetworkSnapshot>
{
    private readonly Dictionary<ulong, (ulong In, ulong Out)> _previous = new();
    private readonly List<MIB_IF_ROW2> _interfaceRowsBuffer = new();
    private readonly HashSet<ulong> _seenLuidsBuffer = new();
    private readonly List<ulong> _staleLuidsBuffer = new();
    private readonly Dictionary<ulong, NicText> _nicTextCache = new();

    private readonly record struct NicText(string Alias, string Description);

    public string Name => "Network";

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        try
        {
            // GetIfTable2 が使えるかどうかを軽く確認しておく。失敗してもここで例外は外に漏らさない。
            IpHlpApi.ReadInterfaceTable(_interfaceRowsBuffer);
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public NetworkSnapshot Sample(TimeSpan elapsed)
    {
        if (!IsAvailable)
        {
            return NetworkSnapshot.Empty;
        }

        try
        {
            IpHlpApi.ReadInterfaceTable(_interfaceRowsBuffer);
            IReadOnlyList<MIB_IF_ROW2> rows = _interfaceRowsBuffer;
            if (rows.Count == 0)
            {
                return NetworkSnapshot.Empty;
            }

            double elapsedSeconds = elapsed.TotalSeconds;
            _seenLuidsBuffer.Clear();
            var interfaces = new List<NetworkInterfaceSnapshot>();

            foreach (MIB_IF_ROW2 row in rows)
            {
                if (row.Type == IpHlpApi.IF_TYPE_SOFTWARE_LOOPBACK)
                {
                    continue;
                }

                if (row.OperStatus != IpHlpApi.IfOperStatusUp)
                {
                    continue;
                }

                if (row.IsFilterInterface)
                {
                    continue;
                }

                bool hasCachedText = _nicTextCache.TryGetValue(row.InterfaceLuid, out NicText cachedText);
                string alias = hasCachedText && row.AliasEquals(cachedText.Alias)
                    ? cachedText.Alias
                    : row.GetAlias();
                if (string.IsNullOrEmpty(alias))
                {
                    continue;
                }

                string description = hasCachedText && row.DescriptionEquals(cachedText.Description)
                    ? cachedText.Description
                    : row.GetDescription();
                if (!hasCachedText || !ReferenceEquals(alias, cachedText.Alias) || !ReferenceEquals(description, cachedText.Description))
                {
                    _nicTextCache[row.InterfaceLuid] = new NicText(alias, description);
                }

                _seenLuidsBuffer.Add(row.InterfaceLuid);

                double receiveBytesPerSec = 0;
                double sendBytesPerSec = 0;

                if (elapsedSeconds > 0 && _previous.TryGetValue(row.InterfaceLuid, out (ulong In, ulong Out) prev))
                {
                    receiveBytesPerSec = row.InOctets >= prev.In
                        ? (row.InOctets - prev.In) / elapsedSeconds
                        : 0;
                    sendBytesPerSec = row.OutOctets >= prev.Out
                        ? (row.OutOctets - prev.Out) / elapsedSeconds
                        : 0;
                }

                _previous[row.InterfaceLuid] = (row.InOctets, row.OutOctets);

                ulong linkSpeed = row.ReceiveLinkSpeed;
                if (linkSpeed == 0 || linkSpeed == ulong.MaxValue)
                {
                    linkSpeed = row.TransmitLinkSpeed;
                }
                if (linkSpeed == ulong.MaxValue)
                {
                    linkSpeed = 0;
                }

                interfaces.Add(new NetworkInterfaceSnapshot(
                    Name: alias,
                    Description: description,
                    LinkSpeedBitsPerSec: linkSpeed,
                    ReceiveBytesPerSec: receiveBytesPerSec,
                    SendBytesPerSec: sendBytesPerSec,
                    IsUp: true));
            }

            // フィルタで除外されなくなった (抜けた) インターフェースの前回値は捨てておく。
            if (_previous.Count > 0)
            {
                _staleLuidsBuffer.Clear();
                foreach (ulong luid in _previous.Keys)
                {
                    if (!_seenLuidsBuffer.Contains(luid))
                    {
                        _staleLuidsBuffer.Add(luid);
                    }
                }
                foreach (ulong luid in _staleLuidsBuffer)
                {
                    _previous.Remove(luid);
                    _nicTextCache.Remove(luid);
                }
            }

            interfaces.Sort((a, b) =>
                (b.ReceiveBytesPerSec + b.SendBytesPerSec).CompareTo(a.ReceiveBytesPerSec + a.SendBytesPerSec));

            double totalReceive = 0;
            double totalSend = 0;
            foreach (NetworkInterfaceSnapshot iface in interfaces)
            {
                totalReceive += iface.ReceiveBytesPerSec;
                totalSend += iface.SendBytesPerSec;
            }

            return new NetworkSnapshot(
                Interfaces: interfaces,
                TotalReceiveBytesPerSec: totalReceive,
                TotalSendBytesPerSec: totalSend);
        }
        catch
        {
            IsAvailable = false;
            return NetworkSnapshot.Empty;
        }
    }

    public void Dispose()
    {
    }
}
