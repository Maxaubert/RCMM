using System.Diagnostics;

namespace RCMM.Core.Services;

public sealed class ExplorerRestart
{
    public void Restart()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { /* ignore */ }
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        });
    }
}
