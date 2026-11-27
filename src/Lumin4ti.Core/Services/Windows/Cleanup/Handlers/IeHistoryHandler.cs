using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

/// <summary>IE 系履歴の消去対象。<c>IntelliForms\Storage2</c> (保存パスワード) はここに含めない。</summary>
internal enum IeHistoryRegistryTarget
{
    /// <summary>HKCU\Software\Microsoft\Internet Explorer\TypedURLs の値。</summary>
    TypedUrls,

    /// <summary>HKCU\Software\Microsoft\Internet Explorer\TypedURLsTime の値。</summary>
    TypedUrlsTime,

    /// <summary>HKCU\Software\Microsoft\Internet Explorer\IntelliForms\Storage1 の値 (フォーム入力履歴)。</summary>
    IntelliFormsStorage1,
}

/// <summary>IE 系履歴の OS 操作の境界。テストでは実機のレジストリ・履歴に触れない実装へ差し替える。</summary>
internal interface IIeHistoryStore
{
    /// <summary>値の数を数える。キーが無ければ 0。失敗は Win32 エラーコード。</summary>
    int CountValues(IeHistoryRegistryTarget target, out int count);

    /// <summary>キーは残して値だけ消す。失敗は Win32 エラーコード (消せた数は removed)。</summary>
    int DeleteValues(IeHistoryRegistryTarget target, out int removed);

    /// <summary><c>IUrlHistoryStg2::ClearHistory</c> を呼ぶ (HRESULT)。</summary>
    int ClearUrlHistory();
}

/// <summary>
/// IE 系の閲覧履歴を消す。<c>IUrlHistoryStg2::ClearHistory</c>、TypedURLs (とその時刻)、
/// IntelliForms の <c>Storage1</c> だけが対象で、<c>Storage2</c> (保存パスワード) には触れない。
/// 履歴は呼び出したプロセスの利用者 (HKCU) のものだけを対象にする。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IeHistoryHandler : ICleanupHandler
{
    private readonly IIeHistoryStore _store;

    public IeHistoryHandler() : this(new SystemIeHistoryStore()) { }

    internal IeHistoryHandler(IIeHistoryStore store) => _store = store;

    public string Id => "ieHistory";

    public IReadOnlyList<string> Services => [];

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long items = 0;
        foreach (var target in CountedTargets)
        {
            var error = _store.CountValues(target, out var count);
            if (error != 0)
            {
                return Task.FromResult(new CleanupHandlerScan(
                    CleanupHandlerScanState.Unavailable, Detail: CleanupMessage.Of("IeQueryFailed", target, error)));
            }

            items += count;
        }

        // 履歴 (IUrlHistoryStg2) の件数は列挙せず、クリア自体は常に実行できるため件数不明の扱いにする。
        return Task.FromResult(new CleanupHandlerScan(CleanupHandlerScanState.Unmeasurable, items));
    }

    public Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(new CleanupHandlerResult(CleanupHandlerStatus.Canceled));
        }

        progress?.Report("  - IE 系履歴を消去しています");
        var failures = new List<string>();
        long removedTotal = 0;
        var executed = 1;
        var succeeded = 0;

        var hr = _store.ClearUrlHistory();
        if (hr >= 0) succeeded++;
        else failures.Add(CleanupMessage.Of("IeUrlHistoryFailed", hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)));

        foreach (var target in DeletedTargets)
        {
            if (ct.IsCancellationRequested) break;
            executed++;
            var error = _store.DeleteValues(target, out var removed);
            removedTotal += target == IeHistoryRegistryTarget.TypedUrlsTime ? 0 : removed;
            if (error == 0) succeeded++;
            else failures.Add(CleanupMessage.Of("IeClearFailed", target, error));
        }

        return Task.FromResult(Classify(DeletedTargets.Length + 1, executed, succeeded, removedTotal, failures));
    }

    /// <summary>全工程が成功した場合だけ成功にする。取消で飛ばした工程があれば取消 (失敗が無い場合)。</summary>
    internal static CleanupHandlerResult Classify(
        int total, int executed, int succeeded, long items, IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return executed < total
                ? new CleanupHandlerResult(CleanupHandlerStatus.Canceled, items)
                : new CleanupHandlerResult(CleanupHandlerStatus.Succeeded, items);
        }

        return succeeded > 0
            ? new CleanupHandlerResult(CleanupHandlerStatus.Partial, items) { Details = failures }
            : new CleanupHandlerResult(CleanupHandlerStatus.Failed) { Details = failures };
    }

    private static readonly IeHistoryRegistryTarget[] CountedTargets =
        [IeHistoryRegistryTarget.TypedUrls, IeHistoryRegistryTarget.IntelliFormsStorage1];

    private static readonly IeHistoryRegistryTarget[] DeletedTargets =
        [IeHistoryRegistryTarget.TypedUrls, IeHistoryRegistryTarget.TypedUrlsTime, IeHistoryRegistryTarget.IntelliFormsStorage1];

    [SupportedOSPlatform("windows")]
    private sealed class SystemIeHistoryStore : IIeHistoryStore
    {
        private const int ErrorFileNotFound = 2;
        private const int ErrorNoMoreItems = 259;
        private const int ErrorCantAccessFile = 1920;
        private const uint RegOptionOpenLink = 0x8;
        private const uint KeyQueryValue = 0x1;
        private const uint KeySetValue = 0x2;
        private const uint RegLink = 6;
        private static readonly Guid UrlHistoryClsid = new("3C374A40-BAE4-11CF-BF7D-00AA006946EE");

        public int CountValues(IeHistoryRegistryTarget target, out int count)
        {
            count = 0;
            var error = Open(target, KeyQueryValue, out var key);
            if (error == ErrorFileNotFound) return 0;
            if (error != 0) return error;
            using (key!)
            {
                return QueryCount(key!, out count);
            }
        }

        public int DeleteValues(IeHistoryRegistryTarget target, out int removed)
        {
            removed = 0;
            var error = Open(target, KeyQueryValue | KeySetValue, out var key);
            if (error == ErrorFileNotFound) return 0;
            if (error != 0) return error;
            using (key!)
            {
                var names = new List<string>();
                error = EnumerateNames(key!, names);
                if (error != 0) return error;
                foreach (var name in names)
                {
                    var deleteError = RegDeleteValueW(key!, name);
                    if (deleteError == 0) removed++;
                    else if (deleteError != ErrorFileNotFound) error = deleteError;
                }
            }

            return error;
        }

        public int ClearUrlHistory()
        {
            object? instance = null;
            try
            {
                var type = Type.GetTypeFromCLSID(UrlHistoryClsid, throwOnError: false);
                if (type is null) return unchecked((int)0x80040154);
                instance = Activator.CreateInstance(type);
                return ((IUrlHistoryStg2)instance!).ClearHistory();
            }
            catch (COMException ex)
            {
                return ex.HResult;
            }
            catch (InvalidCastException)
            {
                return unchecked((int)0x80004002);
            }
            finally
            {
                if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }

        /// <summary>HKCU から各階層をリンク非追従で開く。リンクが 1 つでもあれば拒否する。</summary>
        private static int Open(IeHistoryRegistryTarget target, uint access, out SafeRegistryHandle? handle)
        {
            handle = null;
            string[] path = target switch
            {
                IeHistoryRegistryTarget.TypedUrls => ["Software", "Microsoft", "Internet Explorer", "TypedURLs"],
                IeHistoryRegistryTarget.TypedUrlsTime => ["Software", "Microsoft", "Internet Explorer", "TypedURLsTime"],
                _ => ["Software", "Microsoft", "Internet Explorer", "IntelliForms", "Storage1"],
            };

            var current = Registry.CurrentUser.Handle;
            var ownsCurrent = false;
            try
            {
                foreach (var name in path)
                {
                    var error = RegOpenKeyExW(current, name, RegOptionOpenLink, access, out var raw);
                    if (error != 0) return error;
                    var next = new SafeRegistryHandle(raw, ownsHandle: true);
                    var isLink = IsLink(next);
                    if (ownsCurrent) current.Dispose();
                    current = next;
                    ownsCurrent = true;
                    if (isLink) return ErrorCantAccessFile;
                }

                handle = current;
                ownsCurrent = false;
                return 0;
            }
            finally
            {
                if (ownsCurrent) current.Dispose();
            }
        }

        private static bool IsLink(SafeRegistryHandle key)
        {
            uint size = 0;
            return RegQueryValueExW(key, "SymbolicLinkValue", IntPtr.Zero, out var type, IntPtr.Zero, ref size) != ErrorFileNotFound
                && type == RegLink;
        }

        private static int QueryCount(SafeRegistryHandle key, out int count)
        {
            var error = RegQueryInfoKeyW(key, null, IntPtr.Zero, IntPtr.Zero, out _, out _, out _, out var values,
                out _, out _, out _, IntPtr.Zero);
            count = error == 0 ? (int)Math.Min(values, int.MaxValue) : 0;
            return error;
        }

        private static int EnumerateNames(SafeRegistryHandle key, List<string> names)
        {
            var buffer = new char[16384];
            for (uint index = 0; ; index++)
            {
                var length = (uint)buffer.Length;
                var error = RegEnumValueW(key, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (error == ErrorNoMoreItems) return 0;
                if (error != 0) return error;
                names.Add(new string(buffer, 0, (int)length));
            }
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyExW(SafeRegistryHandle parent, string subKey, uint options, uint access, out IntPtr result);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueExW(SafeRegistryHandle key, string valueName, IntPtr reserved, out uint type, IntPtr data, ref uint dataSize);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int RegQueryInfoKeyW(SafeRegistryHandle key, char[]? className, IntPtr classNameLength, IntPtr reserved,
            out uint subKeys, out uint maxSubKeyLength, out uint maxClassLength, out uint values,
            out uint maxValueNameLength, out uint maxValueLength, out uint securityDescriptor, IntPtr lastWriteTime);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int RegEnumValueW(SafeRegistryHandle key, uint index, [Out] char[] valueName, ref uint valueNameLength,
            IntPtr reserved, IntPtr type, IntPtr data, IntPtr dataSize);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int RegDeleteValueW(SafeRegistryHandle key, string valueName);
    }

    /// <summary>
    /// urlhist.h の IUrlHistoryStg2。IUrlHistoryStg の 5 メソッドに続き AddUrlAndNotify、ClearHistory の順。
    /// ClearHistory の位置を誤ると別の履歴操作を呼ぶため、宣言順を変えない。
    /// </summary>
    [ComImport]
    [Guid("AFA0DC11-C313-11D0-831A-00C04FD5AE38")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUrlHistoryStg2
    {
        [PreserveSig] int AddUrl([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.LPWStr)] string? title, uint flags);
        [PreserveSig] int DeleteUrl([MarshalAs(UnmanagedType.LPWStr)] string url, uint flags);
        [PreserveSig] int QueryUrl([MarshalAs(UnmanagedType.LPWStr)] string url, uint flags, IntPtr statUrl);
        [PreserveSig] int BindToObject([MarshalAs(UnmanagedType.LPWStr)] string url, ref Guid riid, out IntPtr result);
        [PreserveSig] int EnumUrls(out IntPtr enumerator);
        [PreserveSig] int AddUrlAndNotify([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.LPWStr)] string? title,
            uint flags, int writeHistory, IntPtr notify, IntPtr folder);
        [PreserveSig] int ClearHistory();
    }
}
