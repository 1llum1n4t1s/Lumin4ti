using System.Security.Principal;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>バックアップ文書の上限。</summary>
internal static class RegistryBackupLimits
{
    public const int MaxItems = 10_000;
    /// <summary>文書全体のキーと値の合計。</summary>
    public const int MaxNodes = 200_000;
    public const int MaxValueBytes = 1024 * 1024;
    public const long MaxTotalBytes = 32L * 1024 * 1024;
    /// <summary>キー単位の項目のサブツリーの深さ (項目のキー自身を 1 とする)。</summary>
    public const int MaxKeyDepth = 64;
    public const int MaxKeyPathLength = 1024;
    public const int MaxKeyNameLength = 255;
    public const int MaxValueNameLength = 16383;
    /// <summary>1 階層 = SubKeys 配列 + SubKey + Node の 3 段。64 階層に余裕を足す。</summary>
    public const int JsonMaxDepth = 260;
    /// <summary>読み込むファイルの最大バイト数 (base64 の膨張を含む)。</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const int MaxBackups = 1000;
}

/// <summary>バックアップ文書が検証で拒否された。</summary>
internal sealed class RegistryBackupRejectedException(string reason)
    : RegistryBackupException("レジストリのバックアップを拒否しました: " + reason);

/// <summary>
/// 読み込み時の検証。版、SID の形式、パスの許可表 (各カテゴリーの根のキーの配下だけ)、件数・サイズ・深さの上限。
/// 違反は理由付きで拒否する。
/// </summary>
internal static class RegistryBackupValidator
{
    private const string Ext = "<.ext>";

    private static readonly Dictionary<(RegistryCleanCategory, RegistryCleanHive), string[][]> Roots = BuildRoots();

    public static void ThrowIfInvalid(RegistryBackupDocument? document)
    {
        if (!TryValidate(document, out var reason))
        {
            throw new RegistryBackupRejectedException(reason);
        }
    }

    public static bool TryValidate(RegistryBackupDocument? document, out string reason)
    {
        reason = string.Empty;
        if (document is null)
        {
            reason = "文書が空です。";
            return false;
        }

        if (document.FormatVersion != RegistryBackupDocument.CurrentVersion)
        {
            reason = $"形式の版 {document.FormatVersion} には対応していません。";
            return false;
        }

        if (!IsWellFormedSid(document.UserSid))
        {
            reason = "利用者 SID の形式が不正です。";
            return false;
        }

        if (document.CreatedUtc.Kind != DateTimeKind.Utc)
        {
            reason = "作成時刻が UTC ではありません。";
            return false;
        }

        if (document.Items is null || document.Items.Length > RegistryBackupLimits.MaxItems)
        {
            reason = "項目の件数が上限を超えています。";
            return false;
        }

        var budget = new Budget();
        for (var index = 0; index < document.Items.Length; index++)
        {
            var failure = ValidateItem(document.Items[index], budget);
            if (failure is not null)
            {
                reason = $"項目 {index}: {failure}";
                return false;
            }
        }

        return true;
    }

    /// <summary>キーパスが、カテゴリーの根の配下か。<paramref name="wholeKey"/> が真なら根そのものは不可 (キー単位の削除)。</summary>
    public static bool IsAllowedPath(RegistryCleanCategory category, RegistryCleanHive hive, string? keyPath, bool wholeKey)
    {
        if (!TrySplitPath(keyPath, out var segments) ||
            segments.Any(segment => segment.Equals("UserChoice", StringComparison.OrdinalIgnoreCase)) ||
            !Roots.TryGetValue((category, hive), out var roots))
        {
            return false;
        }

        return roots.Any(root => MatchesRoot(root, segments, wholeKey));
    }

    private static string? ValidateItem(RegistryBackupItem? item, Budget budget)
    {
        if (item is null)
        {
            return "項目が空です。";
        }

        if (!Enum.IsDefined(item.Category) || !Enum.IsDefined(item.Hive) || !Enum.IsDefined(item.View))
        {
            return "カテゴリー・ハイブ・表示が不正です。";
        }

        var wholeKey = item.ValueName is null;
        if (!IsAllowedPath(item.Category, item.Hive, item.KeyPath, wholeKey))
        {
            return "許可されていないキーパスです。";
        }

        if (wholeKey)
        {
            return item.ValueType is not null || item.DataBase64 is not null || item.Subtree is null
                ? "キー単位の項目の形が不正です。"
                : ValidateNode(item.Subtree, 1, budget);
        }

        if (item.Subtree is not null || item.ValueType is null || item.DataBase64 is null ||
            item.ValueName!.Length > RegistryBackupLimits.MaxValueNameLength)
        {
            return "値単位の項目の形が不正です。";
        }

        return budget.AddNode() ?? budget.AddData(item.DataBase64);
    }

    private static string? ValidateNode(RegistryBackupKeyNode? node, int depth, Budget budget)
    {
        if (node?.Values is null || node.SubKeys is null)
        {
            return "キーの内容が空です。";
        }

        if (depth > RegistryBackupLimits.MaxKeyDepth)
        {
            return "キーの階層が深すぎます。";
        }

        var valueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in node.Values)
        {
            var failure = ValidateValue(value, valueNames, budget);
            if (failure is not null)
            {
                return failure;
            }
        }

        var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subKey in node.SubKeys)
        {
            var failure = ValidateSubKey(subKey, childNames, depth, budget);
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private static string? ValidateValue(RegistryBackupValue? value, HashSet<string> names, Budget budget)
    {
        if (value?.Name is null || value.DataBase64 is null || value.Name.Length > RegistryBackupLimits.MaxValueNameLength)
        {
            return "値の形が不正です。";
        }

        return !names.Add(value.Name)
            ? "同名の値が重複しています。"
            : budget.AddNode() ?? budget.AddData(value.DataBase64);
    }

    private static string? ValidateSubKey(RegistryBackupSubKey? subKey, HashSet<string> names, int depth, Budget budget)
    {
        if (subKey?.Name is null || !IsValidKeyName(subKey.Name))
        {
            return "子キーの名前が不正です。";
        }

        if (!names.Add(subKey.Name))
        {
            return "同名の子キーが重複しています。";
        }

        return budget.AddNode() ?? ValidateNode(subKey.Node, depth + 1, budget);
    }

    private static bool MatchesRoot(string[] root, string[] segments, bool wholeKey)
    {
        if (segments.Length < root.Length || (wholeKey && segments.Length == root.Length))
        {
            return false;
        }

        for (var i = 0; i < root.Length; i++)
        {
            var matches = root[i] == Ext
                ? segments[i].Length > 1 && segments[i][0] == '.'
                : segments[i].Equals(root[i], StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySplitPath(string? keyPath, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(keyPath) || keyPath.Length > RegistryBackupLimits.MaxKeyPathLength)
        {
            return false;
        }

        segments = keyPath.Split('\\');
        return segments.All(segment => IsValidKeyName(segment) && segment is not "." and not "..");
    }

    private static bool IsValidKeyName(string name) =>
        name.Length is > 0 and <= RegistryBackupLimits.MaxKeyNameLength &&
        !name.Any(char.IsControl) && !name.Contains('\\');

    private static bool IsWellFormedSid(string? sid)
    {
        if (string.IsNullOrEmpty(sid) || sid.Length > 184)
        {
            return false;
        }

        try
        {
            return new SecurityIdentifier(sid).Value.Equals(sid, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Dictionary<(RegistryCleanCategory, RegistryCleanHive), string[][]> BuildRoots()
    {
        const string cv = @"Microsoft\Windows\CurrentVersion";
        var table = new Dictionary<(RegistryCleanCategory, RegistryCleanHive), List<string>>();

        // tail は HKLM では SOFTWARE\ (と SOFTWARE\WOW6432Node\)、HKCU では Software\ の下。利用者の Classes は下で個別に足す。
        void Add(RegistryCleanCategory category, bool machine, bool user, string tail)
        {
            if (machine)
            {
                var list = table.GetValueOrDefault((category, RegistryCleanHive.LocalMachine)) ?? [];
                list.Add(@"SOFTWARE\" + tail);
                list.Add(@"SOFTWARE\WOW6432Node\" + tail);
                table[(category, RegistryCleanHive.LocalMachine)] = list;
            }

            if (user)
            {
                var list = table.GetValueOrDefault((category, RegistryCleanHive.CurrentUser)) ?? [];
                list.Add(@"Software\" + tail);
                table[(category, RegistryCleanHive.CurrentUser)] = list;
            }
        }

        Add(RegistryCleanCategory.AppPaths, true, true, $@"{cv}\App Paths");
        Add(RegistryCleanCategory.Startup, true, true, $@"{cv}\Run");
        Add(RegistryCleanCategory.Startup, true, true, $@"{cv}\Explorer\StartupApproved\Run");
        Add(RegistryCleanCategory.Startup, true, true, $@"{cv}\Explorer\StartupApproved\Run32");
        Add(RegistryCleanCategory.Fonts, true, true, @"Microsoft\Windows NT\CurrentVersion\Fonts");
        Add(RegistryCleanCategory.FileAssociations, false, true, $@"{cv}\Explorer\FileExts");
        // 利用者の Classes は HKCU の Software\Classes (リンク) ではなく、実体のハイブ <SID>_Classes のルートからの相対で持つ。
        Add(RegistryCleanCategory.FileAssociations, true, false, @"Classes\Applications");
        Add(RegistryCleanCategory.FileAssociations, true, false, $@"Classes\{Ext}\OpenWithProgids");
        table[(RegistryCleanCategory.FileAssociations, RegistryCleanHive.UserClasses)] = ["Applications", $@"{Ext}\OpenWithProgids"];
        Add(RegistryCleanCategory.Uninstall, true, true, $@"{cv}\Uninstall");
        Add(RegistryCleanCategory.SharedDlls, true, false, $@"{cv}\SharedDLLs");

        return table.ToDictionary(pair => pair.Key, pair => pair.Value.Select(path => path.Split('\\')).ToArray());
    }

    private sealed class Budget
    {
        private int _nodes;
        private long _bytes;

        public string? AddNode() =>
            ++_nodes > RegistryBackupLimits.MaxNodes ? "キーと値の件数が上限を超えています。" : null;

        public string? AddData(string base64)
        {
            // base64 は 4 文字で 3 バイト。長さだけで先に弾き、巨大な値を復号しない。
            if (base64.Length / 4L * 3 > RegistryBackupLimits.MaxValueBytes + 3)
            {
                return "値のサイズが上限を超えています。";
            }

            byte[] data;
            try
            {
                data = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return "値のデータが base64 ではありません。";
            }

            _bytes += data.Length;
            return data.Length > RegistryBackupLimits.MaxValueBytes || _bytes > RegistryBackupLimits.MaxTotalBytes
                ? "値のサイズが上限を超えています。"
                : null;
        }
    }
}
