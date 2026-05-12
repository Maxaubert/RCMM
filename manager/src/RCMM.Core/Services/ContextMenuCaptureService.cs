using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RCMM.Core.Interop;
using RCMM.Core.Models;
using static RCMM.Core.Interop.ShellInterop;

namespace RCMM.Core.Services;

public sealed class ContextMenuCaptureService : IContextMenuCaptureService
{
    public IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths)
    {
        var result = new List<CapturedItem>();
        var done = new ManualResetEventSlim(false);

        var t = new Thread(() =>
        {
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            try
            {
                foreach (var path in targetPaths)
                {
                    try { result.AddRange(CaptureOne(path)); }
                    catch { /* per-target failures don't kill the batch */ }
                }
            }
            finally
            {
                CoUninitialize();
                done.Set();
            }
        })
        { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        done.Wait();

        return result;
    }

    private static IEnumerable<CapturedItem> CaptureOne(string targetPath)
    {
        var iidShellItem = IID_IShellItem;
        var iidContextMenu = IID_IContextMenu;
        var bhid = BHID_SFUIObject;

        int hr = SHCreateItemFromParsingName(targetPath, IntPtr.Zero, ref iidShellItem, out var psi);
        if (hr != 0 || psi == null) yield break;

        IContextMenu? pcm = null;
        IntPtr hMenu = IntPtr.Zero;
        try
        {
            hr = psi.BindToHandler(IntPtr.Zero, ref bhid, ref iidContextMenu, out var pcmPtr);
            if (hr != 0 || pcmPtr == IntPtr.Zero) yield break;
            pcm = (IContextMenu)Marshal.GetObjectForIUnknown(pcmPtr);
            Marshal.Release(pcmPtr);

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) yield break;

            const int idCmdFirst = 1;
            const int idCmdLast = 0x7FFF;
            hr = pcm.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, CMF_NORMAL | CMF_EXTENDEDVERBS);
            if (hr < 0) yield break;

            int count = GetMenuItemCount(hMenu);
            for (int i = 0; i < count; i++)
            {
                var item = ReadMenuItem(hMenu, i, pcm, idCmdFirst, targetPath);
                if (item != null) yield return item;
            }
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (pcm != null) Marshal.ReleaseComObject(pcm);
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
            return null;

        bool isSeparator = (mii.fType & MFT_SEPARATOR) != 0;
        bool isSubmenu = mii.hSubMenu != IntPtr.Zero;

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

        // Resolve the displayed text
        var sb = new StringBuilder(512);
        GetMenuStringW(hMenu, (uint)position, sb, sb.Capacity, MF_BYPOSITION);
        var display = sb.ToString();
        if (string.IsNullOrEmpty(display)) display = "(unnamed)";
        display = StripAccelerator(display);

        // Resolve the canonical verb
        string? verb = null;
        try
        {
            var buf = new byte[512];
            uint idLocal = mii.wID >= idCmdFirst ? (uint)(mii.wID - idCmdFirst) : mii.wID;
            int hr = pcm.GetCommandString((IntPtr)idLocal, GCS_VERBW, IntPtr.Zero, buf, (uint)buf.Length);
            if (hr == 0)
            {
                verb = Encoding.Unicode.GetString(buf).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(verb)) verb = null;
            }
        }
        catch { verb = null; }

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

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");
}
