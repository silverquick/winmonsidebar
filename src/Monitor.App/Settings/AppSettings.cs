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

    /// <summary>
    /// センサーの表示名の差し替え（生の名前 → 表示したい名前）。
    ///
    /// マザーボードの Super I/O が返す名前は "Temperature #3" や "Fan #1" のような総称で、
    /// どれがどの部位かはボードごとに違ううえ、ソフトウェア側からは判別できない
    /// （実機の ASUS TUF GAMING B550-PLUS でも "Temperature #2/#3/#4/#6" としか出てこない）。
    /// 推測でラベルを付けると根拠なく断定することになるので、利用者が BIOS や他のツールと
    /// 突き合わせて特定した名前をここで与えられるようにする。生の名前は起動時に
    /// %LOCALAPPDATA%\WinMonSidebar\app.log へ "thermal: others = ..." として記録される。
    ///
    /// 別名は表示だけでなく分類にも効く。"VRM" を含む名前を与えればその値は
    /// 「その他温度」ではなく VRM の欄に入る。
    /// 例: { "Temperature #3": "VRM", "Fan #1": "CPU ファン" }
    /// </summary>
    public Dictionary<string, string> SensorAliases { get; set; } = new();
}

/// <summary>
/// AOT/trim 環境でも壊れないよう、リフレクションベースのシリアライザを使わないソースジェネレータ コンテキスト。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
