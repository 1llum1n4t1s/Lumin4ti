# 旧配置からの移行

[release-process.md](release-process.md) から分割した詳細。

`WindowsPerMachineMigration`は通常の自己昇格より前に、旧`%LOCALAPPDATA%\Lumin4ti`版から信頼済みPerMachine版へ移行する。未導入なら固定URLのMSIを取得し、署名と発行元を確認してからインストールする。検証済みMSIの読取ハンドルを保持して、検証からインストール完了まで書換え・削除を防ぐ。取得には呼び出し元のキャンセルと連動した5分の期限を設け、HTTPヘッダー取得と本文コピーに同じ期限を渡す。`ResponseHeadersRead`の`HttpClient.Timeout`だけでは本文待ちを制限できないためである。

インストール先は`VELOPACK_INSTALLDIR=<Program Files>\Lumin4ti`を明示する。署名・MSI登録・`.msi-installed`を確認できる既知の誤配置（ドライブ直下の`Lumin4ti`と`Program Files\ゆろち\Lumin4ti`）も固定MSIで配置を修復する。同一ProductCode/Versionで通常の`/i`が配置を変えない場合だけ`REINSTALL=ALL REINSTALLMODE=vamus`で全再配置・再キャッシュする。任意のカスタム配置は自動回収しない。

信頼済みPerMachine起動側が旧プロセスの終了を待って、既知の旧本体・Updater・キャッシュ・HKCUアンインストール登録・ユーザーショートカットだけを回収する。`Update.exe`や`packages`だけが残る部分移行も次回起動で再回収し、ユーザー書込み可能な旧Updaterは実行しない。設定・ログ・保護バックアップを保持する。現行ログ保存先は旧PerUser配置と同じ基点を使うため、旧配置の回収では直下の`logs`を除外し、ログだけが残る状態を回収完了として扱う。ログ自体の移行は[状態と永続化](data-model.md#状態と永続化)の独立した処理が担う。ショートカット削除後は`SHChangeNotify`でStartメニューの表示キャッシュを更新する。
