namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>アンインストール登録 (<c>Uninstall\*</c>) の判定。</summary>
internal sealed partial class RegistryCategoryDetectors
{
    private void ScanUninstall(RegistryScanSession session)
    {
        foreach (var root in session.Roots)
        {
            ScanKeyContainer(
                session, RegistryCleanCategory.Uninstall, root, $@"{root.Software}\{CurrentVersion}\Uninstall",
                (key, name) => JudgeUninstall(key, name, root));
        }
    }

    /// <summary>
    /// 除外 (<c>WindowsInstaller=1</c>・MSI が把握する製品コード・<c>SystemComponent=1</c>・<c>ParentKeyName</c> / <c>ReleaseType</c> 付き) は Alive。
    /// それ以外は UninstallString (と QuietUninstallString) が欠損確定、InstallLocation が空か欠損確定、DisplayIcon がファイルなら欠損確定のときだけ Missing。
    /// </summary>
    private RegistryJudgement JudgeUninstall(IRegistryCleanerKey key, string name, RegistryScanRoot root)
    {
        if (IsDwordOne(key.GetValue("WindowsInstaller")) || IsDwordOne(key.GetValue("SystemComponent")) ||
            key.GetValue("ParentKeyName") is not null || key.GetValue("ReleaseType") is not null)
        {
            return RegistryJudgement.Alive;
        }

        if (IsProductCode(name))
        {
            switch (_msi.Query(name))
            {
                case MsiProductStatus.Registered:
                    return RegistryJudgement.Alive;
                case MsiProductStatus.Indeterminate:
                    return RegistryJudgement.Unknown(RegistryScanReasons.MsiIndeterminate);
            }
        }

        if (key.GetValue("UninstallString") is not { } uninstall)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.NoCommand);
        }

        Func<string, IReadOnlyList<string>> resolveCommand = value => ResolveCommandLine(value, root.Origin);
        var judgements = new List<RegistryJudgement> { JudgeValue(uninstall, resolveCommand) };
        if (key.GetValue("QuietUninstallString") is { } quiet)
        {
            judgements.Add(JudgeValue(quiet, resolveCommand));
        }

        AddOptionalPath(judgements, key.GetValue("InstallLocation"), Unquote, root);
        AddOptionalPath(judgements, key.GetValue("DisplayIcon"), IconFilePath, root);
        return Combine(judgements);
    }

    /// <summary>空・無しは根拠に加えない。値があれば、パスを取り出して欠損確定でなければならない。</summary>
    private void AddOptionalPath(
        List<RegistryJudgement> judgements, RegistryRawValue? raw, Func<string, string> extractPath, RegistryScanRoot root)
    {
        var (kind, text) = ReadText(raw);
        if (kind == TextKind.WrongType)
        {
            judgements.Add(RegistryJudgement.Unknown(RegistryScanReasons.UnsupportedType, raw!.Type));
            return;
        }

        var path = extractPath(text);
        if (path.Length > 0)
        {
            judgements.Add(JudgePaths(_resolver.ExpandPath(path, root.Origin)));
        }
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1].Trim() : text;

    /// <summary>DisplayIcon (<c>path,index</c>・引用符付き) からファイルのパスを取り出す。</summary>
    internal static string IconFilePath(string text)
    {
        if (text.Length > 0 && text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? text : text[1..close].Trim();
        }

        var comma = text.LastIndexOf(',');
        return comma >= 0 && int.TryParse(text[(comma + 1)..].Trim(), out _) ? text[..comma].Trim() : text;
    }

    /// <summary><c>{8-4-4-4-12}</c> 形式の製品コードか。</summary>
    internal static bool IsProductCode(string name) =>
        name.Length == 38 && name[0] == '{' && name[^1] == '}' && Guid.TryParseExact(name, "B", out _);
}
