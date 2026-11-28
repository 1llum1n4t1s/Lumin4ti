# メンテナンス項目の規約

[AGENTS.md](../../AGENTS.md) から分割した詳細。

## アーキテクチャ

システム全体の構造と設計判断は [DESIGN.md](../../DESIGN.md) を正本とし、この節では実装時に守る規約を扱う。

- Core から UI を参照しない。Avalonia MVVM + CommunityToolkit.Mvvm を使い、`App.axaml.cs` の `ConfigureServices` で手動 DI (全 Singleton) を維持する。
- テストではレジストリ・SCM・COM・デバイスの実変更を行わない。ファイル削除の検証はテスト専用一時領域に限定する。各プロジェクトと操作フロー検証の責務は [DESIGN.md](../design/architecture.md#主要コンポーネント) を参照する。

## メンテナンス項目の中核 (最重要)

すべての「機能」は `IMaintenanceItem` を軸にした 4 型 ([IMaintenanceAction.cs](../../src/Lumin4ti.Core/Interfaces/IMaintenanceAction.cs)):

- **`IMaintenanceAction`** — 「実行」ボタン型 (1 回実行)。`ExecuteAsync(IProgress<string>?, ct)` でライブ進捗を UI へ流せる。
- **`IMaintenanceBatchSetting`** — 対象一覧へ機能のON/OFFを繰り返し適用する一括設定型。同じカードの「一括OFF」「一括ON」から `ApplyAsync(bool enabled, ct)` を呼ぶ。`enabled = true` は対象機能を有効にする意味。現在状態の取得には依存せず、実行ごとに対象を再列挙する。UWPバックグラウンド実行が該当する。
- **`IMaintenanceToggle`** — ON/OFF トグル型。**ON = 最適化を適用 / OFF = 項目の解除値へ設定または明示上書きを削除**。永続バックアップと適用前の元値復元は行わない。MMAgent 系は ON = 機能有効。`GetStateAsync`/`SetStateAsync`。
- **`IMaintenanceChoice`** — ドロップダウン選択型。ON/OFF に収まらない数値・段階設定に使う (例: `MmAgentOperationApiChoice` は「無効」と記録ファイル数を 1 つの操作で選ばせる)。`Options`/`GetSelectedValueAsync`/`SetSelectedValueAsync`。選択肢の表示名は翻訳不要なら `Label` をそのまま出し、翻訳が要るものだけ `LabelKey` を持たせる。`IsDefault` を付けた選択肢に UI が「(既定)」を添える。

`IMaintenanceItem.ParentId` に前提となる項目の Id を入れると、その項目は**親カードの中へ 1 段だけ入れ子表示**され、親トグルが OFF または状態不明の間は操作不可になる (OS 側で連動して無効になる子設定に使う)。親の状態既知・ON/OFF の変化を子へ通知する。親は同じカテゴリに置くこと。

新機能を足すときは Actions 配下にクラスを作り、**[MaintenanceActionCatalog.cs](../../src/Lumin4ti.Core/Services/Windows/MaintenanceActionCatalog.cs) の `Items` に登録するだけ**で UI に現れる。カタログの並び順が画面の表示順。単純なレジストリ tweak は個別クラスを作らず汎用の `RegistryToggle` にスペックを渡す。

## クリーンアップは専用の流れ

左の「クリーンアップ」タブは `CleanupView` で、内側に 通常／高度／スリム化／レジストリ／設定／その他 の 6 タブを持つ。通常・高度・スリム化・レジストリ・設定の各画面は共通の入れ物 `CleanupPage`（`Controls/CleanupPage.cs`）に載せる（型は [cleanup-design.md](../design/cleanup-design.md#共通の画面の型)）。「その他」だけが `CommandCategoryView` で、上の 4 型のカードを表示する。通常・高度・スリム化は規則データ・拡張子パターン・固定項目表を走査して選ぶ専用フローで、`MaintenanceActionCatalog` の単一項目モデルへ載せない。`MainWindowViewModel` は `CommandItemViewModel` 固定にせず `IProcessingOperation` を見る。走査・削除は起動時や再アクティブ時に自動で走らせない。規則データの契約は [cleanup-rules-and-deletion.md](cleanup-rules-and-deletion.md) を参照する。
