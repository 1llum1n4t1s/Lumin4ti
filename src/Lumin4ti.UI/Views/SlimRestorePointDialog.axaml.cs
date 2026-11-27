using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

/// <summary>
/// 復元ポイントを利用できないときの判断 (続行・保護を有効にして再試行・中止) を聞く、スリム化専用のモーダルダイアログ。
/// ×で閉じた場合は中止として扱う (安全側)。
/// </summary>
public partial class SlimRestorePointDialog : Window
{
    private RestorePointDecision _decision = RestorePointDecision.Abort;

    public SlimRestorePointDialog()
    {
        InitializeComponent();
        ContinueButton.Click += (_, _) => Choose(RestorePointDecision.ContinueWithout);
        EnableButton.Click += (_, _) => Choose(RestorePointDecision.EnableProtectionAndRetry);
        AbortButton.Click += (_, _) => Choose(RestorePointDecision.Abort);
    }

    internal RestorePointDecision Decision => _decision;

    internal void Configure(string body, bool canEnableProtection)
    {
        BodyText.Text = body;
        EnableButton.IsVisible = canEnableProtection;
    }

    private void Choose(RestorePointDecision decision)
    {
        _decision = decision;
        Close();
    }
}

/// <summary><see cref="ISlimRestorePointPrompt"/> の実体。UI スレッドでモーダルを開き、中断されたら閉じて中止を返す。</summary>
internal sealed class SlimRestorePointPrompt(Func<Window?> ownerProvider) : ISlimRestorePointPrompt
{
    public async Task<RestorePointDecision> AskAsync(string body, bool canEnableProtection, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return RestorePointDecision.Abort;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ownerProvider() is not { } owner)
            {
                return RestorePointDecision.Abort;
            }

            var dialog = new SlimRestorePointDialog();
            dialog.Configure(body, canEnableProtection);
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            await dialog.ShowDialog(owner);
            return dialog.Decision;
        });
    }
}
