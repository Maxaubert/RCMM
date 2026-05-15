using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RCMM.Core.Diagnostics;
using RCMM.Core.Interop;
using RCMM.Core.Models;
using static RCMM.Core.Interop.ShellInterop;

namespace RCMM.Core.Services;

/// <summary>
/// Last-resort identifier for live captured menu items that have no canonical
/// verb and no fuzzy FileDescription match (Scan for deleted files / Recuva,
/// Include in library / Library Location, Cast to Device / PlayTo, …).
///
/// For each registered shellex CLSID, the invoker:
///   1. CoCreateInstance(CLSID, IID_IShellExtInit)
///   2. Gets an IDataObject for a sample target whose type matches the handler's scope
///   3. Calls IShellExtInit::Initialize so the handler knows which item it's acting on
///   4. QueryInterface to IContextMenu, populates a popup, reads the emitted names
///   5. Records CLSID -> emitted display names
///
/// Runs on its own STA worker with a per-handler try/catch and a global cap on
/// the batch. Failures are logged and the handler is just left out of the index.
/// </summary>
public sealed class ShellexInvoker
{
    private const string Cat = "shellexinvoker";

    private readonly IRegistry _reg;
    private readonly TargetProvider _targets;
    private readonly Dictionary<string, HashSet<string>> _emittedByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _iconByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _titleByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _extraClsids =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _built;

    public ShellexInvoker(IRegistry reg, TargetProvider targets)
    {
        _reg = reg;
        _targets = targets;
    }

    /// <summary>
    /// Adds a CLSID that wasn't found by the shellex enumeration (typically a verb's
    /// ExplorerCommandHandler or a CommandStore VerbHandler). The invoker will probe
    /// it with IExplorerCommand::GetIcon during the next Build pass.
    /// </summary>
    public void RegisterExtraClsid(string clsid)
    {
        if (!string.IsNullOrWhiteSpace(clsid)) _extraClsids.Add(clsid.Trim().ToUpperInvariant());
    }

    /// <summary>Returns the IExplorerCommand-reported icon path for a CLSID, or null.</summary>
    public string? LookupIconPath(string? clsid)
    {
        if (string.IsNullOrEmpty(clsid)) return null;
        BuildDisplayNameToClsidMap();
        return _iconByClsid.TryGetValue(clsid, out var icon) ? icon : null;
    }

    /// <summary>
    /// Returns the IExplorerCommand-reported title for a CLSID — the same text the
    /// shell would render in the menu (e.g. "AMD Software: Adrenalin Edition" for
    /// AMD's Catalyst CLSID). Lets us rename packaged COM rows whose registry-only
    /// DisplayName is a technical class label.
    /// </summary>
    public string? LookupTitle(string? clsid)
    {
        if (string.IsNullOrEmpty(clsid)) return null;
        BuildDisplayNameToClsidMap();
        return _titleByClsid.TryGetValue(clsid, out var title) ? title : null;
    }

    /// <summary>
    /// Returns every display name that this CLSID's IContextMenu emitted when the
    /// invoker probed it (typically one or two human-readable menu items, e.g.
    /// "Scan with Microsoft Defender…" for the EPP CLSID). Lets callers rename a
    /// row whose registry DisplayName is a technical FileDescription.
    /// </summary>
    public IEnumerable<string> LookupEmittedNames(string? clsid)
    {
        if (string.IsNullOrEmpty(clsid)) return Array.Empty<string>();
        BuildDisplayNameToClsidMap();
        return _emittedByClsid.TryGetValue(clsid, out var names) ? (IEnumerable<string>)names : Array.Empty<string>();
    }

    /// <summary>
    /// Probes every registered classic shellex once per process and returns a
    /// display-name -> CLSID map. Cached so a second Rescan is free.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildDisplayNameToClsidMap()
    {
        if (!_built)
        {
            try { BuildOnce(); }
            catch (Exception ex) { Log.Error(Cat, "BuildOnce threw", ex); }
            _built = true;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clsid, names) in _emittedByClsid)
            foreach (var n in names)
                if (!result.ContainsKey(n)) result[n] = clsid;
        return result;
    }

    private void BuildOnce()
    {
        var registrations = CollectRegistrations();
        // Also probe every "extra" CLSID for IExplorerCommand::GetIcon — these come
        // from verb ExplorerCommandHandlers and CommandStore handlers, which never
        // show up in shellex enumeration.
        var extraTarget = _targets.GetTargets().FirstOrDefault();
        if (extraTarget != null)
        {
            foreach (var clsid in _extraClsids)
                registrations.Add((clsid, extraTarget));
        }
        if (registrations.Count == 0)
        {
            Log.Info(Cat, "no shellex registrations to probe");
            return;
        }

        var done = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            int initHr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
            try
            {
                foreach (var reg in registrations)
                {
                    try { InvokeOne(reg.Clsid, reg.TargetPath); }
                    catch (Exception ex) { Log.Debug(Cat, $"invoke {reg.Clsid} target={reg.TargetPath} ex={ex.Message}"); }
                    try { TryGetIconFromExplorerCommand(reg.Clsid); }
                    catch (Exception ex) { Log.Debug(Cat, $"GetIcon {reg.Clsid} ex={ex.Message}"); }
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
        if (!done.Wait(TimeSpan.FromSeconds(45)))
            Log.Warn(Cat, "STA invoker exceeded 45s; using partial results");

        Log.Info(Cat, $"ShellexInvoker probed registrations clsids={_emittedByClsid.Count} iconPaths={_iconByClsid.Count} titles={_titleByClsid.Count}");
    }

    /// <summary>
    /// Asks an IExplorerCommand-implementing CLSID for its icon path. Many modern
    /// shellexes return something like "C:\\Program Files\\Notepad++\\notepad++.exe,0"
    /// even when the registry has no Icon / DefaultIcon hint pointing to that exe.
    /// </summary>
    private void TryGetIconFromExplorerCommand(string clsid)
    {
        var key = clsid.Trim().ToUpperInvariant();
        if (_iconByClsid.ContainsKey(key)) return;

        IntPtr pCmd = IntPtr.Zero;
        IExplorerCommand? cmd = null;
        try
        {
            if (!System.Guid.TryParse(clsid, out var clsidGuid)) return;
            var iid = IID_IExplorerCommand;
            int hr = CoCreateInstance(ref clsidGuid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out pCmd);
            if (hr < 0 || pCmd == IntPtr.Zero) return;
            cmd = (IExplorerCommand)Marshal.GetObjectForIUnknown(pCmd);

            hr = cmd.GetIcon(IntPtr.Zero, out var pIcon);
            if (hr >= 0 && pIcon != IntPtr.Zero)
            {
                try
                {
                    var iconPath = Marshal.PtrToStringUni(pIcon);
                    if (!string.IsNullOrWhiteSpace(iconPath)) _iconByClsid[key] = iconPath!;
                }
                finally { Marshal.FreeCoTaskMem(pIcon); }
            }

            hr = cmd.GetTitle(IntPtr.Zero, out var pTitle);
            if (hr >= 0 && pTitle != IntPtr.Zero)
            {
                try
                {
                    var title = Marshal.PtrToStringUni(pTitle);
                    if (!string.IsNullOrWhiteSpace(title))
                        _titleByClsid[key] = StripAccelerator(title!);
                }
                finally { Marshal.FreeCoTaskMem(pTitle); }
            }
        }
        finally
        {
            if (cmd != null) Marshal.ReleaseComObject(cmd);
            if (pCmd != IntPtr.Zero) Marshal.Release(pCmd);
        }
    }

    private List<(string Clsid, string TargetPath)> CollectRegistrations()
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = _targets.GetTargets();
        var fileTarget = PickTarget(targets, t => File.Exists(t) && Path.GetExtension(t) == ".txt");
        var pngTarget = PickTarget(targets, t => File.Exists(t) && Path.GetExtension(t) == ".png");
        var mp3Target = PickTarget(targets, t => File.Exists(t) && Path.GetExtension(t) == ".mp3");
        var mp4Target = PickTarget(targets, t => File.Exists(t) && Path.GetExtension(t) == ".mp4");
        var folderTarget = PickTarget(targets, t => Directory.Exists(t) && t.Contains("rcmm-capture"));
        var driveTarget = PickTarget(targets, t => t.Length == 3 && t[1] == ':');
        var defaultTarget = folderTarget ?? fileTarget ?? driveTarget ?? targets[0];

        // Standard six scopes.
        foreach (var scope in new[] { Scope.Files, Scope.Folders, Scope.Drives,
                                       Scope.Background, Scope.AllObjects, Scope.Folder })
        {
            var handlersRoot = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
            var picked = TargetForScope(scope, fileTarget, pngTarget, folderTarget, driveTarget) ?? defaultTarget;
            EnumerateHandlersUnder(handlersRoot, picked, seen, result);
        }

        // Stack.* classes (Audio/Image/Video/Document) — PlayTo / Cast to Device lives here.
        foreach (var (stackClass, target) in new[]
        {
            ("Stack.Audio", mp3Target ?? fileTarget),
            ("Stack.Image", pngTarget ?? fileTarget),
            ("Stack.Video", mp4Target ?? fileTarget),
            ("Stack.Document", fileTarget)
        })
        {
            if (target == null) continue;
            EnumerateHandlersUnder(stackClass + @"\shellex\ContextMenuHandlers", target, seen, result);
        }

        // SystemFileAssociations\<.ext|type>\shellex — probe a few common buckets.
        // We can't probe every extension cheaply, so cover the ones the user has
        // a sample for, plus the perceived-type buckets.
        var sfaBuckets = new List<(string, string?)>
        {
            ("image", pngTarget),
            ("audio", mp3Target),
            ("video", mp4Target),
            ("text", fileTarget),
            (".png", pngTarget),
            (".jpg", pngTarget),
            (".mp3", mp3Target),
            (".mp4", mp4Target),
            (".txt", fileTarget),
        };
        foreach (var (bucket, target) in sfaBuckets)
        {
            if (target == null) continue;
            EnumerateHandlersUnder($@"SystemFileAssociations\{bucket}\shellex\ContextMenuHandlers", target, seen, result);
        }

        return result;
    }

    private void EnumerateHandlersUnder(string handlersRoot, string targetPath,
                                        HashSet<string> seen, List<(string, string)> result)
    {
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, handlersRoot)) return;
        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, handlersRoot))
        {
            var defaultVal = _reg.GetValue(RegistryHive.ClassesRoot, handlersRoot + "\\" + name, "") as string;
            var clsid = LooksLikeClsid(defaultVal) ? defaultVal! :
                        LooksLikeClsid(name) ? name : null;
            if (clsid == null) continue;
            if (!seen.Add(clsid)) continue;
            result.Add((clsid, targetPath));
        }
    }

    private static string? TargetForScope(Scope scope, string? file, string? png, string? folder, string? drive)
        => scope switch
        {
            Scope.Files => file,
            Scope.Folders => folder,
            Scope.Background => folder,
            Scope.Drives => drive,
            Scope.AllObjects => file,
            Scope.Folder => folder,
            _ => null
        };

    private static string? PickTarget(IReadOnlyList<string> targets, Func<string, bool> match)
    {
        foreach (var t in targets) if (match(t)) return t;
        return null;
    }

    private void InvokeOne(string clsid, string targetPath)
    {
        IntPtr pInit = IntPtr.Zero;
        IShellExtInit? init = null;
        IContextMenu? pcm = null;
        IntPtr pData = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;
        try
        {
            // 1) CoCreateInstance the handler with IID_IShellExtInit.
            var clsidGuid = ParseClsid(clsid);
            if (clsidGuid == null) return;
            var clsidVal = clsidGuid.Value;
            var iidInit = IID_IShellExtInit;
            int hr = CoCreateInstance(ref clsidVal, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iidInit, out pInit);
            if (hr < 0 || pInit == IntPtr.Zero) return;
            init = (IShellExtInit)Marshal.GetObjectForIUnknown(pInit);

            // 2) Build an IDataObject from the target file.
            pData = GetDataObjectFor(targetPath);
            if (pData == IntPtr.Zero) return;

            // 3) Initialize the handler — gives it the data it acts on.
            hr = init.Initialize(IntPtr.Zero, pData, IntPtr.Zero);
            if (hr < 0) return;

            // 4) QueryInterface to IContextMenu.
            pcm = init as IContextMenu;
            if (pcm == null) return;

            // 5) Populate a hidden menu and read out the items it emits.
            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;
            const int idFirst = 1;
            hr = pcm.QueryContextMenu(hMenu, 0, idFirst, 0x7FFF, CMF_NORMAL | CMF_EXTENDEDVERBS);
            if (hr < 0) return;

            int count = GetMenuItemCount(hMenu);
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var sb = new StringBuilder(512);
                int len = GetMenuStringW(hMenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
                if (len <= 0) continue;
                var name = StripAccelerator(sb.ToString());
                if (string.IsNullOrWhiteSpace(name)) continue;
                emitted.Add(name);
            }
            if (emitted.Count > 0)
            {
                var key = clsid.Trim().ToUpperInvariant();
                if (!_emittedByClsid.TryGetValue(key, out var set))
                    _emittedByClsid[key] = set = new(StringComparer.OrdinalIgnoreCase);
                foreach (var n in emitted) set.Add(n);
            }
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (pData != IntPtr.Zero) Marshal.Release(pData);
            if (init != null) Marshal.ReleaseComObject(init);
            if (pInit != IntPtr.Zero) Marshal.Release(pInit);
        }
    }

    private static IntPtr GetDataObjectFor(string path)
    {
        IShellItem? psi = null;
        IntPtr pData = IntPtr.Zero;
        try
        {
            var iidShellItem = IID_IShellItem;
            var bhid = BHID_DataObject;
            var iidData = IID_IDataObject;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidShellItem, out psi);
            if (hr < 0 || psi == null) return IntPtr.Zero;
            hr = psi.BindToHandler(IntPtr.Zero, ref bhid, ref iidData, out pData);
            if (hr < 0) return IntPtr.Zero;
            return pData;
        }
        catch
        {
            if (pData != IntPtr.Zero) { Marshal.Release(pData); pData = IntPtr.Zero; }
            return IntPtr.Zero;
        }
        finally
        {
            if (psi != null) Marshal.ReleaseComObject(psi);
        }
    }

    private static Guid? ParseClsid(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return new Guid(s); } catch { return null; }
    }

    private static bool LooksLikeClsid(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith('{') && s.EndsWith('}');

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");
}
