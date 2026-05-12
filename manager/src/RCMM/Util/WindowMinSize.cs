using System;
using System.Runtime.InteropServices;

namespace RCMM.Util;

internal sealed class WindowMinSize
{
    private const int GWLP_WNDPROC = -4;
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrev, IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    private readonly IntPtr _hwnd;
    private readonly int _minDipW, _minDipH;
    private readonly IntPtr _prevProc;
    private readonly WndProc _newProc;

    private WindowMinSize(IntPtr hwnd, int minDipW, int minDipH)
    {
        _hwnd = hwnd;
        _minDipW = minDipW;
        _minDipH = minDipH;
        _newProc = Hook;
        _prevProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_newProc));
    }

    public static WindowMinSize Apply(IntPtr hwnd, int minDipW, int minDipH)
        => new(hwnd, minDipW, minDipH);

    private IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == WM_GETMINMAXINFO && hwnd == _hwnd)
        {
            var dpi = Win32.GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lp);
            mmi.ptMinTrackSize.X = (int)(_minDipW * dpi / 96.0);
            mmi.ptMinTrackSize.Y = (int)(_minDipH * dpi / 96.0);
            Marshal.StructureToPtr(mmi, lp, false);
        }
        return CallWindowProc(_prevProc, hwnd, msg, wp, lp);
    }
}
