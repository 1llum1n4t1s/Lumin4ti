using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

internal enum InteractiveScopeReason
{
    None,

    /// <summary>対話利用者と昇格に使った利用者の SID が異なる。</summary>
    DifferentUser,

    /// <summary>どちらかの SID を取得できず、同一と確認できない。</summary>
    UserUnknown,
}

/// <summary>利用者スコープの規則 (HKCU・プロファイル・SQLite・Shell) を使えるか。使えない場合の理由を持つ。</summary>
internal readonly record struct InteractiveScopeResult(bool UserScopeAvailable, InteractiveScopeReason Reason);

/// <summary>OS への境界。テストでは差し替える。</summary>
internal interface IInteractiveUserSource
{
    /// <summary>対話中のユーザー (Explorer のシェルウィンドウを持つプロセスの利用者)。</summary>
    SecurityIdentifier? GetInteractiveUserSid();

    /// <summary>昇格して動いている自プロセスの利用者。</summary>
    SecurityIdentifier? GetElevatedUserSid();
}

/// <summary>
/// 対話利用者と昇格利用者が別人のとき、昇格側の HKCU・プロファイルは対話利用者のものではない。
/// そのため利用者スコープの規則を全て無効にする。確認できない場合も無効にする。
/// </summary>
internal static class InteractiveScope
{
    public static InteractiveScopeResult Evaluate(IInteractiveUserSource source)
    {
        var interactive = source.GetInteractiveUserSid();
        var elevated = source.GetElevatedUserSid();
        if (interactive is null || elevated is null)
        {
            return new InteractiveScopeResult(false, InteractiveScopeReason.UserUnknown);
        }

        return UnelevatedCommandExecutor.AreSameUser(interactive, elevated)
            ? new InteractiveScopeResult(true, InteractiveScopeReason.None)
            : new InteractiveScopeResult(false, InteractiveScopeReason.DifferentUser);
    }

    public static InteractiveScopeResult EvaluateCurrent() => Evaluate(new WindowsInteractiveUserSource());

    /// <summary>規則を実行できるか。machine は常に可、user は利用者スコープが使えるときだけ可。</summary>
    public static bool IsRuleAvailable(CleanupRule rule, InteractiveScopeResult scope) =>
        rule.Scope == CleanupScope.Machine || scope.UserScopeAvailable;

    private sealed class WindowsInteractiveUserSource : IInteractiveUserSource
    {
        public SecurityIdentifier? GetInteractiveUserSid()
        {
            var shell = GetShellWindow();
            if (shell == nint.Zero || GetWindowThreadProcessId(shell, out var pid) == 0)
            {
                return null;
            }

            return WindowsProcessControl.TryQueryUserSid((int)pid);
        }

        public SecurityIdentifier? GetElevatedUserSid()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint GetShellWindow();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
