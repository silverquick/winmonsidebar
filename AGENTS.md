# AGENTS.md

## 概要

Windows 11 向けの、デスクトップ右端に領域を確保して常駐する .NET 10 の WPF システムモニタ サイドバー。対象フレームワークは `net10.0-windows`（Core は `net10.0`）。
`WindowsMonitor.sln` は `src/` のアプリ・計測基盤・Windows/NVIDIA/LHM連携と `tests/` のテストプロジェクトを含む。
- `src/Monitor.App`: WPF UI、設定、アプリ起動を担当する。
- `src/Monitor.Core`: メトリクス、モデル、警告など共通基盤を担当する。
- `src/Monitor.Windows`: Windows API による計測を担当する。
- `src/Monitor.Vendors.Nvidia`: NVIDIA 固有の計測連携を担当する。
- `src/Monitor.Optional.Lhm`: 任意の LibreHardwareMonitor によるセンサー計測を担当する。
- `tests/Monitor.App.Tests` と `tests/Monitor.Windows.Tests`: App/UI と Windows 連携のテストを担当する。

詳細は [README.md](README.md) と `docs/` を読む。

## コマンド

- ビルド: `dotnet build WindowsMonitor.sln --nologo`
- テスト: `dotnet test WindowsMonitor.sln --nologo`
- 発行: `dotnet publish src\Monitor.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out`
- オフラインまたはサンドボックス環境では NuGet audit の NU1900 警告を避けるため `-p:NuGetAudit=false` を追加する。それ以外の警告は解消する（基準は警告 0 件、全テスト成功）。

## 守る制約

- サイドバーの高さ 1392px は使い切っている。行の追加や高さ変更は、ほかの行を削る判断とセットでユーザーに確認する。
- この PC では WPF 描画がスクリーンショットに写らない。見た目は自動確認できないため、確認してほしい箇所を具体的に挙げてユーザーに頼む。
- 毎秒・2 秒ごとのサンプリング経路と ViewModel 更新で、サンプルごとのヒープ確保を増やさない。LINQ、クロージャ、`foreach` によるインターフェース列挙、毎回の文字列生成を避ける。
- プロバイダは例外を外に出さず既定値を返す。取得失敗は `IMetricProvider.LastSampleFailed` で通知する。3 回連続失敗で UI は `—` を表示する。未対応・非搭載を示す `IsAvailable=false` とは区別する。
- 警告は不透明な枠線・文字色・状態変化時のパルスで示す。グローや半透明背景は使わない（実機で見えにくかった）。
- テストは実際の `%LOCALAPPDATA%\WinMonSidebar\settings.json` に書かず、一時フォルダを使う。

## 実機のアプリ

- ユーザーが常用するアプリは `out\Monitor.App.exe` で、管理者権限で動作している。
- 指示なしに停止・起動・`out\` の差し替えをしない。
- 差し替え時はユーザーに右クリック→「終了」でアプリを閉じてもらい、非昇格で `out\` を差し替えて通常起動する。UAC を出す昇格起動は使わない。管理者への切替はユーザーがアプリ内の「管理者で再起動」で行う。
- 起動確認には `%LOCALAPPDATA%\WinMonSidebar\app.log` を使う。

## ドキュメント

- `README.md`: 仕様・アーキテクチャ・設定とログ・既知の制限を記載する。設定キー追加時は「設定とログ」を、挙動や制限の変更時は該当節を更新する。
- `docs/backlog.md`: 課題一覧。ID は `B-NN` の連番で欠番を再利用せず、新しい項目には、ファイル内で最大の番号の次を使う。優先度 `P1` / `P2` / `P3` の節に置く。項目には「種別 / 規模 / 根拠 / 影響 / 対応案」を記す。完了項目は消さず、見出しに取り消し線と `✅ 対応済み（YYYY-MM-DD）` を付ける。部分対応は見出しの括弧内に完了分と残りを記す。根拠の行番号は着手前に確認する。作業中に見つけ、今回は直さない問題は新規項目にする。
- `docs/*-design.md`: 設計判断が要る機能の設計書。例は `docs/storage-sustained-busy-alert-design.md`。
- `docs/agy-implementation-brief.md`: ストレージ高 Busy 継続警告の設計を実装するための過去の一回限りの依頼書。

## コミット

- `git log --oneline -20` の形式に合わせ、日本語の一文で「〜する」と終える。関連するバックログ ID があれば末尾に全角括弧の `（B-NN）` を付ける。
- コード・テスト・README/backlog の更新は同じコミットに含める。
- push はユーザーの指示があるときだけ行う。

