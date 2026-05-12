# RCMM — Right-Click Menu Manager

A Windows 11 utility for curating your right-click menu. v0.1 (this build) covers
hiding entries in the classic ("Show more options") menu.

## Build

Requires .NET 8 SDK and Windows App SDK.

```powershell
dotnet build manager/RCMM.sln
dotnet run --project manager/src/RCMM/RCMM.csproj
```

## Status

- [x] Foundation + classic-menu hide/unhide (Plan 1)
- [x] Capture-based classic menu (Plan 2) — list mirrors actual right-click menu
- [ ] Modern Win11 menu hide (Plan 3)
- [ ] Add custom items (Plan 4)
- [ ] Backup snapshot + Undo all (Plan 5)
