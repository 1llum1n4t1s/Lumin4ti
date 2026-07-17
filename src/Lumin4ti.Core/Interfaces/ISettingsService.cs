using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }

    Task SaveAsync(CancellationToken ct = default);

    /// <summary>この時点までに要求された保存がすべて完了するまで待機する。</summary>
    Task FlushAsync(CancellationToken ct = default);
}
