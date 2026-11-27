using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

/// <summary>ごみ箱 API の境界。テストでは実機のごみ箱に触れない実装へ差し替える。</summary>
internal interface IRecycleBinApi
{
    /// <summary>全ドライブのごみ箱の件数とサイズ (HRESULT を返す)。</summary>
    int Query(out long items, out long bytes);

    /// <summary>確認・進捗・音なしで全ドライブのごみ箱を空にする (HRESULT を返す)。</summary>
    int Empty();
}

/// <summary>
/// ごみ箱を <c>SHQueryRecycleBin</c> / <c>SHEmptyRecycleBin</c> で空にする。
/// ごみ箱は呼び出したプロセスの利用者のものだけを対象にする。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecycleBinHandler : ICleanupHandler
{
    private readonly IRecycleBinApi _api;

    public RecycleBinHandler() : this(new ShellRecycleBinApi()) { }

    internal RecycleBinHandler(IRecycleBinApi api) => _api = api;

    public string Id => "recycleBin";

    public IReadOnlyList<string> Services => [];

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var hr = _api.Query(out var items, out var bytes);
        if (hr < 0)
        {
            return Task.FromResult(new CleanupHandlerScan(
                CleanupHandlerScanState.Unavailable, Detail: Describe("RecycleQueryFailed", hr)));
        }

        return Task.FromResult(new CleanupHandlerScan(
            items > 0 ? CleanupHandlerScanState.Found : CleanupHandlerScanState.Empty, items, bytes));
    }

    public Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(new CleanupHandlerResult(CleanupHandlerStatus.Canceled));
        }

        var beforeHr = _api.Query(out var beforeItems, out var beforeBytes);
        if (beforeHr < 0)
        {
            return Task.FromResult(CleanupHandlerResult.Failed(Describe("RecycleQueryFailed", beforeHr)));
        }

        if (beforeItems == 0)
        {
            return Task.FromResult(new CleanupHandlerResult(CleanupHandlerStatus.NothingToDo));
        }

        progress?.Report("  - ごみ箱を空にしています");
        var emptyHr = _api.Empty();
        var afterHr = _api.Query(out var afterItems, out var afterBytes);
        return Task.FromResult(Classify(beforeItems, beforeBytes, emptyHr, afterHr, afterItems, afterBytes));
    }

    /// <summary>空にした後の再照会で結果を決める。API の戻り値だけでは成功にしない。</summary>
    internal static CleanupHandlerResult Classify(
        long beforeItems, long beforeBytes, int emptyHr, int afterHr, long afterItems, long afterBytes)
    {
        if (afterHr < 0)
        {
            return CleanupHandlerResult.Failed(Describe("RecycleVerifyFailed", afterHr));
        }

        if (afterItems == 0)
        {
            return new CleanupHandlerResult(CleanupHandlerStatus.Succeeded, beforeItems, beforeBytes);
        }

        var removed = Math.Max(0, beforeItems - afterItems);
        var freed = Math.Max(0, beforeBytes - afterBytes);
        var reason = CleanupMessage.Of("RecycleRemaining", afterItems, emptyHr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        return removed > 0
            ? new CleanupHandlerResult(CleanupHandlerStatus.Partial, removed, freed) { Details = [reason] }
            : CleanupHandlerResult.Failed(reason);
    }

    private static string Describe(string name, int hr) =>
        CleanupMessage.Of(name, hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));

    [SupportedOSPlatform("windows")]
    private sealed class ShellRecycleBinApi : IRecycleBinApi
    {
        private const uint NoConfirmation = 0x1;
        private const uint NoProgressUi = 0x2;
        private const uint NoSound = 0x4;

        public int Query(out long items, out long bytes)
        {
            var info = new ShQueryRbInfo { cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
            var hr = SHQueryRecycleBinW(null, ref info);
            items = hr >= 0 ? info.i64NumItems : 0;
            bytes = hr >= 0 ? info.i64Size : 0;
            return hr;
        }

        public int Empty() => SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);

        [StructLayout(LayoutKind.Sequential)]
        private struct ShQueryRbInfo
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
    }
}
