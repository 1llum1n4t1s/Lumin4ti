# レジストリクリーナーの判定と削除・復元の契約

[registry-cleaner.md](../design/registry-cleaner.md) の詳細。判定・バックアップ・復元を変える前に読む。

## 判定（共通）

- `Exists` / `ConfirmedMissing` / `Unknown` の 3 値で判定し、`ConfirmedMissing` だけを候補にする。確定の基準は `StartupCommandParser.IsConfirmedMissing` と同じ（準備済みの固定ドライブ、祖先に再解析点なし、OS が FileNotFound / DirectoryNotFound）。
- `Unknown`: UNC・`\\?\` 等、リムーバブル・ネットワーク・未準備・不在ドライブ、アクセス拒否・I/O エラー、相対名（カテゴリーの例外を除く）、ラッパー経由（`Wrappers`）、曖昧なコマンドライン（`TryResolveExecutable`）、REG_SZ / REG_EXPAND_SZ 以外。
- 環境変数は許可名だけを既知フォルダーで展開する。機械側: `SystemRoot` `windir` `SystemDrive` `ProgramFiles` `ProgramFiles(x86)` `ProgramW6432` `CommonProgramFiles` `CommonProgramFiles(x86)` `CommonProgramW6432` `ProgramData` `ALLUSERSPROFILE`。利用者側（HKCU の値のみ、`CleanupBaseTrust` の ProfileList 照合を通す）: `USERPROFILE` `LOCALAPPDATA` `APPDATA`。それ以外・展開後に `%` が残れば `Unknown`。
- WOW6432Node の値は `System32` を SysWOW64 と System32、`Sysnative` を System32、`ProgramFiles` / `CommonProgramFiles` を 64 ビット版と (x86) 版の両方で確認し、全候補で欠損確定の場合だけ対象にする。
- 保護（保持）: `%SystemRoot%` 配下（フォントだけ `%windir%\Fonts` を許可）、`Program Files*\Windows*`、`Program Files\WindowsApps`、`%LOCALAPPDATA%\Microsoft\WindowsApps`、TrustedInstaller 所有のキー。
- キーは各階層を `REG_OPTION_OPEN_LINK` で開いてリンクを拒否し、同じハンドルで削除する。HKCU は `HKEY_USERS\<SID>` を基点にし（利用者の Classes はリンクの `Software\Classes` を開かず `HKEY_USERS\<SID>_Classes` を直接開く。32 ビット表示の App Paths は 64 ビット側と共有のため走査しない）、対話利用者と昇格利用者の SID が違えば HKCU カテゴリーを無効にして理由を出す（`InteractiveScope`）。他の利用者のハイブは対象外。
- `Unknown`・保護は選択肢に出さず、カテゴリーごとに保持件数と理由を出す。

## カテゴリー別

- App Paths: `(Default)` を引用符を外したパスとして、そのままと `+.exe` を確認。無い・空なら `Unknown`。
- スタートアップ: `TryResolveExecutable`。`RunOnce` / `RunOnceEx` / Policies は対象外。
- フォント: ファイル名だけなら `FOLDERID_Fonts` 配下。`|` を含めば `Unknown`。Type 1 Installer・FontSubstitutes は対象外。
- 関連付け: ProgID が Classes に無いか、全動詞の command が欠損確定の場合だけ。`DelegateExecute` / `DropTarget` / command 無しは `Unknown`。変更後に `SHChangeNotify(SHCNE_ASSOCCHANGED)` を 1 回呼ぶ。
- アンインストール: `WindowsInstaller=1`、MsiQueryProductState で既知の GUID、`SystemComponent=1`、`ParentKeyName` / `ReleaseType` 付きは除外。`UninstallString`（と `QuietUninstallString`）が欠損確定、`InstallLocation` が空か欠損確定、`DisplayIcon` がファイルならそれも欠損確定の場合だけ候補。
- 共有 DLL: 値の名前を完全パスとして判定。

## バックアップと削除

型付き JSON で保護領域へ保存し、書けなければ 1 件も削除しない。lease の中で再照合してから同じハンドルで削除する。形式・保存先・実行順・結果は [registry-cleaner-backup-deletion.md](registry-cleaner-backup-deletion.md) を読む。

復元とテストは [registry-cleaner-restore.md](registry-cleaner-restore.md) を読む。
