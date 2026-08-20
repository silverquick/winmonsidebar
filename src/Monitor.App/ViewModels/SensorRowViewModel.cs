namespace Monitor.App.ViewModels;

/// <summary>
/// 温度・ファン等、名前と整形済み値の組だけを表示すればよい行向けの軽量な読み取り専用行。
/// Thermal セクションの各リスト（コア温度・ファン・その他温度）はこの型で表示する。
/// 更新頻度が低い（SlowInterval=2秒）ため、Disk/Process 行のような差分更新はせず、
/// SidebarViewModel 側でリストごと作り直す。
/// </summary>
public readonly record struct SensorRowViewModel(string Name, string ValueText);
