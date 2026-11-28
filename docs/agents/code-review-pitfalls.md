# コードレビュー時の注意点

[conventions.md](conventions.md) から分割した詳細。

- トグルの多重操作レース: `ToggleSwitch` の `IsEnabled` は `CanToggle` (= 状態既知 かつ 非実行中 かつ 親がある場合は状態既知・ON) にバインドすること。操作はスイッチへ集約する。既定値は `IMaintenanceToggle.DefaultState` (`true`=ON、`false`=OFF、`null`=環境による)、選択型は `Options.IsDefault` から表示し、現在値や推奨値と混同させない。変更時は `CommandCategoryStateQueryTests` のOFF表示・状態不明・実行中・親子連動と既定値表示の検証を通す。表示の理由は [DESIGN.md のメンテナンス操作のモデル](../design/workflow.md#メンテナンス操作のモデル) を参照する。
- 状態表示の乖離を避ける: `GetStateAsync` はレジストリだけでなく実適用状態も見る (例: VBS トグルは bcdedit の `hypervisorlaunchtype` も照合)。部分適用を避けるため、失敗しやすいステップ (bcdedit 等) を先に実行してから残りを書く。
- 状態取得の診断は `StateErrorText`、操作結果は `ResultText` へ分け、状態回復時に診断だけを消す。状態不明の技術的理由は `StateFailureReason`、翻訳可能なトグル固有診断は `StateDiagnostic` で渡し、正常取得時には解除する。失敗・部分成功・受付拒否の記録は `MaintenanceItemLog` を使い、項目名と原因を残す。診断の境界は [DESIGN.md の状態と永続化](../design/data-model.md#状態と永続化) を参照し、変更時は操作フロー検証も通す。親子の操作部品はローカライズ済み項目名をアクセシビリティ名にする。デバイスの空表示は列挙成功時だけ出す。
- 部分失敗を成功と偽らない: マルチステップ (powercfg 等) は重要ステップの失敗で `Fail` を返す。使用中ファイルのスキップのように「想定内の一部未処理」は `Partial` と結果行で伝える。
- NTP と MMAgent の変更では [DESIGN.md の設定変更の範囲](../design/configuration.md#設定変更の範囲) を維持する。同期モードの強制変更や `EnablePrefetcher` 全体の旧バックアップ復元を再導入しない。
- MMAgent の一括再読込・切替時は [DESIGN.md](../design/configuration.md#設定変更の範囲) のキャッシュ更新と並行照会の集約を維持し、取得失敗や不正 JSON の診断を共有照会境界で記録する。外部コマンドの診断には `CommandFailureDiagnostic` を使い、秘密値・制御文字・出力量を制限する。
- クリーンアップのスタイルの宣言順: `caption`・`badge`・`card` など基底のスタイルより後に `.warning`・`.error`・`.compact` を宣言しないと、警告色が基底の色に打ち消されて見えなくなる。`App.axaml` の `CleanupStyles.axaml` は `Application.Styles` の末尾から動かさず、色の変更は実画面（プレビューの書き出し）で確かめる。
- 検証用プレビューから実データへ届く経路: `--preview-state-diagnostics` のクリーンアップ画面は偽データだけで動かし、実ファイル・実レジストリ・復元ポイント・サービスへ届かせない。高度の削除は `%TEMP%\lumin4ti-preview-*` の合成フォルダーの中に閉じ込め、カスタムフォルダーの外のパスは設定の読み取り時に除く。偽でない実装を差し込んだり、読み取り時の除外を外したりしていないか、`LUMIN4TI_PREVIEW_SELFCHECK=1` で確かめる。
- 配布契約は [DistributionContractTests](../../src/Lumin4ti.Tests/DistributionContractTests.cs) が横断で固定している。`Lumin4ti.UI.csproj` / `scripts/release-local.ps1` / `scripts/set-msi-program-files-location.ps1` / `README.md` / `AppSettings.cs` を編集すると、意図せずここで落ちることがある。落ちたら文字列だけ直さず、配布方式を変えていないかを先に確認する。
