using System.Text.RegularExpressions;
using Lumin4ti.Core.Interfaces;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// Windows がシステムアプリとして保護していて登録解除できないゴーストを、
/// 本体 (オプション機能 / Feature on Demand) の入れ直しで修復する。
/// 「システムアプリなら本来入っているべき」ので、削除できないものは消すより戻す方が正しい状態になる。
/// FoD の追加は DISM が唯一の手段のため Dism.exe を使う。
/// </summary>
internal static partial class SystemAppCapabilityRepair
{
    /// <summary>
    /// PackageFamilyName → 本体を提供するオプション機能の ID 接頭辞。
    /// 完全な ID (末尾の "~~~~0.0.1.0") は OS ごとに変わるため、実際の値は DISM の一覧から解決する。
    /// </summary>
    private static readonly Dictionary<string, string> CapabilityPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // 「接続 (ワイヤレスディスプレイ受信)」= スタートに ms-resource:ProductNameWindowsStore で出る典型例
        ["Microsoft.PPIProjection_cw5n1h2txyewy"] = "App.WirelessDisplay.Connect",
        ["MicrosoftCorporationII.QuickAssist_8wekyb3d8bbwe"] = "App.Support.QuickAssist",
    };

    [GeneratedRegex(@"[A-Za-z0-9._]+~[~A-Za-z0-9._-]*", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdPattern();

    /// <summary>この パッケージを入れ直しで修復できるか。</summary>
    public static bool CanRepair(string packageFamilyName) =>
        CapabilityPrefixes.ContainsKey(packageFamilyName);

    /// <summary>
    /// 本体のオプション機能を追加する。成功したら null、失敗したら利用者向けの理由を返す。
    /// </summary>
    public static async Task<string?> TryRepairAsync(
        string packageFamilyName,
        ICommandExecutor executor,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!CapabilityPrefixes.TryGetValue(packageFamilyName, out var prefix))
        {
            return "この項目に対応するオプション機能が分かりません";
        }

        var capabilityId = await ResolveCapabilityIdAsync(prefix, executor, ct);
        if (capabilityId is null)
        {
            return $"オプション機能 {prefix} がこの Windows に見つかりません";
        }

        progress?.Report($"オプション機能を追加しています: {capabilityId}");
        var add = await executor.RunAsync(
            "dism.exe",
            $"/online /Add-Capability /CapabilityName:{capabilityId} /NoRestart",
            ct,
            progress);

        if (DismExitCode.IsSuccessOrRebootRequired(add))
        {
            LoggerBootstrap.Log.Info($"remove-ghost-packages: {capabilityId} を追加 (exit={add.ExitCode})");
            return null;
        }

        LoggerBootstrap.Log.Error(
            $"remove-ghost-packages: {capabilityId} の追加に失敗 (exit={add.ExitCode}): {add.StandardError}");
        return $"オプション機能 {capabilityId} の追加に失敗しました (exit={add.ExitCode})";
    }

    /// <summary>
    /// DISM の機能一覧から接頭辞に一致する完全な機能 ID を取り出す。
    /// 一覧の見出しは OS の表示言語で変わるため、ID そのものを正規表現で拾って言語非依存にする。
    /// </summary>
    private static async Task<string?> ResolveCapabilityIdAsync(
        string prefix,
        ICommandExecutor executor,
        CancellationToken ct)
    {
        var list = await executor.RunAsync("dism.exe", "/online /Get-Capabilities", ct);
        if (!list.Success)
        {
            return null;
        }

        foreach (Match match in CapabilityIdPattern().Matches(list.StandardOutput))
        {
            if (match.Value.StartsWith(prefix + "~", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }
        }

        return null;
    }
}
