# クリーンアップ（規則・走査・削除）

[security.md](security.md) から分割した詳細。

クリーンアップは「通常」「高度」「スリム化」「設定」「その他」の 5 タブで構成する。通常は規則データ、高度は拡張子パターン、スリム化は固定の項目表を対象にする。実装は [Cleanup/](../../src/Lumin4ti.Core/Services/Windows/Cleanup/) に置く。構造と設計判断は [DESIGN.md](../../DESIGN.md) と [設計の詳細](../design/cleanup-design.md) を参照する。

## 規則データ

- 対象は `Definitions/*.json` の規則だけで、アセンブリへ埋め込む。外部ファイルの読み込み・更新経路は作らない。読み込みは source generator と `UnmappedMemberHandling.Disallow` で行う。
- データに SQL・コマンド・実行ファイルを書かせない。`sqlite` の操作名と `handler` 名は C# の列挙に限る。`clearTable` が指定できるのは DB パス・表名・期待する列名だけで、login / password / token / credential / account / card / key / session を含む表名は拒否する。
- 各規則に根拠 (`evidence`) を必須とする。tier A は公式文書または製品の公開ソース 1 件、tier B は出典ホストの異なる独立した二次資料 2 件、tier L は旧版のグループが既に対象にしていた場所の継承（`legacy.` で始まる id で `legacy` が空でない規則だけ。URL を持たず、新しい場所を足す根拠にはならない）。どれも満たさなければ収録しない。
- 他社製品の設定ファイル・項目一覧は、値の転記・機械変換・根拠件数への算入をしない。他社製品の項目一覧や対応表はリポジトリへ入れない。
- 影響度は `junk` / `trace` / `signout` / `rebuild` / `userFiles`。`recommended=true` にできるのは `junk` だけ。`signout` は cookies 分類だけに許し、Cookie を消す操作は必ず `signout` にする。`userFiles`（受信ファイルなど再取得できない利用者データ）は既定オフで、「おすすめ」にも「すべて選択」にも含めず、個別選択と専用の再確認を必須にする。
- 既定の選択は「利用者の上書き → 規則の `recommended`」の順。Cookie とフォーム履歴は既定オフで、利用者が明示的に選んだときだけ消せる。「すべて選択」は Cookie を含むため、サインアウトを伴う警告を表示する。
- 検証器 (`CleanupRuleValidator`) はテストと起動時の両方で動く。違反した規則だけを無効にしてログへ残す。確認する内容は、id の一意性、許可された環境変数、絶対パス、`..` 禁止、基点フォルダー直下の `contents` 禁止、規則間の完全重複禁止、認証情報の拒否表、registry の範囲、サービスの許可表。
- 拒否表は保存パスワード・認証情報・鍵・アプリ設定を対象外にする。ログイン情報のストア、資格情報・鍵・トークンのフォルダーとファイル、暗号化ストレージ、`Storage2` などを含む。ゲームのセーブ、チャットのメッセージ DB、クラウド同期の同期ルートと同期 DB、セキュリティ製品の隔離領域・検出定義・ライセンス、AI・開発ツールの会話履歴・拡張・認証も入れない。
- レジストリは HKCU の `Software\` 配下に限り、`Classes`・`Policies`・`Run`・`RunOnce` は対象にしない。HKLM は固定ハンドラーだけが扱う。各階層を `REG_OPTION_OPEN_LINK` で開き、リンクなら拒否する。値の削除は同じハンドルで行う。
- 規則を増やすときは `Definitions/*.json` へ追加し、個別クラスを作らない。ブラウザーは基点と種別から規則を生成する（`CleanupEngineExpander`）ので、手書きで増やさない。規則数・収録数は文書へ書かない。

## 走査と削除 (回帰厳禁)

実運用では他人の PC の実データを消すため、次を外さない。

- 走査は何も変更しない。走査していない対象は削除しない。走査結果は世代番号を持ち、設定変更・削除開始・再走査で失効する。失効した結果は実行しない。
- 削除は、同じ規則・除外で再列挙し、走査完了時刻より後に作られたファイルを保持する。高度なクリーンと孤児 Installer・無効ショートカットは、FileId・サイズ・更新時刻が走査時と一致した同一ハンドルだけを削除し、不一致は保持する。
- 列挙・属性変更・削除は、再解析を拒否して開いた同じハンドルで行う ([FileCleanupEngine](../../src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupEngine.cs)、[NativeCleanupEntry](../../src/Lumin4ti.Core/Services/Windows/Actions/NativeCleanupEntry.cs))。ジャンクション・シンボリックリンクはリンク自体を保持し、リンク先へ作用しない。パスを再解決して削除する経路 (Shell の削除、`MoveFileEx`) を使わない。
- 削除はごみ箱へ移さず完全削除とし、確認画面に「元に戻せない」と表示する。除外などで意図して残したものは `Retained` として数え、その親フォルダーの削除失敗を Blocked にしない。
- 所有権の奪取・ACL の変更 (`takeown`・`icacls`・`SetNamedSecurityInfo`) はしない。権限不足は件数と代表例を表示して `Partial` にする。
- `TryResolve` は、未解決の環境変数、相対パス、ドライブ直下、基点フォルダー自体を拒否する。基点フォルダーは `Files` 指定のときだけ許可する。
- 部分失敗・サービス停止失敗・プロセス終了拒否・DB ロック・復元ポイント失敗・DISM 非 0・キャンセルのどの経路も成功と表示しない。削除不能の記録は `RecordBlocked` を通し、代表例を件数・長さの上限内で残す。
- 安全な削除（既定オフ）は同一ハンドルで 1 パス上書きしてから削除する。リンク数が 2 以上、圧縮・スパース・EFS・読取専用のファイルは上書きせず削除だけ行う。SSD では保証しないと表示する。
- 準備済みの固定ドライブだけを対象にする。UNC・SUBST などの未対応マッピングは拒否する。
- 利用者スコープの規則（HKCU・プロファイル・SQLite・Shell）は、対話利用者と昇格利用者の SID が異なる場合は全て無効にして理由を表示する。カスタムフォルダーは `settings.json` が非昇格プロセスからも書けるため、実行時にも `CustomFolderGuard` で検査する。ドライブ直下、Windows・Program Files・ProgramData（配下・自身・祖先）、他の利用者のプロファイルとそれを束ねるフォルダー、自分のプロファイル直下・AppData 系・Documents など、パスに認証情報の名前（`.ssh`・`Credentials`・拒否表のファイル名など）を含むもの、ブラウザー・メールのプロファイル基点（Chrome 等の User Data、Firefox・Thunderbird のプロファイル、Opera、Outlook）の自身・祖先・子孫を拒否する（Downloads は許可のままで、カスタムフォルダーは `UserFiles` として個別選択と再確認を必須にする）。8.3 短縮名は展開できない純関数のため拒否する。拒否したものは既定未選択にする。許可したフォルダーの子孫でも、拒否表に当たる名前は `KeepCredentials`（`CleanupTarget.CustomFolder` が付ける）で走査にも削除にも入れず残す（`FileCleanupEngine.KeepsAsCredential`）。
- 規則の基点の環境変数は `CleanupBaseTrust` で検証してから使う。利用者が差し替えられるため、プロファイル系は `USERPROFILE` ではなく OS の既知フォルダーが HKLM の `ProfileList` と一致した場合だけ信頼し、基点がそのプロファイルの内側でなければ使わない（`TEMP`・`TMP` だけは別ドライブも許し、保護された場所・他人のプロファイル・ドライブ直下・高度クリーンの枝刈りの表（`$Recycle.Bin`・`Recovery`・`Windows.old` など）を拒否し、対話利用者が自力で中身を変更できるフォルダー（再解析点を辿らず開き、所有者が利用者、または利用者・Users・Authenticated Users・Everyone への許可 ACE が Modify 相当で拒否 ACE に当たらない。判定不能は拒否）だけを使う）。展開後の基点に `%` が残る場合も使わず、展開済みの実パスは `IsActualPath` の印でエンジン・走査・領域が再展開しない。機械側の変数（`ProgramData`・`SystemRoot`・`ProgramFiles` など）は OS が示す既定の場所と長い名前で一致したときだけ使う。確認できなければ規則を使わず、理由を文言符号（`BaseOutsideProfile`・`BaseProtectedLocation`・`BaseMachineMismatch`）で返す。
- 規則どうしで対象フォルダーが重なる場合（例: `%TEMP%` 全体と `%TEMP%\NeroSupport`）、同じファイルを二重に数えず、二重に失敗にしない。走査は `ClaimSet.ScanResolver` が先の規則に属するものを `Overlaps` へ分け、`CleanupScanSnapshot.Measure` が先の規則も選ばれているときだけ後の規則の分から引く（`TotalFiles`・`TotalBytes` も同じ）。実行は `RunResolver` が、先に処理した対象が扱ったものを飛ばす。同一性は同じ絶対パスで決め、古さの基準時刻は走査・実行で揃える。
- 固定ハンドラーの `Failed` は、他の対象で消した件数にかかわらず規則全体を `Failed` にし、`Partial` は規則を `Partial` にする（サービス復帰の失敗は取消より優先して `Failed`）。ハンドラーが取消を返したら取消として扱い、名前から解決できないハンドラーは失敗にする。
- 画面へ返す理由・結果の文言は `CleanupMessage` の符号（`Cleanup.Msg.*` のキーと引数）で運び、UI が辞書から解決する。ログ・テキスト書き出しでは `CleanupMessage.ToJapanese` で日本語へ戻す。引数は翻訳しない技術値で、符号は入れ子にできる。新しい理由を足すときは日本語のマスター（`CleanupMessages`）と 17 言語の辞書を更新する。
- 状態変更は `MaintenanceOperationCoordinator` の lease を通す。走査は `showProcessing:false`、削除は `showProcessing:true` で取り、サービスの停止と復帰は [WindowsServiceControl](../../src/Lumin4ti.Core/Services/Windows/WindowsServiceControl.cs) の補償を `finally` で必ず行う。

## 実行中の製品・非信頼ファイル

- 実行中の製品は、規則の `processes` に一致し、同一セッション・同一利用者 SID のものだけを扱う。確認（終了する・この項目を飛ばす・中止）→ WM_CLOSE（最大 15 秒）→ 残った場合だけ未保存の入力が失われることを示して再確認 → 同意があれば開いたハンドルで SID と開始時刻を再照合して `TerminateProcess`。強制終了できるのは browserCache・browserTrace・cookies の規則だけで、Explorer と自プロセスは対象にしない。拒否・失敗はその規則だけを `Skipped` にする。
- SQLite は `winsqlite3.dll`（System32）への P/Invoke だけを使い、NuGet を足さない。利用者が書ける非信頼入力として扱い、DEFENSIVE・`trusted_schema=OFF`・トリガー無効・拡張無効で開く。CREATE なしの READWRITE、`locking_mode=EXCLUSIVE`、`secure_delete=ON`、`BEGIN IMMEDIATE` を使い、走査は変更しない。SQLite が名前で開く `-journal`・`-wal` は `DatabaseSidecars` が、検査から操作の終了まで 1 つのハンドルで固定する。方式は DB ヘッダーで決め、どちらの方式でも `-journal` と `-wal` の両方を固定する（空の `-wal` は SQLite が無いものと扱う）。ロールバック方式は `journal_mode=TRUNCATE`、WAL 方式は EXCLUSIVE ロックで `-shm` を使わせず、`PERSIST_WAL` で閉じる時の削除の再試行を避ける。再解析点・ディレクトリ・ハードリンク・中身のあるサイドカー（使用中または未回復）は無変更で拒否し、他者の削除・改名・置換を許さない。自分で作った空のサイドカーだけを、実体を照合してから削除する。期待する表・列と合わない、仮想表、整合性エラー、ロック中は無変更で `Partial` にする。
- INI の編集 (`iniValues`) は 1MB 以下で可逆に変換できるファイルだけを対象にし、プロセス停止を確認して共有なしの同一ハンドルへ書き戻す。書込み失敗時は原文を書き戻して再照合し、補償も失敗なら `Failed` とする。

## 高度なクリーン・スリム化・復元ポイント

- 高度なクリーンは準備済みの固定ドライブをハンドル列挙する（リムーバブルは既定未選択）。`AdvancedProtectedPaths` の枝刈り（Windows 全体、Program Files 系、ProgramData\Microsoft、ごみ箱、System Volume Information、Recovery、Boot、他利用者のプロファイル、認証情報フォルダー、`.git`、Lumin4ti 自身など）を外さない。既定オンの拡張子と追加（既定オフ）の拡張子を分け、判定不能・24 時間以内に更新・0 バイトは未選択にする。結果の表示上限を超えた分は、超過を明示して削除しない。
- スリム化は固定の項目表だけを扱い、`SfcIsFileProtected` が保護と答えるファイル、TrustedInstaller 所有のファイルには触れない。Installer の孤児は、MSI API とレジストリの和集合に無く、30 日以上古いものだけを対象にし、列挙に 1 件でも失敗したら項目ごと利用不可にして、実行直前に登録を再照合する。WinSxS を直接削除せず DISM を使う。IME の本体は削除せず、言語の入力補助機能だけを `Remove-Capability` で扱う。
- スリム化の前に `RestorePointService` で復元ポイントを作る（既定オン）。24 時間以内の既存点の利用・保護の無効・失敗を区別し、失敗時は続行可否を利用者へ確認する。頻度のレジストリは変更しない。
- Lumin4ti は復元ポイントを削除しない（一覧・削除・今すぐ作成の機能は持たない）。

## その他タブに残る項目

- `cleanup-shell-cache`（アイコン・サムネイル・フォントキャッシュ）は旧カードのまま残す。`ShellCacheTargets`、項目 ID、`CleanupExclusions`、起動時再試行（[workflow.md](workflow.md)）を変えない。
- Windows のイベントログ、Defender の検出履歴、GPU 設定、スタートアップ登録、ファイル関連付け、アプリのパッケージ登録、WinSxS の旧コンポーネントは規則データで扱わない。リンク切れスタートアップと関連付け候補だけは専用アクションで扱い、`StartupCommandParser.IsConfirmedMissing` が準備済み固定ドライブ上の欠損を確定できた登録だけを削除する。UNC、リムーバブル、未準備ドライブ、再解析点配下、アクセス不能、解決不能なコマンドは保持する。
- 無効なショートカット・無効なタスクは `ShellLinkReader` と同じ確定欠損の基準で扱い、PIDL のみ・広告型のショートカットと `\Microsoft\` 配下のタスクは保持する。

## テスト

回帰は [FileCleanupTests](../../src/Lumin4ti.Tests/FileCleanupTests.cs) と各規則・走査・ハンドラーのテストが検出する。「許可リストだけを使う」ではなく「保護・認証情報の拒否」を検証する。リンク非追従・同一ハンドル・削除中の差し替えは新しい経路でも再現して検証し、テストでレジストリ・SCM・COM・デバイスを実変更しない。
