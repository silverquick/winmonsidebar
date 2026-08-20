using System.Text.Json.Serialization;

namespace Monitor.App.Settings;

/// <summary>
/// %LOCALAPPDATA%\WinMonSidebar\settings.json に保存されるユーザー設定。
/// </summary>
public sealed class AppSettings
{
    public int ThicknessDip { get; set; } = 340;

    public string Edge { get; set; } = "Right";

    /// <summary>SectionExpander.SectionKey をキーとした展開状態（"cpu" → true など）。</summary>
    public Dictionary<string, bool> ExpandedSections { get; set; } = new();

    public bool ShowAllDisks { get; set; } = true;

    public int TopProcessCount { get; set; } = 8;
}

/// <summary>
/// AOT/trim 環境でも壊れないよう、リフレクションベースのシリアライザを使わないソースジェネレータ コンテキスト。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
