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

### ~~B-02 プロバイダの失敗が記録されず、「取得不能」と「0」の区別がつかない~~ ✅ 対応済み（2026-11-14）
- 対応: 全プロバイダで 2 回までは前回値、3 回連続失敗で「—」を表示し、成功時に復帰する。取得不能中は履歴を追加せず、警告も抑止する。
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

### ~~B-05 設定ファイルの入力を検証しない・破損時に全設定が消える~~ ✅ 対応済み（2026-11-15）
- 対応: `AppSettings.Normalize()` で `TopProcessCount`（`1`〜`16`）・`Edge`・`ThicknessDip`・各種コレクション（非 `null` 化、`HiddenSensors` の空欄除去と上限 128 件）を正規化。破損 JSON は古い `.bak` を上書きして移動する。設定・ログの親からユーザープロファイル直下までの全パス成分（`WinMonSidebar`・`%LOCALAPPDATA%`・その祖先）と最終ファイル・`.tmp`・`.bak` のリパースポイントを拒否する（A2）。検証から使用までの差し替え競合は未対応（B-19）。
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

### ~~B-16 未接続と思われるマザーボード温度センサーが表示される~~ ✅ 対応済み（2026-11-14）
- 種別: 機能 / 規模: S〜M
- 根拠: 2026-11-10 に実機（ASUS TUF GAMING B550-PLUS / Nuvoton NCT6798D、LHM 0.9.6）の全センサーを書き出したところ、`/lpc/nct6798d/0/temperature/3`（`Temperature #3`、約 93°C）と `/temperature/6`（`Temperature #6`、82.0°C）は数秒間ほとんど値が動かなかった。未接続の入力が出す値の可能性が高い（未確定。BIOS の温度表示と見比べれば確定できる）。これらは `OtherTemperatures` として温度セクションの「その他」に表示される（`SidebarViewModel.cs` の `ThermalOtherTemperatures`）。
- 影響: 実在しない高温が画面に出て、異常と誤解しやすい。`SensorAliases` で名前は変えられるが、行を隠す方法がない。
- 対応案: `AppSettings` に非表示にするセンサー名の一覧（例: `HiddenSensors`）を追加し、`LhmThermalProvider` で別名を当てる前の名前で除外する。行が減る方向の変更なので 1392px の高さには影響しない。
- 使用例: `"HiddenSensors": ["Temperature #3", "Temperature #6"]`

### B-17 一時的な初期化失敗からプロバイダが自動復旧しない
- 種別: 信頼性 / 規模: M
- 根拠: `LhmThermalProvider.cs:84-88` の初期化失敗経路、`GpuProvider.cs:84-88 / 106-108` は初期化に失敗すると IsAvailable=false のままになる（行番号は B-02 再レビュー時点）。
- 影響: LHM のオープンや GPU の PDH 初期化が一時的に失敗した場合も、アプリを再起動するまで取得を再試行できない。
- 対応案: 未対応・非搭載と一時的な初期化失敗を区別し、後者だけを低頻度で再初期化する。B-02 のサンプル失敗判定とは別に、キャンセル・破棄・再登録の競合もテストする。

### B-18 メモリの PDH 収集失敗時に古いコミット/プール値を使い続けることがある
- 種別: 信頼性 / 規模: S
- 根拠: `MemoryProvider.cs:170-179 / 183-184 / 209-210`（行番号は B-02 最終レビュー時点）。`Collect` が失敗しても scalar の `GetDouble` が前回の有効値を返すと、committedOk / limitOk / poolPagedOk が true のままになり、Win32 の代替値（CommitFallback / GetPerformanceInfo）が選ばれない。内訳カウンタ 6 本がすべて欠けると LastSampleFailed も false のまま。
- 影響: 稀な条件で、メモリのコミット量・プール量が古い値のまま「取れている」扱いで表示される。B-02 の 3 回連続失敗で「—」にする仕組み自体は正しく動く。
- 対応案: `!collected` のときは scalar 読み取りを無効扱いにして代替経路を選ぶ（代替もなければ失敗サンプルにする）。Collect 失敗＋古い値成功を注入する回帰テストを追加する。

### B-19 設定・ログのパス検証後の差し替え競合を防げない
- 種別: セキュリティ / 規模: M
- 根拠: 2026-11-15 の独立レビューで指摘された `src/Monitor.App/Settings/SettingsStore.cs:137,149,154,180`、`src/Monitor.App/Diagnostics/AppLog.cs:31,43,50`（レビュー時点。着手前に再確認）。属性検証の後でパス名による書き込み・移動・ログ追記を行う。
- 影響: 祖先・最終ファイル・一時・バックアップの静的なリンクを拒否しても、検証と使用の間に同一ユーザーの別プロセスがリンクへ差し替えると、管理者 UI が意図しない場所を書き換える可能性が残る。
- 対応案: 根本対応は A1（UI を管理者権限で常駐させず、必要な計測を権限分離する）。併せてディレクトリ・ファイルハンドルを起点とする再解析拒否・同一性検証・排他的な一時ファイル作成を設計し、設定・退避・ログへ一貫して適用する。

### B-20 AppBar の登録解除を UI スレッド外から重ねて呼ぶと、再登録後のメッセージフックまで外れる
- 種別: 信頼性 / 規模: S
- 根拠: `src/Monitor.App/Shell/AppBarHost.cs:381,408`（2026-11-16 再レビュー時点。着手前に再確認）。UI スレッド外からの `Unregister` はフック解除を UI スレッドへ予約する。予約が実行される前に同じウィンドウを再登録すると、等価な `WndProc` デリゲートが 2 つになり、予約済みの 2 回の解除で新しい登録のフックも外れる。
- 影響: 現在のアプリは `AppBarWindow.OnSourceInitialized` で 1 回だけ登録し、再登録しないので発生しない。将来、再登録の経路を足すと、登録済みなのに位置・全画面・破棄の通知を受け取れなくなる。
- 対応案: 登録ごとに後始末の状態を持ち、UI スレッドで 1 回だけ完了させる。再登録の前に前回の後始末を済ませ、予約済みの処理は完了後なら何もしない。重複した解除の予約→再登録→実行の順のテストを足す。あわせて MetricsHub の終了処理のテスト（同時に呼ぶ DisposeAsync、Dispose 後の Start、例外を投げる Dispose）も補う。

### ~~B-21 CPU/GPU の一過性サンプル失敗とボリューム失敗の診断不足~~ ✅ 対応済み（2026-11-19）（CPU/GPU 誤判定と診断は対応済み、過去のボリューム長時間失敗の原因は新ログ待ち）
- 種別: 信頼性・診断 / 規模: M
- 根拠: B-02（1573337）以降の実ログで GPU 487 streak（1 回 452 / 2 回 33 / 3 回 1）、CPU 312 streak（1 回 311 / 2 回 1）、Volume 2 streak（3 回 / 706 回）を依頼時点で観測。GPU の提示総数は内訳合計 486 と 1 件異なり、作業時の読み取りでは GPU 開始 488 / 回復 487、CPU 開始・回復 313。着手前の `Native/PdhQuery.cs:178-183` は 1 項目のエラーを配列全体へ伝播し、`Providers/CpuProvider.cs:256` / `GpuProvider.cs:150,164` は全体失敗へ反映。Volume の `VolumeProvider.cs:248` はドライブ・コードを残さない。
- 影響: 通常動作中の GPU インスタンス変化や CPU クロック計算不能でも全セクションが前回値となり、3 回で「—」。旧版の Volume は約 1 時間「—」だが原因を追跡できない。
- 対応案: CPU/GPU だけ、正常項目が残る配列の一時項目エラーを許容し、全無効・恒久エラーは失敗のままとする。CPU コア別クロック API の負の計算は既知ベースクロックへ退避可能とする。失敗開始時に遅延評価の理由を記録する。
- 対応: 180 秒の read-only PDH 採取で GPU `CSTATUS_INVALID_DATA` 14 項目（1 サンプル）、CPU コア別クロック API `CALC_NEGATIVE_DENOMINATOR` 1 回を再現。Volume のドライブ・種別・Win32/例外型と CPU/GPU のカウンター・コードを失敗開始時だけ記録し、注入テストを追加。
- 残り: 88f6145 より前の Volume 3 回 / 706 回の原因は旧ログから確定できない。今後の再発時に新しい診断ログで確認する（本項目の修正・診断追加は完了）。

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

### B-15 終了時に UI スレッドを同期ブロックする（Hub の終了待機期限・安全な遅延破棄は対応済み、UI の同期待機は残る）
- 対応: `MetricsHubOptions.DisposeTimeout`（既定 2 秒）をループ停止と全プロバイダ破棄を待つ合計期限とする。期限内に完了しない初期化・Sample があれば、完了後に一度だけ全プロバイダと CTS を破棄する。破棄自体が停止しても呼び出し元は期限で戻る。実行中のプロバイダとの並行破棄は行わず、初期化の間にもキャンセルを確認する（E4）。OS のスケジューリング遅延を除き Hub の待機は期限内だが、UI はこの期限まで同期的に待ち、永久停止した処理のリソース回収やプロセス終了後の後片付けは保証しない。
- 種別: 信頼性 / 規模: S / 確信度: 低
- 根拠: `App.xaml.cs:164` `DisposeAsync().GetAwaiter().GetResult()`。`VolumeProvider.cs:65` `_refreshTask?.Wait(2s)`。ネットワークドライブが応答しないと終了が遅れる。

## README「既知の制限」に載っているもの（参考・優先度は付けない）
- GPU の詳細（温度・ファン・電力）は NVIDIA のみ対応。`IGpuVendorSensors` を実装すれば AMD/Intel も差し込める
- プロセス別のネットワーク使用量は未対応（ETW が必要）
- マルチモニタは未検証
- ~~ライセンスが未定（LibreHardwareMonitorLib は MPL-2.0）~~ ✅ MIT License（サードパーティ表記は THIRD-PARTY-NOTICES.md）に決定（2026-11-15）
