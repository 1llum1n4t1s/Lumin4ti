# 設定変更の範囲

[DESIGN.md](../../DESIGN.md) から分割した詳細。

## 設定変更の範囲

`NtpConfigAction` は、有効なNTPクライアントと同期モード `NTP` を確認してから、手動ピアの `NtpServer` だけを `ntp.jst.mfeed.ad.jp,0x9` へ変更する。ポリシー管理のサーバー、無効なクライアント、`AllSync`・`NT5DS`・`NoSync`・不明な同期モードではサービス停止前に中止する。`Type` と `AnnounceFlags` を維持することで、ドメイン同期や時刻サーバーとしての役割を変更しない。停止・復帰は`WindowsServiceControl`の共通補償フローを使い、停止失敗時も`finally`で復帰を試みる。

MMAgentのスイッチはONが機能有効、OFFが無効を表す。アプリ起動プリフェッチのcmdletが非対応なら、`MmAgentRegistryFallback` が `EnablePrefetcher` のアプリ起動ビットだけを更新し、ブート側のビットを保持する。未設定は既定値3として扱い、0〜3以外やDWORD以外は変更しない。旧 `registry/mmagent-launch-prefetch.json` は保持するが復元には使わない。DWORD全体を戻すとブート側の外部変更まで巻き戻すためである。非対応時は古い `Get-MMAgent` 値による冪等成功より代替書き込みを優先し、書き込み後も実状態が異なれば `Partial` と再起動の案内を返す。

MMAgentの状態は一括再読込ごとに完了済みキャッシュと操作後のoverrideを破棄し、外部変更を取り込む。同じ再読込内の並行取得は共有ロードへ集約する。待機側がタイムアウトしても未完了の共有ロードは維持し、再アクティブによる重複起動を防ぐ。機能切替時の他機能の無効化と、OSが非対応と判定した機能の記録は別に維持する。
