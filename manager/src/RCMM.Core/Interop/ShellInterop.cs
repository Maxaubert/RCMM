using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RCMM.Core.Interop;

internal static class ShellInterop
{
    // === GUIDs ===
    internal static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IContextMenu2 = new("000214f4-0000-0000-c000-000000000046");
    internal static readonly Guid IID_IContextMenu3 = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");
    internal static readonly Guid BHID_SFUIObject = new("3981E224-F559-11D3-8E3A-00C04F6837D5");
    internal static readonly Guid BHID_SFObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");
    internal static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    internal static readonly Guid IID_IShellExtInit = new("000214E8-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    // === SHCreateItemFromParsingName ===
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    // === SHParseDisplayName ===
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    // === SHBindToParent ===
    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHBindToParent(
        IntPtr pidl,
        ref Guid riid,
        out IntPtr ppv,
        out IntPtr ppidlLast);

    // === ILFree ===
    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern void ILFree(IntPtr pidl);

    // === SHGetDesktopFolder (fallback path for binding) ===
    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHGetDesktopFolder(out IntPtr ppshf);

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

    // === IShellFolder ===
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName,
                                            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [In] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, ref IntPtr apidl,
                                        ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    // === IContextMenu ===
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
                                            IntPtr pszName, uint cchMax);
    }

    // === IShellExtInit ===
    [ComImport, Guid("000214E8-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr lpdobj, IntPtr hkeyProgID);
    }

    // === CoCreateInstance ===
    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    internal const uint CLSCTX_INPROC_SERVER = 0x1;
    internal const uint CLSCTX_LOCAL_SERVER = 0x4;
    internal const uint CLSCTX_INPROC_HANDLER = 0x2;

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

    // === Message pump primitives (used by capture worker STA) ===
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DispatchMessage(ref MSG lpMsg);

    internal const uint PM_REMOVE = 0x0001;

    // === Constants ===
    internal const uint CMF_NORMAL = 0;
    internal const uint CMF_DEFAULTONLY = 0x01;
    internal const uint CMF_VERBSONLY = 0x02;
    internal const uint CMF_EXPLORE = 0x04;
    internal const uint CMF_CANRENAME = 0x10;
    internal const uint CMF_NODEFAULT = 0x20;
    internal const uint CMF_ITEMMENU = 0x80;
    internal const uint CMF_EXTENDEDVERBS = 0x100;
    internal const uint CMF_DISABLEDVERBS = 0x200;
    internal const uint CMF_OPTIMIZEFORINVOKE = 0x800;

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

    internal const int SIGDN_NORMALDISPLAY = 0x00000000;

    // === CoInitialize ===
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    internal const uint COINIT_APARTMENTTHREADED = 0x2;
    internal const uint COINIT_DISABLE_OLE1DDE = 0x4;

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    // === CoTaskMemFree (for IShellItem::GetDisplayName output) ===
    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr ptr);
}
