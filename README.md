<div align="center">
  <img src="manager/src/RCMM/Assets/icon.png" alt="RCMM" width="128">

  # RCMM

  Right-Click Menu Manager. Show or hide entries in the Windows Explorer right-click menu. No admin required.

  [![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square)](https://github.com/Maxaubert/RCMM)
</div>

---

## What it does

Captures every entry that appears when you right-click in Windows Explorer. Built-in shell verbs like Cut, Copy, Paste, Send to, Share, plus third-party additions from VLC, WinRAR, Notepad++, Visual Studio, AMD Software, and so on. Toggle any of them off and they disappear from the menu. Toggle back on to bring them back. Apply restarts Explorer and the changes take effect immediately.

Works without admin rights, writes to the per-user HKCU shadow of the shell registry instead of HKCR.

## Install

No installer yet, build from source for now. Releases will land at https://github.com/Maxaubert/RCMM/releases when ready.

## Build from source

Requirements:

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows App SDK (restored automatically from NuGet on build)

```powershell
git clone https://github.com/Maxaubert/RCMM.git
cd RCMM
dotnet build manager\src\RCMM\RCMM.csproj
```

Output: `manager\src\RCMM\bin\Debug\net8.0-windows10.0.19041.0\RCMM.exe`.

## How it works

One process, no service. WinUI 3 / .NET 8 desktop app.

- Walks every shell-relevant registry hive (HKCR merged view, HKCU shadow, `CommandStore`, `PackagedCom`) to enumerate candidate entries.
- Verifies each one by binding `IContextMenu` and `IExplorerCommand` against a sample file or folder, so registry-only ghosts that never actually appear in the menu get filtered out.
- Resolves icons via `ExtractIconEx`, packaged-app `DllPath`, `IExplorerCommand::GetIcon`, and `CommandStore` Icon hints.
- Hides entries by writing to one of three places, depending on the entry type:
  - Classic shell verbs: `LegacyDisable` under `HKCU\Software\Classes\...` (no admin needed).
  - Classic shellex handlers: blocked CLSID under `HKCU\Software\Classes\CLSID\...`.
  - Packaged-COM extensions: `HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked`.
- After Apply, restarts Explorer so the new state takes effect.

## License

MIT (see [LICENSE](LICENSE) once added).
