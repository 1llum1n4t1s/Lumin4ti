using System.ComponentModel;
using System.Security;
using System.Security.Principal;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>ルートを開く工場に、利用者の Classes のハイブ (<c>HKEY_USERS\&lt;SID&gt;_Classes</c>) を開く操作を足したもの。</summary>
internal interface IRegistryCleanerRootFactory : IRegistryCleanerKeyFactory
{
    /// <summary><c>HKEY_USERS\&lt;sid&gt;_Classes</c> を開く。<paramref name="sid"/> は <c>_Classes</c> を含まない利用者の SID。</summary>
    IRegistryCleanerKey OpenUserClasses(string sid, RegistryCleanView view);
}

/// <summary>実レジストリのルートを開く工場。利用者の Classes は SID を検証してから、実体のハイブのルートを直接開く。</summary>
internal sealed class NativeRegistryCleanerRootFactory : IRegistryCleanerRootFactory
{
    private readonly NativeRegistryCleanerKeyFactory _inner = new();

    public IRegistryCleanerKey OpenLocalMachine(RegistryCleanView view) => _inner.OpenLocalMachine(view);

    public IRegistryCleanerKey OpenUserHive(string sid, RegistryCleanView view) => _inner.OpenUserHive(sid, view);

    public IRegistryCleanerKey OpenUserClasses(string sid, RegistryCleanView view)
    {
        var parsed = new SecurityIdentifier(sid);
        return NativeRegistryCleanerKey.OpenRoot(NativeRegistryCleanerKey.HkeyUsers, parsed.Value + RegistryUserClassesHive.Suffix, view);
    }
}

/// <summary>HKCU 系のカテゴリーを走査できたか。使えないときは理由 (<see cref="InteractiveScopeReason"/> と符号) を持つ。</summary>
internal sealed record RegistryUserScopeStatus(bool Available, string? Sid, InteractiveScopeReason Reason, string? ReasonMessage);

/// <summary>走査の結果。<see cref="GenerationId"/> は走査ごとに新しく、削除時の世代照合に使う。</summary>
/// <param name="Items">削除候補 (欠損を確定できたもの)。</param>
/// <param name="Retained">候補にしなかった Unknown・Protected (選択肢には出さず、件数と理由だけ表示する)。</param>
/// <param name="Companions">候補に付随して変更する値 (StartupApproved・MRUList)。</param>
internal sealed record RegistryScanResult(
    string GenerationId,
    DateTime ScannedAtUtc,
    IReadOnlyList<RegistryScanItem> Items,
    IReadOnlyList<RegistryRetainedEntry> Retained,
    IReadOnlyList<RegistryScanCompanion> Companions,
    RegistryUserScopeStatus UserScope);

/// <summary>
/// レジストリクリーナーの走査器。全カテゴリーを読み取りだけで走査する。
/// 対話利用者と昇格利用者の SID が違う (または確認できない) ときは HKCU を読まず、理由を結果に載せる。
/// </summary>
internal sealed class RegistryCleanScanner(
    IRegistryCleanerRootFactory factory,
    RegistryCategoryDetectors detectors,
    Func<InteractiveScopeResult> evaluateScope,
    Func<string?> currentUserSid,
    bool includeWow64View)
{
    /// <summary>実環境で走査する。</summary>
    public static RegistryCleanScanner CreateDefault() => new(
        new NativeRegistryCleanerRootFactory(),
        RegistryCategoryDetectors.CreateDefault(),
        InteractiveScope.EvaluateCurrent,
        ElevatedUserSid,
        Environment.Is64BitOperatingSystem);

    /// <summary>
    /// 指定のカテゴリー (既定は全部) を走査する。HKLM を開けなければ例外。個別のキーの失敗は保持へ記録して続ける。
    /// </summary>
    public RegistryScanResult Scan(IReadOnlyCollection<RegistryCleanCategory>? categories = null, CancellationToken cancellationToken = default)
    {
        var scope = ResolveUserScope();
        using var machine64 = factory.OpenLocalMachine(RegistryCleanView.Registry64);
        using var machine32 = includeWow64View ? factory.OpenLocalMachine(RegistryCleanView.Registry32) : null;
        var (user, userClasses) = scope.Available ? OpenUser(scope, out scope) : (null, null);
        using (user)
        using (userClasses)
        {
            var session = new RegistryScanSession(machine64, machine32, user, userClasses, cancellationToken);
            foreach (var definition in RegistryCleanCategories.All)
            {
                if (categories is null || categories.Contains(definition.Category))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    detectors.Scan(definition.Category, session);
                }
            }

            return new RegistryScanResult(
                Guid.NewGuid().ToString("N"), DateTime.UtcNow, session.Items, session.Retained, session.Companions, scope);
        }
    }

    private RegistryUserScopeStatus ResolveUserScope()
    {
        var scope = evaluateScope();
        if (!scope.UserScopeAvailable)
        {
            return Unavailable(scope.Reason);
        }

        var sid = currentUserSid();
        return string.IsNullOrEmpty(sid)
            ? Unavailable(InteractiveScopeReason.UserUnknown)
            : new RegistryUserScopeStatus(true, sid, InteractiveScopeReason.None, null);
    }

    /// <summary>
    /// 利用者のハイブと、その Classes のハイブ (<c>&lt;SID&gt;_Classes</c>) を開く。どちらかを開けなければ HKCU は無効にして理由を返す
    /// (Classes を読めないと、ProgID が無いことを確定できないため)。
    /// </summary>
    private (IRegistryCleanerKey? User, IRegistryCleanerKey? Classes) OpenUser(RegistryUserScopeStatus scope, out RegistryUserScopeStatus updated)
    {
        updated = scope;
        IRegistryCleanerKey? user = null;
        try
        {
            user = factory.OpenUserHive(scope.Sid!, RegistryCleanView.Registry64);
            return (user, factory.OpenUserClasses(scope.Sid!, RegistryCleanView.Registry64));
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or UnauthorizedAccessException or SecurityException or IOException)
        {
            user?.Dispose();
            updated = Unavailable(InteractiveScopeReason.UserUnknown);
            return (null, null);
        }
    }

    private static RegistryUserScopeStatus Unavailable(InteractiveScopeReason reason) =>
        new(false, null, reason, CleanupMessage.Of(RegistryScanReasons.UserScopeUnavailable, reason));

    private static string? ElevatedUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }
}
