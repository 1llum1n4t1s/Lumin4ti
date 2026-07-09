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

    public async Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        var result = await executor.RunAsync("dism.exe", "/online /get-featureinfo /featurename:Recall /english", ct);
        if (!result.Success)
        {
            // 非対応機種 (機能自体が存在しない) は状態不明
            return null;
        }

        // "/english" 指定で "State : Enabled|Disabled|..." 行をロケール非依存にパースする
        var stateLine = result.StandardOutput.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("State :", StringComparison.OrdinalIgnoreCase));
        if (stateLine is null)
        {
            return null;
        }

        // ON (= 無効化適用済み) は State が Disabled 系のとき
        return stateLine.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
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

        LoggerBootstrap.Log.Error($"{Id}: dism {verb}-feature (exit={result.ExitCode}): {result.StandardError}");
        return MaintenanceActionResult.Fail($"DISM が失敗しました (exit={result.ExitCode})。非対応の PC の可能性があります。");
    }
}
