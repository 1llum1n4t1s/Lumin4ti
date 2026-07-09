using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// DISM 系アクションの共通基底。DISM API の直接 P/Invoke は複雑すぎるため Dism.exe を使うが、
/// 終了コードの解釈 (3010 = 成功・要再起動) と出力整形は C# 側で制御する。
/// </summary>
public abstract class DismActionBase(ICommandExecutor executor) : IMaintenanceAction
{
    public abstract string Id { get; }

    public abstract string Label { get; }

    public abstract string Description { get; }

    public abstract CommandCategory Category { get; }

    public abstract bool RequiresReboot { get; }

    public abstract bool IsLongRunning { get; }

    protected abstract string Arguments { get; }

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) => ExecuteAsync(null, ct);

    public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = await executor.RunAsync("dism.exe", Arguments, ct, progress);

        if (DismExitCode.IsSuccessOrRebootRequired(result))
        {
            LoggerBootstrap.Log.Info($"{Id}: dism.exe {Arguments} (exit={result.ExitCode})");
            var detail = result.ExitCode == DismExitCode.RebootRequired
                ? "  - 完了しました (反映には再起動が必要です)"
                : TrimDismOutput(result.StandardOutput);
            return MaintenanceActionResult.Ok(detail);
        }

        LoggerBootstrap.Log.Error($"{Id}: dism.exe {Arguments} (exit={result.ExitCode}): {result.StandardError}");
        return MaintenanceActionResult.Fail(
            string.IsNullOrWhiteSpace(result.StandardError) ? TrimDismOutput(result.StandardOutput) : result.StandardError);
    }

    /// <summary>DISM のバナー・プログレス行を除いた要点だけに整形する。</summary>
    private static string TrimDismOutput(string output)
    {
        var lines = output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Where(l => !l.StartsWith("展開イメージ", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("Deployment Image", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("バージョン", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("Version", StringComparison.Ordinal))
            .Where(l => !l.Contains('%'));
        return string.Join(Environment.NewLine, lines.Select(l => $"  {l}"));
    }
}

/// <summary>Windows Update コンポーネントストア (WinSxS) のクリーンアップ。</summary>
public sealed class ComponentStoreCleanupAction(ICommandExecutor executor) : DismActionBase(executor)
{
    public override string Id => "wu-component-cleanup";

    public override string Label => "Windows Update の使用スペースを削除";

    public override string Description =>
        "Windows Update が使用するコンポーネントストア (WinSxS フォルダ) に蓄積された古い更新プログラムの世代を完全に削除し、数 GB 単位のディスク領域を回収します。" +
        "実行後は適用済みの更新プログラムをアンインストールできなくなります (通常運用では問題ありません)。実行には数分〜数十分かかります。";

    public override CommandCategory Category => CommandCategory.Cleanup;

    public override bool RequiresReboot => false;

    public override bool IsLongRunning => true;

    protected override string Arguments => "/online /Cleanup-Image /StartComponentCleanup /ResetBase";
}

// (Recall の切り替えは RecallToggle 参照 — ON/OFF 型のため本基底は使わない)
