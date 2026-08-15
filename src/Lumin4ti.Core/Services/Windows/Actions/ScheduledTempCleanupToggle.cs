using System.Runtime.Versioning;
using System.Security;
using System.Text;
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
        isTrustedInstalledExecutable ?? (path => WindowsPerMachineMigration.IsCurrentProcessPerMachine(path));

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
        "タスクはサインインしたユーザーの権限だけで動き、管理者権限や UAC の確認は必要ありません。" +
        "そのため、管理者権限やサービスの停止が要る項目 (システムの一時ファイル・Windows Update キャッシュ等) を選ぶと、" +
        "その項目だけ失敗またはスキップとして記録されます。" +
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
        var result = await executor.RunAsync("schtasks", BuildQueryArguments(), ct);
        return result.Success;
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        if (!on)
        {
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
        // 署名済み・Program Files 配下・MSI インストール済みの正規実体だけを登録対象にする。
        if (!_isTrustedInstalledExecutable(exePath))
        {
            LoggerBootstrap.Log.Error($"{Id}: 実行ファイルが信頼できるインストール済みの実体ではありません ({exePath})");
            return MaintenanceActionResult.Fail("信頼できないインストール状態のため、タスクを登録できませんでした");
        }

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
            createResult = await executor.RunAsync("schtasks", BuildCreateArguments(xmlPath), ct);
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

    internal static string BuildQueryArguments() => $"/query /tn \"{TaskName}\"";

    internal static string BuildDeleteArguments() => $"/delete /tn \"{TaskName}\" /f";

    /// <summary>
    /// タスク定義 XML を書き出した一時ファイルから登録する。バッテリー駆動でも実行する設定は
    /// schtasks のスイッチで指定できないため、/tr ではなく /xml を使う。
    /// </summary>
    internal static string BuildCreateArguments(string xmlPath) =>
        $"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f";

    /// <summary>
    /// タスク定義 XML を組み立てる。要素の並びはタスクスケジューラーのスキーマ順に固定する
    /// (順序が違うと schtasks /xml が受け付けない)。
    /// <list type="bullet">
    /// <item>LeastPrivilege + InteractiveToken … サインインのたびに UAC を出さず非昇格で走らせる。</item>
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
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
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
        BuildTaskXml(exePath, $@"{Environment.UserDomainName}\{Environment.UserName}");
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
