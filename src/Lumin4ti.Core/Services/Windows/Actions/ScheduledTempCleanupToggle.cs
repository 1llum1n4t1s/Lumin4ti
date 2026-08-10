using System.Runtime.Versioning;
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
    Func<string, bool>? isTrustedInstalledExecutable = null) : IMaintenanceToggle
{
    /// <summary>タスク名。専用フォルダの下に置き、他のタスクと混ざらないようにする。</summary>
    internal const string TaskName = @"\Lumin4ti\ScheduledTempCleanup";

    /// <summary>
    /// 登録前のパス信頼確認。既定は実際の署名・インストール状態検証だが、テストでは差し替える。
    /// </summary>
    private readonly Func<string, bool> _isTrustedInstalledExecutable =
        isTrustedInstalledExecutable ?? (path => WindowsPerMachineMigration.IsCurrentProcessPerMachine(path));

    public string Id => "scheduled-temp-cleanup";

    public string Label => "サインイン時に %TEMP% と迷子の nul ファイルを自動削除するタスクを登録";

    public string Description =>
        "Windows タスクスケジューラーに、サインインのたびに %TEMP% (ユーザーの一時フォルダ) の中身と、" +
        "システムドライブ全体に残る「nul」という名前の迷子ファイルを削除するタスクを登録します。" +
        "タスクはサインインしたユーザーの権限だけで動き、管理者権限や UAC の確認は必要ありません。" +
        "手動実行では nul ファイルを作った開発ツールがまだファイルを掴んでいて消せないことがありますが、" +
        "サインイン直後ならそれらのプロセスが起動しておらず削除が成功しやすくなります。" +
        "ドライブ全体を毎回検索するため、ファイル数の多い環境ではサインインのたびに数分以上かかることがあります。" +
        "使用中のファイルは自動的にスキップされます。OFF にするとタスクを削除します。";

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

        var createResult = await executor.RunAsync("schtasks", BuildCreateArguments(exePath), ct);
        if (createResult.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: タスクを登録しました ({exePath})");
            return MaintenanceActionResult.Ok("  - サインイン時に %TEMP% を削除するタスクを登録しました");
        }

        LoggerBootstrap.Log.Error($"{Id}: schtasks /create (exit={createResult.ExitCode}): {createResult.StandardError}");
        return MaintenanceActionResult.Fail($"タスクの登録に失敗しました: {createResult.StandardError}");
    }

    internal static string BuildQueryArguments() => $"/query /tn \"{TaskName}\"";

    internal static string BuildDeleteArguments() => $"/delete /tn \"{TaskName}\" /f";

    /// <summary>
    /// /tr の値は「実行ファイルパス + 引数」を 1 個の引数として渡す必要があるため、
    /// 実行ファイルパス側だけ内側の \" で囲む (パスに空白を含む Program Files 配下でも壊れない)。
    /// /rl limited で非昇格実行にし、サインインのたびに UAC を出さない。
    /// </summary>
    internal static string BuildCreateArguments(string exePath) =>
        $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" {ScheduledTempCleanup.CommandLineArgument}\" " +
        "/sc onlogon /rl limited /f";
}
