using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// システムフォルダ名の表示破損を shell32.dll の再登録で修復する。
/// regsvr32 の /i:U ルーチン呼び出しは DLL 内部実装のため外部プロセスが唯一の手段。
/// </summary>
public sealed class ShellFolderRepairAction(ICommandExecutor executor) : IMaintenanceAction
{
    public string Id => "repair-shell-folder-names";

    public string Label => "システムフォルダ名の破損を修復";

    public string Description => "「ダウンロード」等のシステムフォルダ名が英語表記に化けた場合などの表示破損を shell32.dll の再登録で修復します。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool AffectsExplorer => true;

    public bool IsLongRunning => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var result = await executor.RunAsync("regsvr32.exe", "shell32.dll /i:U /s", ct);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok("  - shell32.dll を再登録しました");
        }

        LoggerBootstrap.Log.Error($"{Id}: exit={result.ExitCode}: {result.StandardError}");
        return MaintenanceActionResult.Fail($"regsvr32 が失敗しました (exit={result.ExitCode})");
    }
}
