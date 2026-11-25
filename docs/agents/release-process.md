# リリースの実行手順

[cloudflare-r2-sigilune.md](cloudflare-r2-sigilune.md) から分割した詳細。

通常は GitHub Actions で実行する（本人の PC は不要）。`/vava` が版を上げた main へ軽量タグ `v<version>` を付けて push すると、そのタグの push で `release.yml` が自動で起動する（先に `guard` ジョブが、軽量タグ・main の祖先・`Directory.Build.props` の版との一致を Secrets なしで確かめる）。`workflow_dispatch` は同じタグを指定した再実行にだけ使う（`skip_upload=true` でビルド・署名・公開前検証だけを行い、成果物を Actions に残せる）。ブランチを指定した実行は、`guard` ジョブと Environment `release` の tag ポリシー `v[0-9]*.[0-9]*.[0-9]*` が拒否する。GitHub Release は作らない。

```powershell
gh workflow run release.yml --repo 1llum1n4t1s/Lumin4ti --ref v<version> -f skip_upload=false
```

ローカルで実行する場合は、リポジトリルートで Sigilune のクラウドログインを事前確認し、成功した場合だけ実行する。失敗時は「Sigilune でクラウドにログインできなかったよ。sigilune status で登録を確認してから /vava を再実行してね」。ログインを自動再試行せず、署名を省略して続行しない。

```powershell
$sigilune = if ($env:SIGILUNE_EXE) { $env:SIGILUNE_EXE } else { Join-Path $env:USERPROFILE '.local\Sigilune\bin\sigilune.exe' }
& $sigilune login
if ($LASTEXITCODE -eq 0) { pwsh -NoProfile -File scripts/release-local.ps1 } else { throw 'Sigilune でクラウドにログインできなかったよ。sigilune status で登録を確認してから /vava を再実行してね' }
```
