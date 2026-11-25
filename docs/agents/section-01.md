# 一時ファイル・キャッシュの削除

[security.md](security.md) から分割した詳細。

ファイル削除は「グループ 1 つ = ボタン 1 つ」とし、対象パスは [FileCleanupGroups.cs](../../src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupGroups.cs) に集約する。掃除対象を増やすときはパス表へ追加し、個別クラスを作らない。

- [FileCleanupEngine](../../src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupEngine.cs) — 削除の実体。`CleanupTarget` は `Contents` (既知のキャッシュ／ログフォルダの中身だけ) / `Files` (既知のキャッシュファイル名だけ) の 2 種。フォルダごとの削除とドライブ全体の再帰検索は扱わない。使用中ファイルは飛ばして続行し、削除数・解放バイト数・ブロック数を `CleanupOutcome` に集計する。削除不能の記録は `RecordBlocked` を通し、パス・理由・エラー番号の代表例を件数・長さの上限内で残す。
- [FileCleanupAction](../../src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupAction.cs) — グループ 1 件分の `IMaintenanceAction`。サービス停止・再起動要否・Explorer 影響・再起動時削除予約をコンストラクタ引数で受ける。

削除対象は、名称と用途の両方から再生成可能と確認できるキャッシュ・ログ・一時領域だけに限定する。Python ランタイム、仮想環境、ローカルビルド成果物、WebStorage、閲覧・利用履歴、オフラインデータ、復旧資産、ドライバインストーラー、アプリ設定、汎用ホームディレクトリは対象にしない。ゴミ箱、`Windows.old`、Outlook OST/NST、ドライブ全体のファイル検索も扱わない。

**安全ガード (回帰厳禁)**: 実運用では他人の PC の実データを消すため、次を外さない。

- `TryResolve` が、環境変数の未解決 (`%ProgramData%` 未定義で `\LGHUB\cache` になる等)、相対パス、ドライブ直下、`%LOCALAPPDATA%` 等の基点フォルダを拒否する。基点フォルダは `Files` 指定 (`IconCache.db` / `FNTCACHE.DAT`) のときだけ許可する。
- ジャンクション・シンボリックリンクは、対象確認時点と削除直前のハンドル属性で検出した場合、リンク自体を保持する。列挙・属性変更・削除は再解析を拒否して開いた同じオブジェクトのハンドルで行い、途中のパス差替えでもリンク先へ作用しない。最終確認後の競合に対する保証範囲は [DESIGN.md の状態と永続化](../design/data-model.md#状態と永続化) を参照する。
- 認証情報・鍵・アプリ設定のフォルダ (`.gnupg` / `.aws` / `.config` / `.codex` 等) はどのグループにも入れない。再生成できないので掃除の巻き添えにしない。[FileCleanupTests](../../src/Lumin4ti.Tests/FileCleanupTests.cs) が回帰を検出する。
- npm は `%LOCALAPPDATA%\npm-cache` / `%USERPROFILE%\.npm` の基点全体を対象にせず、再取得可能な `_cacache` と診断用の `_logs` だけを削除する。`_npx` は展開済みの実行環境なので削除対象へ戻さない。
- Windows のイベントログ、Defender の検出履歴、GPU 設定、スタートアップ登録、ファイル関連付け、アプリのパッケージ登録、WinSxS の旧コンポーネントはキャッシュではないため `FileCleanupGroups` では扱わない。リンク切れスタートアップと関連付け候補だけは専用アクションで扱い、`StartupCommandParser.IsConfirmedMissing` が準備済み固定ドライブ上の欠損を確定できた登録だけを削除する。UNC、リムーバブル、未準備ドライブ、再解析点配下、アクセス不能、解決不能なコマンドは保持する。
- ETL トレースログは `%SystemRoot%\Logs`、`System32\LogFiles`、`Panther`、`%ProgramData%\Microsoft\Diagnosis\ETLLogs` の既知基点だけをリンク非追従で列挙し、`*.etl` のみ削除する。ドライブ全体を対象にする再帰パターン削除は復活させない。
