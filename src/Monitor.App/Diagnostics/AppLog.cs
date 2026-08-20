using System.Globalization;
using System.IO;
using System.Text;

namespace Monitor.App.Diagnostics;

/// <summary>
/// 最小限のファイルログ。WPF はレイアウト/描画パスで発生した例外を
/// <c>DispatcherUnhandledException</c> に流すため、そこで握りつぶすと
/// 「ウィンドウは出ているが中身が真っ白」という状態になり原因が一切分からなくなる。
/// その診断のためにファイルへ落とす。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinMonSidebar",
        "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // ログ出力の失敗でアプリを止めない。
        }
    }

    public static void Write(string context, Exception ex) => Write($"{context}: {ex}");
}
