# レジストリクリーナーの判定と削除・復元の契約

[registry-cleaner.md](../design/registry-cleaner.md) の詳細。判定・バックアップ・復元を変える前に読む。

## 判定（共通）

- `Exists` / `ConfirmedMissing` / `Unknown` の 3 値で判定し、`ConfirmedMissing` だけを候補にする。確定の基準は `StartupCommandParser.IsConfirmedMissing` と同じ（準備済みの固定ドライブ、祖先に再解析点なし、OS が FileNotFound / DirectoryNotFound）。
- `Unknown`: UNC・`\\?\` 等、リムーバブル・ネットワーク・未準備・不在ドライブ、アクセス拒否・I/O エラー、相対名（カテゴリーの例外を除く）、ラッパー経由（`Wrappers`）、曖昧なコマンドライン（`TryResolveExecutable`）、REG_SZ / REG_EXPAND_SZ 以外。
- 環境変数は許可名だけを既知フォルダーで展開する。機械側: `SystemRoot` `windir` `SystemDrive` `ProgramFiles` `ProgramFiles(x86)` `ProgramW6432` `CommonProgramFiles` `CommonProgramFiles(x86)` `CommonProgramW6432` `ProgramData` `ALLUSERSPROFILE`。利用者側（HKCU の値のみ、`CleanupBaseTrust` の ProfileList 照合を通す）: `USERPROFILE` `LOCALAPPDATA` `APPDATA`。それ以外・展開後に `%` が残れば `Unknown`。
- WOW6432Node の値は `System32` を SysWOW64 と System32、`Sysnative` を System32、`ProgramFiles` / `CommonProgramFiles` を 64 ビット版と (x86) 版の両方で確認し、全候補で欠損確定の場合だけ対象にする。
- 保護（保持）: `%SystemRoot%` 配下（フォントだけ `%windir%\Fonts` を許可）、`Program Files*\Windows*`、`Program Files\WindowsApps`、`%LOCALAPPDATA%\Microsoft\WindowsApps`、TrustedInstaller 所有のキー。
- キーは各階層を `REG_OPTION_OPEN_LINK` で開いてリンクを拒否し、同じハンドルで削除する。HKCU は `HKEY_USERS\<SID>` を基点にし、対話利用者と昇格利用者の SID が違えば HKCU カテゴリーを無効にして理由を出す（`InteractiveScope`）。他の利用者のハイブは対象外。
- `Unknown`・保護は選択肢に出さず、カテゴリーごとに保持件数と理由を出す。

## カテゴリー別

- App Paths: `(Default)` を引用符を外したパスとして、そのままと `+.exe` を確認。無い・空なら `Unknown`。
- スタートアップ: `TryResolveExecutable`。`RunOnce` / `RunOnceEx` / Policies は対象外。
- フォント: ファイル名だけなら `FOLDERID_Fonts` 配下。`|` を含めば `Unknown`。Type 1 Installer・FontSubstitutes は対象外。
- 関連付け: ProgID が Classes に無いか、全動詞の command が欠損確定の場合だけ。`DelegateExecute` / `DropTarget` / command 無しは `Unknown`。変更後に `SHChangeNotify(SHCNE_ASSOCCHANGED)` を 1 回呼ぶ。
- アンインストール: `WindowsInstaller=1`、MsiQueryProductState で既知の GUID、`SystemComponent=1`、`ParentKeyName` / `ReleaseType` 付きは除外。`UninstallString`（と `QuietUninstallString`）が欠損確定、`InstallLocation` が空か欠損確定、`DisplayIcon` がファイルならそれも欠損確定の場合だけ候補。
- 共有 DLL: 値の名前を完全パスとして判定。

## バックアップと削除

- 正本は型付き JSON（reg.exe export は値単位で戻せず不採用）。形式の版、UTC 時刻、SID、項目ごとのハイブ・表示（64/32）・キーパス・値の名前・型・元バイト列（base64）・カテゴリーを記録する。キー単位はサブツリー全体。ACL は保存しない。
- 保存先 `%ProgramData%\Lumin4ti\backups\registry\<UTC時刻>-<GUID>.json`（`ProtectedBackupStorage.WriteNewAtomically`、Administrators と SYSTEM のみ）、結果は `<id>.result.json`。自動削除しない。列挙上限 1000 件超は fail closed。
- 実行順（lease、`showProcessing:true`）: 1. 走査世代を CAS で照合 2. 復元ポイント（既定オン、失敗時は続行確認） 3. ハンドルを開いたまま再照合し、ハッシュ（キーはサブツリーの正規化ハッシュと LastWriteTime）が違うか欠損が確定しなければ `Retained` 4. 通った項目だけをバックアップ（WriteThrough、読み戻し検証）。書けなければ 1 件も削除せず `Failed` 5. 同じハンドルで直前に再照合して削除（子から再帰、リンク拒否） 6. 結果ファイルを書く。
- 同じ物理キーは `NtQueryKey(KeyNameInformation)` で判定して二重処理しない。選択済みキーの配下は飛ばす。
- 結果: 削除・`Retained` は成功。アクセス拒否・リンク・TrustedInstaller 所有は項目 `Skipped`・全体 `Partial`。理由は `CleanupMessage` の符号、代表例は最大 8 件。

復元とテストは [registry-cleaner-restore.md](registry-cleaner-restore.md) を読む。
