using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lumin4ti.Core.Interfaces;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>
/// SetupAPI で「インストール済みだが現在は存在しない」PnP デバイスを検出し、
/// NewDev の DiUninstallDevice で削除する。ソフトウェア／仮想デバイスを含めて列挙し、
/// 利用者が選択した項目だけを削除する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDisconnectedDeviceService : IDisconnectedDeviceService
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;

    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpMfg = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorNoSuchDevice = 433;
    private const int ErrorNotFound = 1168;
    private const int MaximumPropertyBytes = 1024 * 1024;

    public Task<IReadOnlyList<DisconnectedDevice>> GetDisconnectedDevicesAsync(
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DisconnectedDevice>>(() => EnumerateDisconnectedDevices(ct), ct);

    public Task<DisconnectedDeviceRemovalResult> RemoveDisconnectedDeviceAsync(
        string instanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return Task.Run(() => RemoveDisconnectedDevice(instanceId, ct), ct);
    }

    private static IReadOnlyList<DisconnectedDevice> EnumerateDisconnectedDevices(CancellationToken ct)
    {
        var presentIds = EnumerateDeviceIds(DigcfAllClasses | DigcfPresent, ct);
        var installedDevices = EnumerateDevices(DigcfAllClasses, ct);

        return installedDevices
            .Where(device => IsDisconnectedCandidate(device.InstanceId, presentIds.Contains(device.InstanceId)))
            .OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.ClassName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DisconnectedDeviceRemovalResult RemoveDisconnectedDevice(
        string instanceId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (EnumerateDeviceIds(DigcfAllClasses | DigcfPresent, ct).Contains(instanceId))
        {
            return new DisconnectedDeviceRemovalResult(DisconnectedDeviceRemovalStatus.Reconnected);
        }

        using var deviceInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (deviceInfoSet.IsInvalid)
        {
            return Failure(Marshal.GetLastWin32Error());
        }

        var deviceInfoData = SpDevInfoData.Create();
        if (!SetupDiOpenDeviceInfoW(
                deviceInfoSet,
                instanceId,
                IntPtr.Zero,
                0,
                ref deviceInfoData))
        {
            var error = Marshal.GetLastWin32Error();
            return error is ErrorNoSuchDevice or ErrorNotFound
                ? new DisconnectedDeviceRemovalResult(DisconnectedDeviceRemovalStatus.AlreadyAbsent)
                : Failure(error);
        }

        // 列挙後に再接続されたデバイスを削除しない。確認後の短い競合窓は SetupAPI 側の
        // デバイス状態検証へ委ねるが、通常の抜き差し競合はここで安全側へ倒す。
        ct.ThrowIfCancellationRequested();
        if (EnumerateDeviceIds(DigcfAllClasses | DigcfPresent, ct).Contains(instanceId))
        {
            return new DisconnectedDeviceRemovalResult(DisconnectedDeviceRemovalStatus.Reconnected);
        }

        ct.ThrowIfCancellationRequested();
        if (!DiUninstallDevice(
                IntPtr.Zero,
                deviceInfoSet,
                ref deviceInfoData,
                0,
                out var requiresRestart))
        {
            return Failure(Marshal.GetLastWin32Error());
        }

        return new DisconnectedDeviceRemovalResult(
            DisconnectedDeviceRemovalStatus.Removed,
            requiresRestart);
    }

    private static HashSet<string> EnumerateDeviceIds(uint flags, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var deviceInfoSet = OpenDeviceInfoSet(flags);

        EnumerateDeviceInfo(deviceInfoSet, ct, (set, data) =>
        {
            var instanceId = TryGetInstanceId(set, data);
            if (!string.IsNullOrEmpty(instanceId))
            {
                ids.Add(instanceId);
            }
        });

        return ids;
    }

    private static IReadOnlyList<DisconnectedDevice> EnumerateDevices(uint flags, CancellationToken ct)
    {
        var devices = new Dictionary<string, DisconnectedDevice>(StringComparer.OrdinalIgnoreCase);
        using var deviceInfoSet = OpenDeviceInfoSet(flags);

        EnumerateDeviceInfo(deviceInfoSet, ct, (set, data) =>
        {
            var instanceId = TryGetInstanceId(set, data);
            if (string.IsNullOrEmpty(instanceId))
            {
                return;
            }

            var displayName = GetStringProperty(set, data, SpdrpFriendlyName);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = GetStringProperty(set, data, SpdrpDeviceDesc);
            }

            devices[instanceId] = new DisconnectedDevice(
                instanceId,
                string.IsNullOrWhiteSpace(displayName) ? instanceId : displayName,
                GetStringProperty(set, data, SpdrpClass),
                GetStringProperty(set, data, SpdrpMfg));
        });

        return devices.Values.ToArray();
    }

    private static SafeDeviceInfoSetHandle OpenDeviceInfoSet(uint flags)
    {
        var deviceInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, flags);
        if (!deviceInfoSet.IsInvalid)
        {
            return deviceInfoSet;
        }

        var error = Marshal.GetLastWin32Error();
        deviceInfoSet.Dispose();
        throw new Win32Exception(error);
    }

    private static void EnumerateDeviceInfo(
        SafeDeviceInfoSetHandle deviceInfoSet,
        CancellationToken ct,
        Action<SafeDeviceInfoSetHandle, SpDevInfoData> visitor)
    {
        for (uint index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            var deviceInfoData = SpDevInfoData.Create();
            if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems)
                {
                    return;
                }

                throw new Win32Exception(error);
            }

            visitor(deviceInfoSet, deviceInfoData);
        }
    }

    private static string TryGetInstanceId(
        SafeDeviceInfoSetHandle deviceInfoSet,
        SpDevInfoData deviceInfoData)
    {
        if (!SetupDiGetDeviceInstanceIdW(
                deviceInfoSet,
                ref deviceInfoData,
                null,
                0,
                out var requiredSize))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer || requiredSize <= 1)
            {
                return string.Empty;
            }
        }

        var buffer = new StringBuilder(requiredSize);
        return SetupDiGetDeviceInstanceIdW(
            deviceInfoSet,
            ref deviceInfoData,
            buffer,
            buffer.Capacity,
            out _)
            ? buffer.ToString()
            : string.Empty;
    }

    private static string GetStringProperty(
        SafeDeviceInfoSetHandle deviceInfoSet,
        SpDevInfoData deviceInfoData,
        uint property)
    {
        if (!SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfoData,
                property,
                out _,
                null,
                0,
                out var requiredSize))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
            {
                return string.Empty;
            }
        }

        if (requiredSize == 0 || requiredSize > MaximumPropertyBytes)
        {
            return string.Empty;
        }

        var buffer = new byte[requiredSize];
        if (!SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfoData,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out var written))
        {
            return string.Empty;
        }

        var byteCount = (int)Math.Min(written, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, byteCount).TrimEnd('\0');
    }

    internal static bool IsDisconnectedCandidate(string instanceId, bool isPresent) =>
        !isPresent && !string.IsNullOrWhiteSpace(instanceId);

    private static DisconnectedDeviceRemovalResult Failure(int errorCode) =>
        new(
            DisconnectedDeviceRemovalStatus.Failed,
            ErrorCode: errorCode,
            ErrorMessage: new Win32Exception(errorCode).Message);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public nuint Reserved;

        public static SpDevInfoData Create() => new()
        {
            Size = (uint)Marshal.SizeOf<SpDevInfoData>(),
        };
    }

    private sealed class SafeDeviceInfoSetHandle : SafeHandleMinusOneIsInvalid
    {
        private SafeDeviceInfoSetHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeDeviceInfoSetHandle SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        SafeDeviceInfoSetHandle deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder? deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeDeviceInfoSetHandle SetupDiCreateDeviceInfoList(
        IntPtr classGuid,
        IntPtr parentWindow);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(
        SafeDeviceInfoSetHandle deviceInfoSet,
        string deviceInstanceId,
        IntPtr parentWindow,
        uint openFlags,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiUninstallDevice(
        IntPtr parentWindow,
        SafeDeviceInfoSetHandle deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool requiresRestart);
}
