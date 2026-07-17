using System.Diagnostics;
using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>
/// エクスプローラー (シェル) を再起動して、シェルキャッシュに依存する変更を反映させる。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExplorerRestarter
{
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(20);

    public static void Restart()
    {
        // bare exe 名の偽プロセスを終了しないよう、正規の実体を先に確定・署名検証する。
        var explorerPath = ResolveExplorerPath();
        if ((File.GetAttributes(explorerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Windows Explorer が再解析ポイントのため使用できません。");
        }

        if (!ExecutableTrustVerifier.TryVerify(explorerPath, "Microsoft Windows", out var trustFailure))
        {
            throw new InvalidOperationException(
                $"Windows Explorer の署名を検証できません: {trustFailure}");
        }

        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        var stoppedProcessIds = new HashSet<int>();

        LoggerBootstrap.Log.Info("Explorer を再起動します");
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                var imagePath = process.MainModule?.FileName;
                if (!IsCurrentSessionExplorer(
                        process.SessionId,
                        imagePath,
                        currentSessionId,
                        explorerPath))
                {
                    continue;
                }

                stoppedProcessIds.Add(process.Id);
                process.Kill();
                _ = process.WaitForExit(milliseconds: 5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 列挙後に終了したプロセスだけは無視できる。現在セッションの正規 Explorer を
                // 開けない場合は replacement 検出が失敗し、呼び出し側へ明示される。
            }
            finally
            {
                process.Dispose();
            }
        }

        if (stoppedProcessIds.Count == 0)
        {
            throw new InvalidOperationException(
                "現在のセッションで署名済み Windows Explorer を特定できませんでした。");
        }

        // 昇格プロセスから explorer.exe を CreateProcess すると high token を継承する。
        // Windows のシェル監視に再起動を任せ、同一セッションに正規の置換プロセスが現れたことを確認する。
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < RestartTimeout)
        {
            if (HasReplacementExplorer(
                    currentSessionId,
                    explorerPath,
                    stoppedProcessIds))
            {
                return;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException(
            "現在のセッションで Windows Explorer が自動再起動しませんでした。タスク マネージャーから explorer.exe を起動してください。");
    }

    internal static bool IsCurrentSessionExplorer(
        int processSessionId,
        string? imagePath,
        int currentSessionId,
        string expectedExplorerPath) =>
        processSessionId == currentSessionId &&
        !string.IsNullOrWhiteSpace(imagePath) &&
        Path.GetFullPath(imagePath).Equals(
            Path.GetFullPath(expectedExplorerPath),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasReplacementExplorer(
        int currentSessionId,
        string explorerPath,
        IReadOnlySet<int> stoppedProcessIds)
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (!stoppedProcessIds.Contains(process.Id) &&
                    IsCurrentSessionExplorer(
                        process.SessionId,
                        process.MainModule?.FileName,
                        currentSessionId,
                        explorerPath))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 起動直後または終了競合。タイムアウトまでは再列挙する。
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    internal static string ResolveExplorerPath() => ResolveExplorerPath(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        File.Exists);

    internal static string ResolveExplorerPath(string windowsDirectory, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!Path.IsPathFullyQualified(windowsDirectory))
        {
            throw new ArgumentException("Windows directory must be an absolute path.", nameof(windowsDirectory));
        }

        var explorerPath = Path.GetFullPath(Path.Combine(windowsDirectory, "explorer.exe"));
        if (!fileExists(explorerPath))
        {
            throw new FileNotFoundException("Windows ディレクトリの explorer.exe が見つかりません。", explorerPath);
        }

        return explorerPath;
    }
}
