namespace Lumin4ti.Core.Models;

/// <summary>状態不明の理由を UI 側で翻訳するためのキーと日本語フォールバック。</summary>
public sealed record MaintenanceStateDiagnostic(string Key, string Fallback);
