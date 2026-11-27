# クリーンアップの基点・カスタムフォルダーの信頼検査

[cleanup-rules-and-deletion.md](cleanup-rules-and-deletion.md) から分割した詳細。

- 利用者スコープの規則（HKCU・プロファイル・SQLite・Shell）は、対話利用者と昇格利用者の SID が異なる場合は全て無効にして理由を表示する。
- カスタムフォルダーは `settings.json` が非昇格プロセスからも書けるため、実行時にも `CustomFolderGuard` で検査する。次を拒否する。
  - ドライブ直下、Windows・Program Files・ProgramData（配下・自身・祖先）。
  - 他の利用者のプロファイルとそれを束ねるフォルダー、自分のプロファイル直下・AppData 系・Documents など。
  - パスに認証情報の名前（`.ssh`・`Credentials`・拒否表のファイル名など）を含むもの。
  - ブラウザー・メールのプロファイル基点（Chrome 等の User Data、Firefox・Thunderbird のプロファイル、Opera、Outlook）の自身・祖先・子孫。
  - 8.3 短縮名（展開できない純関数のため）。
- Downloads は許可のまま。カスタムフォルダーは `UserFiles` として個別選択と再確認を必須にし、拒否したものは既定未選択にする。許可したフォルダーの子孫でも、拒否表に当たる名前は `KeepCredentials`（`CleanupTarget.CustomFolder` が付ける）で走査にも削除にも入れず残す（`FileCleanupEngine.KeepsAsCredential`）。
- 規則の基点の環境変数は `CleanupBaseTrust` で検証してから使う。利用者が差し替えられるため、プロファイル系は `USERPROFILE` ではなく OS の既知フォルダーが HKLM の `ProfileList` と一致した場合だけ信頼し、基点がそのプロファイルの内側でなければ使わない。
- `TEMP`・`TMP` だけは別ドライブも許すが、保護された場所・他人のプロファイル・ドライブ直下・高度クリーンの枝刈りの表（`$Recycle.Bin`・`Recovery`・`Windows.old` など）を拒否する。対話利用者が自力で中身を変更できるフォルダーだけを使う（再解析点を辿らず開き、所有者が利用者、または利用者・Users・Authenticated Users・Everyone への許可 ACE が Modify 相当で拒否 ACE に当たらない。判定不能は拒否）。
- 展開後の基点に `%` が残る場合も使わない。展開済みの実パスは `IsActualPath` の印でエンジン・走査・領域が再展開しない。
- 機械側の変数（`ProgramData`・`SystemRoot`・`ProgramFiles` など）は OS が示す既定の場所と長い名前で一致したときだけ使う。確認できなければ規則を使わず、理由を文言符号（`BaseOutsideProfile`・`BaseProtectedLocation`・`BaseMachineMismatch`）で返す。
