# 変更履歴

Git のバージョン記録・コミット差分と既存の変更履歴をもとに、確認できた版ごとの変更点をまとめています。「Git 記録日」は公開日ではありません。番号の欠番だけから未確認のリリースは補っていません。

## 未リリース

## [1.0.37] — Git 記録日: 2026-09-12

- サインイン時の自動クリーンアップを最高権限で実行し、サービス停止が必要な項目も処理できるよう改善
- GUI のメンテナンス操作とサインイン時クリーンアップの同時実行を防止
- クリーンアップ後のサービス開始が一時的に失敗した場合、Windows の自動回復を待って稼働状態を確認するよう改善

## [1.0.36] — Git 記録日: 2026-09-06

- メンテナンスコマンドが起動直後に生成する子プロセスも、アプリ終了時の終了管理に含めるよう修正
- アプリ起動プリフェッチの復元バックアップが破損している場合、設定を上書きせずエラーを表示するよう修正

## [1.0.35] — Git 記録日: 2026-09-01

- 設定保存とクリーンアップの堅牢性を向上
- 未接続デバイスを選択削除できるよう改善
- 起動時の更新確認を確実に実行

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/30283e8bfb52b7eaf9c98e9547d0eedbcb0033a5) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/b565fef5db32cd02afda0688a3265cb9c9e97aff...30283e8bfb52b7eaf9c98e9547d0eedbcb0033a5)。

## [1.0.34] — Git 記録日: 2026-08-31

- NuGetロックファイルを正規化

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/b565fef5db32cd02afda0688a3265cb9c9e97aff) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/41ade1532a8bdbc32297b4887415c7c8a1117966...b565fef5db32cd02afda0688a3265cb9c9e97aff)。

## [1.0.33] — Git 記録日: 2026-08-30

- クリーンアップ処理の安全性と状態判定を強化
- リンク切れのスタートアップと関連付け整理を復元
- NuGet依存関係を更新

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/41ade1532a8bdbc32297b4887415c7c8a1117966) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/c9566b12ac6c56888ed612295facb71df0c8afd5...41ade1532a8bdbc32297b4887415c7c8a1117966)。

## [1.0.32] — Git 記録日: 2026-08-30

- NuGet依存関係を更新
- Windows操作の安全性と状態判定を強化
- 未接続デバイスの選択削除タブを追加

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/c9566b12ac6c56888ed612295facb71df0c8afd5) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/27b761c6f4d1703d8a19765b717f0e1846580302...c9566b12ac6c56888ed612295facb71df0c8afd5)。

## [1.0.31] — Git 記録日: 2026-08-16

- 検出できた削除候補だけをチェック欄に表示

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/27b761c6f4d1703d8a19765b717f0e1846580302) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/c4a3c9c3b9e4b49f6aa6c8e96699b287baf0e413...27b761c6f4d1703d8a19765b717f0e1846580302)。

## [1.0.30] — Git 記録日: 2026-08-15

- 設定読込失敗と状態再読込の競合を安全側に倒す
- クリーンアップを再生成可能なキャッシュとログに限定

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/c4a3c9c3b9e4b49f6aa6c8e96699b287baf0e413) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/2a5b5b33fb61a45a10f17844f60c5bdeb2610e94...c4a3c9c3b9e4b49f6aa6c8e96699b287baf0e413)。

## [1.0.29] — Git 記録日: 2026-08-12

- 最近使ったファイルの履歴を実体パスの直接指定で確実に消す

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/2a5b5b33fb61a45a10f17844f60c5bdeb2610e94) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/f24a1421d64e62d43d9c1c5d3806c4b4211f6308...2a5b5b33fb61a45a10f17844f60c5bdeb2610e94)。

## [1.0.28] — Git 記録日: 2026-08-12

- クリーンアップ対象と定期実行項目をチェックリストで選べるようにする
- デバッガ接続中の起動で昇格せず継続できるようにする

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/f24a1421d64e62d43d9c1c5d3806c4b4211f6308) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/df666c1cc9c52e14a39192a224eb101b747975b2...f24a1421d64e62d43d9c1c5d3806c4b4211f6308)。

## [1.0.27] — Git 記録日: 2026-08-12

- winget更新で除外のみのとき失敗扱いにしないよう修正する

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/df666c1cc9c52e14a39192a224eb101b747975b2) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/da49b200f2d6a53338a9a2efffe5823a05b9ab05...df666c1cc9c52e14a39192a224eb101b747975b2)。

## [1.0.26] — Git 記録日: 2026-08-11

- サービス停止中のキャンセルで再開ハンドルを失う不具合を修正する
- 依存パッケージを更新する (VelopackUpdateDialog.Avalonia 1.0.14)
- 依存パッケージを更新する (Avalonia 12.1.1)
- 依存パッケージを更新する (SuperLightLogger 1.0.12)

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/da49b200f2d6a53338a9a2efffe5823a05b9ab05) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/92bfe03847511c5d8003686588656b446e924745...da49b200f2d6a53338a9a2efffe5823a05b9ab05)。

## [1.0.25] — Git 記録日: 2026-08-11

- クリーンアップと修復をタブ分割し、迷子の nul ファイル削除とサインイン時自動削除を追加する
- レジストリ復元バックアップのレガシーを利用者スコープへ一度だけ移行する
- キャンセル不可な操作を保護し、OS シャットダウン時も補償完了を待つ
- ビルド品質ゲートを強化し、屋号表記を Kagayoi に統一する

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/92bfe03847511c5d8003686588656b446e924745) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/5fb5a67f253ebe3a4204009c35c5f80fb76b3edf...92bfe03847511c5d8003686588656b446e924745)。

## [1.0.24] — Git 記録日: 2026-07-29

- 失敗の原因を必ずログへ残し、選べない選択肢を取り除く

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/5fb5a67f253ebe3a4204009c35c5f80fb76b3edf) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/740aefd7b84176e0bc55d34b6bcc23d3517e716a...5fb5a67f253ebe3a4204009c35c5f80fb76b3edf)。

## [1.0.23] — Git 記録日: 2026-07-28

- 一時ファイル掃除をグループ別ボタンにし、アプリ別GPU指定の一括削除を追加する
- 依存パッケージを更新する (MSTest 4.3.3)

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/740aefd7b84176e0bc55d34b6bcc23d3517e716a) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/fd38e0c04992a49f4af1e161e101f86e436bf519...740aefd7b84176e0bc55d34b6bcc23d3517e716a)。

## [1.0.22] — Git 記録日: 2026-07-28

- インストール方式の移行と各メンテナンス処理のログを拡充し、失敗原因を確認しやすく改善。
- 外部コマンドの待機・出力回収・文字化けを修正し、長時間処理に対応。

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/fd38e0c04992a49f4af1e161e101f86e436bf519) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/14af825b15e982e9b5a5c506dbc9b5dffa5f8f7b...fd38e0c04992a49f4af1e161e101f86e436bf519)。

## [1.0.21] — Git 記録日: 2026-07-28

- MMAgent の切り替え不能を回避し、外部コマンドの待ち方を直す

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/14af825b15e982e9b5a5c506dbc9b5dffa5f8f7b) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/1848d9451dfbe26af741ffcea64336f523098813...14af825b15e982e9b5a5c506dbc9b5dffa5f8f7b)。

## [1.0.20] — Git 記録日: 2026-07-27

- 検索・入力・ウィジェットの不調を直す再登録アクションを追加する

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/1848d9451dfbe26af741ffcea64336f523098813) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/11f23314a116bfc306da14f39e25dc9d227201a2...1848d9451dfbe26af741ffcea64336f523098813)。

## [1.0.19] — Git 記録日: 2026-07-27

- ユーザー登録だけ残ったゴーストを現在ユーザーからの解除で消す
- 依存パッケージを更新する (SuperLightLogger 1.0.10 / VelopackUpdateDialog.Avalonia 1.0.13)

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/11f23314a116bfc306da14f39e25dc9d227201a2) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/751a4250156996535542c743171a3cccce553610...11f23314a116bfc306da14f39e25dc9d227201a2)。

## [1.0.18] — Git 記録日: 2026-07-27

- ゴースト修復の待ち時間を短縮する

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/751a4250156996535542c743171a3cccce553610) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/2eb2c212d1ce167dbd6f687632450441ae15883a...751a4250156996535542c743171a3cccce553610)。

## [1.0.17] — Git 記録日: 2026-07-27

- 削除できないシステムアプリのゴーストを本体の入れ直しで修復する

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/2eb2c212d1ce167dbd6f687632450441ae15883a) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/a91ad284ebca1b2cde2a0af0a1937d4a89b2babf...2eb2c212d1ce167dbd6f687632450441ae15883a)。

## [1.0.16] — Git 記録日: 2026-07-27

- ゴースト削除の対象をスタート表示項目に限定し削除結果を検証する
- リリース後修正: CA1416 警告の解消と R2 クリーンアップの key 参照バグ修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/a91ad284ebca1b2cde2a0af0a1937d4a89b2babf) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/b0d2fc8e10a9ad1684bdf762d0410f3e00073e81...a91ad284ebca1b2cde2a0af0a1937d4a89b2babf)。

## [1.0.15] — Git 記録日: 2026-07-27

- スタートメニューのゴーストアプリ登録を削除するアクションを追加
- 配信ドメインを nephilim.jp から kagayoi.com へ移行

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/b0d2fc8e10a9ad1684bdf762d0410f3e00073e81) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/6619348273ff182e6473a95c0e96c820dd27d339...b0d2fc8e10a9ad1684bdf762d0410f3e00073e81)。

## [1.0.14] — Git 記録日: 2026-07-23

- クイックアクセス並べ替えをパス昇順で復活

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/6619348273ff182e6473a95c0e96c820dd27d339) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/4982494d412924c767a1cd9c2d769f6939d6a9d9...6619348273ff182e6473a95c0e96c820dd27d339)。

## [1.0.13] — Git 記録日: 2026-07-22

- クイックアクセス並べ替え機能を削除しショートカットアイコン処理を簡素化

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/4982494d412924c767a1cd9c2d769f6939d6a9d9) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/03b14b45f0242b6fcdca2b3c30fb20e2405649ba...4982494d412924c767a1cd9c2d769f6939d6a9d9)。

## [1.0.12] — Git 記録日: 2026-07-22

- クイックアクセスとタスクバーアイコンを修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/03b14b45f0242b6fcdca2b3c30fb20e2405649ba) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/ef701a3cb9ccefd7e87ee2afec4bdad09ba63284...03b14b45f0242b6fcdca2b3c30fb20e2405649ba)。

## [1.0.11] — Git 記録日: 2026-07-22

- 電源構成機能を削除しクイックアクセスとアイコンを修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/ef701a3cb9ccefd7e87ee2afec4bdad09ba63284) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/395afa27d81b9e6a77406edd258497472fe68084...ef701a3cb9ccefd7e87ee2afec4bdad09ba63284)。

## [1.0.10] — Git 記録日: 2026-07-21

- MSI移行時の引数クォートを修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/395afa27d81b9e6a77406edd258497472fe68084) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/8b00b14ef8def141d057c64923b116c301faa6cc...395afa27d81b9e6a77406edd258497472fe68084)。

## [1.0.9] — Git 記録日: 2026-07-21

- MSI配置修復と旧インストール回収を強化

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/8b00b14ef8def141d057c64923b116c301faa6cc) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/44ddf5c5886021fee2d24b063b2124281e647c56...8b00b14ef8def141d057c64923b116c301faa6cc)。

## [1.0.8] — Git 記録日: 2026-07-21

- クイックアクセスとMSI移行の残骸回収を修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/44ddf5c5886021fee2d24b063b2124281e647c56) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/6a62f2d7f83d2de6bbe96fea47d0ea97bff5e246...44ddf5c5886021fee2d24b063b2124281e647c56)。

## [1.0.7] — Git 記録日: 2026-07-21

- PerMachine MSIへの安全な移行に対応

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/6a62f2d7f83d2de6bbe96fea47d0ea97bff5e246) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/e7c272363bfcf1baeb21e9c86fbd7a54e015f95d...6a62f2d7f83d2de6bbe96fea47d0ea97bff5e246)。

## [1.0.6] — Git 記録日: 2026-07-21

- 更新処理とWindows設定の安全性を改善

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/e7c272363bfcf1baeb21e9c86fbd7a54e015f95d) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/19de9c184b5084cefc54baa2cfa44094964ab5d3...e7c272363bfcf1baeb21e9c86fbd7a54e015f95d)。

## [1.0.5] — Git 記録日: 2026-07-18

- スタートメニュー移行とタイムアウト処理を修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/19de9c184b5084cefc54baa2cfa44094964ab5d3) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/a08f256daf432caa2e4b3f0b89b9bd86da18074f...19de9c184b5084cefc54baa2cfa44094964ab5d3)。

## [1.0.4] — Git 記録日: 2026-07-18

- MMAgent互換性とショートカット配置を修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/a08f256daf432caa2e4b3f0b89b9bd86da18074f) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/46716b8c0dd0244bcf899604b55626bc6b43b6a7...a08f256daf432caa2e4b3f0b89b9bd86da18074f)。

## [1.0.3] — Git 記録日: 2026-07-18

- メンテナンス処理の安全性と復元性を改善

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/46716b8c0dd0244bcf899604b55626bc6b43b6a7) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/dfa1e3f38f3da1f9f904185dc726400c01975f5b...46716b8c0dd0244bcf899604b55626bc6b43b6a7)。

## [1.0.2] — Git 記録日: 2026-07-10

- MMAgent トグルの冪等化とエラーメッセージ修正

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/dfa1e3f38f3da1f9f904185dc726400c01975f5b) / [変更差分](https://github.com/1llum1n4t1s/Lumin4ti/compare/a01fed8f33b5d511e3ed754623c1678f53d9894e...dfa1e3f38f3da1f9f904185dc726400c01975f5b)。

## [1.0.1] — Git 記録日: 2026-07-09

- Lumin4ti 本体一式と Cloudflare R2 配信を追加

出典: [版の記録](https://github.com/1llum1n4t1s/Lumin4ti/commit/a01fed8f33b5d511e3ed754623c1678f53d9894e)。
