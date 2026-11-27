# クリーンアップの規則データ

[cleanup-rules-and-deletion.md](cleanup-rules-and-deletion.md) から分割した詳細。

- 対象は `Definitions/*.json` の規則だけで、アセンブリへ埋め込む。外部ファイルの読み込み・更新経路は作らない。読み込みは source generator と `UnmappedMemberHandling.Disallow` で行う。
- データに SQL・コマンド・実行ファイルを書かせない。`sqlite` の操作名と `handler` 名は C# の列挙に限る。`clearTable` が指定できるのは DB パス・表名・期待する列名だけで、login / password / token / credential / account / card / key / session を含む表名は拒否する。
- 各規則に根拠 (`evidence`) を必須とする。tier A は公式文書または製品の公開ソース 1 件、tier B は出典ホストの異なる独立した二次資料 2 件、tier L は旧版のグループが既に対象にしていた場所の継承（`legacy.` で始まる id で `legacy` が空でない規則だけ。URL を持たず、新しい場所を足す根拠にはならない）。どれも満たさなければ収録しない。
- 他社製品の設定ファイル・項目一覧は、値の転記・機械変換・根拠件数への算入をしない。他社製品の項目一覧や対応表はリポジトリへ入れない。
- 影響度は `junk` / `trace` / `signout` / `rebuild` / `userFiles`。`recommended=true` にできるのは `junk` だけ。`signout` は cookies 分類だけに許し、Cookie を消す操作は必ず `signout` にする。`userFiles`（再取得できない利用者データ）は既定オフで、「おすすめ」にも「すべて選択」にも含めず、個別選択と専用の再確認を必須にする。
- 既定の選択は「利用者の上書き → 規則の `recommended`」の順。Cookie とフォーム履歴は既定オフ。「すべて選択」は Cookie を含むため、サインアウトを伴う警告を表示する。
- 検証器 (`CleanupRuleValidator`) はテストと起動時の両方で動き、違反した規則だけを無効にしてログへ残す。確認内容は、id の一意性、許可された環境変数、絶対パス、`..` 禁止、基点フォルダー直下の `contents` 禁止、規則間の完全重複禁止、認証情報の拒否表、registry の範囲、サービスの許可表。
- 拒否表は保存パスワード・認証情報・鍵・アプリ設定を対象外にする。ログイン情報のストア、資格情報・鍵・トークンのフォルダーとファイル、暗号化ストレージ、`Storage2` などを含む。ゲームのセーブ、チャットのメッセージ DB、クラウド同期の同期ルートと同期 DB、セキュリティ製品の隔離領域・検出定義・ライセンス、AI・開発ツールの会話履歴・拡張・認証も入れない。
- レジストリは HKCU の `Software\` 配下に限り、`Classes`・`Policies`・`Run`・`RunOnce` は対象にしない。HKLM は固定ハンドラーだけが扱う。各階層を `REG_OPTION_OPEN_LINK` で開き、リンクなら拒否する。値の削除は同じハンドルで行う。
- 規則を増やすときは `Definitions/*.json` へ追加し、個別クラスを作らない。ブラウザーは基点と種別から規則を生成する（`CleanupEngineExpander`）ので手書きで増やさない。規則数・収録数は文書へ書かない。
