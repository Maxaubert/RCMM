<div align="center">
  <img src="manager/src/RCMM/Assets/icon.png" alt="RCMM" width="128">

  # RCMM

  Right-Click Menu Manager. Show or hide entries in the Windows Explorer right-click menu. No admin required.

  [![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square)](https://github.com/Maxaubert/RCMM)
</div>

---

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

## License

MIT (see [LICENSE](LICENSE) once added).
