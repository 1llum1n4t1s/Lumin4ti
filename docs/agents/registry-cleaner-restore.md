# レジストリクリーナーの復元とテスト

[registry-cleaner-guards.md](registry-cleaner-guards.md) の続き。

## 復元

lease 内で走査結果を失効させて行う。版・SID・パスの許可表（各カテゴリーの根の配下）・件数とサイズの上限を検証し、違反と HKCU の SID 不一致は拒否する。対象が無ければ書き戻して `Restored`、同値なら `AlreadyPresent`、別の値なら上書きせず `Conflict`（全体 `Partial`）。

## テスト

実レジストリ・COM を変えない。抽象は `IRegistryCleanerKey`、`IRegistryFileProbe`、`IRegistryPathResolver`、`IRegistryBackupStore`、`IMsiProductState`。実機では走査だけを行い、削除・復元を実機で確かめるときは本人の許可を得て自作のダミー登録で行う。
