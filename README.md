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
| CPU・マザボ温度 | 任意。[PawnIO](https://github.com/namazso/PawnIO.Setup) のインストールと管理者起動が必要 |
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

`out\Monitor.App.exe`（約 130MB）に .NET ランタイムごと同梱される。WPF・SkiaSharp 等の
ネイティブ DLL（`wpfgfx_cor3.dll` / `PresentationNative_cor3.dll` / `D3DCompiler_47_cor3.dll` 等）は
単一ファイルに埋め込めないため exe と同じ場所に並ぶ。`out\` の内容を丸ごと配置すれば
.NET のインストールなしで動く（`*.pdb` は配布に不要）。

## 表示する情報

| セクション | 内容 |
| --- | --- |
| **CPU** | モデル名 / 使用率 / 論理コアごとの使用率と動作クロック / 現在・ベースクロック / 物理・論理コア数 / パッケージ温度・電力<sup>※</sup> |
| **GPU** | アダプタ名 / 使用率 / エンジン別（3D・Copy・Video・Compute） / VRAM 使用量と総量 / 温度・ホットスポット温度 / ファン % と RPM / 消費電力と電力上限 / コア・メモリクロック |
| **メモリ** | 物理 / 使用中・変更済み・スタンバイ・空き の内訳バー / 圧縮済み / キャッシュ / ページプール・非ページプール / システムキャッシュ / コミットと上限（ピークはツールチップ） / ハードウェア予約 / ハンドル・プロセス・スレッド数 |
| **メモリ（モジュール）** | SMBIOS 由来のスロット構成・容量・規格・動作クロック・メーカー・型番 |
| **ページファイル** | ファイルごとのパス / 割当・使用・ピーク / 使用率 |
| **ストレージ** | 物理ディスクごとにグループ化した一覧。見出し行は型番・バス種別・読み書き B/s・Busy%・温度<sup>※</sup>と、そのディスク単体の書き込み履歴グラフ。配下に論理ボリュームの使用率と使用/合計容量が並ぶ。ネットワークドライブは末尾にまとめる |
| **ネットワーク** | NIC ごとの受信・送信。主要 NIC を大きく、残りは折りたたみ |
| **温度・ファン** | CPU Tctl/Tdie・コア温度・パッケージ電力 / マザーボード温度 / VRM 温度 / ファン RPM<sup>※</sup> |
| **プロセス** | 上位 N 件の CPU・メモリ・ディスク・GPU |

<sup>※</sup> 取得条件については「温度の取得可否」を参照。

各セクションは見出しをクリックして折りたためる。折りたたむと見出しの右に要約
（`72% · 4.25 GHz · 68°C` など）が出る。開閉状態は設定ファイルに保存される。

見出し左のアクセントバーは、通常はセクションごとの色だが、警告状態になると色が変わる。
詳細は次の「警告表示」を参照。

## 警告表示

見出し左の3pxアクセントバーと、該当する値のテキストが、注意（琥珀）/危険（赤）の2段階
（`AlertLevel`: `None` / `Caution` / `Critical`）で色を変える。閾値は `AlertThresholds.cs` に、
判定ロジックは `AlertEvaluator.cs` に集約してあり、各セクションの ViewModel には閾値を
散らばらせていない。

### 警告する条件を絞った理由

使用率が高いこと自体は原則として警告にしない。CPU / GPU の使用率 100% はビルド中やゲーム中なら正常、
瞬間的なディスク Busy 100% は大きなコピー中なら正常、物理メモリの使用率が高いのは Windows がキャッシュ
で空きを埋める正常な挙動である。ここに閾値を置くと常時どこかが点灯し続け、色が警告として
機能しなくなる。警告するのは「枯渇」「温度」「障害」および異常に長く継続した場合のディスク高 Busy（観測警告）
など、ユーザーが実際に手を打つべき・把握すべき状態に絞った。

### 閾値

| カテゴリ | 対象 | 注意 (Caution) | 危険 (Critical) |
| --- | --- | --- | --- |
| 枯渇 | ドライブ空き容量（使用率 かつ 絶対空き容量） | 90%以上 かつ 32GB未満 | 95%以上 かつ 10GB未満 |
| 枯渇 | メモリコミット（使用中 / 上限） | 85%以上 | 95%以上 |
| 温度 | CPU (Tctl/Tdie) | 85°C以上 | 95°C以上 |
| 温度 | GPU コア | 80°C以上 | 87°C以上 |
| 温度 | GPU ホットスポット | 95°C以上 | 105°C以上 |
| 温度 | ディスク | ドライブ申告値を優先<sup>※2</sup> | 同左 |
| 障害 | 冷却異常 | CPU/マザーボード温度 ≥ 85°C<sup>※3</sup> かつ 全認識ファンが 0 RPM | （無し） |
| 障害 | ネットワークドライブ切断 | 即座に警告 | （無し） |
| 観測警告 | ディスク高 Busy 継続 | 95%以上が2分継続<sup>※4</sup> | 99%以上が10分継続<sup>※4</sup> |

<sup>※2</sup> 次項「ディスク温度はドライブ自身の申告値を使う」を参照。
<sup>※3</sup> マザーボード側専用の「注意」閾値表が無いため、CPU の注意閾値をそのまま暫定流用している。
<sup>※4</sup> コピーが正常か、I/O 詰まりが異常かを Busy% だけで断定するものではなく、「高負荷が継続」という観測事実を知らせる警告。発報後は 85% 未満が 15 秒継続するまで維持する（ヒステリシス）。

閾値は `AlertThresholds.cs` に定数として置いてあるだけで、`settings.json` から変更する機能は
無い（今回は未実装）。

### ドライブ容量は率と絶対値の AND

率だけで判定すると、16TB のドライブが 3.9TB 残した状態でも警告になってしまう（率は高いが
実際には余裕がある）。絶対値だけで判定すると、120GB のドライブが 32GB（27%）残した状態で
警告になってしまう（残量の数字は小さいが、そのドライブにとってはまだ余裕がある）。どちらの
単独判定も逼迫していないドライブを点灯させるため、「使用率が高い」かつ「残り容量が実際に
少ない」の両方が揃って初めて警告する。

### ディスク温度はドライブ自身の申告値を使う

SSD は 70°C 前後、HDD は 55°C 前後と、機種によって「正常」な温度がまったく違う。一律の定数を
置くと誤警告になるため、`IOCTL_STORAGE_QUERY_PROPERTY` のバッファにドライブ自身が申告している
警告温度・臨界温度があればそれを優先して使う。開発機の実機7台のうち温度を返した4台で確認した
ところ、**警告温度は 55 / 60 / 70 / 84°C と機種ごとに大きくばらついた**。臨界温度まで申告して
いたのは NVMe 1台だけだった。

申告が欠けている場合は二段でフォールバックする。

1. 警告温度・臨界温度とも申告されていればそれをそのまま使う。
2. 警告温度だけ申告があれば、臨界温度は「警告温度 + 10°C」で補う。
3. どちらも申告が無ければ、SSD/HDD 別の既定値（SSD: 70°C/80°C、HDD: 55°C/65°C）を使う。

### 冷却異常は温度との AND でしか判定しない

最近のマザーボードはアイドル時にファンを止める（Zero RPM）のが正常な挙動なので、RPM 0 単独
では絶対に警告にしない。CPU またはマザーボードの温度が注意閾値を超えていて、かつ認識している
ファンがすべて 0 RPM のときだけ Caution にする（このカテゴリに Critical は無い）。ファンを
1つも認識できない非管理者起動時は、判定そのものを行わない。「1台も認識していない」場合と
「認識した全台が 0 RPM」の場合は、取得できる情報からは区別が付かないため。

### 表示は高さを1pxも増やさない

サイドバーは全セクションが画面に収まるところまで縦を詰めてあり、行を1つ増やすとそこが崩れる。
そのため専用の警告行は作らず、見出し左にもともとあった3pxのアクセントバーの色を差し替える
方式にした。折りたたんだままでも気づけるという利点もある。加えて、該当する値のテキスト自体も
同じ色に変える（例: ストレージ一覧の使用率や温度の数字）。警告中の物理ディスク行は行全体の周囲に
警告色のグロー効果（`DropShadowEffect`）を適用し、どのディスクが警告対象かを一目で識別できるようにしている。

### 色の衝突を解消した

従来の警告色（`WarningBrush`, `#F05A8C`）は、書き込み量を示す `DiskWriteAccentBrush` と全く
同じ色で、「書き込み量」と「警告」が同じピンクという衝突が起きていた。そのため注意（琥珀
`#F0C23C`）と危険（赤 `#F04A4A`）の2色に分離した。琥珀は `DiskAccentBrush` のオレンジと
色相が近すぎるため、G成分を持ち上げて金色寄りにずらしてある。

## パフォーマンス

常時デスクトップ端に常駐するシステムモニタとして、CPU・メモリ負荷および UI スレッドの停滞を
極限まで抑える設計を採用している。第1弾の基盤設計に加え、監査に基づく最適化を施している。

### 計測・描画の基盤設計

- **レーン分割サンプリング**: 高速（1秒: CPU/GPU/RAM/Disk/Net）、中速（5秒: ボリューム容量）、低速（30秒: 静的構成・温度等）に分離し、重い WMI や IOCTL による不要な高頻度ポーリングを抑止。
- **折りたたみセクションの詳細サンプリング停止**: 非表示のセクションはプロバイダ層での重い取得処理（プロセス一覧やモジュール情報等）そのものをスキップ。
- **スナップショット pull モデル**: プロバイダは不変スナップショットを生成して保持し、UI 側が必要なタイミングで pull する疎結合構造によりスレッド競合と UI 描画ブロックを排除。
- **ディスク温度・ブロッキング処理のキャッシュ**: 応答が遅延・ブロックしうる SMART/IOCTL 取得をバックグラウンドスレッドで実行し、直前値をキャッシュして即時返却。
- **描画リソースのキャッシュ**: `Sparkline` や `CoreGrid` 等のカスタムコントロールにおいて、WPF の `Brush` / `Pen` / `StreamGeometry` の毎フレーム再生成を防止。

### ゼロアロケーションと常時監視の最適化

毎秒サンプリング時の GC 負荷と Win32 API 呼び出しのオーバーヘッドを徹底的に削減している。

- **wildcard PDH のアロケーション除去**: `PdhMultiCounter` が結果を再利用ネイティブバッファとゼロアロケーション列挙（`Enumerate()` / `PdhItemSpan`）で返す。バッファ不足時は PDH の ABI 仕様に準拠して `NULL`/0 で必要サイズを再照会。CPU コア別・GPU Engine・ディスク R/W/Busy の毎秒取得による文字列・オブジェクト生成を排除。
- **折りたたみ中セクションの UI 更新分離**: `SidebarViewModel` の更新を「見出し要約・警告レベル（常時更新）」と「展開時のみ更新する詳細（履歴コピー・詳細文字列・行生成）」に分離。折りたたみ中は詳細更新をスキップし、展開された瞬間に最新スナップショットから即時反映。
- **プロセス上位 K 件のみ生成**: 全プロセスの CPU 時間を走査して順位精度を保ちつつ、`ProcessInfo` の実体化と並べ替えを bounded min-heap により表示件数 K 件（`TopProcessCount`）のみに限定。
- **未参照 GPU メトリクス・重複取得の除去**: App 層で未参照の `Shared Usage`（wildcard PDH）の取得を停止し、PDH と重複していた NVAPI 経由の使用率・メモリの毎秒取得を削減。
- **CPU コア instance 比較のゼロアロケーション化**: instance 名を文字列分割（`Split(',')`）せず `TryParseCoreInstance` で数値キーへ直接パースしてソート。
- **ディスク静的ボリューム情報のキャッシュ**: ボリューム一覧・表示名・容量などの静的構成を 30 秒更新時のみ構築し、毎秒の `Sample` では単一参照の atomic 交換により再利用。
- **GPU ベンダー初期化のバックグラウンド遅延化**: NVAPI/NVML の初期化を遅延ファクトリ化しバックグラウンドで実行。初回ウィンドウ表示のクリティカルパスからネイティブ DLL の cold load を排除。
- **フルスクリーン判定のキャッシュ**: 1 秒ポーリング時に毎回取得していた shell HWND、クラス名、サイドバーモニタ矩形をキャッシュし、ディスプレイ/DPI/AppBar 位置変更イベントでのみ無効化。

## 温度の取得可否

温度センサーは項目ごとに難易度がまったく違う。ここが本プロジェクトで最も注意が要る部分。

| センサー | 取得手段 | 管理者権限 | 状態 |
| --- | --- | --- | --- |
| GPU 温度 / ホットスポット | NVAPI（`nvapi64.dll`） | 不要 | 取得可 |
| GPU ファン % / RPM | NVAPI | 不要 | 取得可 |
| GPU 消費電力 / 電力上限 | NVML（`nvml.dll`） | 不要 | 取得可 |
| ディスク温度 | `IOCTL_STORAGE_QUERY_PROPERTY`（`StorageDeviceTemperatureProperty` = 52） | 不要 | ドライブ依存 |
| **CPU 温度（Tctl/Tdie）** | LibreHardwareMonitorLib + **PawnIO** | **必要** | 管理者起動時のみ |
| **CPU パッケージ電力** | 同上 | **必要** | 管理者起動時のみ |
| **マザーボード / VRM 温度** | 同上（Super I/O） | **必要** | 管理者起動時のみ |
| **ファン RPM（ケース・CPU）** | 同上 | **必要** | 管理者起動時のみ |

IOCTL のバッファには現在温度だけでなく、ドライブ自身が申告する警告温度・臨界温度も入っている。
これは表示には出さず、警告表示（前述）のディスク閾値としてのみ使っている。

管理者で起動した場合、ディスク温度も IOCTL より広く取れる（開発機では 4/7 台 → 6/7 台）。
IOCTL で取れなかったドライブは LibreHardwareMonitor 側の値をフォールバックとして使う。

### なぜ CPU 温度だけ特別扱いなのか

CPU 温度には統一された Windows API が存在しない。

- WMI の `MSAcpi_ThermalZoneTemperature` は多くのデスクトップ機で「サポートされていません」を返す
- WMI の `Win32_TemperatureProbe.CurrentReading` は Microsoft 自身が「現在の実装では設定されない」と
  明記している
- AMD Ryzen の Tctl/Tdie は **SMN レジスタを PCI コンフィグ空間経由で読む**必要があり、
  これはカーネルモードでしか行えない

HWiNFO や LibreHardwareMonitor がカーネルドライバを必要とするのはこのため。
本プロジェクトもこの一点だけ `LibreHardwareMonitorLib` に委ね、**管理者権限で起動されたときだけ**
有効化する。

### PawnIO のインストールが必要

`LibreHardwareMonitorLib` 0.9.6 は**ドライバを同梱していない**。かつて使われていた `WinRing0` は
MSR や PCI コンフィグ空間への生アクセスをそのまま呼び出し側に開放する設計で、Microsoft の
脆弱ドライバブロックリストに掲載された。その後継として、サンドボックス化された Pawn バイトコードの
モジュールだけを実行する **PawnIO** という別配布の署名済みドライバに移行している。
DLL に埋め込まれているのはドライバ本体ではなくモジュール（`PawnIo.AMDFamily17.bin`、
`PawnIo.RyzenSMU.bin`、`PawnIo.LpcIO.bin`、`PawnIo.SmbusNCT6793.bin` など）である。

つまり **PawnIO を入れないと、管理者で起動しても CPU・マザーボードの温度は取れない。**
このとき LibreHardwareMonitor は例外を投げず `0.00` を返すため、気付きにくい
（本プロジェクトはこの値を「取得不能」として弾いている。後述）。

```powershell
winget install namazso.PawnIO
```

インストールすると `PawnIO` という名前のカーネルドライバサービスが登録される。
アンインストールは `winget uninstall namazso.PawnIO`。

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

Get-CimInstance Win32_SystemDriver | Where-Object Name -eq 'PawnIO'   # Running なら準備完了
```

開発機（HVCI 無効・脆弱ドライバブロックリスト有効）では PawnIO は問題なくロードでき、
CPU パッケージ 72.9°C / CPU 電力 72.5W / CCD1 (Tdie) 70.8°C / マザーボード 48.5°C /
ファン 6 基の取得を確認している。

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
| ディスク温度・警告/臨界温度 | `IOCTL_STORAGE_QUERY_PROPERTY`（PropertyId = 52） |
| 論理⇔物理の対応 | `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` |
| ドライブ容量 | `GetDiskFreeSpaceExW` / `GetVolumeInformationW` / `WNetGetConnectionW` |
| ネットワーク | IP Helper `GetIfTable2()` の Octets 差分（LUID で NIC を追跡） |
| GPU 使用率・VRAM 使用量 | PDH `\GPU Engine(*)` / `\GPU Adapter Memory(*)` |
| GPU アダプタ名・VRAM 総量 | DXGI `IDXGIAdapter1::GetDesc1`（vtable 直呼び） |
| GPU 温度・ファン・クロック | NVAPI |
| GPU 電力 | NVML |
| プロセス | `Process` + `GetProcessTimes()` |
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
    "storage": true, "network": true, "network-all": false,
    "thermal": false, "process": true
  },
  "ShowAllDisks": true,
  "TopProcessCount": 8,
  "SensorAliases": {
    "Temperature #3": "VRM",
    "Fan #1": "CPU ファン",
    "Fan #2": "ケース前面"
  }
}
```

`SensorAliases` は Super I/O の総称的なセンサー名を読める名前に差し替える。生の名前は起動時に
`app.log` へ `thermal: others = ...` / `thermal: fans = ...` として記録されるので、そこから拾う。
別名は表示だけでなく分類にも効き、`"VRM"` を含む名前を与えるとその値は「その他温度」ではなく
VRM の欄に入る。

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

### フルスクリーン検出は ABN_FULLSCREENAPP だけに頼れない

サイドバーは常時最前面なので、フルスクリーンのアプリが出たら最背面へ降りる必要がある。
AppBar にはそのための通知 `ABN_FULLSCREENAPP` があるが、Windows 10/11 では取りこぼしがあり、
**ブラウザのフルスクリーン（YouTube など）では飛んでこないことがある**。通知だけを信じると
動画の上にサイドバーが乗ったままになる。

そのため 1 秒間隔で前面ウィンドウを自分で調べ、同じモニタの矩形を丸ごと覆っていれば
最前面を降ろす。フルスクリーンのウィンドウは作業領域ではなくモニタ全体を占めるので、
AppBar で領域を確保していてもモニタ矩形との比較で判定できる。デスクトップ（`Progman` /
`WorkerW`）とタスクバー（`Shell_TrayWnd`）は常に画面を覆っているため除外する。
`ABN_FULLSCREENAPP` を受け取ったときも同じ判定処理を通し、通知とポーリングで判断が
食い違わないようにしている。なお、ポーリング時のオーバーヘッドを抑えるため、シェル HWND や
除外クラス名、サイドバー自身のモニタ矩形などの不変情報はキャッシュし、ディスプレイ構成や
DPI、AppBar 位置の変更イベント時のみ無効化している（判定精度を保つため前面ウィンドウ矩形と
モニタの照会は毎秒行う）。

復帰時は、利用者が右クリックメニューで最前面表示を切っている場合に勝手に戻さないよう
`Window.Topmost` を見てから戻す。

### 管理者への昇格は単一インスタンス判定と競合する

「管理者で再起動」は `Process.Start(Verb = "runas")` で自分を起動し直し、元のインスタンスを
`Shutdown()` する。`Process.Start` は**プロセス生成の時点で戻る**一方、昇格した新プロセスが
.NET/WPF の起動を終えて単一インスタンス用ミューテックスに到達するまでには 1 秒近くかかる。
素直に「取れなければ即終了」と書くと、新旧どちらが先に到達するかがタイミング任せになり、
新プロセスが先だと「既に起動中」と誤判定して即終了 → 続いて旧プロセスも終了する。
症状は「UAC を承認したのにアプリが消える」または「昇格しないまま残る」。

そのため起動時は即諦めず、ミューテックスを最大 5 秒待ってから判断する。真の多重起動では
その待ち時間のあとに終了するだけ。

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

これは飾りではない。LibreHardwareMonitor は必要なカーネルモジュールが使えないとき、
例外を投げずに `0.00` を返す。PawnIO を入れる前の CPU パッケージ温度がまさにそれで、
未接続の Super I/O 入力や一部の古い SATA SSD も同じ値を返す。素通しすると画面に
「0.0°C」と表示され、正常値と区別が付かなくなる。そのためプロバイダ層で
0 以下と 150°C 超を取得不能として捨てている。

### PDH のカウンター名は英語で指定する

日本語 Windows でもカウンターを引けるよう、`PdhAddEnglishCounter` を使っている。
また GPU 使用率は 100 を超えうるので `PDH_FMT_NOCAP100` を指定している。

### プロバイダは例外を外に出さない

`Initialize()` / `Sample()` / `Dispose()` はいずれも例外を漏らさない。センサーが無い環境や
権限が足りない環境でも、その項目だけが `null` になってアプリ全体は動き続ける。

## 既知の制限

- **CPU 温度・マザーボード温度・ファン RPM には PawnIO のインストールと管理者起動の両方が要る。**
  どちらか欠けるとその欄は `—` になる。
- **Super I/O のセンサー名が総称的で、どれが VRM か特定できない。** 開発機の
  ASUS TUF GAMING B550-PLUS では `Temperature #2/#3/#4/#6` としか返ってこず、部位は
  ボードごとに違う。推測でラベルを付けると根拠なく断定することになるため、既定では
  そのまま「その他温度」に並べている。BIOS や他ツールと突き合わせて特定できたら
  `settings.json` の `SensorAliases` で名前を与えられる（"VRM" を含む名前にすれば
  VRM の欄に入る）。ファン名（`Fan #1`〜）も同様。
- ディスク温度は非管理者だと `IOCTL_STORAGE_QUERY_PROPERTY` に対応するドライブでしか取れない。
  古い SATA ドライブは `ERROR_INVALID_FUNCTION` を返す（開発機では 7 台中 4 台）。
  管理者起動なら LibreHardwareMonitor 側の SMART 経由でフォールバックし 6 台まで増えるが、
  それでも応答しない個体はある（開発機の INTEL SSDSC2CT120A3 は `0.0` を返すため取得不能扱い）。
- GPU の詳細（温度・ファン・電力）は NVIDIA のみ。AMD / Intel は未対応（`IGpuVendorSensors`
  を実装すれば差し込める設計にはなっている）。
- ネットワークインターフェースの列挙数が `Get-NetAdapter` より多い。IP Helper は Hyper-V の
  vSwitch 拡張アダプタや WAN Miniport の疑似インターフェースまで返すため。
- プロセス別のネットワーク使用量は未対応（ETW が必要）。
- マルチモニタ環境は未検証。実装上は対応しているが、開発機が 1 画面のため確認が取れていない。
- 警告の閾値（`AlertThresholds.cs`）は固定値で、`settings.json` から変更する機能は無い。

## ライセンス

未定。`LibreHardwareMonitorLib` は MPL-2.0。
