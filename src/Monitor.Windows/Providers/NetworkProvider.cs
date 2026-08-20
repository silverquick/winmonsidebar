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

    public string Name => "Network";

    public bool IsAvailable { get; private set; }

    public void Initialize()
    {
        try
        {
            // GetIfTable2 が使えるかどうかを軽く確認しておく。失敗してもここで例外は外に漏らさない。
            IpHlpApi.ReadInterfaceTable();
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
            IReadOnlyList<MIB_IF_ROW2> rows = IpHlpApi.ReadInterfaceTable();
            if (rows.Count == 0)
            {
                return NetworkSnapshot.Empty;
            }

            double elapsedSeconds = elapsed.TotalSeconds;
            var seenLuids = new HashSet<ulong>();
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

                string alias = row.GetAlias();
                if (string.IsNullOrEmpty(alias))
                {
                    continue;
                }

                seenLuids.Add(row.InterfaceLuid);

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
                    Description: row.GetDescription(),
                    LinkSpeedBitsPerSec: linkSpeed,
                    ReceiveBytesPerSec: receiveBytesPerSec,
                    SendBytesPerSec: sendBytesPerSec,
                    IsUp: true));
            }

            // フィルタで除外されなくなった (抜けた) インターフェースの前回値は捨てておく。
            if (_previous.Count > 0)
            {
                var stale = new List<ulong>();
                foreach (ulong luid in _previous.Keys)
                {
                    if (!seenLuids.Contains(luid))
                    {
                        stale.Add(luid);
                    }
                }
                foreach (ulong luid in stale)
                {
                    _previous.Remove(luid);
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
