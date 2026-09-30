using VelopackUpdateDialog;

namespace Lumin4ti.UI.Services;

/// <summary>
/// VelopackUpdateDialog.Avalonia が要求する文字列セット (<see cref="IUpdateDialogStrings"/>) の
/// 現在の表示言語で解決する。シングルトンでも取得時に辞書を参照し、言語切替を反映する。
/// </summary>
public sealed class Lumin4tiUpdateStrings : IUpdateDialogStrings
{
    public static Lumin4tiUpdateStrings Instance { get; } = new();

    private Lumin4tiUpdateStrings()
    {
    }

    public string Title => App.Text("UpdateDialog.Title", "アップデート");

    public string AvailableHeader => App.Text("UpdateDialog.AvailableHeader", "新しいバージョンがあります");

    public string DownloadAndInstall => App.Text("UpdateDialog.DownloadAndInstall", "ダウンロードしてインストール");

    public string IgnoreThisVersion => App.Text("UpdateDialog.IgnoreThisVersion", "このバージョンをスキップ");

    public string UpToDateMessage => App.Text("UpdateDialog.UpToDateMessage", "お使いのバージョンは最新です");

    public string ErrorHeader => App.Text("UpdateDialog.ErrorHeader", "更新の確認に失敗しました");

    public string Close => App.Text("UpdateDialog.Close", "閉じる");

    public string CheckingMessage => App.Text("UpdateDialog.CheckingMessage", "更新を確認しています…");
}
