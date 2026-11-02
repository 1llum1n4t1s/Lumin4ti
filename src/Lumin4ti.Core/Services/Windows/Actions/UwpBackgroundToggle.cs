using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Windows.Management.Deployment;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>ON は全 UWP アプリを「常にオフ」、OFF は明示上書きを削除して Windows 既定へ戻す。</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class UwpBackgroundToggle : IMaintenanceToggle
{
    private readonly Func<IReadOnlyList<string>> _getTargetFamilyNames;
    private readonly IUwpBackgroundSettingsStore _settings;

    public UwpBackgroundToggle()
        : this(GetInstalledPackageFamilyNames, new RegistryUwpBackgroundSettingsStore()) { }

    internal UwpBackgroundToggle(
        Func<IReadOnlyList<string>> getTargetFamilyNames,
        IUwpBackgroundSettingsStore settings)
    {
        _getTargetFamilyNames = getTargetFamilyNames;
        _settings = settings;
    }

    public string Id => "uwp-background-off";
    public string Label => "UWP バックグラウンド実行を一括オフ / Windows 既定 (メモリ節約 200〜500MB)";
    public string Description =>
        "ON にすると、ストアアプリ (UWP) のバックグラウンド実行を全アプリまとめて「常にオフ」にします。" +
        "OFF にすると、対象アプリの明示的な設定を削除して Windows 既定へ戻します。変更前の個別設定は保存しません。";
    public CommandCategory Category => CommandCategory.Performance;
    public bool RequiresReboot => false;
    public bool SupportsExplicitOff => true;
    public MaintenanceStateDiagnostic? StateDiagnostic => null;
    public string? StateFailureReason { get; private set; }

    public Task<bool?> GetStateAsync(CancellationToken ct = default) => Task.Run<bool?>(() =>
    {
        StateFailureReason = null;
        try
        {
            var families = GetTargets();
            var values = _settings.ReadMany(families, ct);
            return families.Length > 0 && families.All(family => values[family] == UwpBackgroundValues.Applied);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            StateFailureReason = $"{error.GetType().Name}: {error.Message}";
            MaintenanceItemLog.StateReadFailed(Id, Label, StateFailureReason);
            return null;
        }
    }, ct);

    public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default) =>
        Task.Run(() => SetState(on, ct), ct);

    private string[] GetTargets() => _getTargetFamilyNames()
        .Where(family => !string.IsNullOrWhiteSpace(family))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private MaintenanceActionResult SetState(bool on, CancellationToken ct)
    {
        var operation = on ? "常にオフの適用" : "Windows 既定への変更";
        var target = on ? UwpBackgroundValues.Applied : default;
        var before = new Dictionary<string, UwpBackgroundValues>(StringComparer.OrdinalIgnoreCase);
        var attempted = new List<string>();
        MaintenanceActionResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            var families = GetTargets();
            var captured = _settings.ReadMany(families, ct);
            // 全対象を読み切ってから書く。操作前の値はこの呼出しのメモリ内だけに保持する。
            foreach (var family in families) before.Add(family, captured[family]);
            foreach (var family in families)
            {
                ct.ThrowIfCancellationRequested();
                if (_settings.ReadMany([family], ct)[family] != before[family])
                    throw new InvalidOperationException($"{family} の設定が操作前に外部で変更されました。");
                if (before[family] == target) continue;
                attempted.Add(family);
                _settings.WriteMany([new(family, target)], ct);
                ct.ThrowIfCancellationRequested();
                if (_settings.ReadMany([family], ct)[family] != target)
                    throw new InvalidOperationException($"{family} のペア値の変更を確認できませんでした。");
            }
            var actual = _settings.ReadMany(families, ct);
            foreach (var family in families)
                if (actual[family] != target)
                    throw new InvalidOperationException($"{family} の最終設定を確認できませんでした。");
            ct.ThrowIfCancellationRequested();
            result = MaintenanceActionResult.Ok($"  - {operation}: {families.Length} パッケージ");
        }
        catch (Exception error)
        {
            var compensationErrors = Compensate(attempted, before, target);
            if (compensationErrors.Count > 0)
                result = MaintenanceActionResult.Fail($"{operation}と補償に失敗しました。部分更新の可能性があります。\n  - {error.Message}\n  - " +
                    string.Join("\n  - ", compensationErrors));
            else if (error is OperationCanceledException && ct.IsCancellationRequested)
                result = MaintenanceActionResult.Canceled("操作を取り消し、変更済みの設定を操作前の値へ補償しました。");
            else
                result = MaintenanceActionResult.Fail($"{operation}に失敗しました。変更済みの設定を操作前の値へ補償しました。\n  - {error.Message}");
        }
        MaintenanceItemLog.LogResult(this, operation, result);
        return result;
    }

    private List<string> Compensate(
        List<string> attempted, IReadOnlyDictionary<string, UwpBackgroundValues> before, UwpBackgroundValues target)
    {
        var errors = new List<string>();
        foreach (var family in attempted.AsEnumerable().Reverse())
        {
            try
            {
                var current = _settings.ReadMany([family], CancellationToken.None)[family];
                if (!current.IsCombinationOf(before[family], target))
                    throw new InvalidOperationException("外部変更を検出したため現在値を保持しました。");
                if (current != before[family])
                    _settings.WriteMany([new(family, before[family])], CancellationToken.None);
            }
            catch (Exception error)
            {
                // 一件の補償失敗で、先に書き換えた他項目の補償を止めない。
                errors.Add($"{family}: 補償失敗: {error.Message}");
            }
        }
        // 未着手の対象は書き戻さず、補償後の実値は操作前に取得した全対象で照合する。
        foreach (var (family, original) in before)
        {
            try
            {
                if (_settings.ReadMany([family], CancellationToken.None)[family] != original)
                    throw new InvalidOperationException("操作前のペア値への補償を確認できませんでした (外部変更または補償不一致)。");
            }
            catch (Exception error)
            {
                errors.Add($"{family}: 補償後の照合失敗: {error.Message}");
            }
        }
        return errors;
    }

    private static IReadOnlyList<string> GetInstalledPackageFamilyNames()
    {
        var packageManager = new PackageManager();
        return packageManager.FindPackagesForUser(string.Empty)
            .Where(package => !package.IsFramework && !package.IsResourcePackage && !package.IsBundle)
            .Select(package => package.Id.FamilyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
