namespace Lumin4ti.Core.Services;

/// <summary>
/// 多重起動防止。ユーザーが書き込める一時ディレクトリを使わず、セッション単位の
/// named mutex で実装する。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly bool _acquired;

    public SingleInstanceGuard(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        if (appName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("アプリ名には英数字、ハイフン、アンダースコアだけを使用できます。", nameof(appName));
        }

        try
        {
            _mutex = new Mutex(
                initiallyOwned: true,
                name: $@"Local\{appName}.SingleInstance",
                createdNew: out _acquired);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            _mutex = null;
            _acquired = false;
        }
    }

    public bool TryAcquire() => _acquired;

    public void Dispose()
    {
        if (_acquired)
        {
            _mutex?.ReleaseMutex();
        }

        _mutex?.Dispose();
    }
}
