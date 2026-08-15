namespace Lumin4ti.Core.Models;

/// <summary>settings.json をどの経路で初期化したか。</summary>
public enum SettingsLoadStatus
{
    /// <summary>既存の設定ファイルを正常に読み込んだ。</summary>
    Loaded,

    /// <summary>設定ファイルが存在せず、初回用の既定値で開始した。</summary>
    Missing,

    /// <summary>既存ファイルを読み込めず、退避用の既定値で開始した。</summary>
    Failed,
}
