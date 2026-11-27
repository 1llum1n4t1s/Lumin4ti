using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>Windows Installer が製品コードを把握しているか。</summary>
internal enum MsiProductStatus
{
    /// <summary>Windows Installer に登録されていない (INSTALLSTATE_UNKNOWN)。</summary>
    NotRegistered,

    /// <summary>登録されている (導入済み・削除済み・アドバタイズ・壊れた状態を含む)。</summary>
    Registered,

    /// <summary>引数不正・照会失敗などで判定できない。</summary>
    Indeterminate,
}

/// <summary>MSI 製品の状態照会。テストでは差し替える。</summary>
internal interface IMsiProductState
{
    /// <summary><paramref name="productCode"/> (<c>{GUID}</c> 形式) の状態を返す。</summary>
    MsiProductStatus Query(string productCode);
}

/// <summary><c>MsiQueryProductStateW</c> による実装。外部プロセスは使わない。</summary>
[SupportedOSPlatform("windows")]
internal sealed class MsiProductStateQuery : IMsiProductState
{
    public MsiProductStatus Query(string productCode)
    {
        try
        {
            return FromInstallState(MsiQueryProductState(productCode));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return MsiProductStatus.Indeterminate;
        }
    }

    /// <summary>INSTALLSTATE の数値を 3 値へ写す。-1 (UNKNOWN) だけが未登録、0 以上は登録済み、それ以外 (-2 INVALIDARG など) は不明。</summary>
    internal static MsiProductStatus FromInstallState(int state) => state switch
    {
        -1 => MsiProductStatus.NotRegistered,
        >= 0 => MsiProductStatus.Registered,
        _ => MsiProductStatus.Indeterminate,
    };

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string product);
}
