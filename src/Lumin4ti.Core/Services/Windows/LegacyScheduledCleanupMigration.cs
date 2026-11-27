using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Xml.Linq;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>
/// 旧版が登録したサインイン時のクリーンアップタスク (<c>\Lumin4ti\ScheduledTempCleanup</c>) を取り除く移行処理。
/// 予約実行は廃止したため、既存タスクだけを消す。固定パスのタスク 1 本だけを扱い、
/// 現在の利用者が所有し、旧版が登録した定義と完全に一致するときだけ削除する。
/// 一致しない・照会できない場合は保持してログへ残す。他の利用者のタスクは消さない
/// (その利用者の次回サインインで、引数付き起動の経路から自己削除される)。
/// COM (Schedule.Service) は <see cref="ILegacyTaskStore"/> の後ろへ置き、テストでは差し替える。
/// COM 移行は 2027-11-30 まで残し、その後の撤去は本人の指示で行う。
/// 引数付き起動の無害終了 (<see cref="RunForObsoleteArgument"/>) は、残存タスクが昇格 GUI を開かないよう期限なしで残す。
/// </summary>
[SupportedOSPlatform("windows")]
public static class LegacyScheduledCleanupMigration
{
    /// <summary>旧タスクが付けて起動する引数。検知したら移行だけ試みて常に終了コード 0 で終わる。</summary>
    public const string ObsoleteArgument = "--scheduled-cleanup-temp";

    internal const string FolderPath = @"\Lumin4ti";
    internal const string TaskName = "ScheduledTempCleanup";
    internal const string LegacyExecutableName = "Lumin4ti.UI.exe";

    // 起動を止める上限。残存タスクの確認は通常ごく短いため、応答しない COM を待ち続けず次回へ持ち越す。
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>通常起動 (昇格済み・単一起動確保後) から呼ぶ。失敗しても例外を投げない。</summary>
    public static void RunAtStartup() => RunGuarded("通常起動");

    /// <summary>旧タスクから引数付きで起動されたときの入口。掃除はせず、移行だけ試みて常に 0 を返す。</summary>
    public static int RunForObsoleteArgument()
    {
        RunGuarded("旧タスクからの引数付き起動");
        return 0;
    }

    private static void RunGuarded(string trigger)
    {
        try
        {
            // 共有ロックは呼び出し側で持ち、期限切れ・完了のどちらでもここで手放す。
            // COM が応答しないままの作業スレッドが、他のメンテナンス操作を止め続けないようにするため。
            using var operationLock = MaintenanceOperationProcessLock.TryAcquire();
            if (operationLock is null)
            {
                LoggerBootstrap.Log.Info($"legacy-scheduled-cleanup ({trigger}): 別のメンテナンス操作が実行中のため次回へ持ち越します");
                return;
            }

            // COM を同期で呼ぶため、応答しない場合に起動を止めないよう期限を付けて別スレッドで実行する。
            // 期限切れ後は取消を要求し、作業スレッドは次の手順へ進まない (実行中の COM 呼び出しは中断できない)。
            var cancellation = new CancellationTokenSource();
            var work = Task.Run(() => RunMigration(trigger, cancellation.Token));
            if (!work.Wait(OverallTimeout))
            {
                cancellation.Cancel();
                LoggerBootstrap.Log.Error($"legacy-scheduled-cleanup ({trigger}): 期限内に完了しませんでした。ロックを手放して次回へ持ち越します");
                return;
            }

            cancellation.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LoggerBootstrap.Log.Error($"legacy-scheduled-cleanup ({trigger}): 移行に失敗しました", ex);
        }
    }

    private static void RunMigration(string trigger, CancellationToken ct)
    {
        try
        {
            using var store = new ComLegacyTaskStore();
            var report = Migrate(store, new ProtectedLegacyTaskDefinitionFiles(), CurrentUser.Get(), ct);
            LoggerBootstrap.Log.Info(
                $"legacy-scheduled-cleanup ({trigger}): task={report.Task}, folderRemoved={report.FolderRemoved}, definitions={report.DefinitionsRemoved}");
        }
        catch (OperationCanceledException)
        {
            LoggerBootstrap.Log.Info($"legacy-scheduled-cleanup ({trigger}): 期限切れのため残りの手順を行いませんでした");
        }
    }

    internal static MigrationReport Migrate(
        ILegacyTaskStore store,
        ILegacyTaskDefinitionFiles files,
        CurrentUser user,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);

        // 取消 (期限切れ) は各手順の開始前と、各削除の直前に確かめる。進行中の COM・ファイル呼び出しは止められない。
        ct.ThrowIfCancellationRequested();
        var task = RemoveTask(store, user, ct);
        ct.ThrowIfCancellationRequested();
        var folderRemoved = task is TaskOutcome.Deleted or TaskOutcome.NotFound && RemoveEmptyFolder(store, ct);
        ct.ThrowIfCancellationRequested();
        var definitionsRemoved = RemoveDefinitionFiles(files, ct);
        return new MigrationReport(task, folderRemoved, definitionsRemoved);
    }

    private static TaskOutcome RemoveTask(ILegacyTaskStore store, CurrentUser user, CancellationToken ct)
    {
        try
        {
            using var task = store.FindTask(FolderPath, TaskName);
            if (task is null)
            {
                return TaskOutcome.NotFound;
            }

            if (!IsOwnedLegacyTask(task.ReadXml(), user, out var reason))
            {
                LoggerBootstrap.Log.Info($"legacy-scheduled-cleanup: 旧版が登録した定義と一致しないため保持します ({reason})");
                return TaskOutcome.Kept;
            }

            // 削除の直前に、同じタスクオブジェクトで再照合する (最初の照合との間の差し替えを検出する)。
            if (!IsOwnedLegacyTask(task.ReadXml(), user, out reason))
            {
                LoggerBootstrap.Log.Info($"legacy-scheduled-cleanup: 削除直前の再照合で定義が変わっていたため保持します ({reason})");
                return TaskOutcome.Kept;
            }

            ct.ThrowIfCancellationRequested();
            task.Delete();
            LoggerBootstrap.Log.Info("legacy-scheduled-cleanup: 旧タスクを削除しました");
            return TaskOutcome.Deleted;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            LoggerBootstrap.Log.Error("legacy-scheduled-cleanup: 旧タスクを照会または削除できないため保持します", ex);
            return TaskOutcome.Failed;
        }
    }

    private static bool RemoveEmptyFolder(ILegacyTaskStore store, CancellationToken ct)
    {
        try
        {
            // 隠しタスクとサブフォルダーを含めて空のときだけ消す。他のタスクが 1 件でもあれば触らない。
            if (store.IsFolderEmpty(FolderPath) is not true)
            {
                return false;
            }

            ct.ThrowIfCancellationRequested();
            store.DeleteFolder(FolderPath);
            return true;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            LoggerBootstrap.Log.Error("legacy-scheduled-cleanup: 空のタスクフォルダーを整理できませんでした", ex);
            return false;
        }
    }

    private static int RemoveDefinitionFiles(ILegacyTaskDefinitionFiles files, CancellationToken ct)
    {
        var removed = 0;
        try
        {
            foreach (var name in files.List())
            {
                if (!IsDefinitionFileName(name))
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                files.Delete(name);
                removed++;
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            LoggerBootstrap.Log.Error("legacy-scheduled-cleanup: 保護ストレージのタスク定義を整理できませんでした", ex);
        }

        return removed;
    }

    internal static bool IsDefinitionFileName(string name) =>
        name.StartsWith("temp-cleanup-", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['\\', '/', ':']) < 0;

    /// <summary>
    /// 定義が、旧版の登録内容 (現在の利用者・単一 Exec・固定引数・Lumin4ti.UI.exe) と一致するか。
    /// 解釈できない・欠けている場合は一致しない扱い。
    /// </summary>
    internal static bool IsOwnedLegacyTask(string xml, CurrentUser user, out string reason)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null)
            {
                reason = "定義が空です";
                return false;
            }

            var ns = root.Name.Namespace;
            if (root.Element(ns + "Principals")?.Elements().ToArray() is not [{ Name.LocalName: "Principal" } principal])
            {
                reason = "Principal が 1 件ではありません";
                return false;
            }

            if (!user.Matches(principal.Element(ns + "UserId")?.Value))
            {
                reason = "所有者が現在の利用者ではありません";
                return false;
            }

            if (root.Element(ns + "Actions")?.Elements().ToArray() is not [{ Name.LocalName: "Exec" } exec])
            {
                reason = "Action が単一の Exec ではありません";
                return false;
            }

            return MatchesExec(exec, ns, out reason);
        }
        catch (System.Xml.XmlException)
        {
            reason = "定義を解釈できません";
            return false;
        }
    }

    private static bool MatchesExec(XElement exec, XNamespace ns, out string reason)
    {
        if (!string.Equals(exec.Element(ns + "Arguments")?.Value.Trim(), ObsoleteArgument, StringComparison.Ordinal))
        {
            reason = "引数が一致しません";
            return false;
        }

        var command = exec.Element(ns + "Command")?.Value.Trim().Trim('"');
        if (string.IsNullOrEmpty(command)
            || !string.Equals(Path.GetFileName(command), LegacyExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            reason = "実行ファイル名が一致しません";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal enum TaskOutcome
    {
        NotFound,
        Deleted,
        Kept,
        Failed,
    }

    internal readonly record struct MigrationReport(TaskOutcome Task, bool FolderRemoved, int DefinitionsRemoved);

    /// <summary>現在の利用者。タスク定義は SID とアカウント名のどちらの表記でも持ち得るため両方で照合する。</summary>
    internal sealed record CurrentUser(string AccountName, string? Sid)
    {
        public static CurrentUser Get()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new CurrentUser(identity.Name, identity.User?.Value);
        }

        public bool Matches(string? value)
        {
            var trimmed = value?.Trim();
            return !string.IsNullOrEmpty(trimmed)
                && (string.Equals(trimmed, AccountName, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(Sid)
                        && string.Equals(trimmed, Sid, StringComparison.OrdinalIgnoreCase)));
        }
    }
}

/// <summary>タスクスケジューラーへの境界。テストでは偽物へ差し替え、実機のタスクを変えない。</summary>
internal interface ILegacyTaskStore : IDisposable
{
    /// <summary>タスクを取得する。存在しないときだけ null。その他の失敗は例外で伝える。</summary>
    ILegacyTask? FindTask(string folderPath, string name);

    /// <summary>隠しタスクとサブフォルダーを含めて空か。フォルダーが無ければ null。</summary>
    bool? IsFolderEmpty(string folderPath);

    void DeleteFolder(string folderPath);
}

internal interface ILegacyTask : IDisposable
{
    /// <summary>現在のタスク定義 XML。呼ぶたびに読み直す。</summary>
    string ReadXml();

    void Delete();
}

/// <summary>保護ストレージ (scheduled-tasks) にある、旧タスク登録用の定義 XML。</summary>
internal interface ILegacyTaskDefinitionFiles
{
    IReadOnlyList<string> List();

    void Delete(string name);
}

[SupportedOSPlatform("windows")]
internal sealed class ProtectedLegacyTaskDefinitionFiles(ProtectedBackupStorage? storage = null) : ILegacyTaskDefinitionFiles
{
    private const string DirectoryName = "scheduled-tasks";
    private const int Limit = 1000;
    private readonly ProtectedBackupStorage _storage = storage ?? ProtectedBackupStorage.Default;

    public IReadOnlyList<string> List()
    {
        // ディレクトリが無いときに作らない (EnumerateFileNames は祖先を作成するため)。
        var directory = _storage.GetFullPath(DirectoryName);
        return Directory.Exists(directory) ? _storage.EnumerateFileNames(DirectoryName, Limit) : [];
    }

    public void Delete(string name) => _storage.Delete(Path.Combine(DirectoryName, name));
}

[SupportedOSPlatform("windows")]
internal sealed class ComLegacyTaskStore : ILegacyTaskStore
{
    private const int TaskEnumHidden = 1;
    private object? _service;
    private object? _root;

    private object Root
    {
        get
        {
            if (_root is not null)
            {
                return _root;
            }

            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            _service = Activator.CreateInstance(type)!;
            ((dynamic)_service).Connect();
            _root = ((dynamic)_service).GetFolder(@"\");
            return _root!;
        }
    }

    public ILegacyTask? FindTask(string folderPath, string name)
    {
        object? folder = GetFolderOrNull(folderPath);
        if (folder is null)
        {
            return null;
        }

        try
        {
            var task = ((dynamic)folder).GetTask(name);
            var result = new ComLegacyTask(folder, task, name);
            folder = null;
            return result;
        }
        catch (COMException ex) when (IsMissing(ex))
        {
            return null;
        }
        finally
        {
            Release(folder);
        }
    }

    public bool? IsFolderEmpty(string folderPath)
    {
        object? folder = GetFolderOrNull(folderPath);
        if (folder is null)
        {
            return null;
        }

        object? tasks = null;
        object? folders = null;
        try
        {
            tasks = ((dynamic)folder).GetTasks(TaskEnumHidden);
            folders = ((dynamic)folder).GetFolders(0);
            return (int)((dynamic)tasks).Count == 0 && (int)((dynamic)folders).Count == 0;
        }
        finally
        {
            Release(folders);
            Release(tasks);
            Release(folder);
        }
    }

    public void DeleteFolder(string folderPath) =>
        ((dynamic)Root).DeleteFolder(folderPath.TrimStart('\\'), 0);

    private object? GetFolderOrNull(string folderPath)
    {
        try
        {
            return ((dynamic)Root).GetFolder(folderPath);
        }
        catch (COMException ex) when (IsMissing(ex))
        {
            return null;
        }
    }

    private static bool IsMissing(COMException ex) =>
        ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    public void Dispose()
    {
        Release(_root);
        Release(_service);
        _root = null;
        _service = null;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ComLegacyTask(object folder, object task, string name) : ILegacyTask
{
    public string ReadXml() => (string)((dynamic)task).Xml;

    public void Delete() => ((dynamic)folder).DeleteTask(name, 0);

    public void Dispose()
    {
        ComLegacyTaskStore.Release(task);
        ComLegacyTaskStore.Release(folder);
    }
}
