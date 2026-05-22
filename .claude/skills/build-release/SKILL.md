---
name: build-release
description: Build, test, publish, screenshot, and release RCMM. Use when building or running the app, running the xUnit tests, producing a self-contained x64 publish or the Inno Setup installer, bumping the version, capturing app screenshots, or cutting a GitHub release.
---

# Build, test, release RCMM

WinUI 3 (Windows App SDK) + .NET 8, **x64 only**. Self-contained publish — no runtime install required. All commands are PowerShell (note the backtick line continuations).

## Build (debug)

```powershell
dotnet build manager\src\RCMM\RCMM.csproj
```

## Run tests (xUnit)

```powershell
dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj
```

## Self-contained x64 publish + Inno Setup installer

```powershell
dotnet publish manager\src\RCMM\RCMM.csproj -c Release -r win-x64 --self-contained true `
  -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None `
  -o dist\publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\RCMM.iss
```

Outputs:

- `dist\publish\` — self-contained Release tree (input to ISCC).
- `dist\installer\RCMM-Setup-x64-<version>.exe` — the shipped installer.

## Screenshots

`scripts\screenshot-rcmm.ps1` launches the built app and captures screenshots — use it when verifying UI changes visually (paste the target design, screenshot the result, list differences, fix).

## Version bump

Edit `installer\RCMM.iss` → `#define MyAppVersion "X.Y.Z"`. Bump this when releasing a new version so Windows' Add/Remove Programs perceives an upgrade. The `chore: bump installer to X.Y.Z` commits in history follow this convention.

## Release

```powershell
git tag -a vX.Y.Z -m "vX.Y.Z"
git push --tags
gh release create vX.Y.Z dist/installer/RCMM-Setup-x64-X.Y.Z.exe --title "RCMM vX.Y.Z" --notes "..."
```

The installer is **unsigned** and distributed via GitHub Releases. `gh release create` publishes publicly — confirm before running it.
