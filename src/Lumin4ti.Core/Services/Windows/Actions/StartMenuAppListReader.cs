using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// スタートメニューのアプリ一覧 (shell:AppsFolder) を読み取る。Get-StartApps と同じ経路で、
/// パッケージアプリは "PackageFamilyName!AppId" 形式の AppUserModelID として並ぶ。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StartMenuAppListReader
{
    /// <summary>
    /// AppsFolder の AppUserModelID を列挙する。読み取れなかった場合は null を返し、
    /// 呼び出し側が「対象を判定できない」として安全側に倒せるようにする。
    /// </summary>
    public static IReadOnlyList<string>? TryReadAppUserModelIds() =>
        // Shell.Application はアパートメントスレッドの COM なので、専用 STA スレッドで扱う。
        RunOnStaThread(ReadCore);

    /// <summary>
    /// スタートメニューのアプリ一覧を再構築させる。一覧をキャッシュしているのは explorer.exe ではなく
    /// StartMenuExperienceHost なので、エクスプローラー再起動ではなくこのプロセスだけを終了する
    /// (Windows が即座に自動復帰させる)。
    /// </summary>
    public static void RefreshStartMenu()
    {
        foreach (var process in Process.GetProcessesByName("StartMenuExperienceHost"))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 既に終了している / 権限が無い場合は再構築を諦める (表示は再サインインで直る)。
                }
            }
        }
    }

    private static IReadOnlyList<string>? ReadCore()
    {
        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            dynamic? shellObject = shell;
            var folder = shellObject?.NameSpace("shell:AppsFolder");
            if (folder is null)
            {
                return null;
            }

            var ids = new List<string>();
            foreach (var item in folder.Items())
            {
                string? path = item?.Path;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    ids.Add(path);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or MissingMemberException)
        {
            LoggerBootstrap.Log.Error("スタートメニューのアプリ一覧を読み取れませんでした", ex);
            return null;
        }
        finally
        {
            if (shell is not null)
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static T? RunOnStaThread<T>(Func<T?> func)
        where T : class
    {
        T? result = null;
        var thread = new Thread(() => result = func())
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>"Family!AppId" 形式の AUMID からパッケージファミリー名を取り出す (Win32 項目は null)。</summary>
    public static string? TryGetPackageFamilyName(string appUserModelId)
    {
        var separator = appUserModelId.IndexOf('!');
        return separator > 0 ? appUserModelId[..separator] : null;
    }
}
