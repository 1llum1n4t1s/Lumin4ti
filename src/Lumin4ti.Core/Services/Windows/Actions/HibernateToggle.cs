using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 休止状態 (ハイバネーション) の無効化トグル。
/// 状態は hiberfil.sys の有無を C# で直接読み、切替は powercfg が正規の手段
/// (同ファイルの生成・削除を伴う) のため外部プロセスで行う。
/// ON = 休止状態を無効化、OFF = 有効 (Windows 既定) に戻す。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HibernateToggle : IMaintenanceToggle
{
    private readonly ICommandExecutor _executor;
    private readonly Func<bool> _isHiberFilePresent;

    public HibernateToggle(ICommandExecutor executor)
        : this(executor, IsHiberFilePresent)
    {
    }

    internal HibernateToggle(ICommandExecutor executor, Func<bool> isHiberFilePresent)
    {
        _executor = executor;
        _isHiberFilePresent = isHiberFilePresent;
    }

    public string Id => "hibernate-off";

    public string Label => "休止状態 (ハイバネーション) を無効化";

    public string Description =>
        "休止状態を無効化して、システムドライブに常時確保される hiberfil.sys (物理メモリの数十% = 数 GB) を削除します。" +
        "高速スタートアップも同時に無効になるため起動が遅くなる場合があります。ノート PC で休止状態を使う運用なら OFF (既定) のままにしてください。";

    public CommandCategory Category => CommandCategory.System;

    public bool RequiresReboot => false;

    public Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Microsoft の契約では hiberfil.sys が無ければ休止状態を利用できない。
        // HibernateEnabledDefault は出荷時既定であり、現在状態の判定には使わない。
        return Task.FromResult<bool?>(!_isHiberFilePresent());
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        var result = await _executor.RunAsync("powercfg.exe", $"/hibernate {(on ? "off" : "on")}", ct);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id} → {(on ? "ON" : "OFF")}");
            return MaintenanceActionResult.Ok(on
                ? "  - 休止状態を無効化しました (hiberfil.sys が削除されます)"
                : "  - 休止状態を有効化しました (Windows 既定)");
        }

        LoggerBootstrap.Log.Error($"{Id}: powercfg /hibernate (exit={result.ExitCode}): {result.StandardError}");
        return MaintenanceActionResult.Fail($"powercfg が失敗しました: {result.StandardError}");
    }

    private static bool IsHiberFilePresent()
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        return !string.IsNullOrEmpty(systemDrive) && File.Exists(Path.Combine(systemDrive, "hiberfil.sys"));
    }
}
