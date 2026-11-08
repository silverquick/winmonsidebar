# バックログ

作成日: 2026-11-08（`92f2d27` 時点のコード調査に基づく）

- 優先度: **P1** = 早めに対処（不具合や停止につながる）、**P2** = 次に着手、**P3** = 余力があれば・要判断
- 規模: S（〜半日）/ M（1〜2日）/ L（それ以上）
- 行番号は調査時点の値。着手前に再確認すること。
- 制約: サイドバーの縦 1392px はすべて使い切っている。**行を追加する案はレイアウトの取捨選択が前提**。

## P1

### ~~B-01 サンプリングレーンが例外で黙って停止する~~ ✅ 対応済み（2026-11-08）
- 種別: 信頼性 / 規模: S
- 根拠: `src/Monitor.Core/MetricsHub.cs:142` の `_diskAlertTracker.Update(...)` が `SampleSafe` の外にある。`RunLoopAsync`（:115-126）は `OperationCanceledException` しか捕捉しない。
- 影響: tracker が想定外の例外を投げると fast lane（CPU/メモリ/ディスク/ネットワーク/GPU）が止まる。アプリは生きて見え、表示だけが固まる。例外は Dispose まで表に出ない。
- 対応案: 各レーンのループ本体を try/catch で囲んで継続させ、ログに残す（B-02 と合わせて進める）。

### B-02 プロバイダの失敗が記録されず、「取得不能」と「0」の区別がつかない（ログは対応済み 2026-11-08 / UI の「—」表示は未対応）
- 種別: 信頼性 / 規模: M
- 根拠: `MetricsHub.cs:249-264` `SampleSafe` の `catch { return fallback; }`。`TryInitialize` と `Publish` も例外を握りつぶす。Core から AppLog を参照できない。
- 影響: センサーやプロバイダが連続して失敗しても app.log に何も残らない。UI は Empty 値（0 など）を表示し続ける。
- 対応案: ログ用 callback を `MetricsHubOptions` に注入し、初回の失敗と連続失敗回数を記録する。取得不能時は UI に「—」を表示する。

### ~~B-03 展開/折りたたみのたびに UI スレッドで設定ファイルを同期書き込みする~~ ✅ 対応済み（2026-11-08）
- 種別: 信頼性・性能 / 規模: S
- 根拠: `src/Monitor.App/ViewModels/SidebarViewModel.cs:1549-1559` `SetExpanded` → `SettingsStore.Save`（`Settings/SettingsStore.cs:56-72`、WriteAllText + Move）。
- 影響: AV スキャン中などで書き込みが遅いと UI が引っかかる。トグルを連打すると何度も書き込む。
- 対応案: debounce したうえでバックグラウンド保存にする。

## P2

### B-04 Core の基盤ロジックにテストがない
- 種別: テスト / 規模: M
- 根拠: `Monitor.Core.Tests` プロジェクトが存在しない。テストから参照されていないもの: MetricsHistory / RingBuffer / ByteFormatter / SettingsStore / Network・Memory・VolumeProvider / LhmThermalProvider / NvidiaGpuSensors。AlertEvaluator を参照するテストは 1 ファイル、MetricsHub は 2 ファイルのみ。
- 対応案: Core.Tests を新設し、閾値の境界、リングバッファのラップアラウンド、ByteFormatter の桁境界から書く。B-01 / B-02 の回帰テスト（プロバイダが例外を投げたときのフォールバック、キャンセル）もここに置く。

### B-05 設定ファイルの入力を検証しない・破損時に全設定が消える
- 種別: 信頼性 / 規模: S
- 根拠: `Settings/AppSettings.cs:19` `TopProcessCount` に上限がない（下限 1 のみ）。不正な `Edge` は黙って Right 扱い。JSON が壊れていると既定値に戻り、次の保存で上書きされる（`SettingsStore.cs:38`）。
- 影響: TopProcessCount を大きくすると 1392px を溢れる。ユーザー設定が気づかないうちに消える。
- 対応案: 値をクランプする。破損したファイルは `.bak` に退避する。

### B-06 警告閾値を設定から変えられない
- 種別: 機能 / 規模: M
- 根拠: `src/Monitor.Core/Alerts/AlertThresholds.cs:3-9`（コード自身が「未実装」と明記）、README:121-122・既知の制限、`docs/storage-sustained-busy-alert-design.md:101,275`。マザーボードの注意閾値は CPU と同じ値を暫定採用（`AlertThresholds.cs:52`）。
- 対応案: `AppSettings` に閾値の上書き項目を追加する（未指定なら現行の定数を使う）。ディスクは媒体種別ごとの既定値も検討する。

### B-07 ホットパスに残っている毎秒/2 秒ごとのアロケーション
- 種別: 性能 / 規模: S〜M
- 根拠:
  - `Monitor.Windows/Providers/ProcessProvider.cs:108,112,175`: `Process.GetProcesses()` に加え、Dictionary と List を毎回生成（2 秒ごと）
  - `NetworkProvider.cs:57`: `new List<NetworkInterfaceSnapshot>()`
  - `MemoryProvider.cs:415,484,493`: List と Dictionary を生成。:316 の `GetProcessesByName("Memory Compression")` は呼び出し頻度を要確認
  - `SidebarViewModel.cs:625,671,1152,1186,1341`: `new List<string>` + `string.Join` によるサマリ生成。前回値と比べて再生成を省く処理がない
- 備考: 直近コミット（`8805b50` / `ea51757`）の続き。Process / Memory と ViewModel の文字列生成がまだ手付かず。

### B-08 警告パルスがストレージだけにある
- 種別: 機能・保守性 / 規模: S〜M
- 根拠: `Themes/Dark.xaml:249-310` の `StorageDiskHeaderStyle` だけに Caution/Critical のストーリーボードがあり、2 本が複製されている。`Controls/SectionExpander.cs:354-355` のアクセントバーは色が変わるだけ。
- 影響: CPU/GPU/メモリ/温度の警告は動きがなく、見落としやすい。
- 対応案: パルスを共通スタイルに切り出して他のセクションにも適用する。行は増えない。実機での見え方はユーザーに確認する（WPF の描画はスクリーンショットに写らないため）。

## P3

### B-09 SidebarViewModel が 1586 行の巨大クラス
- 種別: 保守性 / 規模: L
- 根拠: `SidebarViewModel.cs` が全セクションと展開状態を持っており、展開プロパティはセクションごとにほぼ同じコード。テストの `SidebarViewModelDetailGatingTests.cs` も 910 行ある。
- 対応案: セクションごとの VM に分割し、展開状態の扱いを共通化する。B-04 でテストを固めてから着手する。

### B-10 巨大な Provider とマジックナンバー
- 種別: 保守性 / 規模: M
- 根拠: MemoryProvider 704 行（SMBIOS・ページファイル・圧縮メモリが同居）、GpuProvider 641 行、NvidiaGpuSensors 600 行（catch 15 箇所）。サンプリング間隔（`MetricsHub.cs:11-22`）、`AppBarHost.cs:29` の `FullscreenPollInterval` などがコードに直書き。

### B-11 使われていない設定項目（ShowAllDisks / 幅 / Edge）
- 種別: 保守性 / 規模: S
- 根拠: `AppSettings.cs:17` の `ShowAllDisks` は定義だけで、どこからも参照されていない。幅は `SidebarWindow.xaml.cs:27-31` で 340 に固定され、`ThicknessDip` は毎回上書きされる。Edge は読み込まれるが UI から変更できない。
- 対応案: 使うのか削除するのかを決める（要判断）。

### B-12 Network / Process・使用率に警告がない
- 種別: 機能 / 規模: M
- 根拠: AlertLevel を持つのは CPU/GPU/Memory/Storage/Thermal のみ（`SidebarWindow.xaml:42,70,145,257,473`）。CPU/GPU の警告は温度だけで、使用率に張り付いても警告しない。
- 対応案: 既存のアクセントバー/サマリ色を流用すれば行は増えない。何を異常とみなすかの定義が要る（要判断）。

### B-13 警告の履歴・通知がない
- 種別: 機能 / 規模: M
- 根拠: 警告は見出しの色変化だけで、トレイ・トースト・ログ・履歴のいずれもない。設計書 `storage-sustained-busy-alert-design.md:218` は「通知を加えるならラッチと無効化設定が必要」としている。
- 対応案: まず警告の遷移を app.log に記録するところから始める（B-02 のログ基盤を使う）。

### B-14 UI 更新が DispatcherTimer によるポーリング
- 種別: 性能・保守性 / 規模: M / 確信度: 低
- 根拠: `SidebarViewModel.cs:69-78` は 1 秒間隔のタイマーで `_hub.Latest` を取りに行く。Hub の `SnapshotAvailable` はログ用途にしか使われていない（`App.xaml.cs:98`）。
- 影響: Hub のサンプル周期と位相がずれ、表示が最大 1 秒遅れる。体感で問題がなければ現状維持でよい。

### B-15 終了時に UI スレッドを同期ブロックする
- 種別: 信頼性 / 規模: S / 確信度: 低
- 根拠: `App.xaml.cs:164` `DisposeAsync().GetAwaiter().GetResult()`。`VolumeProvider.cs:65` `_refreshTask?.Wait(2s)`。ネットワークドライブが応答しないと終了が遅れる。

## README「既知の制限」に載っているもの（参考・優先度は付けない）
- GPU の詳細（温度・ファン・電力）は NVIDIA のみ対応。`IGpuVendorSensors` を実装すれば AMD/Intel も差し込める
- プロセス別のネットワーク使用量は未対応（ETW が必要）
- マルチモニタは未検証
- ライセンスが未定（LibreHardwareMonitorLib は MPL-2.0）
