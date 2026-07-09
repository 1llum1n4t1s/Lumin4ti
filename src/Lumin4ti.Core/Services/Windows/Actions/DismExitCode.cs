using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// DISM の終了コード解釈を一元化する。3010 = 「成功・要再起動」を成功として扱うドメイン知識を
/// DismActionBase (IMaintenanceAction) と RecallToggle (IMaintenanceToggle) の両方で共有する
/// (型階層が異なり共通基底を持てないため static ヘルパーにする)。
/// </summary>
internal static class DismExitCode
{
    /// <summary>DISM が要再起動を示す終了コード。</summary>
    public const int RebootRequired = 3010;

    /// <summary>成功、または「成功・要再起動 (3010)」なら true。</summary>
    public static bool IsSuccessOrRebootRequired(CommandExecutionResult result) =>
        result.Success || result.ExitCode == RebootRequired;
}
