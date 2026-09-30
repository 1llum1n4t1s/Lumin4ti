using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// レジストリ値 1 件の切替仕様。
/// ON で <paramref name="AppliedValue"/> を書き込み、OFF は保存した元値と元の型へ戻す。
/// 退避が Missing の場合だけ <paramref name="DefaultValue"/> を使い、null なら値を削除する。
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
    private readonly RegistryValueBackup _backup;

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
            WindowsRegistryValueAccessor.Instance,
            RegistryValueBackup.Default)
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
        IRegistryValueAccessor registry,
        RegistryValueBackup backup)
    {
        _id = id;
        _label = label;
        _description = description;
        _category = category;
        _specs = specs;
        _requiresReboot = requiresReboot;
        _affectsExplorer = affectsExplorer;
        _registry = registry;
        _backup = backup;
    }

    public string Id => _id;

    public string Label => _label;

    public string Description => _description;

    public CommandCategory Category => _category;

    public bool RequiresReboot => _requiresReboot;

    public bool AffectsExplorer => _affectsExplorer;

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
        ct.ThrowIfCancellationRequested();
        return on ? Apply(ct) : Restore(ct);
    }

    private MaintenanceActionResult Apply(CancellationToken ct)
    {
        IReadOnlyList<RegistryWriteOperation> plan;
        IReadOnlyList<RegistryWriteOperation> before;
        Action? cancelPreparation = null;
        try
        {
            // 値変換もバックアップ保存も、一件も変更する前に完了させる。
            var operations = new List<RegistryWriteOperation>(_specs.Count);
            foreach (var spec in _specs)
            {
                ct.ThrowIfCancellationRequested();
                operations.Add(new RegistryWriteOperation(spec, SnapshotFor(spec.AppliedValue, spec.Kind)));
            }

            plan = operations;
            before = CaptureCurrentState(ct);
            cancelPreparation = _backup.PrepareApply(Id, _specs);
            // バックアップ作成中のキャンセルは、レジストリを書き始める前に反映する。
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            if (cancelPreparation is not null)
            {
                try { cancelPreparation(); }
                catch (Exception ex)
                {
                    LoggerBootstrap.Log.Error($"{Id}: ON 準備の取消に失敗し、復元原本を保持しました", ex);
                    return MaintenanceActionResult.Fail($"設定は未変更ですが、ON 準備の取消に失敗しました。復元原本を保持しています。\n  - {ex.Message}");
                }
            }
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error($"{Id}: ON 適用の準備に失敗したため変更しませんでした", ex);
            return MaintenanceActionResult.Fail(
                $"保護バックアップを安全に準備できないため、設定を変更しませんでした。\n  - {ex.Message}");
        }

        var lines = new List<string>();
        try
        {
            foreach (var operation in plan)
            {
                _registry.Write(operation.Spec, operation.Value);
                lines.Add($"  - {operation.Spec.Name} = {operation.Spec.AppliedValue}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CompensateFailedApply(ex, before, cancelPreparation!);
        }

        LoggerBootstrap.Log.Info($"{Id} → ON");
        return MaintenanceActionResult.Ok(lines);
    }

    private MaintenanceActionResult CompensateFailedApply(
        Exception applyError,
        IReadOnlyList<RegistryWriteOperation> before,
        Action cancelPreparation)
    {
        try
        {
            // 再 ON でも、OFF 用の初回原値ではなく今回の操作直前の状態へ補償する。
            foreach (var operation in before)
            {
                _registry.Write(operation.Spec, operation.Value);
            }
            cancelPreparation();
            LoggerBootstrap.Log.Error($"{Id}: ON 適用に失敗し、開始前の状態へ補償しました", applyError);
            return MaintenanceActionResult.Fail(
                $"設定の適用に失敗したため、今回の開始前の状態へ補償しました。既存の復元原本は保持しています。\n" +
                $"  - 適用失敗: {applyError.Message}");
        }
        catch (Exception rollbackError) when (rollbackError is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error(
                $"{Id}: ON 適用に失敗し、補償にも失敗しました: {rollbackError.Message}",
                applyError);
            return CompensationFailure(applyError, rollbackError.Message);
        }
    }

    private MaintenanceActionResult Restore(CancellationToken ct)
    {
        var lines = new List<string>();
        IReadOnlyList<RegistryWriteOperation> before;
        try
        {
            before = CaptureCurrentState(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error($"{Id}: OFF 前の状態を取得できないため変更しませんでした", ex);
            return MaintenanceActionResult.Fail(
                $"復元前の状態を安全に取得できないため、設定を変更しませんでした。\n  - {ex.Message}");
        }

        RegistryBackupRestoreResult restore;
        try
        {
            restore = _backup.TryRestore(Id, _specs, lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CompensateFailedRestore(ex, before, "元の値への復元");
        }

        if (restore.Status == RegistryBackupRestoreStatus.Restored)
        {
            LoggerBootstrap.Log.Info($"{Id} → OFF (元値に復元)");
            return MaintenanceActionResult.Ok(lines);
        }
        if (restore.Status == RegistryBackupRestoreStatus.Incomplete)
        {
            LoggerBootstrap.Log.Error($"{Id}: 値の復元後の所有更新を確認できないため、原本を保持しました: {restore.FailureReason}");
            return MaintenanceActionResult.Fail(
                "設定の値は復元しましたが、所有登録の更新を確認できませんでした。復元原本を保持しています。画面が OFF の場合は ON に戻してから OFF にし、所有登録の解除をやり直してください。\n" +
                $"  - {restore.FailureReason}");
        }

        // Restored は既に複数値を書き換え済みなので中断しない。Invalid / Missing は
        // まだ変更前のため、次の処理へ進む境界でキャンセルを反映できる。
        ct.ThrowIfCancellationRequested();
        if (restore.Status == RegistryBackupRestoreStatus.Invalid)
        {
            LoggerBootstrap.Log.Error($"{Id}: 復元バックアップの検証に失敗したため変更しませんでした: {restore.FailureReason}");
            return MaintenanceActionResult.Fail(
                $"復元バックアップが旧形式・未対応または破損しているため、設定を変更しませんでした。\n" +
                $"  - {restore.FailureReason}");
        }

        return ApplyDefaults(before, ct);
    }

    private MaintenanceActionResult ApplyDefaults(
        IReadOnlyList<RegistryWriteOperation> before,
        CancellationToken ct)
    {
        IReadOnlyList<RegistryWriteOperation> plan;
        try
        {
            // DefaultValue の型も、書き込み開始前に全件検証する。
            var operations = new List<RegistryWriteOperation>(_specs.Count);
            foreach (var spec in _specs)
            {
                ct.ThrowIfCancellationRequested();
                operations.Add(new RegistryWriteOperation(
                    spec,
                    spec.DefaultValue is null
                        ? RegistryValueSnapshot.Missing()
                        : SnapshotFor(spec.DefaultValue, spec.Kind)));
            }

            plan = operations;
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error($"{Id}: 既定値への復元計画を作成できないため変更しませんでした", ex);
            return MaintenanceActionResult.Fail(
                $"Windows 既定値への復元計画を作成できないため、設定を変更しませんでした。\n  - {ex.Message}");
        }

        var lines = new List<string>();
        try
        {
            foreach (var operation in plan)
            {
                _registry.Write(operation.Spec, operation.Value);
                lines.Add(operation.Value.Exists
                    ? $"  - {operation.Spec.Name} = {operation.Spec.DefaultValue} (既定値)"
                    : $"  - {operation.Spec.Name} を削除しました (Windows 既定に戻す)");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CompensateFailedRestore(ex, before, "Windows 既定値への復元");
        }

        LoggerBootstrap.Log.Info($"{Id} → OFF");
        return MaintenanceActionResult.Ok(lines);
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

    private MaintenanceActionResult CompensateFailedRestore(
        Exception restoreError,
        IReadOnlyList<RegistryWriteOperation> before,
        string operationName)
    {
        try
        {
            foreach (var operation in before)
            {
                _registry.Write(operation.Spec, operation.Value);
            }

            LoggerBootstrap.Log.Error(
                $"{Id}: {operationName}に失敗し、開始前の状態へ補償しました",
                restoreError);
            return MaintenanceActionResult.Fail(
                $"{operationName}に失敗したため、開始前の状態へ補償しました。復元バックアップは保持しています。\n" +
                $"  - {restoreError.Message}");
        }
        catch (Exception rollbackError) when (rollbackError is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error(
                $"{Id}: {operationName}に失敗し、開始前状態への補償にも失敗しました: {rollbackError.Message}",
                restoreError);
            return MaintenanceActionResult.Fail(
                $"{operationName}に失敗し、開始前状態への補償にも失敗しました。部分復元の可能性があります。\n" +
                $"  - 復元失敗: {restoreError.Message}\n" +
                $"  - 補償失敗: {rollbackError.Message}");
        }
    }

    private static MaintenanceActionResult CompensationFailure(Exception applyError, string rollbackReason) =>
        MaintenanceActionResult.Fail(
            "設定の適用に失敗し、保存済みスナップショットからの補償にも失敗しました。部分適用の可能性があります。\n" +
            $"  - 適用失敗: {applyError.Message}\n" +
            $"  - 補償失敗: {rollbackReason}");

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
}
