using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 実行ファイルが存在しなくなったスタートアップ登録 (HKCU Run) と、
/// その登録に対応する StartupApproved エントリを Registry API で削除する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokenStartupCleanupAction : IMaintenanceAction
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string Id => "remove-broken-startup";

    public string Label => "リンク切れのスタートアップを削除";

    public string Description => "実行ファイルが存在しなくなったスタートアップ登録 (現在ユーザー) と、対応する状態エントリを削除します。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool IsLongRunning => false;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        Task.Run(() => ExecuteCore(ct), ct);

    private MaintenanceActionResult ExecuteCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lines = new List<string>();

        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            if (runKey is not null)
            {
                foreach (var name in runKey.GetValueNames())
                {
                    ct.ThrowIfCancellationRequested();
                    var command = runKey.GetValue(name)?.ToString();
                    var exe = GetConfirmedMissingExecutable(command);
                    if (exe is null)
                    {
                        continue;
                    }

                    // Run と対応する StartupApproved は一組として削除し、途中キャンセルで
                    // 新しい孤立エントリを残さない。
                    ct.ThrowIfCancellationRequested();
                    runKey.DeleteValue(name, throwOnMissingValue: false);
                    RemoveApprovedEntry(name);
                    lines.Add($"  - 削除: {name} ({exe})");
                }
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("  - リンク切れのスタートアップはありませんでした");
        }

        LoggerBootstrap.Log.Info($"{Id}: {lines.Count} 件処理");
        return MaintenanceActionResult.Ok(lines);
    }

    private static void RemoveApprovedEntry(string name)
    {
        using var approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
        approvedKey?.DeleteValue(name, throwOnMissingValue: false);
    }

    internal static string? GetConfirmedMissingExecutable(
        string? command,
        Func<string, bool>? isConfirmedMissing = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var executable = StartupCommandParser.TryResolveExecutable(command);
        if (executable is null)
        {
            return null;
        }

        isConfirmedMissing ??= static path => StartupCommandParser.IsConfirmedMissing(path);
        return isConfirmedMissing(executable) ? executable : null;
    }
}
