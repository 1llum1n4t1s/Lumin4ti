using System.Text.RegularExpressions;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

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
    /// PackageFamilyName → 本体を提供するオプション機能の既定 ID。
    /// まずこの ID をそのまま追加し、OS 側でバージョン部が違って失敗したときだけ
    /// DISM の一覧から解決し直す (一覧取得は Windows Update への問い合わせで数分かかるため後回しにする)。
    /// </summary>
    private static readonly Dictionary<string, string> CapabilityIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // 「接続 (ワイヤレスディスプレイ受信)」= スタートに ms-resource:ProductNameWindowsStore で出る典型例
        ["Microsoft.PPIProjection_cw5n1h2txyewy"] = "App.WirelessDisplay.Connect~~~~0.0.1.0",
        ["MicrosoftCorporationII.QuickAssist_8wekyb3d8bbwe"] = "App.Support.QuickAssist~~~~0.0.1.0",
    };

    [GeneratedRegex(@"[A-Za-z0-9._]+~[~A-Za-z0-9._-]*", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdPattern();

    /// <summary>この パッケージを入れ直しで修復できるか。</summary>
    public static bool CanRepair(string packageFamilyName) =>
        CapabilityIds.ContainsKey(packageFamilyName);

    /// <summary>
    /// 本体のオプション機能を追加する。成功したら null、失敗したら利用者向けの理由を返す。
    /// </summary>
    public static async Task<string?> TryRepairAsync(
        string packageFamilyName,
        ICommandExecutor executor,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!CapabilityIds.TryGetValue(packageFamilyName, out var capabilityId))
        {
            return "この項目に対応するオプション機能が分かりません";
        }

        var add = await AddCapabilityAsync(capabilityId, executor, progress, ct);
        if (DismExitCode.IsSuccessOrRebootRequired(add))
        {
            LoggerBootstrap.Log.Info($"remove-ghost-packages: {capabilityId} を追加 (exit={add.ExitCode})");
            return null;
        }

        // 既定 ID のバージョン部が OS と食い違う場合だけ、時間のかかる一覧取得へ降りる。
        progress?.Report("既定の機能 ID で追加できなかったため、Windows Update から一覧を取得します (数分かかることがあります)...");
        var resolvedId = await ResolveCapabilityIdAsync(CapabilityPrefixOf(capabilityId), executor, progress, ct);
        if (resolvedId is null || resolvedId.Equals(capabilityId, StringComparison.OrdinalIgnoreCase))
        {
            LoggerBootstrap.Log.Error(
                $"remove-ghost-packages: {capabilityId} の追加に失敗 (exit={add.ExitCode}): {add.StandardError}");
            return $"オプション機能 {capabilityId} の追加に失敗しました (exit={add.ExitCode})";
        }

        var retry = await AddCapabilityAsync(resolvedId, executor, progress, ct);
        if (DismExitCode.IsSuccessOrRebootRequired(retry))
        {
            LoggerBootstrap.Log.Info($"remove-ghost-packages: {resolvedId} を追加 (exit={retry.ExitCode})");
            return null;
        }

        LoggerBootstrap.Log.Error(
            $"remove-ghost-packages: {resolvedId} の追加に失敗 (exit={retry.ExitCode}): {retry.StandardError}");
        return $"オプション機能 {resolvedId} の追加に失敗しました (exit={retry.ExitCode})";
    }

    private static Task<CommandExecutionResult> AddCapabilityAsync(
        string capabilityId,
        ICommandExecutor executor,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"オプション機能を追加しています: {capabilityId}");
        return executor.RunAsync(
            "dism.exe",
            $"/online /Add-Capability /CapabilityName:{capabilityId} /NoRestart",
            ct,
            progress);
    }

    /// <summary>"App.WirelessDisplay.Connect~~~~0.0.1.0" → "App.WirelessDisplay.Connect"。</summary>
    private static string CapabilityPrefixOf(string capabilityId)
    {
        var separator = capabilityId.IndexOf('~');
        return separator > 0 ? capabilityId[..separator] : capabilityId;
    }

    /// <summary>
    /// DISM の機能一覧から接頭辞に一致する完全な機能 ID を取り出す。
    /// 一覧の見出しは OS の表示言語で変わるため、ID そのものを正規表現で拾って言語非依存にする。
    /// </summary>
    private static async Task<string?> ResolveCapabilityIdAsync(
        string prefix,
        ICommandExecutor executor,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var list = await executor.RunAsync("dism.exe", "/online /Get-Capabilities", ct, progress);
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
