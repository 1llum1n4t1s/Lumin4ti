using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// レジストリ値 1 件の切替仕様。
/// ON で <paramref name="AppliedValue"/> を書き込み、OFF は <paramref name="DefaultValue"/> を書き込む。
/// 既定値が null なら値を削除して Windows 既定へ戻す。
/// </summary>
public sealed record RegistryToggleSpec(
    RegistryHive Hive,
    string KeyPath,
    string Name,
    RegistryValueKind Kind,
    object AppliedValue,
    object? DefaultValue = null);

/// <summary>
/// レジストリ値の書き込み/削除だけで ON/OFF が完結する tweak の汎用トグル。
/// 状態判定は「全 spec の現在値が AppliedValue と一致 = ON」。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryToggle : IMaintenanceToggle
{
    private readonly string _id;
    private readonly string _label;
    private readonly string _description;
    private readonly CommandCategory _category;
    private readonly IReadOnlyList<RegistryToggleSpec> _specs;
    private readonly bool _requiresReboot;
    private readonly bool _affectsExplorer;
    private readonly IRegistryValueAccessor _registry;

    public RegistryToggle(
        string id,
        string label,
        string description,
        CommandCategory category,
        IReadOnlyList<RegistryToggleSpec> specs,
        bool requiresReboot = false,
        bool affectsExplorer = false)
        : this(
            id,
            label,
            description,
            category,
            specs,
            requiresReboot,
            affectsExplorer,
            WindowsRegistryValueAccessor.Instance)
    {
    }

    internal RegistryToggle(
        string id,
        string label,
        string description,
        CommandCategory category,
        IReadOnlyList<RegistryToggleSpec> specs,
        bool requiresReboot,
        bool affectsExplorer,
        IRegistryValueAccessor registry)
    {
        _id = id;
        _label = label;
        _description = description;
        _category = category;
        _specs = specs;
        _requiresReboot = requiresReboot;
        _affectsExplorer = affectsExplorer;
        _registry = registry;
    }

    public string Id => _id;

    public string Label => _label;

    public string Description => _description;

    public CommandCategory Category => _category;

    public bool RequiresReboot => _requiresReboot;

    public bool AffectsExplorer => _affectsExplorer;

    public bool SupportsExplicitOff => true;

    internal IReadOnlyList<RegistryToggleSpec> Specs => _specs;

    public Task<bool?> GetStateAsync(CancellationToken ct = default) =>
        Task.Run<bool?>(() => GetState(ct), ct);

    private bool? GetState(CancellationToken ct)
    {
        foreach (var spec in _specs)
        {
            ct.ThrowIfCancellationRequested();
            var current = _registry.Read(spec);
            if (!ValueEqualsApplied(current, spec))
            {
                return false;
            }
        }

        return true;
    }

    public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default) =>
        Task.Run(() => SetState(on, ct), ct);

    private MaintenanceActionResult SetState(bool on, CancellationToken ct)
    {
        var operationName = on ? "設定の適用" : "解除値への変更";
        IReadOnlyList<RegistryWriteOperation> before;
        var plan = new List<RegistryWriteOperation>(_specs.Count);
        try
        {
            // 全値の型検証と操作直前の状態取得を、最初の書き込みより前に済ませる。
            foreach (var spec in _specs)
            {
                ct.ThrowIfCancellationRequested();
                var value = on ? spec.AppliedValue : spec.DefaultValue;
                plan.Add(new RegistryWriteOperation(spec,
                    value is null ? RegistryValueSnapshot.Missing() : SnapshotFor(value, spec.Kind)));
            }
            before = CaptureCurrentState(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error($"{Id}: {operationName}の準備に失敗したため変更しませんでした", ex);
            return MaintenanceActionResult.Fail($"{operationName}の準備に失敗したため、設定を変更しませんでした。\n  - {ex.Message}");
        }

        var attempted = new Stack<RegistryWriteAttempt>();
        try
        {
            for (var index = 0; index < plan.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var operation = plan[index];
                // 書き込み後に例外が出た値も補償対象へ含める。未着手値は含めない。
                attempted.Push(new RegistryWriteAttempt(operation, before[index].Value));
                _registry.Write(operation.Spec, operation.Value);
            }
            ct.ThrowIfCancellationRequested();
            VerifyState(plan);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            var compensationFailure = Compensate(attempted, before);
            if (compensationFailure is not null)
            {
                LoggerBootstrap.Log.Error($"{Id}: {operationName}と開始前状態への補償に失敗しました: {compensationFailure}", ex);
                return MaintenanceActionResult.Fail(
                    $"{operationName}に失敗し、開始前状態への補償にも失敗しました。部分適用の可能性があります。\n" +
                    $"  - 変更失敗: {ex.Message}\n  - 補償失敗: {compensationFailure}");
            }

            LoggerBootstrap.Log.Error($"{Id}: {operationName}に失敗し、開始前の状態へ補償しました", ex);
            if (ex is OperationCanceledException) throw;
            return MaintenanceActionResult.Fail($"{operationName}に失敗したため、開始前の状態へ補償しました。\n  - {ex.Message}");
        }

        LoggerBootstrap.Log.Info($"{Id} → {(on ? "ON" : "OFF")}");
        return MaintenanceActionResult.Ok(plan.Select(operation =>
            on ? $"  - {operation.Spec.Name} = {operation.Spec.AppliedValue}" :
            operation.Value.Exists ? $"  - {operation.Spec.Name} = {operation.Spec.DefaultValue} (解除値)" :
            $"  - {operation.Spec.Name} の明示設定を削除しました"));
    }

    private IReadOnlyList<RegistryWriteOperation> CaptureCurrentState(CancellationToken ct)
    {
        var current = new List<RegistryWriteOperation>(_specs.Count);
        foreach (var spec in _specs)
        {
            ct.ThrowIfCancellationRequested();
            var value = _registry.Read(spec);
            value.Validate();
            current.Add(new RegistryWriteOperation(spec, value));
        }
        return current;
    }

    private void VerifyState(IReadOnlyList<RegistryWriteOperation> plan)
    {
        foreach (var operation in plan)
        {
            if (!_registry.Read(operation.Spec).EquivalentTo(operation.Value))
                throw new IOException($"{operation.Spec.Name} の実状態が要求値と一致しません。");
        }
    }

    private string? Compensate(Stack<RegistryWriteAttempt> attempted, IReadOnlyList<RegistryWriteOperation> before)
    {
        string? externalChange = null;
        // 試行済みの値だけを逆順に補償する。1 値の失敗で残りを打ち切らない。
        foreach (var attempt in attempted)
        {
            var operation = attempt.Operation;
            try
            {
                var current = _registry.Read(operation.Spec);
                if (current.EquivalentTo(attempt.Before)) continue;
                if (!current.EquivalentTo(operation.Value))
                {
                    externalChange ??= $"{operation.Spec.Name} に外部変更を検出したため、その値を保持しました。";
                    LoggerBootstrap.Log.Error($"{Id}: {operation.Spec.Name} に外部変更を検出したため補償を中止しました");
                    continue;
                }
                _registry.Write(operation.Spec, attempt.Before);
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"{Id}: {operation.Spec.Name} の開始前状態への書き戻しに失敗しました", ex);
            }
        }
        var errors = new List<string>();
        if (externalChange is not null) errors.Add(externalChange);
        // 未着手値は書き戻さないが、全体が開始前と同じかは全件照合する。
        foreach (var operation in before)
        {
            try
            {
                if (!_registry.Read(operation.Spec).EquivalentTo(operation.Value))
                    errors.Add($"{operation.Spec.Name} の実状態が開始前と一致しません (外部変更または補償不一致)。");
            }
            catch (Exception ex)
            {
                errors.Add($"{operation.Spec.Name} の補償後の実状態を確認できません: {ex.Message}");
            }
        }
        return errors.Count == 0 ? null : string.Join(" / ", errors);
    }

    private static RegistryValueSnapshot SnapshotFor(object value, RegistryValueKind kind) =>
        RegistryValueSnapshot.FromRegistry(kind, NormalizeForWrite(value, kind));

    private static object NormalizeForWrite(object value, RegistryValueKind kind) =>
        // 0xFFFFFFFF 等 int 範囲外の DWORD は unchecked で int に落として書く
        kind == RegistryValueKind.DWord ? unchecked((int)Convert.ToUInt32(value)) : value;

    private static bool ValueEqualsApplied(RegistryValueSnapshot current, RegistryToggleSpec spec)
    {
        var applied = SnapshotFor(spec.AppliedValue, spec.Kind);
        if (current.Kind != applied.Kind)
        {
            return false;
        }

        // 既存挙動との互換性のため文字列だけは大文字小文字を区別しない。
        if (applied.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString)
        {
            return current.Exists && string.Equals(
                current.StringValue,
                applied.StringValue,
                StringComparison.OrdinalIgnoreCase);
        }

        return current.EquivalentTo(applied);
    }

    private sealed record RegistryWriteOperation(RegistryToggleSpec Spec, RegistryValueSnapshot Value);

    private sealed record RegistryWriteAttempt(RegistryWriteOperation Operation, RegistryValueSnapshot Before);
}
