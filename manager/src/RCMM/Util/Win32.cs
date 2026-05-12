using System.Runtime.InteropServices;

namespace RCMM.Util;

internal static class Win32
{
    [DllImport("User32.dll")] internal static extern uint GetDpiForWindow(System.IntPtr hwnd);
    [DllImport("Dwmapi.dll")] internal static extern int DwmSetWindowAttribute(System.IntPtr hwnd, uint attr, ref uint value, int size);
    internal const uint DWMWA_BORDER_COLOR = 34;
    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
}
