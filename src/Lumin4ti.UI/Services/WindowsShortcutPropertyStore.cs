using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumin4ti.UI.Services;

/// <summary>
/// Windows Shell shortcut のプロパティストアを読み書きする。
/// Velopack 1.2.0 の ShellLink.SetAppUserModelId は既存リンクへの書き込みを
/// Commit しないため、Windows の IPropertyStore 契約を直接使う。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShortcutPropertyStore
{
    private const uint StorageModeRead = 0;
    private const uint StorageModeReadWrite = 2;
    private const ushort VariantTypeUnicodeString = 31;

    private static readonly Guid AppUserModelPropertyFormatId =
        new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    internal static void SetAppUserModelId(string shortcutPath, string appUserModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);

        var shellLink = (object)new ShellLinkComObject();
        try
        {
            var persistFile = (IPersistFile)shellLink;
            persistFile.Load(shortcutPath, StorageModeReadWrite);

            var propertyStore = (IPropertyStore)shellLink;
            var key = new PropertyKey(AppUserModelPropertyFormatId, 5);
            var value = PropertyVariant.FromString(appUserModelId);
            try
            {
                Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, value));
                Marshal.ThrowExceptionForHR(propertyStore.Commit());
                persistFile.Save(shortcutPath, remember: true);
            }
            finally
            {
                value.Clear();
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(shellLink);
        }
    }

    internal static string? GetAppUserModelId(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);

        var shellLink = (object)new ShellLinkComObject();
        try
        {
            ((IPersistFile)shellLink).Load(shortcutPath, StorageModeRead);
            var propertyStore = (IPropertyStore)shellLink;
            var key = new PropertyKey(AppUserModelPropertyFormatId, 5);
            Marshal.ThrowExceptionForHR(propertyStore.GetValue(ref key, out var value));
            try
            {
                return value.VariantType == VariantTypeUnicodeString
                    ? Marshal.PtrToStringUni(value.PointerValue)
                    : null;
            }
            finally
            {
                value.Clear();
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(shellLink);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ShellLinkComObject;

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassId(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropertyVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, [In] PropertyVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyVariant
    {
        public ushort VariantType;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        public IntPtr PointerValue;
        private IntPtr _unionPadding;

        public static PropertyVariant FromString(string value) =>
            new()
            {
                VariantType = VariantTypeUnicodeString,
                PointerValue = Marshal.StringToCoTaskMemUni(value),
            };

        public void Clear() => _ = PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyVariant value);
}
