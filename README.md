# winmonsidebar

デスクトップの右端に領域を確保して常駐する、Windows 用のシステムモニタ サイドバー。

```
┌──────────────────────────────┬──────────┐
│                              │ System   │
│                              │ Monitor  │
│                              ├──────────┤
│      通常のウィンドウは       │ CPU  32% │
│      この領域までしか         │ 4.45 GHz │
│      広がらない               │ ▁▃▂▅▃▁▂ │
│      （最大化しても）         │ ██▁▃▂▅▃ │
│                              ├──────────┤
│                              │ GPU  58% │
│                              │ 55°C 76W │
│                              ├──────────┤
│                              │ MEM  68% │
│                              ├──────────┤
│                              │ DISK ... │
│                              │ NET  ... │
└──────────────────────────────┴──────────┘
                                 └ 340px
```

タスクバーと同じ Win32 の **AppBar**（`SHAppBarMessage`）として登録するため、単なる最前面
オーバーレイとは違い、**他のウィンドウを最大化してもこの領域を避ける**。

## 動作環境

| 項目 | 要件 |
| --- | --- |
| OS | Windows 11（Windows 11 Pro build 26200 で開発・動作確認） |
| ランタイム | .NET 10 |
| アーキテクチャ | x64 のみ（`Directory.Build.props` で固定） |
| GPU 詳細 | NVIDIA GPU のとき有効（NVAPI / NVML）。他ベンダーでは DXGI + PDH の範囲まで |
| ビルド | .NET 10 SDK（10.0.400 で確認） |

Visual Studio も Windows SDK も不要。`dotnet` CLI だけでビルドできる。

## ビルドと実行

```powershell
# ビルド
dotnet build WindowsMonitor.sln -c Debug

# 実行
.\src\Monitor.App\bin\x64\Debug\net10.0-windows\Monitor.App.exe
```

出力先が `bin\Debug\` ではなく `bin\x64\Debug\` になるのは、`Directory.Build.props` で
`Platforms` / `PlatformTarget` を x64 に固定しているため。

### 単一 exe として発行する

```powershell
dotnet publish src\Monitor.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out
```

`out\Monitor.App.exe`（約 129MB）に .NET ランタイムごと同梱される。WPF のネイティブ DLL
5 個（`wpfgfx_cor3.dll` 等）は単一ファイルに埋め込めないため exe と同じ場所に並ぶ。この 6 個を
まとめて配置すれば .NET のインストールなしで動く。

## 表示する情報

| セクション | 内容 |
| --- | --- |
| **CPU** | モデル名 / 使用率 / 論理コアごとの使用率と動作クロック / 現在・ベースクロック / 物理・論理コア数 / パッケージ温度・電力<sup>※</sup> |
| **GPU** | アダプタ名 / 使用率 / エンジン別（3D・Copy・Video・Compute） / VRAM 使用量と総量 / 温度・ホットスポット温度 / ファン % と RPM / 消費電力と電力上限 / コア・メモリクロック |
| **メモリ** | 物理 / 使用中・変更済み・スタンバイ・空き の内訳バー / 圧縮済み / キャッシュ / ページプール・非ページプール / システムキャッシュ / コミットとコミットピーク / ハードウェア予約 / ハンドル・プロセス・スレッド数 |
| **メモリ（モジュール）** | SMBIOS 由来のスロット構成・容量・規格・動作クロック・メーカー・型番 |
| **ページファイル** | ファイルごとのパス / 割当・使用・ピーク / 使用率 |
| **ディスク** | 物理ディスクごとの型番・バス種別・容量・読み書き B/s・Busy% ・温度<sup>※</sup> |
| **ドライブ** | 全論理ドライブの使用率（ネットワークドライブを含む） |
| **ネットワーク** | NIC ごとの受信・送信。主要 NIC を大きく、残りは折りたたみ |
| **温度・ファン** | CPU Tctl/Tdie・コア温度・パッケージ電力 / マザーボード温度 / VRM 温度 / ファン RPM<sup>※</sup> |
| **プロセス** | 上位 N 件の CPU・メモリ・ディスク・GPU |

<sup>※</sup> 取得条件については「温度の取得可否」を参照。

各セクションは見出しをクリックして折りたためる。折りたたむと見出しの右に要約
（`72% · 4.25 GHz · 68°C` など）が出る。開閉状態は設定ファイルに保存される。

## 温度の取得可否

温度センサーは項目ごとに難易度がまったく違う。ここが本プロジェクトで最も注意が要る部分。

| センサー | 取得手段 | 管理者権限 | 状態 |
| --- | --- | --- | --- |
| GPU 温度 / ホットスポット | NVAPI（`nvapi64.dll`） | 不要 | 取得可 |
| GPU ファン % / RPM | NVAPI | 不要 | 取得可 |
| GPU 消費電力 / 電力上限 | NVML（`nvml.dll`） | 不要 | 取得可 |
| ディスク温度 | `IOCTL_STORAGE_QUERY_PROPERTY`（`StorageDeviceTemperatureProperty` = 52） | 不要 | ドライブ依存 |
| **CPU 温度（Tctl/Tdie）** | LibreHardwareMonitorLib（カーネルドライバ） | **必要** | 管理者起動時のみ |
| **マザーボード / VRM 温度** | 同上（Super I/O） | **必要** | 管理者起動時のみ |
| **ファン RPM（ケース・CPU）** | 同上 | **必要** | 管理者起動時のみ |

### なぜ CPU 温度だけ特別扱いなのか

CPU 温度には統一された Windows API が存在しない。

- WMI の `MSAcpi_ThermalZoneTemperature` は多くのデスクトップ機で「サポートされていません」を返す
- WMI の `Win32_TemperatureProbe.CurrentReading` は Microsoft 自身が「現在の実装では設定されない」と
  明記している
- AMD Ryzen の Tctl/Tdie は **SMN レジスタを PCI コンフィグ空間経由で読む**必要があり、
  これはカーネルモードでしか行えない

HWiNFO や LibreHardwareMonitor が自前の署名済みカーネルドライバを同梱しているのはこのため。
本プロジェクトもこの一点だけ `LibreHardwareMonitorLib` に委ね、**管理者権限で起動されたときだけ**
有効化する。

非管理者で起動した場合、「温度・ファン」セクションには説明と「管理者で再起動」ボタンが出るだけで、
**それ以外の機能はすべて通常どおり動く**。

> 非管理者でも `LibreHardwareMonitor` の API 自体は呼べてしまうが、温度が `0.00` という
> 紛らわしい偽の値で返ってくる。そのため権限を判定して**プロバイダごと無効化**し、
> `null`（UI では `—`）を返す設計にしている。

### 管理者権限が使えるかどうかの前提

カーネルドライバのロードは Windows 側の設定に左右される。

- **コア分離（メモリ整合性 / HVCI）が有効**だと、この種のドライバはブロックされることがある
- 脆弱ドライバブロックリスト（`VulnerableDriverBlocklistEnable`）は既定で有効。旧世代の
  `WinRing0x64.sys` を使うライブラリはこれに掲載されており弾かれる

確認コマンド:

```powershell
Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard |
  Select-Object SecurityServicesRunning   # 2 が含まれていれば HVCI 稼働中
```

## アーキテクチャ

UI を差し替え可能にするため、計測層は UI にも OS にも依存しない。

```
Monitor.App              WPF シェル（AppBar / 自前描画コントロール / ViewModel）
      ↓
Monitor.Core             IMetricProvider<T> / モデル / MetricsHub / 履歴
      ↑                        ↑                        ↑
Monitor.Windows    Monitor.Vendors.Nvidia    Monitor.Optional.Lhm
      ↓                        ↓                        ↓
kernel32 / psapi        nvapi64.dll             LibreHardwareMonitorLib
pdh / iphlpapi          nvml.dll                （カーネルドライバ）
dxgi / mpr / ntdll
```

```
winmonsidebar/
├─ WindowsMonitor.sln
├─ Directory.Build.props            Nullable / ImplicitUsings / x64 固定
└─ src/
   ├─ Monitor.Core/                 TFM: net10.0        ← OS 非依存
   │  ├─ Abstractions/              IMetricProvider<T> / IThermalProvider / IGpuVendorSensors
   │  ├─ Models/                    各 Snapshot 型
   │  ├─ Collections/RingBuffer.cs  スパークライン用の固定長リングバッファ
   │  ├─ MetricsHistory.cs          系列ごとの履歴
   │  ├─ MetricsHub.cs              サンプリングの司令塔
   │  └─ Formatting/                バイト・温度・クロック・電力の整形
   ├─ Monitor.Windows/              TFM: net10.0-windows
   │  ├─ Native/                    Kernel32 / Psapi / Pdh / PdhQuery / IpHlpApi /
   │  │                             Dxgi / StorageApi / Smbios / Shell32 / User32 / DwmApi
   │  └─ Providers/                 Cpu / Memory / Disk / Volume / Network / Gpu / Process
   ├─ Monitor.Vendors.Nvidia/       NVAPI + NVML
   ├─ Monitor.Optional.Lhm/         LibreHardwareMonitorLib（管理者時のみ）
   └─ Monitor.App/
      ├─ Shell/                     AppBarHost / AppBarWindow
      ├─ Controls/                  Sparkline / GaugeBar / CoreGrid / StackedBar / SectionExpander
      ├─ Settings/                  settings.json の読み書き
      ├─ ViewModels/
      ├─ Views/SidebarWindow.xaml
      └─ Themes/Dark.xaml
```

### 計測手段

| 項目 | 手段 |
| --- | --- |
| CPU 使用率（全体） | `GetSystemTimes()` の差分 |
| CPU コア別使用率・クロック | PDH `\Processor Information(*)` |
| CPU モデル名・ベースクロック | レジストリ `HARDWARE\DESCRIPTION\System\CentralProcessor\0` |
| 物理コア数 | `GetLogicalProcessorInformationEx(RelationProcessorCore)` |
| メモリ | `GlobalMemoryStatusEx()` / `GetPerformanceInfo()` / PDH `\Memory\*` |
| メモリモジュール | `GetSystemFirmwareTable('RSMB')` → SMBIOS Type 16 / Type 17 を直接パース |
| 圧縮済みメモリ | `Memory Compression` プロセスのワーキングセット |
| ページファイル | `NtQuerySystemInformation(SystemPagefileInformation)` |
| ディスク R/W・Busy | PDH `\PhysicalDisk(*)` |
| ディスク型番・容量・SSD 判定 | `IOCTL_STORAGE_QUERY_PROPERTY` |
| ディスク温度 | `IOCTL_STORAGE_QUERY_PROPERTY`（PropertyId = 52） |
| 論理⇔物理の対応 | `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` |
| ドライブ容量 | `GetDiskFreeSpaceExW` / `GetVolumeInformationW` / `WNetGetConnectionW` |
| ネットワーク | IP Helper `GetIfTable2()` の Octets 差分（LUID で NIC を追跡） |
| GPU 使用率・VRAM 使用量 | PDH `\GPU Engine(*)` / `\GPU Adapter Memory(*)` |
| GPU アダプタ名・VRAM 総量 | DXGI `IDXGIAdapter1::GetDesc1`（vtable 直呼び） |
| GPU 温度・ファン・クロック | NVAPI |
| GPU 電力 | NVML |
| プロセス | `Process` + `GetProcessTimes()` / `GetProcessIoCounters()` |
| AppBar | `SHAppBarMessage()` |
| 見た目（Acrylic・ダーク） | `DwmSetWindowAttribute()` |

## 依存関係の方針

外部 NuGet 依存は **`LibreHardwareMonitorLib` 0.9.6（MPL-2.0）の 1 つだけ**。しかも
`Monitor.Optional.Lhm` プロジェクトからしか参照しない。他のプロジェクトは Windows 標準 DLL と
ベンダー DLL への P/Invoke だけで完結している。

`System.Management`（WMI）も使っていない。メモリのモジュール情報は SMBIOS を直接読み、
ディスク情報は IOCTL で取っている。

これは「出所不明な依存を持ち込まない」ためと、WMI 経由の値がしばしば壊れている
（例: `Win32_VideoController.AdapterRAM` は 4GB で頭打ちになる）ためでもある。

## 設定とログ

| ファイル | パス |
| --- | --- |
| 設定 | `%LOCALAPPDATA%\WinMonSidebar\settings.json` |
| ログ | `%LOCALAPPDATA%\WinMonSidebar\app.log` |

```json
{
  "ThicknessDip": 340,
  "Edge": "Right",
  "ExpandedSections": {
    "cpu": true, "gpu": true, "memory": false, "memory-modules": false,
    "disk": true, "volumes": true, "network": true, "network-all": false,
    "thermal": false, "process": true
  },
  "ShowAllDisks": true,
  "TopProcessCount": 8
}
```

サイドバーを右クリックすると「最前面表示」「管理者で再起動」「幅をリセット」「終了」が出る。

## 実装上の注意点

これらは自明でない上に、間違えたときの影響が大きい。

### AppBar の解除は必ず行う

`SHAppBarMessage(ABM_REMOVE)` を呼ばずにプロセスが終わると、**デスクトップの作業領域が
縮んだまま元に戻らない**。ユーザーの環境を壊すので、解除は多重の安全網で保証している。

- `WM_DESTROY`（`AppBarHost` のメッセージフック）
- `Window.OnClosed`
- `Application.SessionEnding`（ログオフ・シャットダウン）
- `AppDomain.ProcessExit`
- `AppDomain.UnhandledException`

`OnClosing` では解除**しない**。閉じる操作はキャンセルされうるため、そこで解除すると
「生きているのに領域を確保しないウィンドウ」が残ってしまう。

二重登録を防ぐため、名前付き Mutex `Global\WinMonSidebar_SingleInstance` で単一インスタンス化
している（AppBar が 2 つ登録されると作業領域が二重に削られる）。

### 座標は物理ピクセルで扱う

AppBar API が扱う矩形は物理ピクセル、WPF の `Window.Left/Top/Width/Height` は DIP。
混ぜると高 DPI 環境で位置がずれるため、位置決めは `GetDpiForWindow()` で DPI を取ってから
`SetWindowPos()` を物理ピクセルで直接呼んでいる。マニフェストで `PerMonitorV2` を宣言済み。

### ネットワークドライブでサンプリングを止めない

`GetDiskFreeSpaceExW` はネットワークドライブが不通のとき数十秒ブロックしうる。これを
サンプリングスレッドで行うとサイドバー全体が固まる。`VolumeProvider` は容量取得を別スレッドで
5 秒間隔に回し、`Sample()` は直前の結果のキャッシュを即座に返す。

### 取得できない値は 0 ではなく null

`0°C` と「温度が取れない」を混同させないため、取得不能な値はすべて `double?` / `int?` の
`null` で表し、UI では `—` と表示する。

### PDH のカウンター名は英語で指定する

日本語 Windows でもカウンターを引けるよう、`PdhAddEnglishCounter` を使っている。
また GPU 使用率は 100 を超えうるので `PDH_FMT_NOCAP100` を指定している。

### プロバイダは例外を外に出さない

`Initialize()` / `Sample()` / `Dispose()` はいずれも例外を漏らさない。センサーが無い環境や
権限が足りない環境でも、その項目だけが `null` になってアプリ全体は動き続ける。

## 既知の制限

- **CPU 温度・マザーボード温度・ファン RPM は未検証**。実装済みだが、管理者権限での動作確認が
  まだ取れていない。カーネルドライバのロードが Windows の設定に左右されるため。
- ディスク温度は `IOCTL_STORAGE_QUERY_PROPERTY` に対応するドライブでしか取れない。古い SATA
  ドライブは `ERROR_INVALID_FUNCTION` を返す（開発機では 7 台中 4 台で取得可）。ATA SMART
  passthrough なら取れるが管理者権限が要る。
- GPU の詳細（温度・ファン・電力）は NVIDIA のみ。AMD / Intel は未対応（`IGpuVendorSensors`
  を実装すれば差し込める設計にはなっている）。
- ネットワークインターフェースの列挙数が `Get-NetAdapter` より多い。IP Helper は Hyper-V の
  vSwitch 拡張アダプタや WAN Miniport の疑似インターフェースまで返すため。
- プロセス別のネットワーク使用量は未対応（ETW が必要）。
- マルチモニタ環境は未検証。実装上は対応しているが、開発機が 1 画面のため確認が取れていない。

## ライセンス

未定。`LibreHardwareMonitorLib` は MPL-2.0。
