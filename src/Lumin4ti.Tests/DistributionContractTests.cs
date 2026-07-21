using System.Xml.Linq;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DistributionContractTests
{
    [TestMethod]
    public void 配布契約はVelopackのPerMachineMsiとR2更新を維持する()
    {
        var repositoryRoot = FindRepositoryRoot();
        var uiProject = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Lumin4ti.UI",
            "Lumin4ti.UI.csproj"));
        var releaseScript = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "scripts",
            "release-local.ps1"));
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var appSettings = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Lumin4ti.Core",
            "Models",
            "AppSettings.cs"));

        StringAssert.Contains(uiProject, "PackageReference Include=\"Velopack\"");
        StringAssert.Contains(releaseScript, "vpk pack");
        StringAssert.Contains(releaseScript, "--msi");
        StringAssert.Contains(releaseScript, "--instLocation PerMachine");
        StringAssert.Contains(releaseScript, "Where-Object { $_.Name -notlike '*-Setup.exe' }");
        StringAssert.Contains(releaseScript, "旧PerUserインストーラ");
        StringAssert.Contains(readme, "Lumin4ti-win.msi");
        Assert.IsFalse(readme.Contains("Lumin4ti-win-Setup.exe", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(appSettings, "https://lumin4ti.nephilim.jp");
        StringAssert.Contains(appSettings, "UpdateChannel => \"win\"");

        Assert.IsFalse(
            releaseScript.Contains("wix build", StringComparison.OrdinalIgnoreCase) ||
            releaseScript.Contains(".msix", StringComparison.OrdinalIgnoreCase),
            "Velopack以外のインストーラー方式へ変更するには個別の明示承認が必要です。");
    }

    [TestMethod]
    public void PerUser移行は通常の管理者昇格より前に実行する()
    {
        var repositoryRoot = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Lumin4ti.UI",
            "Program.cs"));
        var migration = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Lumin4ti.Core",
            "Services",
            "Windows",
            "WindowsPerMachineMigration.cs"));

        var migrationIndex = program.IndexOf(
            "WindowsPerMachineMigration.HandleStartupAsync",
            StringComparison.Ordinal);
        var elevationIndex = program.IndexOf(
            "WindowsElevationHelper.IsRunningAsAdministrator",
            StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, migrationIndex);
        Assert.IsGreaterThan(migrationIndex, elevationIndex);
        StringAssert.Contains(migration, "ExecutableTrustVerifier.TryVerify");
        StringAssert.Contains(migration, "Lumin4ti-win.msi");
        StringAssert.Contains(migration, "VELOPACK_INSTALLDIR=");
        StringAssert.Contains(migration, "Environment.SpecialFolder.ProgramFiles");
        StringAssert.Contains(migration, "HasLegacyInstallationArtifacts");
        StringAssert.Contains(migration, "TryDeleteTreeWithoutFollowingReparsePoints");
        Assert.IsFalse(
            migration.Contains("ProcessStartInfo(updaterPath)", StringComparison.Ordinal),
            "ユーザー書き込み可能な旧Updaterを新しい昇格プロセスから実行してはいけません。");
    }

    [TestMethod]
    public void 未承認のインストーラープロジェクトやNativeAot設定を含まない()
    {
        var repositoryRoot = FindRepositoryRoot();
        var forbiddenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wixproj",
            ".wxs",
            ".msixproj",
            ".vdproj",
        };
        var forbiddenFiles = EnumerateSourceFiles(repositoryRoot)
            .Where(path =>
                forbiddenExtensions.Contains(Path.GetExtension(path)) ||
                Path.GetFileName(path).Equals("Package.appxmanifest", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.HasCount(0, forbiddenFiles,
            $"未承認のインストーラー定義があります: {string.Join(", ", forbiddenFiles)}");

        var packagingConfigurations = EnumerateSourceFiles(repositoryRoot)
            .Where(path =>
                Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(ContainsAlternativePackagingConfiguration)
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.HasCount(0, packagingConfigurations,
            $"未承認のインストーラー／NativeAOT設定があります: {string.Join(", ", packagingConfigurations)}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumin4ti.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Lumin4ti.slnx を含むリポジトリルートを特定できません。");
    }

    private static IEnumerable<string> EnumerateSourceFiles(string repositoryRoot)
    {
        var directories = new Stack<string>();
        directories.Push(repositoryRoot);
        while (directories.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or "bin" or "obj" or "local-release" or "node_modules" ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                directories.Push(child);
            }
        }
    }

    private static bool ContainsAlternativePackagingConfiguration(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var rootSdk = document.Root?.Attribute("Sdk")?.Value ?? string.Empty;
        if (rootSdk.Contains("WixToolset.Sdk", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return document.Descendants().Any(element =>
            element.Name.LocalName.Equals("PublishAot", StringComparison.OrdinalIgnoreCase) &&
            element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
            element.Name.LocalName.Equals("GenerateAppxPackageOnBuild", StringComparison.OrdinalIgnoreCase) &&
            element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
            element.Name.LocalName.Equals("WindowsPackageType", StringComparison.OrdinalIgnoreCase) &&
            element.Value.Trim().Equals("MSIX", StringComparison.OrdinalIgnoreCase));
    }
}
