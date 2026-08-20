using System.IO;
using System.Text.Json;

namespace Monitor.App.Settings;

/// <summary>
/// <see cref="AppSettings"/> の読み書き。失敗しても例外を外へ投げない
/// （設定の読み書きに失敗してもアプリ本体は動き続ける）。
/// </summary>
public static class SettingsStore
{
    private static readonly object SyncRoot = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinMonSidebar",
        "settings.json");

    /// <summary>
    /// 設定を読み込む。ファイルが無い/壊れている/読めない等どのような理由でも
    /// 例外を投げず、既定値の <see cref="AppSettings"/> を返す。
    /// </summary>
    public static AppSettings Load()
    {
        lock (SyncRoot)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(FilePath);
                AppSettings? settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                return settings ?? new AppSettings();
            }
            catch
            {
                // 破損した設定ファイル/権限不足/その他 I/O エラーはすべて既定値へフォールバックする。
                return new AppSettings();
            }
        }
    }

    /// <summary>
    /// 設定を保存する。書き込み中のクラッシュでファイルが壊れないよう、
    /// 一時ファイルへ書いてから置き換える。高頻度呼び出しに耐えるよう内部で lock する。
    /// 失敗しても例外は外へ投げない。
    /// </summary>
    public static void Save(AppSettings settings)
    {
        lock (SyncRoot)
        {
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

                string tempPath = FilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            catch
            {
                // 保存失敗はアプリの継続動作を妨げない。次回起動時は既定値/前回値のまま扱われる。
            }
        }
    }
}
