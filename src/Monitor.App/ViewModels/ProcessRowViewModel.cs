using System.ComponentModel;
using System.Runtime.CompilerServices;
using Monitor.Core.Formatting;
using Monitor.Core.Models;

namespace Monitor.App.ViewModels;

/// <summary>
/// プロセス一覧の1行分。<see cref="SidebarViewModel"/> が PID で照合して既存インスタンスを
/// 差分更新するため、Pid 以外のすべてのプロパティは変更通知付きで再代入可能にしてある。
/// </summary>
public sealed class ProcessRowViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _cpuText = string.Empty;
    private string _memoryText = string.Empty;

    public ProcessRowViewModel(int pid)
    {
        Pid = pid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Pid { get; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public void Update(ProcessInfo info)
    {
        Name = string.IsNullOrEmpty(info.Name) ? $"PID {info.Pid}" : info.Name;
        CpuText = $"{info.CpuPercent:F1}%";
        MemoryText = ByteFormatter.Bytes(info.WorkingSetBytes);
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
