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

    /// <summary>展開トグルなど UI 起因の保存をまとめる待ち時間。</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinMonSidebar",
        "settings.json");

    private static readonly DebouncedSettingsWriter Debouncer =
        new(static json => WriteJson(FilePath, json), DebounceDelay);

    private static volatile string? s_filePathOverride;

    public static string FilePath => s_filePathOverride ?? DefaultFilePath;

    /// <summary>
    /// テスト用の seam。保存先を差し替え、実ユーザーの設定ファイルへ書かないようにする。null で既定へ戻す。
    /// </summary>
    internal static void SetFilePathForTest(string? path) => s_filePathOverride = path;

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
    /// 設定を即座に（呼び出し元スレッドで同期的に）保存する。書き込み中のクラッシュでファイルが壊れないよう、
    /// 一時ファイルへ書いてから置き換える。debounce 中の保留内容は破棄する
    /// （<paramref name="settings"/> の方が新しい状態を表すため）。失敗しても例外は外へ投げない。
    /// </summary>
    public static void Save(AppSettings settings)
    {
        if (TrySerialize(settings) is { } json)
        {
            Debouncer.WriteNow(json);
        }
    }

    /// <summary>
    /// UI 起因・高頻度の保存用。呼び出し元スレッドで JSON のスナップショットを取り、書き込みは
    /// 約 500ms の debounce 後にバックグラウンドで行う（最後の内容が勝つ）。失敗しても例外は外へ投げない。
    /// 終了時は <see cref="FlushPendingSaves"/> で保留分を書き出すこと。
    /// </summary>
    public static void SaveDebounced(AppSettings settings)
    {
        if (TrySerialize(settings) is { } json)
        {
            Debouncer.Schedule(json);
        }
    }

    /// <summary>debounce 中の保存があれば呼び出し元スレッドで同期的に書き出す。例外は外へ投げない。</summary>
    public static void FlushPendingSaves() => Debouncer.Flush();

    private static string? TrySerialize(AppSettings settings)
    {
        try
        {
            return JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        }
        catch
        {
            // シリアライズ失敗も保存失敗と同様に扱い、アプリの継続動作を妨げない。
            return null;
        }
    }

    /// <summary>
    /// シリアライズ済みの JSON を一時ファイルへ書いてから置き換える。高頻度呼び出しに耐えるよう内部で lock する。
    /// 失敗しても例外は外へ投げない。
    /// </summary>
    internal static void WriteJson(string filePath, string json)
    {
        lock (SyncRoot)
        {
            try
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }
            catch
            {
                // 保存失敗はアプリの継続動作を妨げない。次回起動時は既定値/前回値のまま扱われる。
            }
        }
    }
}
