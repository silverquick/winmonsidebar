using Monitor.Core.Formatting;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>「ネットワーク」セクション内の入れ子一覧（主表示以外の NIC）の1行分。都度作り直す。</summary>
public sealed class NetworkInterfaceRowViewModel
{
    public NetworkInterfaceRowViewModel(NetworkInterfaceSnapshot nic)
    {
        NameText = nic.Name.Length > 0 ? nic.Name : "(不明なアダプタ)";
        DownText = "↓ " + ByteFormatter.Bits(nic.ReceiveBytesPerSec);
        UpText = "↑ " + ByteFormatter.Bits(nic.SendBytesPerSec);
        IsUp = nic.IsUp;
    }

    public string NameText { get; }

    public string DownText { get; }

    public string UpText { get; }

    public bool IsUp { get; }
}
