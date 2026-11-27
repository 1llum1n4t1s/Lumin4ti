# メンテナンス操作のモデル

[workflow.md](workflow.md) から分割した詳細。

状態変更中は中央の進捗カードを重ね、背面の操作・ナビゲーションを無効にする。表示は操作leaseと設定保存の保留数から導き、補償・再検証まで終わってから解除する。中断可能な項目だけ既存の中断コマンドをカードから呼び、終了時の受付閉鎖とは区別する。読み取り専用の起動時状態照会では表示せず、更新・確認用の別ダイアログは独立した操作を維持する。

`MaintenanceActionCatalog`が画面へ公開する操作の正本であり、各項目は次のいずれかを実装する。

- `IMaintenanceAction`: 一度だけ実行するボタン操作。進捗行と`MaintenanceActionResult`を返す。
- `IMaintenanceBatchSetting`: 一覧の機能を一括OFF／一括ONにする反復可能な操作。`ApplyAsync(bool enabled, ct)`のtrueは機能の有効化を意味する。通常Actionと共通の実行経路で排他・キャンセル・結果・操作名付きログを扱う。
- `IMaintenanceToggle`: 現在状態を取得し、ONで適用、OFFで項目の解除値へ設定または明示設定を削除する。元値復元との違いと補償の境界は[状態と永続化](data-model.md#状態と永続化)にまとめる。
- `IMaintenanceChoice`: 複数の値から選び、適用後の実値を再取得する。

`ParentId`を持つ項目は親カード内へ1段だけ配置され、親が無効または状態不明の間は操作できない。親の状態既知・ON/OFFの変化を子の操作可否へ通知する。カタログの並び順が画面の表示順になる。

トグルの操作は通常スイッチまたはカスタム時のON/OFF選択欄のいずれかに集約し、`CanToggle`条件で状態不明・実行中・親が無効な間は操作させない。各設定の説明には現在状態と区別して既定値を表示する。`IMaintenanceToggle.DefaultState`は`true`が既定ON、`false`が既定OFF、`null`が環境依存または一律の既定値が未確認であることを表す。選択型は`Options`の`IsDefault`付き選択肢を表示し、既定選択肢がなければ環境による／不明と表示する。既定値はこのアプリで扱う操作の標準状態であり、推奨値やそのPCの現在値を意味しない。

VBS無効化の既定は強制上書きを置かないOFF。MMAgentのメモリ圧縮とアプリ起動プリフェッチは既定ONとし、PageCombining、PreLaunch、Recall、audio-priorityは環境による／不明と表示する。既定値の表示は状態照会やOS設定変更を行わず、ロケール変更時に再評価する。

`IMaintenanceToggle.IsCustomState`は適用値にも解除値にも一致しない独自設定や一部適用を表す。状態既知のカスタム状態では、通常スイッチと同じ位置にON/OFF選択欄を表示し、どちらにも一度の選択で変更できる。通常のON/OFF状態ではスイッチを使い、両者を同時には表示しない。状態不明を操作可能なカスタム状態として扱わない。

チェック一覧は検出済みグループの選択スナップショットで、開き直すと更新し、実行時は対象を再列挙する。0件でも再取得の入口を残す。実行中は開閉と選択を止め、取消時は保存済みの選択表示を保持する。対象探索は実行時にUIスレッド外へ移し、ETLツリーはフォルダーごとに結果を返して取消を確認する。

操作データは次の順に流れる。

1. `MainWindowViewModel`がカテゴリごとの`CommandCategoryViewModel`を公開する。
2. ViewModelが`MaintenanceOperationCoordinator`からプロセス間で排他的なleaseを取得する。旧タスクの移行も同じ共有ロックを取得し、GUI操作と重なった場合は次回へ持ち越す。クリーンアップの走査は`showProcessing:false`で、削除は`showProcessing:true`で取得する。
3. CoreのAction / BatchSetting / Toggle / ChoiceがWindows API、レジストリ、または`ICommandExecutor`を通してOSを操作する。
4. 進捗と`Success` / `Partial` / `Failed` / `Canceled`をUIへ返す。複数段階の重要ステップが失敗した場合は`Failed`、使用中ファイルのスキップなど想定内の未処理は`Partial`として可視化する。
5. トグルと選択項目は実状態を再取得し、必要な項目だけExplorerを再起動する。

GUI と旧タスクの移行を含め、状態変更操作はマシン全体で同時に1件だけ実行する。これにより、複数のレジストリ変更、サービス停止、外部コマンド、Explorer再起動が競合しない。

状態読込の診断は`StateErrorText`、操作結果は`ResultText`へ分けて表示する。トグルの`StateDiagnostic`は翻訳キーと日本語フォールバックを持ち、言語変更時にも項目固有の理由を再評価できる。状態再取得に成功すると診断だけを消し、直近の操作結果を保持する。親子の操作部品にはローカライズ済み項目名をアクセシビリティ名として与える。

サイドメニューのカラーアイコンはMicrosoft Fluent UI System Icons由来のPNGをAvaloniaリソースとして埋め込み、外部取得せず表示する。出典・元SVG・変換条件は [素材README](../../src/Lumin4ti.UI/Resources/FluentSystemIcons/README.md) にまとめ、MITライセンスをビルド・publish出力へ同梱する。
