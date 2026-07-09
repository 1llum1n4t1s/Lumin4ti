using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
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

    public string Id => "env-path-sort";

    public string Label => "環境変数 Path をソート";

    public string Description => "ユーザーとシステムの Path 環境変数のエントリを重複除去して昇順に並べ替えます。";

    public CommandCategory Category => CommandCategory.Organize;

    public bool RequiresReboot => false;

    public bool IsLongRunning => false;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var results = new[]
        {
            TrySortRegistryPath(Registry.CurrentUser, "Environment", "ユーザー"),
            TrySortRegistryPath(Registry.LocalMachine, SystemEnvPath, "システム"),
        };

        var lines = results.Select(r => r.Line).ToList();
        // 片方でもソートできれば部分成功として扱う (非昇格実行時はシステム側が権限不足になる)
        var success = results.Any(r => r.Success);

        LoggerBootstrap.Log.Info($"{Id}: {(success ? "完了" : "失敗")}");
        return Task.FromResult(success
            ? MaintenanceActionResult.Ok(lines)
            : MaintenanceActionResult.Fail(string.Join(Environment.NewLine, lines)));
    }

    private static (bool Success, string Line) TrySortRegistryPath(RegistryKey root, string keyPath, string label)
    {
        try
        {
            return (true, SortRegistryPath(root, keyPath, label));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            LoggerBootstrap.Log.Error($"{label} Path のソートに失敗しました (権限不足)", ex);
            return (false, $"  - {label} Path: 権限がないためスキップしました (管理者として実行してください)");
        }
    }

    private static string SortRegistryPath(RegistryKey root, string keyPath, string label)
    {
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key is null || !key.GetValueNames().Contains("Path", StringComparer.OrdinalIgnoreCase))
        {
            return $"  - {label} Path: 見つかりませんでした (スキップ)";
        }

        var kind = key.GetValueKind("Path");
        var raw = key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return $"  - {label} Path: 空のためスキップ";
        }

        var before = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        var sorted = SortPathValue(raw);
        key.SetValue("Path", sorted, kind);

        var after = sorted.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        return $"  - {label} Path: {before} 件 → {after} 件 (重複除去 + 昇順ソート)";
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
}
