using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Lumin4ti.Verification;

/// <summary>
/// 自己検査・書き出しの間、前面ウィンドウの持ち主を定期的に調べる (自分のプロセスが前面になった回数と、
/// 自分の窓が IsActive になった回数、同時に開いていた窓の最大数を数える)。調べるだけで、窓には何もしない。
/// </summary>
internal sealed class PreviewForeground : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyList<Window>> _windows;
    private readonly uint _self = (uint)Environment.ProcessId;
    private int _samples;
    private int _ownForeground;
    private int _ownActive;
    private int _maxWindows;
    private int _maxVisibleHwnd;
    private uint _lastForegroundPid;

    public PreviewForeground(Func<IReadOnlyList<Window>> windows)
    {
        _windows = windows;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    public static PreviewForeground ForApplication() => new(() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? []);

    /// <summary>前面になった回数 (このプロセスのどの HWND でも) と、可視のトップレベル HWND が 0 のとき true。ownWindowActive は Headless では仮想の活性なので判定に含めない。</summary>
    public bool Clean => _ownForeground == 0 && _maxVisibleHwnd == 0;

    public string Summary =>
        $"samples={_samples} ownProcessForeground={_ownForeground} visibleHwndMax={_maxVisibleHwnd} ownWindowActive(仮想)={_ownActive} maxWindows={_maxWindows} lastForegroundPid={_lastForegroundPid} selfPid={_self}";

    /// <summary>このプロセスが持つ可視のトップレベル HWND (メニュー・ToolTip などのポップアップを含む) の数。</summary>
    public static int VisibleWindowCount()
    {
        var self = (uint)Environment.ProcessId;
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self && IsWindowVisible(hwnd)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private void Sample()
    {
        _samples++;
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            _lastForegroundPid = pid;
            if (pid == _self) _ownForeground++;
        }

        _maxVisibleHwnd = Math.Max(_maxVisibleHwnd, VisibleWindowCount());
        var windows = _windows();
        _maxWindows = Math.Max(_maxWindows, windows.Count);
        if (windows.Any(w => w.IsActive)) _ownActive++;
    }

    public void Dispose() => _timer.Stop();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
