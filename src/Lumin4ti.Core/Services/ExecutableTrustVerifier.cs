using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Lumin4ti.Core.Services;

/// <summary>実行ファイルの Authenticode 署名と署名者を Windows の信頼ポリシーで検証する。</summary>
[SupportedOSPlatform("windows")]
public static class ExecutableTrustVerifier
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>
    /// Microsoft Store 配下の winget など、OS 提供物を Authenticode と Common Name で検証する。
    /// </summary>
    public static bool TryVerify(
        string? filePath,
        string expectedCommonName,
        out string failureReason)
        => TryVerifyCore(
            filePath,
            AuthenticodeRevocationMode.CacheOnly,
            certificate => HasExpectedCommonName(certificate.Subject, expectedCommonName),
            out failureReason);

    private static bool TryVerifyCore(
        string? filePath,
        AuthenticodeRevocationMode revocationMode,
        Func<X509Certificate2, bool> signerPolicy,
        out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath) || !File.Exists(filePath))
        {
            failureReason = "検証対象の実行ファイルが見つかりません";
            return false;
        }

        var pathPointer = nint.Zero;
        var fileInfoPointer = nint.Zero;
        var trustData = default(WinTrustData);
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(filePath);
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            trustData = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustDataUiChoice.None,
                RevocationChecks = WinTrustDataRevocationChecks.WholeChain,
                UnionChoice = WinTrustDataChoice.File,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustDataStateAction.Verify,
                ProviderFlags = BuildProviderFlags(revocationMode),
            };

            var action = WinTrustActionGenericVerifyV2;
            var status = WinVerifyTrust(nint.Zero, ref action, ref trustData);
            if (status != 0)
            {
                failureReason = $"Authenticode 署名を検証できませんでした (0x{status:X8})";
                return false;
            }

            using var certificate = GetVerifiedSignerCertificate(trustData.StateData);
            if (!signerPolicy(certificate))
            {
                failureReason = $"署名者が想定と異なります ({certificate.Subject})";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is
            CryptographicException or
            ExternalException or
            ArgumentException or
            OverflowException)
        {
            failureReason = $"署名検証に失敗しました: {ex.Message}";
            return false;
        }
        finally
        {
            if (trustData.StateData != nint.Zero)
            {
                trustData.StateAction = WinTrustDataStateAction.Close;
                var action = WinTrustActionGenericVerifyV2;
                _ = WinVerifyTrust(nint.Zero, ref action, ref trustData);
            }

            if (fileInfoPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            if (pathPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static X509Certificate2 GetVerifiedSignerCertificate(nint stateData)
    {
        if (stateData == nint.Zero)
        {
            throw new CryptographicException("署名検証状態を取得できませんでした");
        }

        var providerData = WTHelperProvDataFromStateData(stateData);
        var signer = providerData == nint.Zero
            ? nint.Zero
            : WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
        var providerCertificate = signer == nint.Zero
            ? nint.Zero
            : WTHelperGetProvCertFromChain(signer, 0);
        if (providerCertificate == nint.Zero)
        {
            throw new CryptographicException("検証済み署名者の証明書を取得できませんでした");
        }

        var provider = Marshal.PtrToStructure<CryptProviderCertificate>(providerCertificate);
        if (provider.CertificateContext == nint.Zero)
        {
            throw new CryptographicException("署名者の証明書コンテキストがありません");
        }

        var context = Marshal.PtrToStructure<CertificateContext>(provider.CertificateContext);
        if (context.EncodedCertificate == nint.Zero || context.EncodedCertificateSize == 0)
        {
            throw new CryptographicException("署名者証明書のデータが空です");
        }

        var raw = new byte[checked((int)context.EncodedCertificateSize)];
        Marshal.Copy(context.EncodedCertificate, raw, 0, raw.Length);
        return X509CertificateLoader.LoadCertificate(raw);
    }

    internal static WinTrustProviderFlags BuildProviderFlags(AuthenticodeRevocationMode mode) =>
        WinTrustProviderFlags.RevocationCheckChainExcludeRoot |
        (mode == AuthenticodeRevocationMode.CacheOnly
            ? WinTrustProviderFlags.CacheOnlyUrlRetrieval
            : WinTrustProviderFlags.None);

    internal static bool HasExpectedCommonName(string subject, string expectedCommonName)
    {
        var expected = "CN=" + expectedCommonName;
        return subject.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               subject.StartsWith(expected + ",", StringComparison.OrdinalIgnoreCase);
    }

    // 昇格対象の隣接ディレクトリに置かれた同名 DLL を読み込まない。
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        nint windowHandle,
        [In] ref Guid actionId,
        [In, Out] ref WinTrustData trustData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public WinTrustDataUiChoice UiChoice;
        public WinTrustDataRevocationChecks RevocationChecks;
        public WinTrustDataChoice UnionChoice;
        public nint FileInfo;
        public WinTrustDataStateAction StateAction;
        public nint StateData;
        public nint UrlReference;
        public WinTrustProviderFlags ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        public uint Size;
        public nint CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        public uint EncodingType;
        public nint EncodedCertificate;
        public uint EncodedCertificateSize;
        public nint CertificateInfo;
        public nint CertificateStore;
    }

    private enum WinTrustDataUiChoice : uint
    {
        None = 2,
    }

    private enum WinTrustDataRevocationChecks : uint
    {
        WholeChain = 1,
    }

    private enum WinTrustDataChoice : uint
    {
        File = 1,
    }

    private enum WinTrustDataStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [Flags]
    internal enum WinTrustProviderFlags : uint
    {
        None = 0,
        RevocationCheckChainExcludeRoot = 0x00000080,
        CacheOnlyUrlRetrieval = 0x00001000,
    }
}

public enum AuthenticodeRevocationMode
{
    Online,
    CacheOnly,
}
