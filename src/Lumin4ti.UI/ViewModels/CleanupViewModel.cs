using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// 左の「クリーンアップ」タブ。内側に 通常／高度／スリム化／設定／その他 の 5 タブを持つ。
/// 「その他」は従来の <see cref="CommandCategoryViewModel"/> (カタログに残る項目) をそのまま使う。
/// 構築しても走査・削除は始めない (起動時・再アクティブ時に自動で走らせない)。
/// </summary>
public sealed class CleanupViewModel : ObservableObject
{
    public CommonCleanViewModel Common { get; }

    public AdvancedCleanViewModel Advanced { get; }

    public SlimViewModel Slim { get; }

    public CleanupSettingsViewModel Settings { get; }

    /// <summary>「その他」タブ。<see cref="CommandCategory.Cleanup"/> の項目。</summary>
    public CommandCategoryViewModel Other { get; }

    /// <summary>実行中表示の対象にする操作 (MainWindowViewModel が購読する)。</summary>
    public IReadOnlyList<IProcessingOperation> Operations { get; }

    public CleanupViewModel(
        MaintenanceActionCatalog catalog,
        MaintenanceOperationCoordinator coordinator,
        CommonCleanViewModel common,
        AdvancedCleanViewModel advanced,
        SlimViewModel slim,
        CleanupSettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        Common = common;
        Advanced = advanced;
        Slim = slim;
        Settings = settings;
        Other = new CommandCategoryViewModel(
            catalog,
            coordinator,
            CommandCategory.Cleanup,
            "クリーンアップ",
            "ディスク領域の回収と、不要なファイル・登録情報の削除を行います。");
        Operations = [Common, Advanced, Slim, Settings];
    }

    /// <summary>「その他」のトグル状態を読み込む (MainWindowViewModel の一括再読込から呼ぶ)。</summary>
    public Task LoadToggleStatesAsync(CancellationToken ct = default) => Other.LoadToggleStatesAsync(ct);
}
