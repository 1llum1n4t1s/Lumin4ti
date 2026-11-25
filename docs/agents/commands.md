# ビルド・テスト・実行

[AGENTS.md](../../AGENTS.md) から分割した詳細。

## ビルド・テスト・実行

```bash
dotnet build Lumin4ti.slnx           # ビルド (0 warnings を維持する方針)
dotnet test Lumin4ti.slnx            # 全テスト (MSTest)
# 単一テストクラス/メソッド:
dotnet test Lumin4ti.slnx --filter "FullyQualifiedName~RegistryToggleTests"
dotnet test Lumin4ti.slnx --filter "FullyQualifiedName~UwpBackgroundToggleTests"
# 実行 (通常起動は UAC 昇格が入る):
./src/Lumin4ti.UI/bin/Debug/net11.0-windows10.0.20348.0/Lumin4ti.UI.exe
```

- TFM は `net11.0-windows10.0.20348.0` ([Directory.Build.props](../../Directory.Build.props))。UWP パッケージ列挙 (WinRT `PackageManager`) のため Windows SDK 付き。
- `.github/workflows/dependency-validation.yml` は main への push・全 PR・手動実行で Windows 上の R2 転送 fixture 検証・restore・Release build・test を行う。`release.yml` も署名・配信の前に同じ検証を行う `test` ジョブを置き、失敗すると配信ジョブは始まらない。CI に頼らず、変更後もローカルの `dotnet build` (0 warnings) と `dotnet test` を必ず両方通す。
- **バージョン (`Directory.Build.props` の `<Version>`) は `/vava` 経由でのみ更新**。コード修正のついでに触らない。
- 復元・サービス補償・終了時排他・状態表示などの操作フローを変更したら、`pwsh -NoProfile -File scripts/verify-review-fixes.ps1` も実行する。対象ケースと再現方法は [WORKFLOWS.md](../../scripts/verification/WORKFLOWS.md) を参照し、成果物 `local-release/review-fixes-verification.json` を保持する。検証の境界は [DESIGN.md](../design/architecture.md#主要コンポーネント) に記載し、実 GUI の E2E 検証と区別する。
