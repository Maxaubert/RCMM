using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RCMM.Core.Diagnostics;
using RCMM.Core.Interop;
using RCMM.Core.Models;
using static RCMM.Core.Interop.ShellInterop;

namespace RCMM.Core.Services;

public sealed class ContextMenuCaptureService : IContextMenuCaptureService
{
    private const string Cat = "capture";

    public IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths)
    {
        Log.Info(Cat, $"CaptureAll start, targets={targetPaths.Count}");
        var result = new List<CapturedItem>();
        var done = new ManualResetEventSlim(false);

        var t = new Thread(() =>
        {
            int initHr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
            Log.Hr(Cat, "CoInitializeEx", initHr);
            try
            {
                foreach (var path in targetPaths)
                {
                    PumpMessages();
                    try { CaptureOne(path, result); }
                    catch (Exception ex)
                    {
                        Log.Error(Cat, $"target={path} unhandled", ex);
                    }
                }
            }
            finally
            {
                if (initHr >= 0) CoUninitialize();
                done.Set();
            }
        })
        { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!done.Wait(TimeSpan.FromSeconds(30)))
            Log.Warn(Cat, "STA worker exceeded 30s timeout; returning partial results");

        Log.Info(Cat, $"CaptureAll done, items={result.Count}");
        return result;
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void CaptureOne(string targetPath, List<CapturedItem> sink)
    {
        Log.Debug(Cat, $"target={targetPath} begin");
        int initialCount = sink.Count;
        bool isContainer = IsContainerPath(targetPath);

        // For every target we run parent-getUIObjectOf — this is Explorer's canonical
        // path and produces the file/folder-as-item menu (Open, Cut, Send to, …).
        TryStrategy(targetPath, "parent-getUIObjectOf", TryParentGetUIObjectOf, sink);

        // Containers also need the right-click-empty-area menu (View, Sort, New, Paste,
        // plus background-scope shell extensions like Open in Terminal, AMD Software).
        if (isContainer)
            TryStrategy(targetPath, "folder-background", TryFolderBackground, sink);

        // shellitem-bindToHandler is the cheap legacy path; we still call it because
        // for some file types (notably .zip on this machine) it returns items the
        // other strategies miss.
        TryStrategy(targetPath, "shellitem-bindToHandler", TryShellItemBindToHandler, sink);

        if (sink.Count == initialCount)
            Log.Warn(Cat, $"target={targetPath} no strategy yielded items");
    }

    private static void TryStrategy(string targetPath, string name,
                                    Func<string, IContextMenu?> factory,
                                    List<CapturedItem> sink)
    {
        IContextMenu? pcm = null;
        try { pcm = factory(targetPath); }
        catch (Exception ex) { Log.Error(Cat, $"{name} factory threw target={targetPath}", ex); }
        if (pcm == null) return;
        try { EnumerateMenu(pcm, targetPath, name, sink); }
        catch (Exception ex) { Log.Error(Cat, $"{name} enumerate threw target={targetPath}", ex); }
        finally { Marshal.ReleaseComObject(pcm); }
    }

    private static void EnumerateMenu(IContextMenu pcm, string targetPath, string strategy, List<CapturedItem> sink)
    {

        IntPtr hMenu = IntPtr.Zero;
        try
        {
            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                Log.Warn(Cat, $"target={targetPath} CreatePopupMenu returned NULL");
                return;
            }

            const int idCmdFirst = 1;
            const int idCmdLast = 0x7FFF;
            // Match the flag set Explorer passes when it builds the menu so shellexes that
            // gate their items on these capabilities (Rename, BinarySubMenus, etc.) still
            // contribute. CMF_EXPLORE specifically encourages background menus to surface
            // their View/Sort verbs.
            uint flags = CMF_NORMAL | CMF_EXTENDEDVERBS | CMF_CANRENAME;
            if (strategy == "folder-background") flags |= CMF_EXPLORE;
            int hr = pcm.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, flags);
            Log.Hr(Cat, "QueryContextMenu", hr, $"target={targetPath} strategy={strategy} flags=0x{flags:X}");
            if (hr < 0) return;

            int count = GetMenuItemCount(hMenu);
            Log.Debug(Cat, $"target={targetPath} strategy={strategy} menuItemCount={count}");
            int captured = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var item = ReadMenuItem(hMenu, i, pcm, idCmdFirst, targetPath);
                    if (item == null) continue;
                    sink.Add(item);
                    captured++;
                    if (!item.IsSeparator)
                        Log.Debug(Cat, $"  [{strategy}/{targetPath.Substring(Math.Max(0, targetPath.Length - 18))}] '{item.DisplayName}' verb='{item.Verb ?? ""}' sub={item.IsSubmenu}");
                }
                catch (Exception ex)
                {
                    Log.Error(Cat, $"target={targetPath} position={i} read failed", ex);
                }
            }
            Log.Info(Cat, $"target={targetPath} strategy={strategy} captured={captured}/{count}");
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
        }
    }

    private static IContextMenu? TryParentGetUIObjectOf(string path)
    {
        IntPtr pidlFull = IntPtr.Zero;
        IntPtr pParentPtr = IntPtr.Zero;
        IntPtr pcmPtr = IntPtr.Zero;
        try
        {
            var iidShellFolder = IID_IShellFolder;
            var iidContextMenu = IID_IContextMenu;

            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidlFull, 0, out _);
            Log.Hr(Cat, "SHParseDisplayName", hr, $"path={path}");
            if (hr < 0 || pidlFull == IntPtr.Zero) return null;

            hr = SHBindToParent(pidlFull, ref iidShellFolder, out pParentPtr, out IntPtr pidlChild);
            Log.Hr(Cat, "SHBindToParent", hr, $"path={path}");
            if (hr < 0 || pParentPtr == IntPtr.Zero) return null;

            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(pParentPtr);
            try
            {
                Log.Debug(Cat, $"GetUIObjectOf entering path={path}");
                hr = parent.GetUIObjectOf(IntPtr.Zero, 1, ref pidlChild, ref iidContextMenu, IntPtr.Zero, out pcmPtr);
                Log.Hr(Cat, "GetUIObjectOf(IContextMenu)", hr, $"path={path}");
                if (hr < 0 || pcmPtr == IntPtr.Zero) return null;
                var pcm = (IContextMenu)Marshal.GetObjectForIUnknown(pcmPtr);
                Marshal.Release(pcmPtr);
                pcmPtr = IntPtr.Zero;
                return pcm;
            }
            finally
            {
                Marshal.ReleaseComObject(parent);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"TryParentGetUIObjectOf path={path} threw", ex);
            return null;
        }
        finally
        {
            if (pcmPtr != IntPtr.Zero) Marshal.Release(pcmPtr);
            if (pParentPtr != IntPtr.Zero) Marshal.Release(pParentPtr);
            if (pidlFull != IntPtr.Zero) ILFree(pidlFull);
        }
    }

    private static IContextMenu? TryFolderBackground(string path)
    {
        IntPtr pidl = IntPtr.Zero;
        IntPtr pDesktop = IntPtr.Zero;
        IntPtr pSf = IntPtr.Zero;
        IntPtr pcmPtr = IntPtr.Zero;
        try
        {
            var iidShellFolder = IID_IShellFolder;
            var iidContextMenu = IID_IContextMenu;

            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            Log.Hr(Cat, "SHParseDisplayName(folder-bg)", hr, $"path={path}");
            if (hr < 0 || pidl == IntPtr.Zero) return null;

            hr = SHGetDesktopFolder(out pDesktop);
            Log.Hr(Cat, "SHGetDesktopFolder", hr);
            if (hr < 0 || pDesktop == IntPtr.Zero) return null;
            var desktop = (IShellFolder)Marshal.GetObjectForIUnknown(pDesktop);

            try
            {
                hr = desktop.BindToObject(pidl, IntPtr.Zero, ref iidShellFolder, out pSf);
                Log.Hr(Cat, "Desktop.BindToObject(IShellFolder)", hr, $"path={path}");
                if (hr < 0 || pSf == IntPtr.Zero) return null;
            }
            finally
            {
                Marshal.ReleaseComObject(desktop);
            }

            var sf = (IShellFolder)Marshal.GetObjectForIUnknown(pSf);
            try
            {
                hr = sf.CreateViewObject(IntPtr.Zero, ref iidContextMenu, out pcmPtr);
                Log.Hr(Cat, "CreateViewObject(IContextMenu)", hr, $"path={path}");
                if (hr < 0 || pcmPtr == IntPtr.Zero) return null;
                var pcm = (IContextMenu)Marshal.GetObjectForIUnknown(pcmPtr);
                Marshal.Release(pcmPtr);
                pcmPtr = IntPtr.Zero;
                return pcm;
            }
            finally
            {
                Marshal.ReleaseComObject(sf);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"TryFolderBackground path={path} threw", ex);
            return null;
        }
        finally
        {
            if (pcmPtr != IntPtr.Zero) Marshal.Release(pcmPtr);
            if (pSf != IntPtr.Zero) Marshal.Release(pSf);
            if (pDesktop != IntPtr.Zero) Marshal.Release(pDesktop);
            if (pidl != IntPtr.Zero) ILFree(pidl);
        }
    }

    private static IContextMenu? TryShellItemBindToHandler(string path)
    {
        IShellItem? psi = null;
        IntPtr pcmPtr = IntPtr.Zero;
        try
        {
            var iidShellItem = IID_IShellItem;
            var iidContextMenu = IID_IContextMenu;
            var bhid = BHID_SFUIObject;

            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidShellItem, out psi);
            Log.Hr(Cat, "SHCreateItemFromParsingName(fallback)", hr, $"path={path}");
            if (hr < 0 || psi == null) return null;

            hr = psi.BindToHandler(IntPtr.Zero, ref bhid, ref iidContextMenu, out pcmPtr);
            Log.Hr(Cat, "BindToHandler(SFUIObject,IContextMenu)", hr, $"path={path}");
            if (hr < 0 || pcmPtr == IntPtr.Zero) return null;

            var pcm = (IContextMenu)Marshal.GetObjectForIUnknown(pcmPtr);
            Marshal.Release(pcmPtr);
            pcmPtr = IntPtr.Zero;
            return pcm;
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"TryShellItemBindToHandler path={path} threw", ex);
            return null;
        }
        finally
        {
            if (pcmPtr != IntPtr.Zero) Marshal.Release(pcmPtr);
            if (psi != null) Marshal.ReleaseComObject(psi);
        }
    }

    private static CapturedItem? ReadMenuItem(IntPtr hMenu, int position, IContextMenu pcm, int idCmdFirst, string targetPath)
    {
        var mii = new MENUITEMINFOW
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
            fMask = MIIM_ID | MIIM_FTYPE | MIIM_SUBMENU
        };
        if (!GetMenuItemInfoW(hMenu, (uint)position, true, ref mii))
        {
            int err = Marshal.GetLastWin32Error();
            Log.Warn(Cat, $"GetMenuItemInfoW failed position={position} target={targetPath} win32={err}");
            return null;
        }

        bool isSeparator = (mii.fType & MFT_SEPARATOR) != 0;
        bool isSubmenu = mii.hSubMenu != IntPtr.Zero;
        bool isOwnerDrawn = (mii.fType & MFT_OWNERDRAW) != 0;

        if (isSeparator)
        {
            return new CapturedItem
            {
                TargetPath = targetPath,
                Position = position,
                DisplayName = "",
                IsSeparator = true,
                IsSubmenu = false
            };
        }

        string display = ReadDisplayName(hMenu, position, pcm, mii, idCmdFirst, isOwnerDrawn, targetPath);
        string? verb = ReadVerb(pcm, mii.wID, idCmdFirst);

        return new CapturedItem
        {
            TargetPath = targetPath,
            Position = position,
            DisplayName = display,
            Verb = verb,
            IsSeparator = false,
            IsSubmenu = isSubmenu
        };
    }

    private static string ReadDisplayName(IntPtr hMenu, int position, IContextMenu pcm, MENUITEMINFOW miiIn,
                                          int idCmdFirst, bool isOwnerDrawn, string targetPath)
    {
        // 1) plain GetMenuStringW
        var sb = new StringBuilder(512);
        int len = GetMenuStringW(hMenu, (uint)position, sb, sb.Capacity, MF_BYPOSITION);
        if (len > 0)
        {
            var s = sb.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return StripAccelerator(s);
        }

        // 2) GetMenuItemInfoW with MIIM_STRING + buffer
        try
        {
            uint cch = 256;
            var buf = Marshal.AllocCoTaskMem((int)((cch + 1) * 2));
            try
            {
                var mii = new MENUITEMINFOW
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                    fMask = MIIM_STRING,
                    dwTypeData = buf,
                    cch = cch
                };
                if (GetMenuItemInfoW(hMenu, (uint)position, true, ref mii) && mii.cch > 0)
                {
                    var s = Marshal.PtrToStringUni(mii.dwTypeData, (int)mii.cch);
                    if (!string.IsNullOrWhiteSpace(s)) return StripAccelerator(s);
                }
            }
            finally { Marshal.FreeCoTaskMem(buf); }
        }
        catch (Exception ex)
        {
            Log.Debug(Cat, $"MIIM_STRING read failed pos={position} target={targetPath} ex={ex.Message}");
        }

        // 3) Help text via GetCommandString (some owner-drawn extensions only expose this)
        try
        {
            uint idLocal = miiIn.wID >= idCmdFirst ? (uint)(miiIn.wID - idCmdFirst) : miiIn.wID;
            var s = CallGetCommandString(pcm, idLocal, GCS_HELPTEXTW);
            if (!string.IsNullOrWhiteSpace(s)) return s!;
        }
        catch (Exception ex)
        {
            Log.Debug(Cat, $"GCS_HELPTEXTW failed pos={position} target={targetPath} ex={ex.Message}");
        }

        Log.Debug(Cat, $"position={position} target={targetPath} ownerDrawn={isOwnerDrawn} wID={miiIn.wID} display unresolved → (unnamed)");
        return "(unnamed)";
    }

    private static string? ReadVerb(IContextMenu pcm, uint wID, int idCmdFirst)
    {
        try
        {
            uint idLocal = wID >= idCmdFirst ? (uint)((int)wID - idCmdFirst) : wID;
            var s = CallGetCommandString(pcm, idLocal, GCS_VERBW);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch (Exception ex)
        {
            Log.Debug(Cat, $"ReadVerb wID={wID} threw ex={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Calls IContextMenu::GetCommandString with an unmanaged buffer to avoid the
    /// CLR's SAFEARRAY marshaller (which AVs on byte[] for non-Dispatch COM interfaces).
    /// Returns the buffer contents as a string, or null on failure.
    /// </summary>
    private static string? CallGetCommandString(IContextMenu pcm, uint idLocal, uint uType)
    {
        const int cch = 256;
        bool isUnicode = uType == GCS_VERBW || uType == GCS_HELPTEXTW || uType == GCS_VALIDATEW;
        int bytes = isUnicode ? cch * 2 : cch;
        IntPtr buf = Marshal.AllocCoTaskMem(bytes);
        try
        {
            // Zero the buffer first so a misbehaving handler that returns success but
            // writes nothing doesn't leave us reading random bytes.
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(buf, i, 0);
            int hr = pcm.GetCommandString((IntPtr)idLocal, uType, IntPtr.Zero, buf, (uint)cch);
            if (hr != 0) return null;
            return isUnicode
                ? Marshal.PtrToStringUni(buf)
                : Marshal.PtrToStringAnsi(buf);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buf);
        }
    }

    private static bool IsContainerPath(string path)
    {
        try
        {
            if (Directory.Exists(path)) return true;
            // Drive root like "C:\"
            if (path.Length == 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return true;
        }
        catch { }
        return false;
    }

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");
}
