using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// オペレーションレコーダー API が記録に使うファイル数の設定。ON/OFF に収まらないので選択式にしている。
///
/// 「無効」は選択肢に持たない。この API はプリフェッチ機構に連動し、無効化は親項目の
/// 「アプリ起動プリフェッチ」を OFF にすることと同義で、単独では選べないため
/// (Windows が Disable-MMAgent を 0x80070032 で拒否する環境もある)。
/// ここでは記録量だけを扱い、有効・無効の表現は親トグルに任せる。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MmAgentOperationApiChoice : IMaintenanceChoice
{
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
        "SysMain (旧 Superfetch) の動作を外部ツールが記録・再生するための API が、記録に使うファイル数を選びます。" +
        "ベンチマークや性能解析ツール向けの機能で、通常の利用では使いません。数を小さくするほど記録の負荷とディスク使用量を抑えられます。" +
        "機能そのものを止めたい場合は、親項目の「アプリ起動プリフェッチ」を OFF にしてください " +
        "(この API はプリフェッチ機構に連動して無効になるため、単独で無効にすることはできません)。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    /// <summary>プリフェッチ機構に連動するため、アプリ起動プリフェッチの子項目として表示する。</summary>
    public string? ParentId => "mmagent-launch-prefetch";

    // 数値は言語に依存しないので、そのまま表示する (翻訳キーは不要)。
    // 既定印は DefaultMaxFiles から導出し、定数と選択肢が食い違わないようにする。
    public IReadOnlyList<MaintenanceChoiceOption> Options { get; } =
    [
        .. new[] { 128, 256, 512, 1024 }.Select(files =>
        {
            var value = files.ToString(CultureInfo.InvariantCulture);
            return new MaintenanceChoiceOption(value, value, IsDefault: files == DefaultMaxFiles);
        }),
    ];

    /// <summary>
    /// 現在の記録ファイル数を返す。有効・無効は親トグルが表すため、ここでは問い合わせない
    /// (無効時に選択肢に無い値を返すと、UI が未知の値として選択肢を作ってしまう)。
    /// </summary>
    public async Task<string?> GetSelectedValueAsync(CancellationToken ct = default)
    {
        var maxFiles = await ReadMaxFilesAsync(ct);
        return maxFiles?.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<MaintenanceActionResult> SetSelectedValueAsync(string value, CancellationToken ct = default)
    {
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
            else
            {
                // 有効化できたか分からないまま古い値を残さない (実際の状態は最後に読み直す)。
                _stateProvider.Invalidate("OperationAPI");
                LoggerBootstrap.Log.Info(
                    $"{Id}: Enable-MMAgent -OperationAPI が失敗: {FirstLine(enable.StandardError)}");
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
        var applied = $"  - 記録ファイル数を {maxFiles} に設定しました";

        // 記録ファイル数を書けても、プリフェッチ機構が OFF なら機能自体は無効のまま。
        // 表示は読み直した実状態 (無効) になるので、成功だけを伝えると食い違って見える。
        if (await _stateProvider.ReadFreshAsync("OperationAPI", ct) == false)
        {
            LoggerBootstrap.Log.Info($"{Id}: 記録ファイル数は設定したが機能は無効のまま");
            return MaintenanceActionResult.Partial(
                $"{applied}{Environment.NewLine}" +
                "  - ただしオペレーションレコーダーは無効のままです (プリフェッチ機構に連動して無効化されています)" +
                $"{Environment.NewLine}" +
                "  - 有効にするには「アプリ起動プリフェッチ」を ON にしてください");
        }

        return MaintenanceActionResult.Ok(applied);
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
