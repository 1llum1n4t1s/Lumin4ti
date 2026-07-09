using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 実行ファイルが存在しなくなったスタートアップ登録 (HKCU Run) と、
/// 対応する孤立 StartupApproved エントリを Registry API で削除する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokenStartupCleanupAction : IMaintenanceAction
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string Id => "remove-broken-startup";

    public string Label => "リンク切れのスタートアップを削除";

    public string Description => "実行ファイルが存在しなくなったスタートアップ登録 (現在ユーザー) と、対応する孤立エントリを削除します。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool IsLongRunning => false;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        var allNames = new List<string>();

        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            if (runKey is not null)
            {
                foreach (var name in runKey.GetValueNames())
                {
                    allNames.Add(name);
                    var command = runKey.GetValue(name)?.ToString();
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        continue;
                    }

                    var exe = StartupCommandParser.TryResolveExecutable(command);
                    if (exe is null || File.Exists(exe))
                    {
                        continue;
                    }

                    runKey.DeleteValue(name, throwOnMissingValue: false);
                    allNames.Remove(name);
                    RemoveApprovedEntry(name);
                    lines.Add($"  - 削除: {name} ({exe})");
                }
            }
        }

        // Run に対応する登録が無い孤立 StartupApproved エントリを掃除
        using (var approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true))
        {
            if (approvedKey is not null)
            {
                foreach (var name in approvedKey.GetValueNames())
                {
                    if (!allNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        approvedKey.DeleteValue(name, throwOnMissingValue: false);
                        lines.Add($"  - 孤立削除: {name}");
                    }
                }
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("  - リンク切れのスタートアップはありませんでした");
        }

        LoggerBootstrap.Log.Info($"{Id}: {lines.Count} 件処理");
        return Task.FromResult(MaintenanceActionResult.Ok(lines));
    }

    private static void RemoveApprovedEntry(string name)
    {
        using var approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
        approvedKey?.DeleteValue(name, throwOnMissingValue: false);
    }
}
