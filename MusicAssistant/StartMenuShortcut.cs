using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MusicAssistant;

/// <summary>
/// Start Menu shortcut carrying the app's AppUserModelID.
///
/// Windows resolves an unpackaged app's name and icon for the media overlay
/// and notifications through a Start Menu shortcut whose
/// System.AppUserModel.ID matches the id the process set. Without it the
/// overlay says "Unknown app". Created once per user, refreshed if the exe moved.
/// </summary>
/// <remarks>
/// @link https://learn.microsoft.com/windows/win32/shell/appids
/// </remarks>
public static class StartMenuShortcut
{
    public static void Ensure(string appId, string displayName, string exePath, string iconPath)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            var path   = Path.Combine(folder, displayName + ".lnk");
            Directory.CreateDirectory(folder);

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
            link.SetIconLocation(iconPath, 0);
            link.SetDescription(displayName);

            // System.AppUserModel.ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
            var store = (IPropertyStore)link;
            var key   = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            var value = new PropVariant(appId);
            store.SetValue(ref key, ref value);
            store.Commit();
            value.Clear();

            ((IPersistFile)link).Save(path, true);
        }
        catch (Exception ex)
        {
            App.Log($"Start Menu shortcut not created: {ex.Message}");
        }
    }

    // =========================================================================
    // COM
    // =========================================================================

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId   = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        private ushort valueType;
        private ushort reserved1, reserved2, reserved3;
        private IntPtr pointer;
        private IntPtr pointer2;

        public PropVariant(string value)
        {
            valueType = 31; // VT_LPWSTR
            reserved1 = reserved2 = reserved3 = 0;
            pointer   = Marshal.StringToCoTaskMemUni(value);
            pointer2  = IntPtr.Zero;
        }

        public void Clear()
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            pointer = IntPtr.Zero;
        }
    }
}
