using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>
/// MsiEnumProductsEx・MsiEnumPatchesEx を全 SID (S-1-1-0)・全 context で列挙し、各登録の LocalPackage を集める。
/// 列挙・取得のどれか 1 件でも失敗すれば全体を失敗とする。読み取り専用。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MsiApiRegistrationSource : IInstallerRegistrationSource
{
    private const uint ContextAll = 7;
    private const uint PatchStateAll = 15;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorUnknownProperty = 1608;
    private const string AllUsers = "S-1-1-0";

    public InstallerRegistrationSnapshot Read(CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var failure = ReadProducts(paths, ct) ?? ReadPatches(paths, ct);
            return failure is null ? new(paths, null) : InstallerRegistrationSnapshot.Failed(failure);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return InstallerRegistrationSnapshot.Failed($"msi.dll を呼び出せません: {ex.Message}");
        }
    }

    private static string? ReadProducts(HashSet<string> paths, CancellationToken ct)
    {
        for (uint index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            var product = new StringBuilder(39);
            var sid = new StringBuilder(256);
            var sidLength = (uint)sid.Capacity;
            var status = MsiEnumProductsExW(null, AllUsers, ContextAll, index, product, out var context, sid, ref sidLength);
            if (status == ErrorMoreData)
            {
                sid = new StringBuilder((int)sidLength + 1);
                sidLength = (uint)sid.Capacity;
                status = MsiEnumProductsExW(null, AllUsers, ContextAll, index, product, out context, sid, ref sidLength);
            }

            if (status == ErrorNoMoreItems) return null;
            if (status != 0) return $"MsiEnumProductsEx が {status} を返しました (index {index})";

            var userSid = context == 4 ? null : sid.ToString();
            var failure = GetProductPackage(product.ToString(), userSid, context, paths);
            if (failure is not null) return failure;
        }
    }

    private static string? GetProductPackage(string product, string? userSid, uint context, HashSet<string> paths)
    {
        var value = new StringBuilder(260);
        var length = (uint)value.Capacity;
        var status = MsiGetProductInfoExW(product, userSid, context, "LocalPackage", value, ref length);
        if (status == ErrorMoreData)
        {
            value = new StringBuilder((int)length + 1);
            length = (uint)value.Capacity;
            status = MsiGetProductInfoExW(product, userSid, context, "LocalPackage", value, ref length);
        }

        return Collect(status, value, paths, $"製品 {product}");
    }

    private static string? ReadPatches(HashSet<string> paths, CancellationToken ct)
    {
        for (uint index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            var patch = new StringBuilder(39);
            var target = new StringBuilder(39);
            var sid = new StringBuilder(256);
            var sidLength = (uint)sid.Capacity;
            var status = MsiEnumPatchesExW(null, AllUsers, ContextAll, PatchStateAll, index, patch, target, out var context, sid, ref sidLength);
            if (status == ErrorMoreData)
            {
                sid = new StringBuilder((int)sidLength + 1);
                sidLength = (uint)sid.Capacity;
                status = MsiEnumPatchesExW(null, AllUsers, ContextAll, PatchStateAll, index, patch, target, out context, sid, ref sidLength);
            }

            if (status == ErrorNoMoreItems) return null;
            if (status != 0) return $"MsiEnumPatchesEx が {status} を返しました (index {index})";

            var userSid = context == 4 ? null : sid.ToString();
            var failure = GetPatchPackage(patch.ToString(), target.ToString(), userSid, context, paths);
            if (failure is not null) return failure;
        }
    }

    private static string? GetPatchPackage(string patch, string product, string? userSid, uint context, HashSet<string> paths)
    {
        var value = new StringBuilder(260);
        var length = (uint)value.Capacity;
        var status = MsiGetPatchInfoExW(patch, product, userSid, context, "LocalPackage", value, ref length);
        if (status == ErrorMoreData)
        {
            value = new StringBuilder((int)length + 1);
            length = (uint)value.Capacity;
            status = MsiGetPatchInfoExW(patch, product, userSid, context, "LocalPackage", value, ref length);
        }

        return Collect(status, value, paths, $"パッチ {patch}");
    }

    /// <summary>プロパティが無い (1608) 以外の失敗は列挙失敗として返す。</summary>
    private static string? Collect(uint status, StringBuilder value, HashSet<string> paths, string subject)
    {
        if (status == ErrorUnknownProperty) return null;
        if (status != 0) return $"{subject} の LocalPackage を取得できません ({status})";
        if (value.Length > 0) paths.Add(value.ToString());
        return null;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumProductsExW(string? productCode, string? userSid, uint context, uint index,
        StringBuilder installedProductCode, out uint installedContext, StringBuilder sid, ref uint sidLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(string productCode, string? userSid, uint context, string property,
        StringBuilder value, ref uint valueLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumPatchesExW(string? productCode, string? userSid, uint context, uint filter, uint index,
        StringBuilder patchCode, StringBuilder targetProductCode, out uint targetContext, StringBuilder targetSid, ref uint targetSidLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetPatchInfoExW(string patchCode, string productCode, string? userSid, uint context, string property,
        StringBuilder value, ref uint valueLength);
}

/// <summary>
/// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData 以下の全 SID の
/// Products\*\InstallProperties と Patches\* にある LocalPackage を集める。読み取り専用。
/// UserData が無い・読めない場合は失敗とする (登録なしと誤認して全件を孤児にしないため)。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerUserDataRegistrationSource : IInstallerRegistrationSource
{
    private const string UserDataKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";

    public InstallerRegistrationSnapshot Read(CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var userData = machine.OpenSubKey(UserDataKey);
            if (userData is null)
            {
                return InstallerRegistrationSnapshot.Failed("UserData キーを開けません。");
            }

            foreach (var sid in userData.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var sidKey = userData.OpenSubKey(sid);
                if (sidKey is not null)
                {
                    CollectSid(sidKey, paths, ct);
                }
            }

            return new(paths, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return InstallerRegistrationSnapshot.Failed($"UserData を読み取れません: {ex.Message}");
        }
    }

    private static void CollectSid(RegistryKey sidKey, HashSet<string> paths, CancellationToken ct)
    {
        using (var products = sidKey.OpenSubKey("Products"))
        {
            foreach (var code in products?.GetSubKeyNames() ?? [])
            {
                ct.ThrowIfCancellationRequested();
                using var properties = products!.OpenSubKey(code + @"\InstallProperties");
                AddPackage(properties, paths);
            }
        }

        using var patches = sidKey.OpenSubKey("Patches");
        foreach (var code in patches?.GetSubKeyNames() ?? [])
        {
            ct.ThrowIfCancellationRequested();
            using var patch = patches!.OpenSubKey(code);
            AddPackage(patch, paths);
        }
    }

    private static void AddPackage(RegistryKey? key, HashSet<string> paths)
    {
        if (key?.GetValue("LocalPackage") is string { Length: > 0 } path)
        {
            paths.Add(path);
        }
    }
}
