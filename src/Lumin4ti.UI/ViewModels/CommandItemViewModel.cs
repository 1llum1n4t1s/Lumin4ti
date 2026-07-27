using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// メンテナンス項目 1 行分。実行ボタン型 (IMaintenanceAction) と
/// ON/OFF トグル型 (IMaintenanceToggle) の両方をこの VM で扱う。
/// </summary>
public partial class CommandItemViewModel : ObservableObject
{
    private readonly Func<CommandItemViewModel, Task> _run;
    private readonly Func<CommandItemViewModel, bool, Task> _setToggle;
    private readonly Func<CommandItemViewModel, string, Task>? _setChoice;
    private bool _suppressToggleWrite;

    public IMaintenanceItem Item { get; }

    /// <summary>表示用ラベル (ローカライズ済み。辞書に訳が無ければ日本語マスター)。</summary>
    public string Label => App.Text(Item.LabelKey, Item.Label);

    /// <summary>表示用説明 (ローカライズ済み)。</summary>
    public string Description => App.Text(Item.DescriptionKey, Item.Description);

    public bool RequiresReboot => Item.RequiresReboot;

    public bool AffectsExplorer => Item.AffectsExplorer;

    public bool IsLongRunning => Item.IsLongRunning;

    /// <summary>ON/OFF トグル型か (false なら実行ボタン型)。</summary>
    public bool IsToggle => Item is IMaintenanceToggle;

    public bool IsAction => Item is IMaintenanceAction;

    /// <summary>選択式 (ドロップダウン) 型か。</summary>
    public bool IsChoice => Item is IMaintenanceChoice;

    /// <summary>ドロップダウンの選択肢 (選択式のときのみ非空)。</summary>
    public ObservableCollection<ChoiceOptionViewModel> ChoiceOptions { get; }

    /// <summary>選択中の項目。ユーザー操作で変わったときだけ適用処理を走らせる。</summary>
    [ObservableProperty]
    private ChoiceOptionViewModel? selectedChoice;

    /// <summary>ドロップダウンを操作できるか (状態既知かつ非実行中)。</summary>
    public bool CanChoose => IsStateKnown && !IsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    private bool isRunning;

    /// <summary>トグルの現在状態 (ON = 最適化適用中)。</summary>
    [ObservableProperty]
    private bool isChecked;

    /// <summary>トグル状態を読み取れたか。false の間はスイッチを無効表示する。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanChoose))]
    private bool isStateKnown;

    /// <summary>トグルを操作可能か。状態既知かつ実行中でないときのみ (多重操作レース防止)。</summary>
    public bool CanToggle => IsStateKnown && !IsRunning;

    /// <summary>実行中のアクションをキャンセルするための CTS (実行中のみ非 null)。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>実行中でキャンセルできる状態か (キャンセルボタンの表示制御)。</summary>
    [ObservableProperty]
    private bool canCancel;

    /// <summary>直近の実行結果の要約 (成功/失敗 + 出力)。未実行時は空。</summary>
    [ObservableProperty]
    private string resultText = string.Empty;

    /// <summary>実行中のライブ出力 (直近数行)。完了したらクリアされ ResultText に置き換わる。</summary>
    [ObservableProperty]
    private string progressText = string.Empty;

    [ObservableProperty]
    private bool lastRunFailed;

    [ObservableProperty]
    private bool lastRunWarning;

    public IAsyncRelayCommand RunCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public CommandItemViewModel(
        IMaintenanceItem item,
        Func<CommandItemViewModel, Task> run,
        Func<CommandItemViewModel, bool, Task> setToggle,
        Func<CommandItemViewModel, string, Task>? setChoice = null)
    {
        Item = item;
        _run = run;
        _setToggle = setToggle;
        _setChoice = setChoice;
        ChoiceOptions = item is IMaintenanceChoice choice
            ? [.. choice.Options.Select(o => new ChoiceOptionViewModel(o))]
            : [];
        RunCommand = new AsyncRelayCommand(() => _run(this), () => !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);

        // 言語切替時にローカライズ済みプロパティを再評価する
        App.LocaleChanged += OnLocaleChanged;
    }

    private void OnLocaleChanged()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
        foreach (var option in ChoiceOptions)
        {
            option.NotifyLocaleChanged();
        }
    }

    /// <summary>実行開始時に呼ぶ。キャンセル用トークンを生成して返す。</summary>
    public CancellationToken BeginRun()
    {
        _cts = new CancellationTokenSource();
        CanCancel = true;
        return _cts.Token;
    }

    /// <summary>実行終了時に呼ぶ。キャンセル用トークンを破棄する。</summary>
    public void EndRun()
    {
        CanCancel = false;
        _cts?.Dispose();
        _cts = null;
    }

    internal void ApplyResultStatus(MaintenanceActionStatus status)
    {
        LastRunFailed = status == MaintenanceActionStatus.Failed;
        LastRunWarning = status == MaintenanceActionStatus.Partial;
    }

    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 既に完了・破棄済みは無視
        }
    }

    /// <summary>
    /// 選択式の現在値を UI へ反映する (適用処理は走らせない)。
    /// 一覧に無い値 (別ツールで設定された数値など) が返った場合は、その値を選択肢へ足して現状を隠さない。
    /// </summary>
    public void ApplySelectedValue(string? value)
    {
        _suppressToggleWrite = true;
        try
        {
            if (value is null)
            {
                IsStateKnown = false;
                ResultText = App.Text(
                    "Toggle.StateUnknown",
                    "状態を取得できませんでした (管理者権限がないか、この PC では利用できない機能です。デバッグ起動は昇格されないため、通常起動でお試しください)");
                return;
            }

            var option = ChoiceOptions.FirstOrDefault(o => o.Value == value);
            if (option is null)
            {
                option = new ChoiceOptionViewModel(new MaintenanceChoiceOption(value, value));
                ChoiceOptions.Add(option);
            }

            SelectedChoice = option;
            IsStateKnown = true;
        }
        finally
        {
            _suppressToggleWrite = false;
        }
    }

    /// <summary>状態読み取り結果を UI 通知なしの副作用ゼロで反映する。</summary>
    public void ApplyState(bool? state)
    {
        _suppressToggleWrite = true;
        try
        {
            if (state is null)
            {
                IsStateKnown = false;
                ResultText = App.Text(
                    "Toggle.StateUnknown",
                    "状態を取得できませんでした (管理者権限がないか、この PC では利用できない機能です。デバッグ起動は昇格されないため、通常起動でお試しください)");
            }
            else
            {
                IsChecked = state.Value;
                IsStateKnown = true;
            }
        }
        finally
        {
            _suppressToggleWrite = false;
        }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_suppressToggleWrite || !IsToggle)
        {
            return;
        }

        _ = _setToggle(this, value);
    }

    partial void OnSelectedChoiceChanged(ChoiceOptionViewModel? value)
    {
        if (_suppressToggleWrite || !IsChoice || value is null || _setChoice is null)
        {
            return;
        }

        _ = _setChoice(this, value.Value);
    }

    partial void OnIsRunningChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    partial void OnCanCancelChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
}

/// <summary>ドロップダウンの選択肢 1 つ分。表示名は言語切替で再評価する。</summary>
public sealed class ChoiceOptionViewModel(MaintenanceChoiceOption option) : ObservableObject
{
    public string Value => option.Value;

    /// <summary>
    /// 表示名。翻訳キーを持つ選択肢だけ辞書から引き、数値のような言語非依存の値はそのまま出す。
    /// 既定の選択肢には「(既定)」を添えて、Windows 標準がどれか分かるようにする。
    /// </summary>
    public string Display
    {
        get
        {
            var label = option.LabelKey is { } key ? App.Text(key, option.Label) : option.Label;
            return option.IsDefault ? $"{label} ({App.Text("Choice.Default", "既定")})" : label;
        }
    }

    public void NotifyLocaleChanged() => OnPropertyChanged(nameof(Display));

    public override string ToString() => Display;
}
