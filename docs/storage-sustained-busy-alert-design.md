# ストレージ高 Busy 継続警告 設計提案

## 1. 結論

推奨案は、物理ディスクごとの高 Busy 継続状態を追跡する小さなステートフル部品を `Monitor.Core.Alerts` に追加し、`MetricsHub` の高速レーンで新しいディスクサンプルを得た時だけ更新する方式である。警告レベルの決定自体は `AlertEvaluator` の純粋関数に残す。初期値は **Caution: Busy 95%以上が2分継続、Critical: Busy 99%以上が10分継続**、解除は **85%未満が15秒継続**を提案する。

UI は行を増やさず、警告中の物理ディスク見出し行全体（`StorageDiskHeaderStyle` が適用される `Border`）へ警告色のグローを付ける方式を正式な推奨案とする。グローは `DropShadowEffect` を `ShadowDepth=0` で使い、どのディスクが警告対象かを一目で識別できるようにする。セクション見出しは既存どおり全ストレージ警告の最大値を `SectionExpander.AlertLevel` に渡し、点滅は採用しない。

この警告は「長時間高 Busy」という観測事実を知らせるものであり、コピーが正常か、I/O 詰まりが異常かを Busy% だけで断定するものではない。したがって文言・ツールチップ上も「障害」ではなく「高負荷が継続」と表現するべきである。

## 2. 現状確認

依頼時の前提は、次の補足を除いて正しい。

| 前提 | 確認結果 |
|---|---|
| Busy 値の取得 | `DiskProvider` が PDH `\\PhysicalDisk(*)\\% Disk Time` を読み、物理ディスクごとの `DiskDeviceSnapshot.BusyPercent` と全体の `DiskSnapshot.BusyPercent` に格納している。0～100%へクランプ済み。 |
| 現在のストレージ警告 | 物理ディスク行は温度、ローカルボリューム行は容量枯渇、ネットワーク行は切断を評価する。Busy は未評価。 |
| 警告方針 | `README.md` と `AlertLevel.cs` に「ディスク Busy 100% は大きなコピー中なら正常なので、使用率そのものは警告にしない」と明記されている。新要望とは方針変更が必要。 |
| 判定の集約 | 閾値は `AlertThresholds.cs`、瞬時値からの判定は `AlertEvaluator.cs` に集約され、現状の `AlertEvaluator` は static・ステートレスである。 |
| UI の既存パターン | `SectionExpander` は `AlertLevel` により3pxアクセントバーと要約文字を Caution/Critical 色へ差し替える。`AlertLevelToBrushConverter` と `AlertCautionBrush` (`#F0C23C`) / `AlertCriticalBrush` (`#F04A4A`) も存在する。ディスク行では温度文字が行の `AlertLevel` にバインドされているが、Busy文字は通常色のまま。 |
| 更新周期 | `MetricsHubOptions.FastInterval` の既定値は1秒で、ディスクは高速レーンで毎秒サンプルされる。UI 側の適用タイマーも1秒。 |

注意点として、`MetricsHub` は高速レーン以外（プロセス2秒、温度5秒、ボリューム5秒）からも合成スナップショットを公開し、その際は直近の同じ `DiskSnapshot` を再利用する。受信回数を秒数として数えると誤計上するため、状態更新は高速レーンの新規ディスクサンプルに限定するか、ディスクサンプル固有時刻を明示的に持たせる必要がある。

また、折りたたみ中も `ApplyStorage` は `EvaluateStorageAlertLevel` を実行して見出し警告を更新するため、行を生成しなくてもセクション警告を保てる既存構造になっている。

## 3. 検知方式の比較

### 案A: 連続経過時間 + 解除ヒステリシス（推奨）

物理ディスクごとに「高 Busy 域に入ってからの実経過時間」を保持する。Busy が閾値を下回れば継続時間をリセットする。ただし一瞬の揺れで表示が点滅しないよう、発報後は解除閾値未満が一定時間続くまで維持する。

- Caution: `BusyPercent >= 95%` が120秒連続
- Critical: `BusyPercent >= 99%` が600秒連続
- 解除: `BusyPercent < 85%` が15秒連続
- 85～95%は発報前なら継続を中断し、発報後ならレベルを維持するヒステリシス帯とする
- サンプル欠落・ディスク消失・不正値では連続性を断ち、状態をリセットする

メリット:

- 挙動を説明しやすく、ユーザーが「何分張り付いたか」を直感的に理解できる。
- 1秒スパイクや短いコピーを確実に除外できる。
- 状態が小さく、物理ディスク数に比例する辞書だけで済む。
- 仮想時刻を使った決定的な単体テストが容易。

デメリット:

- 10分以上続く正当な大容量コピーは Critical になり得る。Busy%単独では用途を識別できない。
- 94%が長時間続く場合は検知しないなど、閾値境界に敏感。
- 短い測定欠落を即リセットすると検知が遅れる。初版では安全側にリセットし、必要なら後から1～2サンプルの欠落猶予を追加する。

実装コスト: **低～中**。トラッカー1クラス、モデルへの評価結果追加、既存集約への合流、XAMLバインド、単体テストが中心。

### 案B: 固定時間窓の占有率（sliding window）

直近の一定窓について、Busy が閾値以上だったサンプル割合を評価する。例として Caution は「直近3分のうち95%以上が80%以上」、Critical は「直近10分のうち99%以上が90%以上」とする。

メリット:

- 1～数秒だけ落ちる鋸歯状の高負荷でも「実質張り付き」として検知できる。
- 一時的なサンプル欠落やノイズへの耐性が案Aより高い。
- 占有率と窓を調整すれば、実機データに合わせやすい。

デメリット:

- 「連続」の意味が利用者に分かりにくく、パラメータが増える。
- ディスクごとに時刻付きサンプル列またはリングバッファが必要。
- 発報・解除の境界挙動と、間隔変更・欠測時の扱いが複雑になる。
- 正当な長時間コピーとの区別は、案A同様にできない。

実装コスト: **中**。リングバッファ、時間窓の刈り込み、時間加重または不規則サンプル処理のテストが必要。

### 案C: EWMA（指数移動平均）+ 継続時間

Busy%を指数移動平均で平滑化し、平滑値が閾値以上の状態が続いたら発報する。例として時定数30秒、Caution は EWMA 90%以上を2分、Critical は EWMA 97%以上を10分とする。

メリット:

- 瞬間的な落ち込み・ノイズに強く、メモリ使用量はディスクごとに定数。
- サンプル間隔を実経過時間から係数へ反映すれば、周期の揺れにも対応できる。

デメリット:

- 現在値と警告の関係が直感的でなく、解除が遅れて見える。
- 時定数を含めた調整が必要で、初期値の根拠を説明しにくい。
- Busy 100%への気づきという単純な要望に対して過剰設計になりやすい。

実装コスト: **中**。保存状態は小さいが、数式・時刻処理・テストケースが案Aより増える。

## 4. 閾値の提案と根拠

初版は案Aの次の値を採用する。

| レベル | 発報条件 | 意図 |
|---|---|---|
| Caution | 95%以上が2分連続 | OSの短いバックグラウンドI/Oや瞬間的なバーストを除外しつつ、「張り付き」に比較的早く気づける。100%ぴったりだけにするとカウンターの揺れで連続判定が切れるため95%とする。 |
| Critical | 99%以上が10分連続 | ほぼ完全飽和が長時間続く状態だけを赤にする。コピーでも起こり得るため、即時の赤表示は避ける。 |
| 解除 | 85%未満が15秒連続 | 95%近辺の揺れによる色の頻繁な切替を抑える。 |

この初期値は医学的・ベンダー規格のような絶対基準ではなく、誤検知抑制を優先したプロダクト初期値である。実機ログを収集できるなら、「発報したが正常だった割合」「発報まで遅すぎた事例」を見て調整する。特に HDD と SSD/NVMe では Busy の意味と体感が異なるため、将来は媒体種別別の既定値や設定可能化を検討できるが、初版から分岐を増やさない。

## 5. 推奨アーキテクチャと状態保持

`AlertEvaluator` 自体はステートレスな純粋関数のまま維持する。継続時間は入力履歴なしには求められないため、別クラス `DiskBusyAlertTracker`（仮称）に明示的に隔離する。

処理の責務は次のように分ける。

1. `DiskProvider` は今までどおり生の Busy% を採取する。警告知識を持たせない。
2. `MetricsHub` の高速レーンが、新しいディスクサンプルと実際の `elapsed` をトラッカーへ1回だけ渡す。
3. トラッカーは `PhysicalDriveNumber` をキーに開始時刻・現在レベル・解除候補時間を保持する。
4. レベル決定は `AlertEvaluator.DiskSustainedBusy(busyPercent, highDuration, recoveryDuration, previousLevel)` のような純粋関数へ委譲する。
5. 評価結果を物理ディスクごとにスナップショットへ添付し、ViewModel は温度警告との最大値を行・セクションへ反映する。

状態を `SidebarViewModel` に置く案は、UI再生成で状態が失われ、UIが適用できなかったサンプルを取りこぼし、同じディスク値を再利用した合成スナップショットを識別する追加策も必要になるため非推奨である。`AlertEvaluator` を直接ステートフル化する案も、static API、純粋関数テスト、複数利用者・複数ハブの独立性を壊すため非推奨である。

評価結果のモデル表現は、第一候補として `DiskDeviceSnapshot` に `BusyAlertLevel` を追加し、`MetricsHub` が provider の返した各要素を `with` で付加した新しい `DiskSnapshot` を公開する。生メトリクスと評価結果を厳密に分けたい場合は `MetricsSnapshot` に別の `DiskAlertSnapshot` を追加できるが、コンストラクタ・テストfixtureへの波及が大きい。初版は前者が低コストである。

時間はサンプル件数ではなく `elapsed` または単調増加時計で測る。`DateTimeOffset.UtcNow` の時刻変更影響を避けるため、`MetricsHub` が既に計測している `Stopwatch` 由来の `elapsed` を加算するのがよい。異常に長い停止・サスペンド復帰を「連続高負荷」と数えないよう、許容可能なサンプル間隔（例: FastIntervalの3倍）を超えた場合はリセットする。

## 6. ファイル単位の変更方針

### `src/Monitor.Core/Alerts/AlertThresholds.cs`

- `DiskBusyCautionPercent = 95.0`
- `DiskBusyCautionDuration = TimeSpan.FromMinutes(2)` 相当
- `DiskBusyCriticalPercent = 99.0`
- `DiskBusyCriticalDuration = TimeSpan.FromMinutes(10)` 相当
- `DiskBusyRecoveryPercent = 85.0`
- `DiskBusyRecoveryDuration = TimeSpan.FromSeconds(15)` 相当
- 設計意図と「Busyだけでは障害を断定しない」旨をコメントする。

`const TimeSpan` は使えないため、秒数を `const double/int` にするか `static readonly TimeSpan` に統一する。

### `src/Monitor.Core/Alerts/AlertEvaluator.cs`

- 継続時間と前状態を受けて `AlertLevel` を返す純粋な `DiskSustainedBusy` を追加する。
- NaN/Infinity、範囲外値、負の時間の扱いを明示する。
- クラス全体の説明を「3カテゴリ」に固定せず、高負荷継続という観測警告を含む記述へ更新する。

### `src/Monitor.Core/Alerts/DiskBusyAlertTracker.cs`（新規）

- `PhysicalDriveNumber` ごとの状態辞書を保持する。
- `Update(DiskSnapshot, TimeSpan elapsed)` で各ディスクを1回更新し、消えたディスクの状態を削除する。
- 長すぎる間隔、空スナップショット、測定不能値では連続性をリセットする。
- 時計・サンプリング周期に依存しない単体テスト可能なAPIにする。

### `src/Monitor.Core/MetricsHub.cs`

- トラッカーをハブのインスタンス状態として保持する。
- `RunFastLaneAsync` で `DiskProvider.Sample` 直後に一度だけ更新する。他レーンの `Publish` では再評価しない。
- 評価済みのディスクスナップショットを公開する。
- テスト用 `PublishSnapshotForTest` の経路が警告追跡を通すか否かを明示し、必要なら専用フックを用意する。

### `src/Monitor.Core/Models/DiskSnapshot.cs`

- 推奨する低コスト案では `DiskDeviceSnapshot` に `BusyAlertLevel`（既定 `None`）を追加する。
- 代替の厳密分離案ではこのファイルを変更せず、新しい `DiskAlertSnapshot` モデルを用いる。

### `src/Monitor.App/ViewModels/SidebarViewModel.cs`

- `BuildDiskRowAction` で `busyLevel = d.BusyAlertLevel` を取得する。
- 行全体の `AlertLevel` は `MaxLevel(temperatureLevel, busyLevel)` とし、既存のセクション最大値集約へ自然に合流させる。
- 折りたたみ時の `EvaluateStorageAlertLevel` にも Busy 警告を含める。
- 温度色とBusy色を個別に表示するため、行の総合レベルとは別に温度レベル・Busyレベルを `StorageRowViewModel` へ渡す。
- ツールチップに発報中のみ「高 Busy が継続」の説明を追加することを検討する。新規行は不要。

### `src/Monitor.App/ViewModels/StorageRowViewModel.cs`

- `BusyAlertLevel` と `TemperatureAlertLevel` を追加する。
- `UpdateAsDisk` の引数を拡張する。
- 既存 `AlertLevel` は行・セクション集約用の最大値として残す。

この `AlertLevel` の束ね方は、物理ディスク見出し行のグロートリガーにも利用する。すなわち `AlertLevel = MaxLevel(TemperatureAlertLevel, BusyAlertLevel)` とし、Caution/Critical の高い方を行全体の表示へ反映する。これにより Busy 継続警告だけでなく、既存の温度警告でも同じ「この物理ディスク行が警告中」という視覚表現になる。Busy のときだけグローさせる要件へ後から絞る場合は、スタイルのトリガー元を `BusyAlertLevel` に差し替えればよいが、初版は原因別の色とは別に行の総合状態を示す既存 `AlertLevel` を使う方が一貫している。

### `src/Monitor.App/Views/SidebarWindow.xaml`

- `StorageDiskRowTemplate` のルート `Border` は、引き続き `StorageDiskHeaderStyle` を適用する。グローの実体はテーマ側のスタイルへ集約し、このテンプレートには別の枠要素や余白を追加しない。
- Busy%文字だけを着色する案も併用可能であり、その場合は `BusyPercentText.Foreground` を `BusyAlertLevel` + `AlertLevelToBrushConverter` にバインドする。ただし行全体グローだけで対象ディスクと警告レベルを十分識別できるため、初版ではグロー単独を推奨する。併用するならグローを弱めにし、強調過多にならないか実機表示で確認する。
- 温度文字は行全体の `AlertLevel` ではなく `TemperatureAlertLevel` に変更する。これを行わないと Busy 警告だけで温度文字まで黄色/赤になり、原因を誤認させる。
- 新しい行・ゲージ・マージンは追加しない。

### `src/Monitor.App/Themes/Dark.xaml`

- `StorageDiskHeaderStyle` に `DataContext.AlertLevel` を見る Caution/Critical の `DataTrigger` を追加し、対象 `Border.Effect` に警告色の `DropShadowEffect` を設定する。
- 通常時（`None`）は `Effect` を設定しない。Caution/Critical のときだけグローを生成し、解除時には既存の通常表示へ戻す。
- 色は既存テーマと揃え、Caution は `#F0C23C`、Critical は `#F04A4A` を基準にする。テーマ色の変更と追従させる必要があるため、実装時は既存警告色リソースとの二重定義を避ける構成を検討する。
- `Effect` をスタイル内で共有すると `Freezable` の共有・変更可否が問題になり得るため、トリガーごとに不変の効果インスタンスを与え、実行時に個別変更しない。

### `README.md` と `src/Monitor.Core/Alerts/AlertLevel.cs`

- 「Busy 100% は警告しない」という絶対的記述を、「瞬間的な高Busyは警告しないが、異常に長く継続した場合は観測警告にする」へ更新する。
- 枯渇・温度・障害の3カテゴリだけ、という記述も新方針と整合させる。

### テスト

- `tests/Monitor.Core.Tests` 相当の警告テストへ、閾値直前/一致、Caution→Critical、短い低下、15秒解除、ディスク消失、欠測、長時間停止、複数ディスク独立性を追加する。
- `tests/Monitor.App.Tests/SidebarViewModelDetailGatingTests.cs` に、折りたたみ中も Busy 警告が見出しへ反映・解除されるケースを追加する。
- XAMLテストに、Busy文字が `BusyAlertLevel`、温度文字が `TemperatureAlertLevel` へバインドされていることを追加する。
- `MetricsHub` テストに、他レーンの再公開では継続時間が増えないことを追加する。

## 7. UI 表現

正式な推奨表示は、**警告中の物理ディスク見出し行全体を警告色でグローさせる方式**とする。対象は `StorageDiskRowTemplate` のルートであり、既存 `StorageDiskHeaderStyle` が適用されている `Border` である。Busy%の数字だけを見る必要がなく、視線移動を抑えながら、どの物理ディスクが警告中かを一目で識別できる。また、既存スタイルへのトリガー追加を中心に実現できるため、テンプレート構造や行高への波及が少ない。

- Caution の目安は `ShadowDepth=0`、`BlurRadius=4`、`Opacity=0.60`、`Color=#F0C23C` とする。
- Critical の目安は `ShadowDepth=0`、`BlurRadius=5`、`Opacity=0.80`、`Color=#F04A4A` とし、Caution よりわずかに強くする。
- `ShadowDepth` は必ず0に固定し、一方向の影ではなく行の周囲を均等に囲む発光として見せる。実機でにじみが強すぎる場合は、まず `BlurRadius` を3～4、`Opacity` を0.45～0.70の範囲で調整する。
- ストレージセクションの既存3pxアクセントバーと、折りたたみ時の要約文字を既存集約レベルで着色する。
- 温度・容量・切断は、それぞれ原因となった既存値だけを着色する。物理ディスク見出し行のグローは `AlertLevel = MaxLevel(TemperatureAlertLevel, BusyAlertLevel)` に従い、その行の総合警告状態を示す。
- Busy%文字だけの警告色はグローとの併用が可能だが、必須ではない。グロー単独で行と深刻度を識別できるため、初版はグローのみで十分とする。原因を数値位置でも明示したいという評価結果が得られた場合に限り、Busy%文字色も追加する。
- アニメーション・点滅は使わない。常時表示サイドバーで注意を奪い、アクセシビリティ上の負担があり、既存デザイン言語から外れる割に「正常なコピーとの区別」には寄与しないためである。

単純な `BorderThickness` による枠は採用しない。枠線は要素の測定・配置や必要サイズへ影響し、既存の「高さを1pxも増やせない」という制約に抵触し得る。一方、WPF の `Effect` は要素の描画結果へレンダリング時に適用され、`Measure` / `Arrange` パスには関与しない。そのため `DropShadowEffect` のぼかしが見た目上は行の外側へ広がっても、行の DesiredSize、実配置サイズ、マージン、既存の行高は増えない。ただし、親要素のクリッピング設定や隣接要素との重なり方によりグロー端が切れる可能性は、実機で確認する。

パフォーマンス面では、`DropShadowEffect` が環境や描画条件によってソフトウェアレンダリングパスに乗り、常時多数の要素へ適用するとCPU/GPU負荷を増やす可能性がある。本案では通常時の `Effect` は未設定で、Caution/Critical 中の少数の物理ディスク行だけを対象とし、`BlurRadius` も4～5程度に抑えるため、常時全行へ適用する場合よりリスクは限定的である。それでも低性能環境、リモートデスクトップ、複数ディスク同時警告時のサイドバー描画負荷を計測し、問題が出る場合は Opacity/BlurRadius の縮小、Busy%文字色だけへのフォールバック、または効果の無効化設定を検討する。

Critical でも通知トーストや音は初版に含めない。ユーザー要望は「気づける」ことであり、まず既存サイドバー内の一貫した色表現で十分かを検証する。将来通知を加える場合は、同一エピソードで一度だけ通知するラッチと設定による無効化が必要になる。

## 8. 導入時の判断事項

- 長時間の正当なコピーも発報することを受け入れ、「異常断定」ではなく「高負荷継続」と位置づける。
- 初版は固定閾値とし、既存どおり `settings.json` 対応は別課題にする。
- 実装前に、通常利用・大容量コピー・Windows Update・ウイルススキャン時の Busy ログを数台で採り、2分/10分が過敏でないか確認する。
- 物理ドライブ番号は同一プロセス稼働中のキーとして利用し、デバイス消失時に必ず状態を破棄する。より強い同一性が必要になった場合はデバイスIDのモデル追加を別途検討する。
