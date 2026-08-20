using System.Diagnostics;
using System.Windows;
using Monitor.App.Settings;
using Monitor.App.Shell;
using Monitor.App.ViewModels;
using Monitor.Core;

namespace Monitor.App.Views;

/// <summary>
/// AppBar として右端に常駐するメインウィンドウ。表示ロジックは <see cref="SidebarViewModel"/> に任せ、
/// このクラスはウィンドウの組み立てと右クリックメニューのハンドリングだけを担う。
/// </summary>
public partial class SidebarWindow : AppBarWindow
{
    private const int FixedThicknessDip = 340;

    private readonly SidebarViewModel _viewModel;
    private readonly AppSettings _settings;

    public SidebarWindow(MetricsHub hub, AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;

        Edge = Enum.TryParse(settings.Edge, ignoreCase: true, out AppBarEdge edge) ? edge : AppBarEdge.Right;

        // 幅は 340 DIP 固定（設定ファイルの値がどうであれ、この値を正とする）。
        ThicknessDip = FixedThicknessDip;
        settings.ThicknessDip = FixedThicknessDip;

        _viewModel = new SidebarViewModel(hub, Dispatcher, settings);
        DataContext = _viewModel;
    }

    private void OnTopmostMenuItemClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
    }

    private void OnExitMenuItemClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    private void OnResetWidthMenuItemClick(object sender, RoutedEventArgs e)
    {
        ThicknessDip = FixedThicknessDip;
        _settings.ThicknessDip = FixedThicknessDip;
        SettingsStore.Save(_settings);
    }

    private void OnRestartAsAdminMenuItemClick(object sender, RoutedEventArgs e)
    {
        RestartAsAdmin();
    }

    private void OnRestartAsAdminButtonClick(object sender, RoutedEventArgs e)
    {
        RestartAsAdmin();
    }

    /// <summary>
    /// 自身を管理者権限で起動し直し、現在のインスタンスを終了する。<see cref="AppBarWindow.OnClosed"/>
    /// 経由で AppBar の解除処理が走ってから終了するよう、Shutdown() を使う（プロセスを直接殺さない）。
    /// 新プロセスの起動自体に失敗した場合（UAC 拒否含む）は現在のインスタンスを終了させない。
    /// </summary>
    private void RestartAsAdmin()
    {
        try
        {
            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(startInfo);
        }
        catch
        {
            // UAC のキャンセルや起動失敗はここで握りつぶし、現在のインスタンスは動作を継続する。
            return;
        }

        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
