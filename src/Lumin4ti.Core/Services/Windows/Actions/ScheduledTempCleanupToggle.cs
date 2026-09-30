using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// Windows タスクスケジューラーに、サインインのたびに %TEMP% を削除するタスクを登録・解除するトグル。
/// タスクの実体は Lumin4ti.exe 自身を <see cref="ScheduledTempCleanup.CommandLineArgument"/> 付きで
/// 呼び出す (実処理は <see cref="ScheduledTempCleanup.Run"/> 側。既存の FileCleanupEngine の
/// 安全ガードをそのまま使える)。ON = タスク登録、OFF = タスク削除。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTempCleanupToggle(
    ICommandExecutor executor,
    ICleanupPreferences? preferences = null,
    Func<string, bool>? isTrustedInstalledExecutable = null) : IMaintenanceToggle, IMaintenanceCheckList
{
    /// <summary>タスク名。専用フォルダの下に置き、他のタスクと混ざらないようにする。</summary>
    internal const string TaskName = @"\Lumin4ti\ScheduledTempCleanup";

    /// <summary>
    /// 登録前のパス信頼確認。既定は実際の署名・インストール状態検証だが、テストでは差し替える。
    /// </summary>
    private readonly Func<string, bool> _isTrustedInstalledExecutable =
        isTrustedInstalledExecutable ?? ScheduledTaskExecutableTrust.IsTrustedInstalledExecutable;

    /// <summary>
    /// タスク定義 XML の置き場。Administrators / SYSTEM 以外が書けない場所へ置くため、
    /// レジストリ復元用と同じ保護ストレージを既定にする。
    /// </summary>
    private readonly ITaskDefinitionStore _taskDefinitionStore = ProtectedTaskDefinitionStore.Default;

    /// <summary>置き場を差し替えるテスト用の経路 (非昇格のテスト実行では ProgramData へ書けない)。</summary>
    internal ScheduledTempCleanupToggle(
        ICommandExecutor executor,
        ITaskDefinitionStore taskDefinitionStore,
        Func<string, bool>? isTrustedInstalledExecutable = null)
        : this(executor, (ICleanupPreferences?)null, isTrustedInstalledExecutable)
    {
        ArgumentNullException.ThrowIfNull(taskDefinitionStore);
        _taskDefinitionStore = taskDefinitionStore;
    }

    public string Id => "scheduled-temp-cleanup";

    public string Label => "サインイン時にクリーンアップを自動実行するタスクを登録";

    public string Description =>
        "Windows タスクスケジューラーに、サインインのたびにクリーンアップを実行するタスクを登録します。" +
        "実行する項目は下の一覧で選べ、各項目が消す対象も項目カードのチェックリストの設定がそのまま使われます " +
        "(画面のボタンで実行したときとまったく同じ処理が走ります)。" +
        "タスクは最高権限で動くため、サービスの停止が必要な項目も実行できます。" +
        "サインイン時に UAC の確認は表示されません。" +
        "選んだキャッシュやログの量によっては、サインインのたびに数分以上かかることがあります。" +
        "使用中のファイルは自動的にスキップされます。OFF にするとタスクを削除します。";

    public string CheckListCaption => "サインイン時に実行する項目を選ぶ";

    public string CheckListCaptionKey => "CheckList.ScheduledGroups";

    public IReadOnlyList<MaintenanceCheckListEntry> GetCheckListEntries()
    {
        var selected = new HashSet<string>(
            preferences?.ScheduledGroupIds ?? CleanupPreferences.DefaultScheduledGroupIds,
            StringComparer.OrdinalIgnoreCase);

        // 画面に並ぶクリーンアップ項目と同じ生成経路から作る (選べる項目と実際に走る項目をずらさない)。
        return
        [
            .. FileCleanupGroups.CreateCleanupActions(executor, preferences)
                .Select(action => new MaintenanceCheckListEntry(
                    action.Id,
                    action.Label,
                    selected.Contains(action.Id),
                    action.LabelKey)),
        ];
    }

    public async Task SetCheckListEntrySelectedAsync(string value, bool selected, CancellationToken ct = default)
    {
        if (preferences is null)
        {
            return;
        }

        preferences.SetScheduledGroupEnabled(value, selected);
        await preferences.SaveAsync(ct);
    }

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public async Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        var result = await executor.RunAsync("schtasks", BuildQueryXmlArguments(), ct);
        if (!result.Success)
        {
            return false;
        }

        var currentUser = GetCurrentUserIdentity();
        return GetTaskOwnership(
            result.StandardOutput,
            currentUser.AccountName,
            currentUser.Sid)
            == TaskOwnership.CurrentUser
            ? true
            : null;
    }

    /// <summary>
    /// 過去バージョンが登録した既存タスクを、現行の権限・ログオン対象を持つ定義へ更新する。
    /// タスクが無い場合と、開発ビルドなど信頼済みインストール外からの起動では何もしない。
    /// </summary>
    public async Task RepairLegacyRegistrationAsync(CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        var queryResult = await executor.RunAsync(
            "schtasks", BuildQueryXmlArguments(), ct, timeout: TimeSpan.FromSeconds(15));
        var currentUser = GetCurrentUserIdentity();
        if (!queryResult.Success
            || GetTaskOwnership(
                queryResult.StandardOutput,
                currentUser.AccountName,
                currentUser.Sid) != TaskOwnership.CurrentUser
            || IsCurrentTaskDefinition(
                queryResult.StandardOutput,
                exePath,
                currentUser.AccountName,
                currentUser.Sid))
        {
            return;
        }

        if (!_isTrustedInstalledExecutable(exePath))
        {
            return;
        }

        LoggerBootstrap.Log.Info($"{Id}: 旧形式のタスク定義を検出したため更新します");
        var repairResult = await SetStateAsync(true, ct);
        if (repairResult.Status == MaintenanceActionStatus.Failed)
        {
            LoggerBootstrap.Log.Error($"{Id}: 旧形式のタスク定義を更新できませんでした");
        }
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        if (!on)
        {
            var taskToDelete = await QueryExistingTaskAsync(ct);
            var ownershipError = GetOwnershipError(taskToDelete.QuerySucceeded, taskToDelete.Ownership);
            if (ownershipError is not null)
            {
                return ownershipError;
            }

            var deleteResult = await executor.RunAsync("schtasks", BuildDeleteArguments(), ct);
            if (deleteResult.Success)
            {
                LoggerBootstrap.Log.Info($"{Id}: タスクを削除しました");
                return MaintenanceActionResult.Ok("  - タスクを削除しました");
            }

            LoggerBootstrap.Log.Error($"{Id}: schtasks /delete (exit={deleteResult.ExitCode}): {deleteResult.StandardError}");
            return MaintenanceActionResult.Fail($"タスクの削除に失敗しました: {deleteResult.StandardError}");
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            LoggerBootstrap.Log.Error($"{Id}: 実行ファイルのパスを取得できませんでした");
            return MaintenanceActionResult.Fail("実行ファイルのパスを取得できませんでした");
        }

        // ユーザー書き込み可能な場所から起動されたプロセス (移行前残骸・手動コピー等) のパスを
        // タスクスケジューラーへ固定登録すると、改ざん後の自動実行を許してしまう。
        // 署名済み・Program Files\Lumin4ti\current 配下・MSI インストール済みで、
        // 実行ファイルまでの各要素を非管理者が差し替えられない正規実体だけを登録対象にする。
        if (!_isTrustedInstalledExecutable(exePath))
        {
            LoggerBootstrap.Log.Error($"{Id}: 実行ファイルが信頼できるインストール済みの実体ではありません ({exePath})");
            return MaintenanceActionResult.Fail("信頼できないインストール状態のため、タスクを登録できませんでした");
        }

        var existingTask = await QueryExistingTaskAsync(ct);
        if (existingTask.QuerySucceeded)
        {
            var ownershipError = GetOwnershipError(querySucceeded: true, existingTask.Ownership);
            if (ownershipError is not null)
            {
                return ownershipError;
            }
        }

        // 照会失敗には「タスクが無い」と一時的な照会不能の両方が含まれる。
        // /f を付けず新規作成だけを試せば、前者は登録でき、後者でも既存タスクを上書きしない。
        var overwriteExisting = existingTask.QuerySucceeded;

        // schtasks のスイッチにはバッテリー関連の指定が無いため、タスク定義 XML をファイルへ書いて
        // /xml で登録する。XML は UTF-16 (BOM 付き) でないと schtasks が受け付けない。
        // 置き場を %TEMP% にすると、書き終えてから昇格した schtasks が読むまでの間に同一ユーザーの
        // 非昇格プロセスが定義を差し替えられる (ログオン時に自動実行されるタスクの乗っ取り)。
        // 実行ファイルのパスだけ検証しても意味が無くなるため、Administrators / SYSTEM しか
        // 書けない ProgramData 配下の保護ストレージへ置いてから渡す。
        var definitionName = $"temp-cleanup-{Guid.NewGuid():N}.xml";
        string xmlPath;
        try
        {
            xmlPath = _taskDefinitionStore.WriteNew(
                definitionName,
                [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(BuildTaskXml(exePath))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            LoggerBootstrap.Log.Error($"{Id}: タスク定義 XML を書き出せませんでした", ex);
            return MaintenanceActionResult.Fail($"タスク定義の書き出しに失敗しました: {ex.Message}");
        }

        CommandExecutionResult createResult;
        try
        {
            createResult = await executor.RunAsync(
                "schtasks",
                BuildCreateArguments(xmlPath, overwriteExisting),
                ct);
        }
        finally
        {
            _taskDefinitionStore.Delete(definitionName);
        }

        if (createResult.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: タスクを登録しました ({exePath})");
            return MaintenanceActionResult.Ok("  - サインイン時にクリーンアップを実行するタスクを登録しました");
        }

        LoggerBootstrap.Log.Error($"{Id}: schtasks /create (exit={createResult.ExitCode}): {createResult.StandardError}");
        return MaintenanceActionResult.Fail($"タスクの登録に失敗しました: {createResult.StandardError}");
    }

    private async Task<(bool QuerySucceeded, TaskOwnership Ownership)> QueryExistingTaskAsync(
        CancellationToken ct)
    {
        var queryResult = await executor.RunAsync(
            "schtasks", BuildQueryXmlArguments(), ct, timeout: TimeSpan.FromSeconds(15));
        if (!queryResult.Success)
        {
            return (false, TaskOwnership.Unknown);
        }

        var currentUser = GetCurrentUserIdentity();
        return (
            true,
            GetTaskOwnership(queryResult.StandardOutput, currentUser.AccountName, currentUser.Sid));
    }

    private MaintenanceActionResult? GetOwnershipError(
        bool querySucceeded,
        TaskOwnership ownership)
    {
        string? message = (querySucceeded, ownership) switch
        {
            (true, TaskOwnership.OtherUser) => "別の Windows ユーザーが登録したタスクのため変更できません",
            (true, TaskOwnership.Unknown) => "登録済みタスクの所有者を確認できないため変更できません",
            (false, _) => "登録済みタスクの有無と所有者を確認できないため削除できません",
            _ => null,
        };

        if (message is null)
        {
            return null;
        }

        LoggerBootstrap.Log.Error($"{Id}: {message}");
        return MaintenanceActionResult.Fail(message);
    }

    internal static string BuildQueryXmlArguments() => $"/query /tn \"{TaskName}\" /xml ONE";

    internal static string BuildDeleteArguments() => $"/delete /tn \"{TaskName}\" /f";

    /// <summary>
    /// タスク定義 XML を書き出した一時ファイルから登録する。バッテリー駆動でも実行する設定は
    /// schtasks のスイッチで指定できないため、/tr ではなく /xml を使う。
    /// </summary>
    internal static string BuildCreateArguments(string xmlPath, bool overwriteExisting = true) =>
        $"/create /tn \"{TaskName}\" /xml \"{xmlPath}\"{(overwriteExisting ? " /f" : string.Empty)}";

    /// <summary>
    /// 登録済み定義が、現行の権限・実行ファイル・多重起動・電源設定と一致するか確認する。
    /// schtasks は Principal と LogonTrigger の UserId を SID / アカウント名という異なる表記へ
    /// 正規化する場合があるため、実効ユーザーの両表記と照合する。
    /// </summary>
    internal static bool IsCurrentTaskDefinition(string xml, string exePath)
    {
        var currentUser = GetCurrentUserIdentity();
        return IsCurrentTaskDefinition(xml, exePath, currentUser.AccountName, currentUser.Sid);
    }

    internal static bool IsCurrentTaskDefinition(
        string xml,
        string exePath,
        string currentAccountName,
        string? currentSid)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var root = document.Root;
            if (root is null)
            {
                return false;
            }

            var ns = root.Name.Namespace;
            var principals = root.Element(ns + "Principals")?.Elements().ToArray();
            var triggers = root.Element(ns + "Triggers")?.Elements().ToArray();
            var actions = root.Element(ns + "Actions")?.Elements().ToArray();
            if (principals is not [{ Name.LocalName: "Principal" } principal]
                || triggers is not [{ Name.LocalName: "LogonTrigger" } trigger]
                || actions is not [{ Name.LocalName: "Exec" } action])
            {
                return false;
            }

            var settings = root.Element(ns + "Settings");
            string? Value(XElement? parent, string name) => parent?.Element(ns + name)?.Value;

            var command = Value(action, "Command")?.Trim().Trim('"');
            return string.Equals(Value(principal, "RunLevel"), "HighestAvailable", StringComparison.Ordinal)
                && string.Equals(Value(principal, "LogonType"), "InteractiveToken", StringComparison.Ordinal)
                && IsCurrentUser(Value(principal, "UserId"), currentAccountName, currentSid)
                && IsCurrentUser(Value(trigger, "UserId"), currentAccountName, currentSid)
                && string.Equals(Value(settings, "MultipleInstancesPolicy"), "IgnoreNew", StringComparison.Ordinal)
                && string.Equals(Value(settings, "DisallowStartIfOnBatteries"), "false", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Value(settings, "StopIfGoingOnBatteries"), "false", StringComparison.OrdinalIgnoreCase)
                && string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Value(action, "Arguments")?.Trim(),
                    ScheduledTempCleanup.CommandLineArgument,
                    StringComparison.Ordinal);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static TaskOwnership GetTaskOwnership(
        string xml,
        string currentAccountName,
        string? currentSid)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var root = document.Root;
            if (root is null)
            {
                return TaskOwnership.Unknown;
            }

            var ns = root.Name.Namespace;
            var userIds = root.Element(ns + "Principals")?
                .Elements(ns + "Principal")
                .Select(principal => principal.Element(ns + "UserId")?.Value)
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .ToArray();

            if (userIds is not { Length: > 0 })
            {
                return TaskOwnership.Unknown;
            }

            return userIds.Any(userId => !IsCurrentUser(userId, currentAccountName, currentSid))
                ? TaskOwnership.OtherUser
                : TaskOwnership.CurrentUser;
        }
        catch (System.Xml.XmlException)
        {
            return TaskOwnership.Unknown;
        }
    }

    private enum TaskOwnership
    {
        Unknown,
        CurrentUser,
        OtherUser,
    }

    private static bool IsCurrentUser(string? value, string accountName, string? sid) =>
        string.Equals(value?.Trim(), accountName, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(sid)
            && string.Equals(value?.Trim(), sid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// タスク定義 XML を組み立てる。要素の並びはタスクスケジューラーのスキーマ順に固定する
    /// (順序が違うと schtasks /xml が受け付けない)。
    /// <list type="bullet">
    /// <item>HighestAvailable + InteractiveToken … サインインしたユーザーの最高権限で、UAC を出さずに走らせる。</item>
    /// <item>DisallowStartIfOnBatteries / StopIfGoingOnBatteries = false … ノート PC でバッテリー駆動中に
    /// サインインした回もスキップさせず、実行中に電源を抜かれても中断しない。</item>
    /// <item>IgnoreNew … 前回の掃除が長引いている間に再サインインしても二重起動させない。</item>
    /// </list>
    /// </summary>
    internal static string BuildTaskXml(string exePath, string userId) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>サインイン時に、Lumin4ti で選択したクリーンアップ項目を実行します。</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userId)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>false</StartWhenAvailable>
            <Enabled>true</Enabled>
            <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
              <Arguments>{ScheduledTempCleanup.CommandLineArgument}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>現在サインインしているユーザーを Principal に固定する。</summary>
    private static string BuildTaskXml(string exePath) =>
        BuildTaskXml(exePath, GetCurrentUserIdentity().AccountName);

    private static (string AccountName, string? Sid) GetCurrentUserIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return (identity.Name, identity.User?.Value);
    }
}

/// <summary>
/// 昇格した schtasks へ渡すタスク定義 XML の置き場。書き出してから読み取られるまでの間に
/// 差し替えられない場所である必要がある。
/// </summary>
internal interface ITaskDefinitionStore
{
    /// <summary>定義を新規作成し、schtasks へ渡すフルパスを返す。</summary>
    string WriteNew(string name, byte[] content);

    /// <summary>登録後に定義を削除する。消し残りは実害が無いため例外は投げない。</summary>
    void Delete(string name);
}

/// <summary>
/// Administrators / SYSTEM だけが書ける ProgramData 配下へ置く既定の実装。
/// レジストリ復元用バックアップと同じ <see cref="ProtectedBackupStorage"/> に載せる。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedTaskDefinitionStore(ProtectedBackupStorage storage) : ITaskDefinitionStore
{
    private const string DirectoryName = "scheduled-tasks";

    public static ProtectedTaskDefinitionStore Default { get; } = new(ProtectedBackupStorage.Default);

    public string WriteNew(string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var relativePath = Path.Combine(DirectoryName, name);
        storage.WriteNewAtomically(relativePath, stream => stream.Write(content));
        return storage.GetFullPath(relativePath);
    }

    public void Delete(string name)
    {
        try
        {
            storage.Delete(Path.Combine(DirectoryName, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 消し残っても次回は別名で作り直すため実害が無い。
        }
    }
}
