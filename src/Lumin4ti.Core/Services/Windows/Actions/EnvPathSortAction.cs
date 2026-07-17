using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// ユーザーとシステムの Path 環境変数を Registry API で読み、重複除去 + 昇順ソートして書き戻す。
/// REG_EXPAND_SZ の %変数% を壊さないよう DoNotExpandEnvironmentNames で読み、値種別を維持して書く。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnvPathSortAction : IMaintenanceAction
{
    private const string SystemEnvPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly nint HwndBroadcast = new(0xffff);
    private readonly ProtectedBackupStorage _backupStorage;

    public EnvPathSortAction()
        : this(ProtectedBackupStorage.Default)
    {
    }

    internal EnvPathSortAction(ProtectedBackupStorage backupStorage)
    {
        _backupStorage = backupStorage ?? throw new ArgumentNullException(nameof(backupStorage));
    }

    public string Id => "env-path-sort";

    public string Label => "環境変数 Path をソート";

    public string Description =>
        "ユーザーとシステムの Path 環境変数のエントリを重複除去して昇順に並べ替えます。" +
        "Path の順序は同名コマンドの解決先に影響するため、変更前の値を保護バックアップへ保存します。";

    public CommandCategory Category => CommandCategory.Organize;

    public bool RequiresReboot => false;

    public bool IsLongRunning => false;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        Task.Run(() => ExecuteCore(ct), ct);

    private MaintenanceActionResult ExecuteCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // プロセスのビット数に左右されず、Windows がシステム環境変数として使うビューを明示する。
        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        EnvPathBackupEntry user;
        EnvPathBackupEntry system;
        try
        {
            user = CaptureRegistryPath(currentUser, "Environment");
            system = CaptureRegistryPath(localMachine, SystemEnvPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidDataException)
        {
            LoggerBootstrap.Log.Error($"{Id}: Path の読み取りに失敗したため変更しませんでした", ex);
            return MaintenanceActionResult.Fail($"Path の現在値を安全に読み取れないため、変更しませんでした。{Environment.NewLine}  - {ex.Message}");
        }

        var userSorted = user.Value is { Length: > 0 } userValue ? SortPathValue(userValue) : user.Value;
        var systemSorted = system.Value is { Length: > 0 } systemValue ? SortPathValue(systemValue) : system.Value;
        var userChanges = user.Exists && !string.Equals(user.Value, userSorted, StringComparison.Ordinal);
        var systemChanges = system.Exists && !string.Equals(system.Value, systemSorted, StringComparison.Ordinal);
        if (!userChanges && !systemChanges)
        {
            return MaintenanceActionResult.Ok(
                "  - ユーザー / システム Path は既に重複除去・昇順ソート済みでした");
        }

        var backupName = Path.Combine(
            "environment",
            $"path-before-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        try
        {
            var backup = new EnvPathBackupDocument(
                EnvPathBackupDocument.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                user,
                system);
            _backupStorage.WriteNewAtomically(
                backupName,
                stream => Lumin4tiJson.Serialize(stream, backup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            LoggerBootstrap.Log.Error($"{Id}: 保護バックアップを作成できないため変更しませんでした", ex);
            return MaintenanceActionResult.Fail(
                $"Path の保護バックアップを作成できないため、変更しませんでした。{Environment.NewLine}  - {ex.Message}");
        }

        // バックアップ確定後、まだ一件も書いていない境界でだけキャンセルを受け付ける。
        ct.ThrowIfCancellationRequested();

        var results = new List<(bool Success, string Line)>();
        try
        {
            results.Add((true, WriteSortedRegistryPath(
                currentUser, "Environment", "ユーザー", user, userSorted)));
            results.Add((true, WriteSortedRegistryPath(
                localMachine, SystemEnvPath, "システム", system, systemSorted)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            var rollback = TryRestoreBoth(currentUser, localMachine, user, system);
            LoggerBootstrap.Log.Error($"{Id}: Path の書き込みに失敗しました。{rollback.Line}", ex);
            return MaintenanceActionResult.Fail(
                $"Path の書き込みに失敗しました。{Environment.NewLine}  - {ex.Message}{Environment.NewLine}{rollback.Line}" +
                $"{Environment.NewLine}  - 保護バックアップ: {_backupStorage.GetFullPath(backupName)}");
        }

        results.Add(TryBroadcastEnvironmentChange());
        var result = CreateResult(results);
        result = result with
        {
            Detail = result.Detail + Environment.NewLine +
                     $"  - 変更前の保護バックアップ: {_backupStorage.GetFullPath(backupName)}",
        };
        LoggerBootstrap.Log.Info($"{Id}: {result.Status}");

        // commit 後のキャンセルで「未変更」と誤解させない。安全な完了結果と
        // バックアップ位置を返し、キャンセル要求が遅かったことを明示する。
        if (ct.IsCancellationRequested)
        {
            result = result with
            {
                Detail = result.Detail + Environment.NewLine +
                         "  - キャンセル要求は変更開始後だったため、安全な完了まで継続しました",
            };
        }

        return result;
    }

    internal static MaintenanceActionResult CreateResult(IReadOnlyCollection<(bool Success, string Line)> results)
    {
        var detail = string.Join(Environment.NewLine, results.Select(r => r.Line));

        if (results.All(r => r.Success))
        {
            return MaintenanceActionResult.Ok(detail);
        }

        // Path の書き込み自体が完了し、実行中アプリへの通知だけ失敗した場合は
        // サインアウト後に反映される部分成功として明示する。
        return results.Count >= 3 && results.Take(results.Count - 1).All(r => r.Success)
            ? MaintenanceActionResult.Partial(detail)
            : MaintenanceActionResult.Fail(detail);
    }

    private static (bool Success, string Line) TryBroadcastEnvironmentChange()
    {
        // 既存 Explorer が新しい環境ブロックを使えるよう、公式の WM_SETTINGCHANGE を通知する。
        // 応答しないウィンドウがあってもアプリ全体を固めない。
        if (SendMessageTimeout(
                HwndBroadcast,
                WmSettingChange,
                nint.Zero,
                "Environment",
                SmtoAbortIfHung,
                5000,
                out _) != nint.Zero)
        {
            return (true, "  - 環境変数の変更を実行中のアプリへ通知しました");
        }

        var errorCode = Marshal.GetLastWin32Error();
        var error = errorCode == 0
            ? "応答待ちがタイムアウトしました"
            : new Win32Exception(errorCode).Message;
        return (false, $"  - 環境変数の変更通知に失敗しました ({error})。サインアウト後に反映されます");
    }

    private static EnvPathBackupEntry CaptureRegistryPath(RegistryKey root, string keyPath)
    {
        using var key = root.OpenSubKey(keyPath);
        if (key is null || !key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase))
        {
            return new(false, null, null);
        }

        var kind = key.GetValueKind("Path");
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            throw new InvalidDataException($"{keyPath}\\Path が REG_SZ / REG_EXPAND_SZ ではありません ({kind})。");
        }

        var value = key.GetValue(
            "Path",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (value is null)
        {
            throw new InvalidDataException($"{keyPath}\\Path を文字列として読み取れません。");
        }

        return new(true, value, kind);
    }

    private static string WriteSortedRegistryPath(
        RegistryKey root,
        string keyPath,
        string label,
        EnvPathBackupEntry before,
        string? sorted)
    {
        if (!before.Exists)
        {
            return $"  - {label} Path: 見つかりませんでした (スキップ)";
        }

        if (string.IsNullOrEmpty(before.Value))
        {
            return $"  - {label} Path: 空のためスキップ";
        }

        if (string.Equals(before.Value, sorted, StringComparison.Ordinal))
        {
            return $"  - {label} Path: 既に重複除去・昇順ソート済み";
        }

        using var key = root.OpenSubKey(keyPath, writable: true)
            ?? throw new IOException($"{label} Path のレジストリキーが書き込み前に失われました。");
        key.SetValue("Path", sorted!, before.Kind!.Value);
        key.Flush();

        var written = key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        if (!string.Equals(written, sorted, StringComparison.Ordinal))
        {
            throw new IOException($"{label} Path の書き込み結果が一致しませんでした。");
        }

        var beforeCount = before.Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        var afterCount = sorted!.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        return $"  - {label} Path: {beforeCount} 件 → {afterCount} 件 (重複除去 + 昇順ソート)";
    }

    private static (bool Success, string Line) TryRestoreBoth(
        RegistryKey currentUser,
        RegistryKey localMachine,
        EnvPathBackupEntry user,
        EnvPathBackupEntry system)
    {
        // 書き込み順 (ユーザー → システム) の逆順で戻し、一方が失敗しても
        // もう一方の復元を必ず試す。
        return TryRestoreAll(
        [
            ("システム Path", () => RestoreRegistryPath(localMachine, SystemEnvPath, system)),
            ("ユーザー Path", () => RestoreRegistryPath(currentUser, "Environment", user)),
        ]);
    }

    internal static (bool Success, string Line) TryRestoreAll(
        IReadOnlyCollection<(string Label, Action Restore)> operations)
    {
        var failures = new List<string>();
        foreach (var (label, restore) in operations)
        {
            try
            {
                restore();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                failures.Add($"{label}: {ex.Message}");
            }
        }

        return failures.Count == 0
            ? (true, "  - 書き込み前のユーザー / システム Path へロールバックしました")
            : (false, $"  - 自動ロールバックの一部または全部に失敗しました: {string.Join(" / ", failures)}");
    }

    private static void RestoreRegistryPath(
        RegistryKey root,
        string keyPath,
        EnvPathBackupEntry snapshot)
    {
        if (!snapshot.Exists)
        {
            using var existing = root.OpenSubKey(keyPath, writable: true);
            existing?.DeleteValue("Path", throwOnMissingValue: false);
            existing?.Flush();
            return;
        }

        using var key = root.CreateSubKey(keyPath);
        key.SetValue("Path", snapshot.Value!, snapshot.Kind!.Value);
        key.Flush();
        var restored = key.GetValue(
            "Path",
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!string.Equals(restored, snapshot.Value, StringComparison.Ordinal) ||
            key.GetValueKind("Path") != snapshot.Kind)
        {
            throw new IOException($"{keyPath}\\Path のロールバック検証に失敗しました。");
        }
    }

    /// <summary>「;」区切りの Path 値を trim → 空除去 → 大文字小文字無視で重複除去 → 昇順ソートする。</summary>
    internal static string SortPathValue(string raw)
    {
        var entries = raw.Split(';')
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase);
        return string.Join(';', entries);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        string lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nint result);
}

internal sealed record EnvPathBackupDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    EnvPathBackupEntry User,
    EnvPathBackupEntry System)
{
    public const int CurrentSchemaVersion = 1;
}

internal sealed record EnvPathBackupEntry(
    bool Exists,
    string? Value,
    RegistryValueKind? Kind);
