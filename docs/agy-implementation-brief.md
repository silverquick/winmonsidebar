# 実装依頼: ストレージ 高Busy継続警告

`docs/storage-sustained-busy-alert-design.md` の設計を、そのまま実装してください。設計自体の変更・再検討は不要です（すでにユーザー承認済み）。

## やること

1. `docs/storage-sustained-busy-alert-design.md` の「6. ファイル単位の変更方針」に列挙されている各ファイルを、記載どおりに変更する。
   - `src/Monitor.Core/Alerts/AlertThresholds.cs`: 閾値定数を追加
   - `src/Monitor.Core/Alerts/AlertEvaluator.cs`: `DiskSustainedBusy` 純粋関数を追加
   - `src/Monitor.Core/Alerts/DiskBusyAlertTracker.cs`: 新規、継続時間トラッカー
   - `src/Monitor.Core/MetricsHub.cs`: トラッカーをインスタンス状態として保持、高速レーンで更新
   - `src/Monitor.Core/Models/DiskSnapshot.cs`: `DiskDeviceSnapshot` に `BusyAlertLevel` 追加
   - `src/Monitor.App/ViewModels/SidebarViewModel.cs`: `BusyAlertLevel`/`TemperatureAlertLevel` の取り回し、`MaxLevel` 集約
   - `src/Monitor.App/ViewModels/StorageRowViewModel.cs`: `BusyAlertLevel`/`TemperatureAlertLevel` 追加
   - `src/Monitor.App/Views/SidebarWindow.xaml`: 温度文字は `TemperatureAlertLevel` にバインドし直す（Busy警告で温度文字まで色が変わらないように）
   - `src/Monitor.App/Themes/Dark.xaml`: `StorageDiskHeaderStyle` に `AlertLevel` の `DataTrigger` を追加し、`Border.Effect` に `DropShadowEffect`（設計書7節の数値: Caution BlurRadius=4/Opacity=0.60/#F0C23C, Critical BlurRadius=5/Opacity=0.80/#F04A4A, ShadowDepth=0固定）を設定。通常時（None）は `Effect` を外す。
   - `README.md` と `src/Monitor.Core/Alerts/AlertLevel.cs`: 「Busy100%は警告しない」という断定的な記述を、設計書9行目・182行目相当の表現に更新

2. 設計書「6. ファイル単位の変更方針」テスト節に列挙されているテストケースを追加する。
   - `tests/Monitor.Core.Tests` 相当: 閾値直前/一致、Caution→Critical遷移、短い低下での非解除、15秒解除、ディスク消失、欠測、長時間停止（サンプル間隔超過でのリセット）、複数ディスク独立性
   - `tests/Monitor.App.Tests/SidebarViewModelDetailGatingTests.cs`: 折りたたみ中もBusy警告が見出しへ反映・解除されるケース
   - `MetricsHub` テスト: 他レーンの再公開(2秒/5秒レーン)では継続時間が増えないこと

3. 実装が終わったら:
   - `dotnet build` と既存テストスイートを実行し、全て green であることを確認する（ビルド・テストログを保存すること）。
   - 変更内容を意味のある単位でコミットする（コミットメッセージは日本語で可）。最後に必ず1回はコミットしてください。

## 制約・注意点

- サイドバーの表示は行を1つも増やさない。新規ゲージ・新規行は禁止。
- `AlertEvaluator` はステートレスな純粋関数のまま維持する。状態は `DiskBusyAlertTracker` に閉じ込める。
- アニメーション・点滅は使わない。
- 既存の警告色 (`AlertCautionBrush #F0C23C` / `AlertCriticalBrush #F04A4A`) を流用し、二重定義しない。
- わからない設計判断が出てきたら、設計書の記述を優先し、それでも決められない場合は保守的な（既存動作を壊さない）方を選ぶこと。ユーザーへの質問はしなくてよい。
