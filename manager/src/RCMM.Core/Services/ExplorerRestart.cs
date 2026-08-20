using System.Diagnostics;

namespace RCMM.Core.Services;

public sealed class ExplorerRestart
{
    public void Restart()
    {
        // Kill() only *signals* termination. If we launch the new explorer.exe
        // while an old shell instance still owns Shell_TrayWnd, the new process
        // sees an existing shell and starts as a plain folder window instead of
        // becoming the shell — the old one then finishes dying and the user is
        // left with no taskbar/desktop. Wait (bounded, so a zombie process can't
        // hang the apply) for every old instance to actually exit first.
        var old = Process.GetProcessesByName("explorer");
        foreach (var p in old)
        {
            try { p.Kill(); } catch { /* already gone / access denied */ }
        }
        foreach (var p in old)
        {
            try { p.WaitForExit(5000); }
            catch { /* exited between Kill and here, or access denied */ }
            finally { p.Dispose(); }
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        });
    }
}
