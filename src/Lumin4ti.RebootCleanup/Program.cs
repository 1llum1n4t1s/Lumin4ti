using Lumin4ti.Core.Services.Windows;

// UI・移行・Velopack を参照しない。旧 Core / 未知引数は GUI へ落とさず失敗終了する。
try
{
    return args.Length == 0 && OperatingSystem.IsWindows() ? RebootCacheCleanup.RunBoot() : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Lumin4ti reboot cleanup failed: {exception.GetType().Name}");
    return 1;
}
