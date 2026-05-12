using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RCMM.Core.Interop;

internal static class ShellInterop
{
    // === GUIDs ===
    internal static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    internal static readonly Guid BHID_SFUIObject = new("3981E224-F559-11D3-8E3A-00C04F6837D5");

    // === SHCreateItemFromParsingName ===
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // === IContextMenu ===
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
                                            [Out] byte[] pszName, uint cchMax);
    }

    // === HMENU ===
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMenuItemCount(IntPtr hMenu);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMenuItemInfoW(
        IntPtr hMenu,
        uint uItem,
        [MarshalAs(UnmanagedType.Bool)] bool fByPosition,
        ref MENUITEMINFOW lpmii);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMenuStringW(
        IntPtr hMenu,
        uint uIDItem,
        StringBuilder lpString,
        int cchMax,
        uint flags);

    // === Constants ===
    internal const uint CMF_NORMAL = 0;
    internal const uint CMF_EXTENDEDVERBS = 0x100;

    internal const uint MIIM_ID = 0x2;
    internal const uint MIIM_SUBMENU = 0x4;
    internal const uint MIIM_TYPE = 0x10;
    internal const uint MIIM_STRING = 0x40;
    internal const uint MIIM_BITMAP = 0x80;
    internal const uint MIIM_FTYPE = 0x100;

    internal const uint MFT_STRING = 0;
    internal const uint MFT_BITMAP = 0x4;
    internal const uint MFT_OWNERDRAW = 0x100;
    internal const uint MFT_SEPARATOR = 0x800;

    internal const uint MF_BYPOSITION = 0x400;
    internal const uint MF_BYCOMMAND = 0x0;

    internal const uint GCS_VERBA = 0;
    internal const uint GCS_HELPTEXTA = 1;
    internal const uint GCS_VALIDATEA = 2;
    internal const uint GCS_VERBW = 4;
    internal const uint GCS_HELPTEXTW = 5;
    internal const uint GCS_VALIDATEW = 6;

    // === CoInitialize ===
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    internal const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    // === CoTaskMemFree (for IShellItem::GetDisplayName output) ===
    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr ptr);
}
