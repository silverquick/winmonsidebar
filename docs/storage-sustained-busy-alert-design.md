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

## 7.1 実機評価後の再検討: 発報時だけの枠線パルスを条件付きで採用する

### 結論と判断変更

当初の「アニメーション・点滅は使わない」という判断は、**常時点滅を避ける判断としては維持するが、発報直後だけの短いパルスまで一律に禁止する判断は変更する**。推奨は、現在の静的な警告色枠線を基礎表示として残し、`None` から `Caution` / `Critical` へ入った時、および `Caution` から `Critical` へ上がった時だけ、枠線をゆっくり数回明滅させる条件付き採用である。

理由は、グロー、グロー＋背景塗りの2方式が実機で繰り返し「見づらい」と評価され、静的な視覚差だけでは視野周辺にある常時表示サイドバーの状態変化へ気づきにくい、という具体的な証拠が得られたためである。一方、アニメーションは「高 Busy が正当なコピーか異常か」を判別する情報を増やさず、継続中ずっと動かすと注意を奪い続ける。当初懸念はここには依然として当てはまる。このため、アニメーションの役割を「状態の説明」ではなく「状態遷移への注意喚起」に限定する。

なお本節でいう「パルス」は、要素を完全に表示/非表示へ切り替える強い点滅ではない。警告枠の不透明度だけを滑らかに上下させ、終了後は静的な警告色で表示し続ける。

### 具体的な表示仕様

| 項目 | Caution | Critical |
|---|---|---|
| 対象 | `StorageDiskHeaderStyle` の枠線だけ | 同左 |
| 色 | 現行 `AlertCautionBrush` (`#F0C23C`) | 現行 `AlertCriticalBrush` (`#F04A4A`) |
| 変化 | 枠線の不透明度を `1.0 → 0.45 → 1.0` | 枠線の不透明度を `1.0 → 0.25 → 1.0` |
| 1周期 | 1.2秒（下げ0.6秒、戻し0.6秒） | 同じ1.2秒 |
| 回数 | 2周期（計2.4秒） | 3周期（計3.6秒） |
| 終了後 | 不透明度1.0の静的な琥珀枠＋Busy値の琥珀色 | 不透明度1.0の静的な赤枠＋Busy値の赤色 |

周期はレベル間で変えない。速さの違いは意味を覚えにくく、速いCriticalは刺激も増すためである。深刻度は既存どおり色で示し、Criticalだけ振幅と回数を少し増やす。Busy%文字、行背景、行内のスパークライン、行全体のOpacityは動かさない。特に文字を点滅させると値の読み取りを妨げ、背景全面の明滅は刺激面積が大きくなる。グロー (`DropShadowEffect`) の復活も、過去の実機評価と描画コストの両面から行わない。

パルスは警告状態の間ずっと繰り返さない。`AlertLevel` はヒステリシスにより発報後も維持されるため、毎秒のメトリクス更新では再始動せず、次のレベル遷移だけを開始条件にする。`Caution → Critical` は新たな注意喚起としてCriticalパルスを開始する。`Critical → Caution` は現在の評価器では通常発生せず、解除時はアニメーションせず通常表示へ戻す。折りたたみ中に発報した場合はストレージセクションの既存アクセント色が警告を保持し、後から展開された行についてパルスを再生するかは実装時に一貫性を確認する。推奨は、展開を「新規発報」と扱わず静的警告だけを表示することである。

### WPFでの実装案と性能

実装する場合は `StorageDiskHeaderStyle` の `DataTrigger.EnterActions` に `BeginStoryboard` を置き、`DoubleAnimation`（または往復を明示した2本の `DoubleAnimationUsingKeyFrames`）で、枠線専用要素の `Opacity` を有限回だけ変更する。`AutoReverse=True`、`RepeatBehavior="2x"` / `"3x"`、`FillBehavior="Stop"` とし、アニメーション終了後はトリガーの静的 `BorderBrush` とOpacity 1.0が再び有効になる構成にする。

現在のルート `Border.Opacity` を直接動かすと、文字とスパークラインまで暗くなるため不適切である。実装時はレイアウトサイズを変えない枠線専用のオーバーレイ `Border` をテンプレート内に重ねるか、警告枠専用Brushを要素ごとに持たせてそのBrushの `Opacity` を動かす。後者では共有された `StaticResource` Brushを直接アニメーションせず、各行用のBrushインスタンスが必要になる。単純さと共有リソースの副作用回避を優先すると、枠線専用オーバーレイが分かりやすい。

Opacityの短い有限アニメーションは、ぼかし付き `DropShadowEffect` や背景全面の連続アニメーションより描画範囲と継続時間が小さい。物理ディスク数も通常は少なく、毎秒の値更新で再始動しないため、性能影響は限定的と見込む。ただしWPFではアニメーション中の領域を再描画するので、RDP、ソフトウェアレンダリング、複数ディスク同時発報でUIスレッド負荷とフレーム落ちを実機確認する。アニメーション対象に `Effect`、レイアウトプロパティ、行全体を選ばない。

### アクセシビリティ条件

WCAG 2.2 SC 2.3.1（Level A）は、1秒間に3回を超える閃光を含めないか、一般閃光・赤色閃光の閾値以下にすることを求める。本案の1.2秒/周期は約0.83周期/秒で、上限より十分遅い。ただし「3回以下なら誰にでも安全」という意味ではないため、刺激面積を1px枠へ限定し、完全なオン/オフ、高コントラストの背景反転、速い赤色点滅を避ける。参考: [WCAG 2.2 SC 2.3.1](https://www.w3.org/TR/WCAG22/#three-flashes-or-below-threshold)、[Understanding SC 2.3.1](https://www.w3.org/WAI/WCAG22/Understanding/three-flashes-or-below-threshold.html)。

加えて、OSの「アニメーションを表示する」設定を尊重し、WPFの `SystemParameters.ClientAreaAnimation` が無効ならStoryboardを開始せず静的表示だけにする。可能ならアプリ設定にも「警告アニメーション」を設け、既定はOS設定に追従、利用者が明示的に無効化できるようにする。WPFの対応プロパティ: [SystemParameters.ClientAreaAnimation](https://learn.microsoft.com/dotnet/api/system.windows.systemparameters.clientareaanimation)。WCAG 2.2 SC 2.3.3は主にユーザー操作で起動する非本質的アニメーションを無効化できることを扱うLevel AAA基準で、本警告はユーザー操作起因ではないため直接の適用対象ではないが、動きに弱い利用者へ停止手段を与える設計原則として参照する: [Understanding SC 2.3.3](https://www.w3.org/WAI/WCAG22/Understanding/animation-from-interactions.html)。

警告の意味を色と動きだけに依存させない。現在のBusy%文字色に加え、ツールチップまたはアクセシブル名へ「高 Busy 継続: 注意/危険」を含める。スクリーンリーダーへの毎秒通知は避け、レベル遷移時だけ通知する場合も、設定で抑止できるようにする。

### パルスが無効な場合の静的フォールバック

OS設定またはアプリ設定でアニメーションが無効でも、現在の1px警告色枠＋Busy%文字色は維持する。案Cの最終実機評価でも視認性が不足する場合は、点滅を強める前に次を順に検討する。

1. Busy%の直前に小さな警告アイコン（例: `!` を含む三角形）を置き、色だけでなく形でも状態を示す。既存列幅に収まらなければBusy列内の数値と置換せず、オーバーレイまたはツールチップと組み合わせる。
2. モデル名の直後または空き領域へ `BUSY` / `高負荷` の短い状態ラベルを追加する。Caution/Criticalの文言またはアイコン形状を変え、色覚に依存しない。
3. 枠線のうち左辺だけを2～3pxの静的な警告色アクセントにする。四辺1px枠より面積を確保しつつ、背景全面塗りより文字との干渉が少ない。オーバーレイ描画にして行高・列幅を変えない。
4. 静的枠のコントラストを実背景上で測り、色または太さを調整する。1px線は高DPIや縮小表示で弱く見えるため、DPI・RDPを含む実機確認を行う。

この順序は、警告の意味を形・文言でも伝える案を優先し、刺激の強い全面明滅や常時点滅を最終手段にも採らないためのものである。

## 8. 導入時の判断事項

- 長時間の正当なコピーも発報することを受け入れ、「異常断定」ではなく「高負荷継続」と位置づける。
- 初版は固定閾値とし、既存どおり `settings.json` 対応は別課題にする。
- 実装前に、通常利用・大容量コピー・Windows Update・ウイルススキャン時の Busy ログを数台で採り、2分/10分が過敏でないか確認する。
- 物理ドライブ番号は同一プロセス稼働中のキーとして利用し、デバイス消失時に必ず状態を破棄する。より強い同一性が必要になった場合はデバイスIDのモデル追加を別途検討する。
