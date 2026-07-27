using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// オペレーションレコーダー API の設定。ON/OFF トグルではなく選択式にしている。
///
/// この機能は Windows によって Disable-MMAgent が拒否される (0x80070032) ことがあり、
/// その場合でも Set-MMAgent -MaxOperationAPIFiles で記録するファイル数は減らせる。
/// 「無効にできる環境では無効、できない環境では記録量を絞る」を 1 つの操作で選べるようにする。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MmAgentOperationApiChoice : IMaintenanceChoice
{
    /// <summary>無効を表す選択値 (数値ではないので特別扱いする)。</summary>
    public const string DisabledValue = "disabled";

    /// <summary>Windows 既定の記録ファイル数。</summary>
    private const int DefaultMaxFiles = 512;

    private readonly ICommandExecutor _executor;
    private readonly MmAgentStateProvider _stateProvider;

    public MmAgentOperationApiChoice(ICommandExecutor executor, MmAgentStateProvider stateProvider)
    {
        _executor = executor;
        _stateProvider = stateProvider;
    }

    public string Id => "mmagent-operation-api";

    public string Label => "オペレーションレコーダー API";

    public string Description =>
        "SysMain (旧 Superfetch) の動作を外部ツールが記録・再生するための API です。ベンチマークや性能解析ツール向けで、通常の利用では使いません。" +
        "推奨は「無効」ですが、この機能の無効化を Windows が拒否する環境があります (その場合は記録するファイル数を減らすと負荷を抑えられます)。" +
        "無効化はプリフェッチ機構に連動するため、「アプリ起動プリフェッチ」や「アプリ事前起動」を OFF にすると一緒に無効になります。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    // 数値は言語に依存しないのでそのまま表示し、翻訳が要る「無効」だけキーを持たせる。
    public IReadOnlyList<MaintenanceChoiceOption> Options { get; } =
    [
        new(DisabledValue, "無効", LabelKey: "Choice.Disabled"),
        new("128", "128"),
        new("256", "256"),
        new("512", "512", IsDefault: true),
        new("1024", "1024"),
    ];

    public async Task<string?> GetSelectedValueAsync(CancellationToken ct = default)
    {
        var enabled = await _stateProvider.GetAsync("OperationAPI", ct);
        if (enabled == false)
        {
            return DisabledValue;
        }

        var maxFiles = await ReadMaxFilesAsync(ct);
        return maxFiles?.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<MaintenanceActionResult> SetSelectedValueAsync(string value, CancellationToken ct = default)
    {
        if (string.Equals(value, DisabledValue, StringComparison.OrdinalIgnoreCase))
        {
            return await DisableAsync(ct);
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maxFiles) || maxFiles <= 0)
        {
            return MaintenanceActionResult.Fail($"記録ファイル数として解釈できない値です: {value}");
        }

        // 無効状態から数値を選んだ場合は、まず機能自体を有効に戻す (失敗しても数値設定は試す)。
        if (await _stateProvider.GetAsync("OperationAPI", ct) == false)
        {
            var enable = await RunAsync("Enable-MMAgent -OperationAPI -ErrorAction Stop", ct);
            if (enable.Success)
            {
                _stateProvider.SetKnownValue("OperationAPI", true);
            }
        }

        var result = await RunAsync(
            $"Set-MMAgent -MaxOperationAPIFiles {maxFiles.ToString(CultureInfo.InvariantCulture)} -ErrorAction Stop",
            ct);
        if (!result.Success)
        {
            var reason = FirstLine(result.StandardError);
            LoggerBootstrap.Log.Error($"{Id}: Set-MMAgent -MaxOperationAPIFiles {maxFiles} が失敗: {reason}");
            return MaintenanceActionResult.Fail(
                $"記録ファイル数を変更できませんでした{(reason.Length > 0 ? $": {reason}" : string.Empty)}");
        }

        LoggerBootstrap.Log.Info($"{Id}: 記録ファイル数を {maxFiles} に設定");
        return MaintenanceActionResult.Ok($"  - 記録ファイル数を {maxFiles} に設定しました");
    }

    private async Task<MaintenanceActionResult> DisableAsync(CancellationToken ct)
    {
        var result = await RunAsync("Disable-MMAgent -OperationAPI -ErrorAction Stop", ct);
        if (result.Success)
        {
            _stateProvider.SetKnownValue("OperationAPI", false);
            LoggerBootstrap.Log.Info($"{Id}: 無効化しました");
            return MaintenanceActionResult.Ok("  - オペレーションレコーダー API を無効化しました");
        }

        // 既に無効なら目的は達成済み (冪等)。
        if (await _stateProvider.ReadFreshAsync("OperationAPI", ct) == false)
        {
            _stateProvider.SetKnownValue("OperationAPI", false);
            return MaintenanceActionResult.Ok("  - オペレーションレコーダー API は既に無効です");
        }

        var reason = FirstLine(result.StandardError);
        if (MmAgentFeatureToggle.IsNotSupportedError(result.StandardError))
        {
            LoggerBootstrap.Log.Error($"{Id}: Disable-MMAgent はこの Windows でサポートされていません");
            return MaintenanceActionResult.Fail(
                "この Windows の Disable-MMAgent が無効化を拒否しました (この要求はサポートされていません)。" +
                "「アプリ起動プリフェッチ」または「アプリ事前起動」を OFF にすると連動して無効になります。" +
                "そのままにする場合は、記録ファイル数を小さい値にすると負荷を抑えられます。");
        }

        LoggerBootstrap.Log.Error($"{Id}: Disable-MMAgent が失敗: {reason}");
        return MaintenanceActionResult.Fail(
            $"無効化できませんでした{(reason.Length > 0 ? $": {reason}" : string.Empty)}");
    }

    /// <summary>Get-MMAgent の MaxOperationAPIFiles を読む (共有プロバイダは真偽値しか保持しないため個別に取得)。</summary>
    private async Task<int?> ReadMaxFilesAsync(CancellationToken ct)
    {
        var result = await RunAsync(
            "Get-MMAgent -ErrorAction Stop | Select-Object -ExpandProperty MaxOperationAPIFiles | ConvertTo-Json -Compress",
            ct);
        if (!result.Success)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput.Trim());
            return doc.RootElement.ValueKind == JsonValueKind.Number ? doc.RootElement.GetInt32() : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private Task<CommandExecutionResult> RunAsync(string command, CancellationToken ct) =>
        _executor.RunAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"", ct);

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    /// <summary>既定値 (Windows の初期状態) の選択値。</summary>
    public static string DefaultValue => DefaultMaxFiles.ToString(CultureInfo.InvariantCulture);
}
