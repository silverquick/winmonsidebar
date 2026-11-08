using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Monitor.App.Settings;

namespace Monitor.App.Tests;

/// <summary>
/// テストアセンブリ全体の前処理。SidebarViewModel の既定経路など、テスト中に SettingsStore へ
/// 保存が流れても実ユーザーの設定ファイル（%LOCALAPPDATA%）へ書かないよう、保存先を一時フォルダへ差し替える。
/// </summary>
[TestClass]
public static class TestAssemblySetup
{
    private static string? s_tempDirectory;

    [AssemblyInitialize]
    public static void Initialize(TestContext _)
    {
        s_tempDirectory = Path.Combine(Path.GetTempPath(), "WinMonSidebarTests_" + Guid.NewGuid().ToString("N"));
        SettingsStore.SetFilePathForTest(Path.Combine(s_tempDirectory, "settings.json"));
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        // 保留中の保存を一時フォルダへ書き出してから片付ける（既定パスへ戻した後に書かれないように）。
        SettingsStore.FlushPendingSaves();

        try
        {
            if (s_tempDirectory is not null && Directory.Exists(s_tempDirectory))
            {
                Directory.Delete(s_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダの削除失敗はテスト結果に影響させない。
        }
    }
}
