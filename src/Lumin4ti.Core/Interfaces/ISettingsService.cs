using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }

    Task SaveAsync(CancellationToken ct = default);
}
