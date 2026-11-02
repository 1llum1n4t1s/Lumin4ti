using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

/// <summary>利用者が操作する項目の失敗を、項目名と安全な原因要約で記録する共通境界。</summary>
public static class MaintenanceItemLog
{
    public static void StateReadFailed(string itemId, string itemName, string reason) =>
        Failure(itemId, itemName, "状態読み込み", reason);

    public static void Failure(string itemId, string itemName, string operation, string reason) =>
        LoggerBootstrap.Log.Error($"{Identity(itemId, itemName)}: {Clean(operation)}に失敗: {Reason(reason)}");

    public static void LogRejected(IMaintenanceItem item, string reason) =>
        Failure(item.Id, item.Label, "操作受付", reason);

    public static void LogException(IMaintenanceItem item, string operation, Exception exception) =>
        Failure(item.Id, item.Label, operation, exception.ToString());

    public static void LogResult(
        IMaintenanceItem item,
        string operation,
        MaintenanceActionResult result,
        TimeSpan? elapsed = null)
    {
        var duration = elapsed is { } time ? $" ({time.TotalSeconds:F1} 秒)" : string.Empty;
        var message = $"{Identity(item.Id, item.Label)}: {Clean(operation)} {result.Status}{duration}";
        if (result.Status is MaintenanceActionStatus.Failed or MaintenanceActionStatus.Partial)
        {
            LoggerBootstrap.Log.Error($"{message}; 原因: {Reason(result.Detail)}");
        }
        else
        {
            LoggerBootstrap.Log.Info(message);
        }
    }

    private static string Identity(string id, string name) => $"{Clean(id)} [{Clean(name)}]";

    private static string Reason(string reason)
    {
        var clean = Clean(reason);
        return clean.Length == 0 ? "詳細な理由が返されませんでした。" : clean;
    }

    private static string Clean(string value) => CommandFailureDiagnostic.Sanitize(value);
}
