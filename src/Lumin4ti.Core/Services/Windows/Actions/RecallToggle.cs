using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// Windows Recall 機能の無効化トグル。Windows 機能の有効/無効は DISM が正規の手段のため
/// dism.exe を使うが、状態問い合わせ・終了コード解釈 (3010 = 要再起動) は C# 側で行う。
/// ON = Recall 無効化を適用、OFF = 有効 (Windows 既定) に戻す。
/// </summary>
public sealed class RecallToggle(ICommandExecutor executor) : IMaintenanceToggle
{
    public string Id => "recall-off";

    public string Label => "Windows Recall を無効化";

    public string Description =>
        "画面のスナップショットを定期的に記録して AI 検索できるようにする Recall 機能を無効化します (Click-to-do・セマンティック検索などの他の AI 機能は維持されます)。" +
        "記録処理の分のメモリ・ディスク消費とプライバシー上の懸念を抑えられます。Windows 11 24H2 + Copilot+ PC 限定の機能のため、非対応の PC では状態を取得できず切り替えできません。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => true;

    public MaintenanceStateDiagnostic? StateDiagnostic { get; private set; }
    public string? StateFailureReason { get; private set; }

    public async Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        StateDiagnostic = null;
        StateFailureReason = null;
        var result = await executor.RunAsync("dism.exe", "/online /get-featureinfo /featurename:Recall /english", ct);
        if (!DismExitCode.IsSuccessOrRebootRequired(result))
        {
            StateDiagnostic = new("Toggle.RecallQueryFailed",
                "DISM で Recall の状態を取得できません。非対応環境や管理者権限の不足などが考えられます。詳細はログを確認してください。");
            StateFailureReason = $"DISM の Recall 照会に失敗: {CommandFailureDiagnostic.Format(result)}";
            MaintenanceItemLog.StateReadFailed(Id, Label, StateFailureReason);
            return null;
        }

        // "/english" 指定で "State : Enabled|Disabled|..." 行をロケール非依存にパースする
        var stateLine = result.StandardOutput.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("State :", StringComparison.OrdinalIgnoreCase));
        if (stateLine is null)
        {
            StateDiagnostic = new("Toggle.RecallStateMissing",
                "DISM の出力に Recall の状態が見つかりません。詳細はログを確認してください。");
            StateFailureReason = $"DISM の出力に Recall の State 行がありません: {CommandFailureDiagnostic.Format(result)}";
            MaintenanceItemLog.StateReadFailed(Id, Label, StateFailureReason);
            return null;
        }

        var state = ParseFeatureState(stateLine);
        if (state is null)
        {
            StateDiagnostic = new("Toggle.RecallStateUnsupported",
                "DISM が返した Recall の状態を判定できません。詳細はログを確認してください。");
            StateFailureReason = $"DISM の Recall 状態値が未対応です: {CommandFailureDiagnostic.Sanitize(stateLine)}";
            MaintenanceItemLog.StateReadFailed(Id, Label, StateFailureReason);
        }
        return state;
    }

    /// <summary>
    /// ON は無効化済み、または再起動後に無効化される保留状態。
    /// DISM の既知状態だけを受理し、将来追加された状態を誤って OFF と断定しない。
    /// </summary>
    internal static bool? ParseFeatureState(string stateLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateLine);

        var separator = stateLine.IndexOf(':');
        if (separator < 0 ||
            !stateLine[..separator].Trim().Equals("State", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var state = stateLine[(separator + 1)..].Trim();
        if (state.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Disable Pending", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("DisablePending", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Disabled with Payload Removed", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Removed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (state.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Enable Pending", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("EnablePending", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        var verb = on ? "disable" : "enable";
        var result = await executor.RunAsync("dism.exe", $"/online /{verb}-feature /featurename:Recall /norestart", ct);

        if (DismExitCode.IsSuccessOrRebootRequired(result))
        {
            LoggerBootstrap.Log.Info($"{Id} → {(on ? "ON" : "OFF")} (exit={result.ExitCode})");
            return MaintenanceActionResult.Ok($"  - Recall を{(on ? "無効化" : "有効化")}しました (反映には再起動が必要です)");
        }

        LoggerBootstrap.Log.Error($"{Id}: dism {verb}-feature: {CommandFailureDiagnostic.Format(result)}");
        return MaintenanceActionResult.Fail($"DISM が失敗しました (exit={result.ExitCode})。非対応の PC の可能性があります。");
    }
}
