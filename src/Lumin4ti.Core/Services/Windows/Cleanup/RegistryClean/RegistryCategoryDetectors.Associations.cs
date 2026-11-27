namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>ファイルの関連付け: OpenWithList・Classes\Applications・OpenWithProgids の判定。ProgID 自体と UserChoice は読まない・触れない。</summary>
internal sealed partial class RegistryCategoryDetectors
{
    private const RegistryCleanCategory Associations = RegistryCleanCategory.FileAssociations;

    private void ScanFileAssociations(RegistryScanSession session)
    {
        ScanOpenWithLists(session);
        ScanApplications(session);
        ScanOpenWithProgIds(session);
    }

    // ---- OpenWithList (HKCU のみ) ----

    private void ScanOpenWithLists(RegistryScanSession session)
    {
        foreach (var root in session.Roots.Where(root => root.Hive == RegistryCleanHive.CurrentUser))
        {
            var fileExts = $@"Software\{CurrentVersion}\Explorer\FileExts";
            foreach (var extension in ListSubKeys(session, Associations, root, fileExts))
            {
                session.Token.ThrowIfCancellationRequested();
                var path = $@"{fileExts}\{extension}\OpenWithList";
                ScanValueContainer(
                    session, Associations, root, path,
                    (_, _, raw) => JudgeOpenWithEntry(raw, session),
                    skipName: name => name.Length != 1,
                    prepareCompanion: (container, slot) => PrepareMruList(root, path, container, slot));
            }
        }
    }

    private RegistryJudgement JudgeOpenWithEntry(RegistryRawValue raw, RegistryScanSession session)
    {
        var (kind, text) = ReadText(raw);
        if (kind == TextKind.WrongType)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.UnsupportedType, raw.Type);
        }

        return text.Length == 0 ? RegistryJudgement.Unknown(RegistryScanReasons.EmptyValue) : JudgeOpenWithApp(text, session);
    }

    /// <summary>スロットを消すときに更新する MRUList を、現在の値のハッシュ付きで記録する。MRUList が無ければ更新は不要。</summary>
    private static Func<RegistryScanItem, RegistryScanCompanion>? PrepareMruList(
        RegistryScanRoot root, string path, IRegistryCleanerKey container, string slot)
    {
        if (container.GetValue("MRUList") is not { } raw)
        {
            return null;
        }

        var related = RelatedValue(Associations, root, root.View, path, container, "MRUList", raw, string.Empty);
        return owner => new RegistryScanCompanion(
            owner, RegistryCompanionKind.OpenWithMruList, related with { Reason = owner.Reason }, [slot]);
    }

    /// <summary>
    /// OpenWithList の名前 (実行ファイル名) を、登録 (Classes\Applications の open コマンド・App Paths) から解決して判定する。
    /// 登録が無い、または 1 つでも存在・不明なら候補にしない。全登録が欠損確定のときだけ、標準の検索先 (Windows・System32・PATH) に実体が
    /// 無いことも確かめる。名前ごとに結果を使い回す。
    /// </summary>
    private RegistryJudgement JudgeOpenWithApp(string appName, RegistryScanSession session)
    {
        if (!session.AppCache.TryGetValue(appName, out var judgement))
        {
            judgement = JudgeOpenWithAppUncached(appName, session);
            session.AppCache[appName] = judgement;
        }

        return judgement;
    }

    private RegistryJudgement JudgeOpenWithAppUncached(string appName, RegistryScanSession session)
    {
        if (appName.IndexOfAny(['\\', '/', ':', '%']) >= 0)
        {
            return JudgePaths(_resolver.ExpandPath(appName, RegistryPathOrigin.User));
        }

        var registrations = new List<RegistryJudgement>();
        foreach (var root in session.ClassesRoots.Where(root => root.View == RegistryCleanView.Registry64))
        {
            using var application = OpenPath(root.Key, root.Classes($@"Applications\{appName}\shell\open\command"), root.View);
            if (application is not null)
            {
                registrations.Add(JudgeValue(application.GetValue(string.Empty), value => ResolveCommandLine(value, root.Origin)));
            }
        }

        foreach (var root in AppPathRoots(session))
        {
            using var appPath = OpenPath(root.Key, $@"{root.Software}\{CurrentVersion}\App Paths\{appName}", root.View);
            if (appPath is not null)
            {
                registrations.Add(JudgeValue(appPath.GetValue(string.Empty), value => _resolver.ResolveAppPath(value, root.Origin)));
            }
        }

        if (registrations.Count == 0)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.NoRegistration);
        }

        var combined = Combine(registrations);
        return combined.Kind == RegistryJudgementKind.Missing ? CheckSearchDirectories(appName, combined) : combined;
    }

    private RegistryJudgement CheckSearchDirectories(string appName, RegistryJudgement missing)
    {
        foreach (var directory in _searchDirectories())
        {
            var path = Path.Combine(directory, appName);
            switch (_probe.Probe(path))
            {
                case RegistryFileState.Exists:
                    return RegistryJudgement.Alive;
                case RegistryFileState.Unknown:
                    return RegistryJudgement.Unknown(RegistryScanReasons.UnknownPath, path);
            }
        }

        return missing;
    }

    // ---- Classes\Applications ----

    private void ScanApplications(RegistryScanSession session)
    {
        foreach (var root in session.ClassesRoots.Where(root => root.View == RegistryCleanView.Registry64))
        {
            ScanKeyContainer(session, Associations, root, root.Classes("Applications"), (key, _) => JudgeShellCommands(key, root));
        }
    }

    /// <summary>
    /// <c>shell</c> の全動詞の <c>command</c> が欠損確定のときだけ Missing。<c>DelegateExecute</c> / <c>DropTarget</c> / command 無しは Unknown。
    /// </summary>
    private RegistryJudgement JudgeShellCommands(IRegistryCleanerKey key, RegistryScanRoot root)
    {
        using var shell = key.OpenChild("shell", RegistryCleanAccess.Read, root.View);
        var verbs = shell?.GetSubKeyNames() ?? [];
        if (shell is null || verbs.Count == 0)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.NoCommand);
        }

        var judgements = new List<RegistryJudgement>();
        foreach (var verb in verbs)
        {
            judgements.Add(JudgeVerb(shell, verb, root));
        }

        return Combine(judgements);
    }

    private RegistryJudgement JudgeVerb(IRegistryCleanerKey shell, string verbName, RegistryScanRoot root)
    {
        using var verb = shell.OpenChild(verbName, RegistryCleanAccess.Read, root.View);
        if (verb is null)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.NoCommand);
        }

        if (verb.GetSubKeyNames().Contains("DropTarget", StringComparer.OrdinalIgnoreCase))
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.Delegated);
        }

        using var command = verb.OpenChild("command", RegistryCleanAccess.Read, root.View);
        if (command is null)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.NoCommand);
        }

        return command.GetValueNames().Contains("DelegateExecute", StringComparer.OrdinalIgnoreCase)
            ? RegistryJudgement.Unknown(RegistryScanReasons.Delegated)
            : JudgeValue(command.GetValue(string.Empty), value => ResolveCommandLine(value, root.Origin));
    }

    // ---- OpenWithProgids ----

    private void ScanOpenWithProgIds(RegistryScanSession session)
    {
        foreach (var root in session.ClassesRoots.Where(root => root.View == RegistryCleanView.Registry64))
        {
            ScanProgIdParent(session, root, root.ClassesRootPath);
        }

        foreach (var root in session.Roots.Where(root => root.Hive == RegistryCleanHive.CurrentUser))
        {
            ScanProgIdParent(session, root, $@"Software\{CurrentVersion}\Explorer\FileExts");
        }
    }

    /// <param name="parent">拡張子のキーを並べる親。空文字なら根そのもの (利用者の Classes)。</param>
    private void ScanProgIdParent(RegistryScanSession session, RegistryScanRoot root, string parent)
    {
        foreach (var extension in ListSubKeys(session, Associations, root, parent).Where(name => name.StartsWith('.')))
        {
            session.Token.ThrowIfCancellationRequested();
            var extensionPath = parent.Length == 0 ? extension : $@"{parent}\{extension}";
            ScanValueContainer(
                session, Associations, root, $@"{extensionPath}\OpenWithProgids",
                (_, progId, _) => JudgeProgId(progId, session));
        }
    }

    /// <summary>
    /// ProgID が Classes (HKLM 64/32・利用者の <c>&lt;SID&gt;_Classes</c>) のどこにも無ければ Missing。あれば全動詞の command で判定する。
    /// パッケージアプリの ProgID (<c>AppX</c>) と <c>\</c> を含む名前は検証できず Unknown。
    /// HKCU を読めない (利用者スコープ無効) ときは、無いことを確定できず Unknown。
    /// </summary>
    private RegistryJudgement JudgeProgId(string progId, RegistryScanSession session)
    {
        if (!session.ProgIdCache.TryGetValue(progId, out var judgement))
        {
            judgement = JudgeProgIdUncached(progId, session);
            session.ProgIdCache[progId] = judgement;
        }

        return judgement;
    }

    private RegistryJudgement JudgeProgIdUncached(string progId, RegistryScanSession session)
    {
        if (progId.Length == 0 || progId.Contains('\\') || progId.StartsWith("AppX", StringComparison.OrdinalIgnoreCase))
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.ProgIdUnverifiable);
        }

        var found = new List<RegistryJudgement>();
        foreach (var root in session.ClassesRoots)
        {
            using var key = OpenPath(root.Key, root.Classes(progId), root.View);
            if (key is not null)
            {
                found.Add(JudgeShellCommands(key, root));
            }
        }

        if (found.Count > 0)
        {
            return Combine(found);
        }

        return session.UserAvailable
            ? new RegistryJudgement(RegistryJudgementKind.Missing, CleanupMessage.Of(RegistryScanReasons.ProgIdMissing, progId))
            : RegistryJudgement.Unknown(RegistryScanReasons.ProgIdUnverifiable);
    }
}
