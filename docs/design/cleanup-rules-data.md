# クリーンアップの規則データ

[cleanup-design.md](cleanup-design.md) から分割した詳細。

通常クリーンの対象は `Lumin4ti.Core/Services/Windows/Cleanup/Definitions/*.json` の規則で、アセンブリへ埋め込む。外部ファイルの読み込み・更新経路は作らず、利用者が書ける定義を昇格プロセスが読むことはない。読み込みは source generator と `UnmappedMemberHandling.Disallow` で行う。

- 規則は分類（windows / storeApps / browserCache / browserTrace / cookies / traces / components / apps）、種別、影響度、検出条件、対象、実行中を確認するプロセス、停止するサービス、根拠を持つ。対象は `contents`・`files`・`registryValues`・`registryTree`・`sqlite`・`iniValues`・`handler` のいずれかで、SQL・コマンド・実行ファイルはデータに書かせない。
- 影響度は `junk`・`trace`・`signout`・`rebuild`・`userFiles`。再生成可能でサインアウトや履歴の喪失を伴わない `junk` だけが「おすすめ」になれる。Cookie とフォーム履歴は既定オフで、利用者が明示的に選んだときだけ削除する。受信ファイルなど再取得できない `userFiles` は「すべて選択」にも含めず、個別選択と専用の再確認を要する。保存パスワード・認証情報・鍵・アプリ設定は規則に入れない。
- 根拠は tier A（公式文書または製品の公開ソース）、tier B（独立した二次資料 2 件）、tier L（旧版が対象にしていた場所の継承）。どれも満たさない項目は収録しない。他社製品の設定ファイルを転記・変換せず、独自に裏付ける。
- ブラウザーは基点と種別（キャッシュ・履歴・フォーム・セッション・ブックマークの控え・Cookie）から規則を生成する（`CleanupEngineExpander`）。
- 検証器（`CleanupRuleValidator`）は、id の一意性、許可された環境変数、絶対パス、基点直下の禁止、規則間の完全重複、認証情報の拒否表、registry の範囲、サービスの許可表をテストと起動時に確認し、違反した規則だけを無効にしてログへ残す。
- 表示名は製品名を翻訳せず、種別を書式キー（「{0} のキャッシュ」など）で組み立てる。Windows 項目は `Text.Cleanup.Rule.{id}` を持つ。

## 基点の信頼確認

規則の基点の環境変数は `CleanupBaseTrust` で、OS が別経路で持つ値と照合してから使う。プロファイル系は既知フォルダーが HKLM の `ProfileList` と一致するプロファイルの内側だけを許し（`TEMP`・`TMP` は別ドライブも許すが、保護された場所・他人のプロファイル・ドライブ直下は拒否）、機械側は OS が示す既定の場所と一致するものだけを許す。確認できなければ規則を使わず、理由を表示する。
