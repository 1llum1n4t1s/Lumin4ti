using System.Runtime.Versioning;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Lumin4ti.Core.Services;

/// <summary>
/// bare な実行ファイル名 (dism.exe 等) を確定的なフルパスへ解決する。
/// UseShellExecute=false で bare 名を起動すると CreateProcess の検索順序で
/// アプリ自身のインストールディレクトリが System32 より先に照合され、
/// 昇格プロセスがそこに植え付けられた偽バイナリを実行してしまう (バイナリプランティング LPE)。
/// 呼び出し側は論理名を渡し、ここで必ず System32 / 既知の正規パスへ解決してから起動する。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class SystemProcessResolver
{
    private const string DesktopAppInstallerFamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    private const string DesktopAppInstallerName = "Microsoft.DesktopAppInstaller";
    private const string MicrosoftPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    private const string MicrosoftPublisherId = "8wekyb3d8bbwe";

    public static string Resolve(string fileName)
    {
        // ディレクトリ指定済み (MpCmdRun.exe 等) も、相対パスや不存在を許さない。
        if (fileName.Contains('\\') || fileName.Contains('/'))
        {
            if (!Path.IsPathFullyQualified(fileName))
            {
                throw new FileNotFoundException($"相対パスの外部コマンドは実行できません: {fileName}", fileName);
            }

            var fullPath = Path.GetFullPath(fileName);
            return RequireExisting(fullPath, fileName);
        }

        var name = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";

        // powershell.exe は System32\WindowsPowerShell\v1.0 配下
        if (name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return RequireExisting(ps, name);
        }

        // ユーザー書込み可能な App Execution Alias は使わず、Store が検証した
        // DesktopAppInstaller パッケージの保護済み実体だけを起動する。
        if (name.Equals("winget.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveWinget();
        }

        // それ以外 (dism/powercfg/bcdedit/net/regsvr32/defrag/WSReset 等) は System32 直下
        var system32 = Path.Combine(Environment.SystemDirectory, name);
        return RequireExisting(system32, name);
    }

    private static string ResolveWinget()
    {
        try
        {
            var packages = new PackageManager()
                .FindPackagesForUser(string.Empty, DesktopAppInstallerFamilyName)
                .OrderByDescending(p => PackVersion(p.Id.Version));

            foreach (var package in packages)
            {
                if (!IsTrustedDesktopAppInstaller(package))
                {
                    continue;
                }

                var installLocation = Path.GetFullPath(package.InstalledLocation.Path);
                var windowsApps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsApps");
                var winget = Path.Combine(installLocation, "winget.exe");
                if (!IsPathInside(installLocation, windowsApps) ||
                    !IsPathInside(winget, installLocation) ||
                    !File.Exists(winget) ||
                    (File.GetAttributes(winget) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (ExecutableTrustVerifier.TryVerify(winget, "Microsoft Corporation", out _))
                {
                    return winget;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FileNotFoundException(
                "信頼できる Microsoft Desktop App Installer の winget.exe を確認できませんでした",
                "winget.exe",
                ex);
        }

        throw new FileNotFoundException(
            "信頼できる Microsoft Desktop App Installer の winget.exe が見つかりませんでした",
            "winget.exe");
    }

    private static bool IsTrustedDesktopAppInstaller(Package package) =>
        package.Id.Name.Equals(DesktopAppInstallerName, StringComparison.Ordinal) &&
        package.Id.Publisher.Equals(MicrosoftPublisher, StringComparison.Ordinal) &&
        package.Id.PublisherId.Equals(MicrosoftPublisherId, StringComparison.Ordinal) &&
        package.SignatureKind == PackageSignatureKind.Store &&
        !package.IsFramework &&
        !package.IsResourcePackage &&
        !package.IsBundle &&
        package.Status.VerifyIsOK();

    internal static bool IsPathInside(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTrustedDesktopAppInstallerIdentity(
        string name,
        string publisher,
        string publisherId,
        PackageSignatureKind signatureKind,
        bool statusOk) =>
        name.Equals(DesktopAppInstallerName, StringComparison.Ordinal) &&
        publisher.Equals(MicrosoftPublisher, StringComparison.Ordinal) &&
        publisherId.Equals(MicrosoftPublisherId, StringComparison.Ordinal) &&
        signatureKind == PackageSignatureKind.Store &&
        statusOk;

    private static ulong PackVersion(PackageVersion version) =>
        ((ulong)version.Major << 48) |
        ((ulong)version.Minor << 32) |
        ((ulong)version.Build << 16) |
        version.Revision;

    private static string RequireExisting(string fullPath, string logicalName)
    {
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"既知のシステムコマンドが正規パスに見つかりません: {logicalName}",
                fullPath);
        }

        return fullPath;
    }
}
