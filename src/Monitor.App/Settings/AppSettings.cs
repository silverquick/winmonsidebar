using System.Text.Json.Serialization;

namespace Monitor.App.Settings;

/// <summary>
/// %LOCALAPPDATA%\WinMonSidebar\settings.json に保存されるユーザー設定。
/// </summary>
public sealed class AppSettings
{
    public const int FixedThicknessDip = 340;
    public const int MinTopProcessCount = 1;
    public const int MaxTopProcessCount = 16;
    public const int MaxHiddenSensorsCount = 128;

    public int ThicknessDip { get; set; } = FixedThicknessDip;

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

    /// <summary>表示しないセンサーの生の名前または LHM 識別子（大文字小文字を区別しない）。</summary>
    public List<string> HiddenSensors { get; set; } = new();

    /// <summary>
    /// 読み込んだ設定値を安全な範囲に正規化し、null コレクションを補う。
    /// </summary>
    public void Normalize()
    {
        ThicknessDip = FixedThicknessDip;

        if (!string.Equals(Edge, "Left", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Edge, "Top", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Edge, "Right", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Edge, "Bottom", StringComparison.OrdinalIgnoreCase))
        {
            Edge = "Right";
        }

        TopProcessCount = Math.Clamp(TopProcessCount, MinTopProcessCount, MaxTopProcessCount);
        ExpandedSections ??= new Dictionary<string, bool>();
        SensorAliases ??= new Dictionary<string, string>();
        HiddenSensors ??= new List<string>();

        int writeIndex = 0;
        for (int i = 0; i < HiddenSensors.Count && writeIndex < MaxHiddenSensorsCount; i++)
        {
            string entry = HiddenSensors[i];
            if (!string.IsNullOrWhiteSpace(entry))
            {
                HiddenSensors[writeIndex++] = entry;
            }
        }

        if (writeIndex < HiddenSensors.Count)
        {
            HiddenSensors.RemoveRange(writeIndex, HiddenSensors.Count - writeIndex);
        }
    }
}

/// <summary>
/// AOT/trim 環境でも壊れないよう、リフレクションベースのシリアライザを使わないソースジェネレータ コンテキスト。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
