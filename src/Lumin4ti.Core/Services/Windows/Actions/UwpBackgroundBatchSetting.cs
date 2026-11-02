using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Windows.Management.Deployment;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>現在の全 UWP アプリのバックグラウンド実行を一括で禁止または許可する。</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class UwpBackgroundBatchSetting : IMaintenanceBatchSetting
{
    private readonly Func<IReadOnlyList<string>> _getTargetFamilyNames;
    private readonly IUwpBackgroundSettingsStore _settings;

    public UwpBackgroundBatchSetting()
        : this(GetInstalledPackageFamilyNames, new RegistryUwpBackgroundSettingsStore()) { }

    internal UwpBackgroundBatchSetting(
        Func<IReadOnlyList<string>> getTargetFamilyNames,
        IUwpBackgroundSettingsStore settings)
    {
        _getTargetFamilyNames = getTargetFamilyNames;
        _settings = settings;
    }

    public string Id => "uwp-background-off";
    public string Label => "UWP バックグラウンド実行を一括設定";
    public string Description =>
        "「一括OFF」は現在のストアアプリ (UWP) のバックグラウンド実行をまとめて禁止し、「一括ON」はまとめて許可します。" +
        "アプリ追加後も同じ操作を再実行できます。変更するのはアプリ別設定だけで、実効動作は Windows 全体設定・ポリシー・アプリの制約に依存します。変更前の個別設定は保存しません。";
    public CommandCategory Category => CommandCategory.Performance;
    public bool RequiresReboot => false;
    public Task<MaintenanceActionResult> ApplyAsync(bool enabled, CancellationToken ct = default) =>
        Task.Run(() => Apply(enabled, ct), ct);

    private string[] GetTargets() => _getTargetFamilyNames()
        .Where(family => !string.IsNullOrWhiteSpace(family))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private MaintenanceActionResult Apply(bool enabled, CancellationToken ct)
    {
        var operation = enabled ? "バックグラウンド実行の一括ON (許可)" : "バックグラウンド実行の一括OFF (禁止)";
        var target = enabled ? UwpBackgroundValues.Enabled : UwpBackgroundValues.DisabledValues;
        var before = new Dictionary<string, UwpBackgroundValues>(StringComparer.OrdinalIgnoreCase);
        var attempted = new List<string>();
        MaintenanceActionResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            var families = GetTargets();
            var captured = _settings.ReadMany(families, ct);
            // 全対象を読み切ってから書く。操作前の値はこの呼出しのメモリ内だけに保持する。
            foreach (var family in families) before.Add(family, captured[family]);
            foreach (var family in families)
            {
                ct.ThrowIfCancellationRequested();
                if (_settings.ReadMany([family], ct)[family] != before[family])
                    throw new InvalidOperationException($"{family} の設定が操作前に外部で変更されました。");
                if (before[family] == target) continue;
                attempted.Add(family);
                _settings.WriteMany([new(family, target)], ct);
                ct.ThrowIfCancellationRequested();
                if (_settings.ReadMany([family], ct)[family] != target)
                    throw new InvalidOperationException($"{family} のペア値の変更を確認できませんでした。");
            }
            var actual = _settings.ReadMany(families, ct);
            foreach (var family in families)
                if (actual[family] != target)
                    throw new InvalidOperationException($"{family} の最終設定を確認できませんでした。");
            ct.ThrowIfCancellationRequested();
            result = MaintenanceActionResult.Ok($"  - アプリ別設定を{(enabled ? "一括ON (許可)" : "一括OFF (禁止)")}に変更: {families.Length} パッケージ\n" +
                "  - 実効動作は Windows 全体設定・ポリシー・アプリの制約に依存します。");
        }
        catch (Exception error)
        {
            var compensationErrors = Compensate(attempted, before, target);
            if (compensationErrors.Count > 0)
                result = MaintenanceActionResult.Fail($"{operation}と補償に失敗しました。部分更新の可能性があります。\n  - {error.Message}\n  - " +
                    string.Join("\n  - ", compensationErrors));
            else if (error is OperationCanceledException && ct.IsCancellationRequested)
                result = MaintenanceActionResult.Canceled($"{operation}を取り消し、変更済みの設定を操作前の値へ補償しました。");
            else
                result = MaintenanceActionResult.Fail($"{operation}に失敗しました。" +
                    (attempted.Count == 0 ? "設定は変更していません。" : "変更済みの設定を操作前の値へ補償しました。") +
                    $"\n  - {error.Message}");
        }
        MaintenanceItemLog.LogResult(this, operation, result);
        return result;
    }

    private List<string> Compensate(
        List<string> attempted, IReadOnlyDictionary<string, UwpBackgroundValues> before, UwpBackgroundValues target)
    {
        var errors = new List<string>();
        foreach (var family in attempted.AsEnumerable().Reverse())
        {
            try
            {
                var current = _settings.ReadMany([family], CancellationToken.None)[family];
                if (!current.IsCombinationOf(before[family], target))
                    throw new InvalidOperationException("外部変更を検出したため現在値を保持しました。");
                if (current != before[family])
                    _settings.WriteMany([new(family, before[family])], CancellationToken.None);
            }
            catch (Exception error)
            {
                // 一件の補償失敗で、先に書き換えた他項目の補償を止めない。
                errors.Add($"{family}: 補償失敗: {error.Message}");
            }
        }
        // 未着手の対象は書き戻さず、補償後の実値は操作前に取得した全対象で照合する。
        foreach (var (family, original) in before)
        {
            try
            {
                if (_settings.ReadMany([family], CancellationToken.None)[family] != original)
                    throw new InvalidOperationException("操作前のペア値への補償を確認できませんでした (外部変更または補償不一致)。");
            }
            catch (Exception error)
            {
                errors.Add($"{family}: 補償後の照合失敗: {error.Message}");
            }
        }
        return errors;
    }

    private static IReadOnlyList<string> GetInstalledPackageFamilyNames()
    {
        var packageManager = new PackageManager();
        return packageManager.FindPackagesForUser(string.Empty)
            .Where(package => !package.IsFramework && !package.IsResourcePackage && !package.IsBundle)
            .Where(package =>
            {
                // 固定のインストール済み manifest の属性だけを読む。判定不能なら全件の書込前に中止する。
                try
                {
                    using var manifest = File.OpenText(Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml"));
                    return ContainsUwpApplications(manifest);
                }
                catch (Exception error)
                {
                    throw new InvalidDataException($"{package.Id.FamilyName} の manifest を確認できませんでした: {error.Message}", error);
                }
            })
            .Select(package => package.Id.FamilyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool ContainsUwpApplications(TextReader manifest)
    {
        using var reader = XmlReader.Create(manifest, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        // Windows 8/8.1 の既知パッケージは Windows 10 UWP の一括設定対象に含めない。
        if (document.Root?.Name == XName.Get("Package", "http://schemas.microsoft.com/appx/2010/manifest"))
            return false;
        if (document.Root?.Name != foundation + "Package")
            throw new InvalidDataException("アプリパッケージの manifest 形式を確認できませんでした。");
        var hasUwp = false;
        foreach (var application in document.Root.Elements(foundation + "Applications").Elements(foundation + "Application"))
        {
            var applicationRuntime = GetUwpRuntime(application);
            hasUwp |= applicationRuntime == true;
            // EntryPoint は任意。データ・コーデックだけの宣言は対象外とし、
            // 背景タスク専用アプリは Extension 自身の起動属性から判定する。
            // https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-f-application-extension
            foreach (var extension in application.Elements(foundation + "Extensions").Elements()
                .Where(element => element.Name.LocalName == "Extension"))
                hasUwp |= GetUwpRuntime(extension, applicationRuntime) == true;
        }
        return hasUwp;
    }

    private static bool? GetUwpRuntime(XElement element, bool? inheritedRuntime = null)
    {
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
        XNamespace previewSecurity2 = "http://schemas.microsoft.com/appx/manifest/preview/windows10/security/2";
        var behavior = (string?)element.Attribute(uap10 + "RuntimeBehavior");
        var previewBehavior = (string?)element.Attribute(previewSecurity2 + "RuntimeBehavior");
        if (behavior == "appSilo")
            throw new InvalidDataException("uap10 の RuntimeBehavior を判定できませんでした。");
        if (behavior is not null && previewBehavior is not null && behavior != previewBehavior)
            throw new InvalidDataException("アプリの RuntimeBehavior に矛盾があります。");
        behavior ??= previewBehavior;
        if (behavior is not null && behavior is not ("windowsApp" or "win32App" or "packagedClassicApp" or "appSilo"))
            throw new InvalidDataException("アプリの RuntimeBehavior を判定できませんでした。");
        var entryPoint = (string?)element.Attribute("EntryPoint");
        var startPage = (string?)element.Attribute("StartPage");
        bool? legacyUwp = !string.IsNullOrWhiteSpace(entryPoint)
            ? !entryPoint.Equals("windows.fullTrustApplication", StringComparison.OrdinalIgnoreCase)
                && !entryPoint.Equals("windows.partialTrustApplication", StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(startPage) ? true : null;
        if (behavior is not null)
        {
            var isUwp = behavior == "windowsApp";
            if (legacyUwp is { } legacy && legacy != isUwp)
                throw new InvalidDataException("アプリの RuntimeBehavior と起動属性に矛盾があります。");
            return isUwp;
        }
        else if (legacyUwp is { } legacy)
            return legacy;
        if (inheritedRuntime is not null)
            return inheritedRuntime;
        if (!string.IsNullOrWhiteSpace((string?)element.Attribute("Executable")) ||
            !string.IsNullOrWhiteSpace((string?)element.Attribute(uap10 + "HostId")))
            throw new InvalidDataException("アプリの EntryPoint を判定できませんでした。");
        return null;
    }
}
