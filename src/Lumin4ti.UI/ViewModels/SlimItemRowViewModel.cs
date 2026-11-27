using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// スリム化タブの項目 1 行。名前・説明・警告・走査後の状態とサイズ・実行後の結果を持つ。
/// 選択は既定でオフで、実行できる (<see cref="SlimScanStatus.Available"/>) 項目だけ選べる。
/// </summary>
public sealed class SlimItemRowViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;
    private bool _canSelect;
    private bool _editable;
    private string _statusText = string.Empty;
    private string _sizeText = string.Empty;
    private string _reasonText = string.Empty;
    private string _resultText = string.Empty;
    private string _resultMessage = string.Empty;
    private bool _isFailure;
    private SlimScanItem? _scanItem;
    private SlimItemResult? _result;

    internal SlimItemRowViewModel(SlimItemDefinition definition, Action selectionChanged)
    {
        Definition = definition;
        _selectionChanged = selectionChanged;
        ApplyNotScanned();
    }

    internal SlimItemDefinition Definition { get; }

    // 名前・説明・警告は現在の言語で引く (言語を切り替えたら RefreshLocale で通知する)。
    public string Name => CleanupText.GetByCoreKey(Definition.NameKey);

    public string Description => CleanupText.GetByCoreKey(Definition.DescriptionKey);

    /// <summary>警告文 (警告なしは空)。</summary>
    public string WarningText => Definition.WarningKey is { } key ? CleanupText.GetByCoreKey(key) : string.Empty;

    /// <summary>警告区分の見出し (補足・注意・元に戻せません。警告なしは空)。</summary>
    public string LevelText => Definition.Warning is SlimWarningLevel.None
        ? string.Empty
        : CleanupText.Get($"Cleanup.Slim.Ui.Level.{Definition.Warning}");

    public bool HasWarning => Definition.WarningKey is not null;

    public bool IsIrreversible => Definition.Warning == SlimWarningLevel.Irreversible;

    /// <summary>項目のアクセシビリティ名 (名前と警告区分)。</summary>
    public string AutomationName => LevelText.Length == 0 ? Name : $"{Name} ({LevelText})";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            // 実行できない項目は選択できない。
            var next = value && _canSelect;
            if (SetProperty(ref _isSelected, next))
            {
                _selectionChanged();
            }
        }
    }

    public bool CanSelect
    {
        get => _canSelect;
        private set
        {
            if (SetProperty(ref _canSelect, value))
            {
                OnPropertyChanged(nameof(IsCheckEnabled));
            }
        }
    }

    /// <summary>チェックボックスを操作できるか (選択できる項目で、実行中・確認中でないとき)。</summary>
    public bool IsCheckEnabled => _canSelect && _editable;

    internal void RefreshEditable(bool editable)
    {
        _editable = editable;
        OnPropertyChanged(nameof(IsCheckEnabled));
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SizeText
    {
        get => _sizeText;
        private set => SetProperty(ref _sizeText, value);
    }

    /// <summary>利用不可・対象なしの理由 (無いときは空)。</summary>
    public string ReasonText
    {
        get => _reasonText;
        private set
        {
            if (SetProperty(ref _reasonText, value))
            {
                OnPropertyChanged(nameof(HasReason));
            }
        }
    }

    public bool HasReason => _reasonText.Length > 0;

    /// <summary>実行結果の区分 (成功・一部のみ・失敗…)。実行前は空。</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => _resultText.Length > 0;

    /// <summary>実行結果の詳細 (Core が返したメッセージ)。</summary>
    public string ResultMessage
    {
        get => _resultMessage;
        private set => SetProperty(ref _resultMessage, value);
    }

    /// <summary>成功以外の結果か (色分け用)。</summary>
    public bool IsFailure
    {
        get => _isFailure;
        private set => SetProperty(ref _isFailure, value);
    }

    /// <summary>走査前の状態へ戻す (選択・結果を消す)。</summary>
    internal void ApplyNotScanned()
    {
        _scanItem = null;
        _result = null;
        Reset(canSelect: false);
        StatusText = string.Empty;
        SizeText = string.Empty;
        ReasonText = string.Empty;
    }

    internal void ApplyScan(SlimScanItem item)
    {
        _scanItem = item;
        _result = null;
        Reset(item.Selectable);
        RefreshTexts();
    }

    internal void ApplyResult(SlimItemResult result)
    {
        _scanItem = null;
        _result = result;
        Reset(canSelect: false);
        RefreshTexts();
        IsFailure = result.Status != SlimItemStatus.Succeeded;
    }

    /// <summary>言語を切り替えたとき、表示済みの文言を引き直す。選択・走査結果・実行結果は変えない。</summary>
    internal void RefreshLocale()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(AutomationName));
        RefreshTexts();
    }

    /// <summary>保持している走査結果または実行結果から、状態・理由・サイズ・結果の文言を作る (選択は変えない)。</summary>
    private void RefreshTexts()
    {
        if (_result is { } result)
        {
            StatusText = string.Empty;
            SizeText = result.FreedBytes > 0 ? CleanupText.FormatBytes(result.FreedBytes) : string.Empty;
            ReasonText = string.Empty;
            ResultText = CleanupText.Get($"Cleanup.Slim.Ui.Result.{result.Status}");
            ResultMessage = CleanupText.Detail(result.Message);
        }
        else if (_scanItem is { } item)
        {
            StatusText = CleanupText.Get($"Cleanup.Slim.Ui.Status.{item.Status}");
            ReasonText = item.Reason == SlimReason.None ? string.Empty : CleanupText.Get($"Cleanup.Slim.Ui.Reason.{item.Reason}");
            SizeText = item.Selectable ? FormatSize(item) : string.Empty;
        }
        else
        {
            StatusText = string.Empty;
            SizeText = string.Empty;
            ReasonText = string.Empty;
            ResultText = string.Empty;
            ResultMessage = string.Empty;
        }
    }

    private void Reset(bool canSelect)
    {
        var wasSelected = _isSelected;
        _canSelect = false;
        SetProperty(ref _isSelected, false, nameof(IsSelected));
        CanSelect = canSelect;
        ResultText = string.Empty;
        ResultMessage = string.Empty;
        IsFailure = false;
        if (wasSelected)
        {
            _selectionChanged();
        }
    }

    private static string FormatSize(SlimScanItem item)
    {
        if (item.Bytes is not { } bytes)
        {
            return CleanupText.Get("Cleanup.Slim.Ui.SizeUnknown");
        }

        var size = CleanupText.FormatBytes(bytes);
        return item.BytesIsEstimate ? CleanupText.Get("Cleanup.Slim.Ui.Estimate", size) : size;
    }
}
