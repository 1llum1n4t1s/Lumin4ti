namespace Lumin4ti.Core.Interfaces;

/// <summary>現在接続されていない、過去にインストール済みの PnP デバイス。</summary>
public sealed record DisconnectedDevice(
    string InstanceId,
    string DisplayName,
    string ClassName,
    string Manufacturer);

/// <summary>未接続デバイスの削除結果。</summary>
public enum DisconnectedDeviceRemovalStatus
{
    Removed,
    AlreadyAbsent,
    Reconnected,
    Protected,
    Failed,
}

/// <summary>未接続デバイス 1 件の削除結果。</summary>
public sealed record DisconnectedDeviceRemovalResult(
    DisconnectedDeviceRemovalStatus Status,
    bool RequiresRestart = false,
    int ErrorCode = 0,
    string ErrorMessage = "");

/// <summary>
/// Windows の PnP デバイスストアから未接続デバイスを列挙し、選択されたデバイスを削除する。
/// </summary>
public interface IDisconnectedDeviceService
{
    Task<IReadOnlyList<DisconnectedDevice>> GetDisconnectedDevicesAsync(
        CancellationToken ct = default);

    Task<DisconnectedDeviceRemovalResult> RemoveDisconnectedDeviceAsync(
        string instanceId,
        CancellationToken ct = default);
}
