using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

/// <summary>
/// 削除の実行中に製品を閉じる判断を聞く、「通常」専用のモーダルダイアログ。実行中はメイン画面が処理中表示で
/// 塞がれるため、インライン欄ではなく別ウィンドウにする。×で閉じた場合・所有ウィンドウが無い場合は安全側 (中止・強制終了しない) とする。
/// </summary>
public partial class CommonCleanPromptDialog : Window
{
    public CommonCleanPromptDialog()
    {
        InitializeComponent();
    }

    /// <summary>押されたボタンの番号 (0 = 主、1 = 副、2 = 中止)。閉じられたら -1。</summary>
    internal int Choice { get; private set; } = -1;

    internal void Configure(string body, IReadOnlyList<string> buttons)
    {
        BodyText.Text = body;
        var all = new[] { PrimaryButton, SecondaryButton, AbortButton };
        for (var i = 0; i < all.Length; i++)
        {
            var index = i;
            all[i].IsVisible = i < buttons.Count;
            if (i < buttons.Count)
            {
                all[i].Content = buttons[i];
                all[i].Click += (_, _) =>
                {
                    Choice = index;
                    Close();
                };
            }
        }
    }
}

/// <summary><see cref="ICleanupRunPrompts"/> の実体。UI スレッドでモーダルを開き、中断されたら閉じて安全側を返す。</summary>
internal sealed class CommonCleanProcessPrompts : ICleanupRunPrompts
{
    public async Task<CleanupProcessChoice> ChooseForRunningProcessesAsync(CleanupProcessPrompt prompt, CancellationToken ct)
    {
        var body = CleanupText.Get("Cleanup.Process.Body", prompt.RuleName, string.Join(", ", prompt.ProcessNames));
        var choice = await AskAsync(
            body,
            [CleanupText.Get("Cleanup.Process.Close"), CleanupText.Get("Cleanup.Process.Skip"), CleanupText.Get("Cleanup.Process.Abort")],
            ct).ConfigureAwait(false);
        return choice switch
        {
            0 => CleanupProcessChoice.Close,
            1 => CleanupProcessChoice.SkipRule,
            _ => CleanupProcessChoice.Abort,
        };
    }

    public async Task<bool> ConfirmForceTerminationAsync(CleanupProcessPrompt prompt, CancellationToken ct)
    {
        var body = CleanupText.Get("Cleanup.Process.ForceBody", prompt.RuleName, string.Join(", ", prompt.ProcessNames));
        var choice = await AskAsync(
            body,
            [CleanupText.Get("Cleanup.Process.ForceYes"), CleanupText.Get("Cleanup.Process.ForceNo")],
            ct).ConfigureAwait(false);
        return choice == 0;
    }

    private static async Task<int> AskAsync(string body, IReadOnlyList<string> buttons, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return -1;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } owner)
            {
                return -1;
            }

            var dialog = new CommonCleanPromptDialog();
            dialog.Configure(body, buttons);
            using var registration = ct.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            await dialog.ShowDialog(owner);
            return dialog.Choice;
        });
    }
}
