using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// ゴミ箱を空にする。PowerShell の Clear-RecycleBin ではなく Shell API を直接呼び、
/// 空にする前の容量を取得して解放量を報告する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleBinCleanupAction : IMaintenanceAction
{
    private const int NoConfirmation = 0x00000001;
    private const int NoProgressUi = 0x00000002;
    private const int NoSound = 0x00000004;
    private const int SOk = 0;
    private const int SFalse = 1;

    /// <summary>ゴミ箱が既に空のときに返る HRESULT (E_UNEXPECTED)。異常ではない。</summary>
    private const int EUnexpected = unchecked((int)0x8000FFFF);

    public string Id => "cleanup-recycle-bin";

    public string Label => "ゴミ箱を空にする";

    public string Description =>
        "すべてのドライブのゴミ箱を空にして、削除済みファイルが占めている領域を解放します。" +
        "確認ダイアログは出ず、実行した時点で元に戻せなくなります。必要なファイルが入っていないか、先にゴミ箱の中身を確認してください。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                var freedBytes = TryQuerySize();

                // rootPath = null で全ドライブのゴミ箱が対象になる。
                var hresult = SHEmptyRecycleBin(nint.Zero, null, NoConfirmation | NoProgressUi | NoSound);
                if (hresult is not (SOk or SFalse or EUnexpected))
                {
                    LoggerBootstrap.Log.Error($"{Id}: SHEmptyRecycleBin hr=0x{hresult:X8}");
                    return MaintenanceActionResult.Fail(
                        $"ゴミ箱を空にできませんでした (HRESULT=0x{hresult:X8})");
                }

                LoggerBootstrap.Log.Info($"{Id}: 完了 bytes={freedBytes?.ToString() ?? "unknown"}");
                return MaintenanceActionResult.Ok(freedBytes is { } bytes
                    ? $"  - ゴミ箱を空にして {FileCleanupEngine.FormatBytes(bytes)} を解放しました"
                    : "  - ゴミ箱を空にしました");
            },
            ct);

    /// <summary>空にする前のゴミ箱の合計サイズ。取得できない環境では null。</summary>
    private static long? TryQuerySize()
    {
        var info = new ShQueryRecycleBinInfo { CbSize = Marshal.SizeOf<ShQueryRecycleBinInfo>() };
        return SHQueryRecycleBin(null, ref info) == SOk ? info.Size : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRecycleBinInfo
    {
        public int CbSize;
        public long Size;
        public long NumItems;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(nint owner, string? rootPath, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRecycleBinInfo info);
}
