using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

/// <summary>タスクスケジューラーの 1 タスク。テストでは偽物に差し替え、実機のタスクを変えない。</summary>
internal interface IScheduledTaskItem : IDisposable
{
    /// <summary>フォルダーを含むタスクのパス (例: <c>\Vendor\Updater</c>)。</summary>
    string Path { get; }

    /// <summary>現在のタスク定義 XML。呼ぶたびに読み直す。</summary>
    string ReadXml();

    /// <summary>このタスクを削除する。</summary>
    void Delete();
}

/// <summary>列挙したタスク。<see cref="Dispose"/> で全て解放する。</summary>
internal sealed class ScheduledTaskListing : IDisposable
{
    public List<IScheduledTaskItem> Items { get; } = [];

    /// <summary>列挙できなかったフォルダー・タスクの数。</summary>
    public int Unreadable { get; set; }

    public void Dispose()
    {
        foreach (var item in Items) item.Dispose();
        Items.Clear();
    }
}

/// <summary>タスクスケジューラーの列挙の境界。</summary>
internal interface IScheduledTaskCatalog : IDisposable
{
    /// <summary>隠しタスクを含めて列挙する。<c>\Microsoft\</c> 以下と <c>\Lumin4ti*</c> は返さなくてよい (呼び出し側も除外する)。</summary>
    ScheduledTaskListing List(CancellationToken ct);
}

/// <summary>1 タスクの判定結果。</summary>
/// <param name="Missing">単一 Exec の実行ファイルの欠損が確定したか。</param>
/// <param name="Command">判定に使った実行ファイル。判定できなければ null。</param>
/// <param name="Reason">保持する理由 (欠損確定なら空)。</param>
internal readonly record struct ScheduledTaskVerdict(bool Missing, string? Command, string Reason);

/// <summary>
/// 実行ファイルの欠損が確定した、単一 Exec のタスクだけを削除する。
/// <list type="bullet">
/// <item><c>\Microsoft\</c> 以下と Lumin4ti 自身のタスク (<c>\Lumin4ti*</c>) は対象にしない。</item>
/// <item>COM ハンドラー・複数アクション・環境変数を含むコマンド・相対パス・引用符なしの空白入りコマンドは判定しない
/// (タスクは他の利用者・環境で実行され得るため)。欠損は <see cref="StartupCommandParser.IsConfirmedMissing"/> が確定した場合だけ。</item>
/// <item>実行時は走査で見つけたタスクだけを対象にし、削除の直前に削除するのと同じタスクオブジェクトから定義を読み直して再照合する。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InvalidScheduledTaskHandler : ICleanupHandler
{
    private readonly Func<IScheduledTaskCatalog> _catalogFactory;
    private readonly Func<string, bool> _isConfirmedMissing;
    private HashSet<string>? _scanned;

    public InvalidScheduledTaskHandler()
        : this(static () => new ComScheduledTaskCatalog(), static path => StartupCommandParser.IsConfirmedMissing(path))
    {
    }

    internal InvalidScheduledTaskHandler(Func<IScheduledTaskCatalog> catalogFactory, Func<string, bool> isConfirmedMissing)
    {
        _catalogFactory = catalogFactory;
        _isConfirmedMissing = isConfirmedMissing;
    }

    public string Id => "invalidScheduledTask";

    public IReadOnlyList<string> Services => [];

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct)
    {
        try
        {
            var (paths, unreadable) = Collect(ct);
            _scanned = paths;
            var detail = unreadable > 0 ? CleanupMessage.Of("TasksUnreadable", unreadable) : null;
            return Task.FromResult(paths.Count == 0
                ? new CleanupHandlerScan(CleanupHandlerScanState.Empty, 0, 0, detail)
                : new CleanupHandlerScan(CleanupHandlerScanState.Found, paths.Count, null, detail));
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return Task.FromResult(new CleanupHandlerScan(
                CleanupHandlerScanState.Unavailable,
                Detail: CleanupMessage.Of("TaskSchedulerQueryFailed", CommandFailureDiagnostic.Sanitize(ex.Message))));
        }
    }

    public Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            return Task.FromResult(Run(progress, ct));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new CleanupHandlerResult(CleanupHandlerStatus.Canceled));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Task.FromResult(CleanupHandlerResult.Failed(CleanupMessage.Of("TaskSchedulerOperateFailed", CommandFailureDiagnostic.Sanitize(ex.Message))));
        }
    }

    /// <summary>対象にしないタスク (<c>\Microsoft\</c> 以下、Lumin4ti 自身)。フォルダーは末尾に <c>\</c> を付けて渡す。</summary>
    internal static bool IsExcluded(string path) =>
        path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"\Lumin4ti", StringComparison.OrdinalIgnoreCase);

    /// <summary>タスク定義が、単一 Exec の実行ファイルの欠損が確定したものかを判定する。</summary>
    internal static ScheduledTaskVerdict Evaluate(string xml, Func<string, bool> isConfirmedMissing)
    {
        XElement? root;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1 << 20 });
            root = XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return Keep("定義を解釈できません");
        }

        if (root?.Element(root.Name.Namespace + "Actions")?.Elements().ToArray() is not [{ Name.LocalName: "Exec" } exec])
        {
            return Keep("単一の Exec ではありません");
        }

        var command = exec.Element(root.Name.Namespace + "Command")?.Value.Trim();
        if (!TryGetExecutablePath(command, out var path))
        {
            return Keep("実行ファイルのパスを確定できません");
        }

        return isConfirmedMissing(path)
            ? new ScheduledTaskVerdict(true, path, string.Empty)
            : new ScheduledTaskVerdict(false, path, "実行ファイルが存在する、または欠損を確定できません");
    }

    private static ScheduledTaskVerdict Keep(string reason) => new(false, null, reason);

    /// <summary>引用符付き、または空白を含まない絶対パスだけを実行ファイルとして扱う。環境変数付きは他の利用者で値が変わるため扱わない。</summary>
    private static bool TryGetExecutablePath(string? command, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(command) || command.Contains('%')) return false;

        var quoted = command.Length > 2 && command[0] == '"' && command[^1] == '"';
        var candidate = quoted ? command[1..^1] : command;
        if (candidate.Contains('"') || (!quoted && candidate.Any(char.IsWhiteSpace))) return false;
        if (!System.IO.Path.IsPathFullyQualified(candidate)) return false;

        path = candidate;
        return true;
    }

    private (HashSet<string> Paths, int Unreadable) Collect(CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var catalog = _catalogFactory();
        using var listing = catalog.List(ct);
        var unreadable = listing.Unreadable;
        foreach (var item in listing.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (IsExcluded(item.Path)) continue;
            try
            {
                if (Evaluate(item.ReadXml(), _isConfirmedMissing).Missing) paths.Add(item.Path);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                unreadable++;
            }
        }

        return (paths, unreadable);
    }

    private CleanupHandlerResult Run(IProgress<string>? progress, CancellationToken ct)
    {
        // 走査していないタスクは削除しない。走査が無ければここで走査する。
        var allowed = _scanned ?? Collect(ct).Paths;
        _scanned = null;

        var deleted = 0L;
        var details = new List<string>();
        try
        {
            using var catalog = _catalogFactory();
            using var listing = catalog.List(ct);
            foreach (var item in listing.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (allowed.Contains(item.Path) && !IsExcluded(item.Path) && TryDelete(item, progress, details)) deleted++;
            }
        }
        catch (OperationCanceledException)
        {
            return new CleanupHandlerResult(CleanupHandlerStatus.Canceled, deleted) { Details = details };
        }

        return Classify(deleted, details);
    }

    /// <summary>削除の直前に、削除するのと同じオブジェクトから定義を読み直して再照合する。変わっていれば残す。</summary>
    private bool TryDelete(IScheduledTaskItem item, IProgress<string>? progress, List<string> details)
    {
        try
        {
            if (!Evaluate(item.ReadXml(), _isConfirmedMissing).Missing) return false;
            progress?.Report($"  - タスク: {item.Path}");
            item.Delete();
            return true;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            details.Add($"{item.Path}: {CommandFailureDiagnostic.Sanitize(ex.Message)}");
            return false;
        }
    }

    internal static CleanupHandlerResult Classify(long deleted, List<string> failures)
    {
        if (failures.Count == 0)
        {
            return deleted == 0
                ? new CleanupHandlerResult(CleanupHandlerStatus.NothingToDo)
                : new CleanupHandlerResult(CleanupHandlerStatus.Succeeded, deleted);
        }

        return deleted > 0
            ? new CleanupHandlerResult(CleanupHandlerStatus.Partial, deleted) { Details = failures }
            : new CleanupHandlerResult(CleanupHandlerStatus.Failed) { Details = failures };
    }
}

/// <summary>COM (Schedule.Service) によるタスクスケジューラーの列挙。</summary>
[SupportedOSPlatform("windows")]
internal sealed class ComScheduledTaskCatalog : IScheduledTaskCatalog
{
    private const int TaskEnumHidden = 1;
    private const int MaxDepth = 16;
    private object? _service;
    private object? _root;

    private object Root
    {
        get
        {
            if (_root is not null) return _root;
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            _service = Activator.CreateInstance(type)!;
            ((dynamic)_service).Connect();
            _root = ((dynamic)_service).GetFolder(@"\");
            return _root!;
        }
    }

    public ScheduledTaskListing List(CancellationToken ct)
    {
        var listing = new ScheduledTaskListing();
        try
        {
            Walk(Root, 0, listing, ct, ownsFolder: false);
        }
        catch
        {
            listing.Dispose();
            throw;
        }

        return listing;
    }

    private void Walk(object folder, int depth, ScheduledTaskListing listing, CancellationToken ct, bool ownsFolder)
    {
        object? tasks = null;
        object? folders = null;
        try
        {
            var path = (string)((dynamic)folder).Path;
            if (InvalidScheduledTaskHandler.IsExcluded(path.TrimEnd('\\') + @"\")) return;

            tasks = ((dynamic)folder).GetTasks(TaskEnumHidden);
            AddTasks(tasks, listing, ct);
            if (depth >= MaxDepth) return;

            folders = ((dynamic)folder).GetFolders(0);
            foreach (var child in EnumerateFolders(folders, listing))
            {
                ct.ThrowIfCancellationRequested();
                Walk(child, depth + 1, listing, ct, ownsFolder: true);
            }
        }
        catch (COMException)
        {
            listing.Unreadable++;
        }
        finally
        {
            Release(folders);
            Release(tasks);
            if (ownsFolder) Release(folder);
        }
    }

    private void AddTasks(object tasks, ScheduledTaskListing listing, CancellationToken ct)
    {
        int count = ((dynamic)tasks).Count;
        for (var i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                listing.Items.Add(new ComScheduledTaskItem(this, ((dynamic)tasks).Item(i)));
            }
            catch (COMException)
            {
                listing.Unreadable++;
            }
        }
    }

    private static IEnumerable<object> EnumerateFolders(object folders, ScheduledTaskListing listing)
    {
        int count = ((dynamic)folders).Count;
        for (var i = 1; i <= count; i++)
        {
            object? child = null;
            try
            {
                child = ((dynamic)folders).Item(i);
            }
            catch (COMException)
            {
                listing.Unreadable++;
            }

            if (child is not null) yield return child;
        }
    }

    internal void DeleteTask(string taskPath, string name)
    {
        var index = taskPath.LastIndexOf('\\');
        var folderPath = index <= 0 ? @"\" : taskPath[..index];
        object? folder = null;
        try
        {
            folder = ((dynamic)Root).GetFolder(folderPath);
            ((dynamic)folder).DeleteTask(name, 0);
        }
        finally
        {
            Release(folder);
        }
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
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
internal sealed class ComScheduledTaskItem(ComScheduledTaskCatalog catalog, object task) : IScheduledTaskItem
{
    public string Path => (string)((dynamic)task).Path;

    public string ReadXml() => (string)((dynamic)task).Xml;

    public void Delete() => catalog.DeleteTask(Path, (string)((dynamic)task).Name);

    public void Dispose() => ComScheduledTaskCatalog.Release(task);
}
