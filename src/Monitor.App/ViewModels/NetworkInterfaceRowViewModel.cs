using System.ComponentModel;
using System.Runtime.CompilerServices;
using Monitor.Core.Formatting;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>「ネットワーク」セクション内の入れ子一覧（主表示以外の NIC）の1行分。</summary>
public sealed class NetworkInterfaceRowViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs NameTextChangedEventArgs = new(nameof(NameText));
    private static readonly PropertyChangedEventArgs DownTextChangedEventArgs = new(nameof(DownText));
    private static readonly PropertyChangedEventArgs UpTextChangedEventArgs = new(nameof(UpText));
    private static readonly PropertyChangedEventArgs IsUpChangedEventArgs = new(nameof(IsUp));

    private string _name;
    private string _description;
    private string _nameText = string.Empty;
    private string _downText = string.Empty;
    private string _upText = string.Empty;
    private bool _isUp;

    public NetworkInterfaceRowViewModel(NetworkInterfaceSnapshot nic)
    {
        _name = nic.Name;
        _description = nic.Description;
        Update(nic);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string NameText { get => _nameText; private set => SetProperty(ref _nameText, value); }

    public string DownText { get => _downText; private set => SetProperty(ref _downText, value); }

    public string UpText { get => _upText; private set => SetProperty(ref _upText, value); }

    public bool IsUp { get => _isUp; private set => SetProperty(ref _isUp, value); }

    public bool Matches(NetworkInterfaceSnapshot nic) =>
        string.Equals(_name, nic.Name, StringComparison.Ordinal)
        && string.Equals(_description, nic.Description, StringComparison.Ordinal);

    public void Update(NetworkInterfaceSnapshot nic)
    {
        _name = nic.Name;
        _description = nic.Description;
        NameText = nic.Name.Length > 0 ? nic.Name : "(不明なアダプタ)";
        DownText = "↓ " + ByteFormatter.Bits(nic.ReceiveBytesPerSec);
        UpText = "↑ " + ByteFormatter.Bits(nic.SendBytesPerSec);
        IsUp = nic.IsUp;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChangedEventArgs eventArgs = propertyName switch
        {
            nameof(NameText) => NameTextChangedEventArgs,
            nameof(DownText) => DownTextChangedEventArgs,
            nameof(UpText) => UpTextChangedEventArgs,
            nameof(IsUp) => IsUpChangedEventArgs,
            _ => new PropertyChangedEventArgs(propertyName),
        };
        PropertyChanged?.Invoke(this, eventArgs);
    }
}
