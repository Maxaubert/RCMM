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
public sealed class ShellexInvoker : IShellexInvoker
{
    private const string Cat = "shellexinvoker";

    private readonly IRegistry _reg;
    private readonly TargetProvider _targets;
    // Published probe snapshots. The STA build worker never mutates these in
    // place: it accumulates into worker-private dictionaries and, after each
    // registration, swaps in fresh deep copies under _publishGate. Readers
    // therefore always enumerate an immutable snapshot, even when the 45s
    // timeout fired and the abandoned worker is still probing in the background.
    private Dictionary<string, HashSet<string>> _emittedByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _iconByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _titleByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _publishGate = new();
    private readonly HashSet<string> _extraClsids =
        new(StringComparer.OrdinalIgnoreCase);
    // True only after a build that ran to completion (or threw); a timed-out
    // build instead records its worker's done-event in _abandonedBuild so a
    // later call can retry once that worker has actually exited.
    private bool _built;
    private ManualResetEventSlim? _abandonedBuild;

    // Test seams — production code leaves these untouched. ProbeOverride
    // stands in for the per-registration COM work (InvokeOne + GetIcon/GetTitle)
    // so the timeout/publication protocol can be exercised without real shell
    // extensions; BuildTimeout shortens the 45s cap to keep such tests fast.
    internal Action<string, string,
        Dictionary<string, HashSet<string>>,
        Dictionary<string, string>,
        Dictionary<string, string>>? ProbeOverride { get; set; }
    internal TimeSpan BuildTimeout { get; set; } = TimeSpan.FromSeconds(45);

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
        EnsureBuilt();
        return ReadPublished().Icons.TryGetValue(clsid, out var icon) ? icon : null;
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
        EnsureBuilt();
        return ReadPublished().Titles.TryGetValue(clsid, out var title) ? title : null;
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
        EnsureBuilt();
        // Returning the set by reference is safe: published snapshots are
        // immutable once swapped in (see the field comment).
        return ReadPublished().Emitted.TryGetValue(clsid, out var names) ? (IEnumerable<string>)names : Array.Empty<string>();
    }

    /// <summary>
    /// Probes every registered classic shellex once per process and returns a
    /// display-name -> CLSID map. Cached so a second Rescan is free.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildDisplayNameToClsidMap()
    {
        EnsureBuilt();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clsid, names) in ReadPublished().Emitted)
            foreach (var n in names)
                if (!result.ContainsKey(n)) result[n] = clsid;
        return result;
    }

    // All callers of EnsureBuilt run on the single rescan worker (serialized by
    // MainViewModel's work gate), so it needs no locking of its own — only the
    // published-dictionary handoff with the STA build worker is synchronized.
    private void EnsureBuilt()
    {
        if (_built) return;
        if (_abandonedBuild != null)
        {
            // A previous build timed out. While its worker is still probing,
            // don't stack a second STA worker (or a second 45s wait) on top —
            // keep serving the partial snapshot, exactly like the old cached
            // behaviour. Once that worker has actually exited we allow one
            // fresh build, so a merely-slow machine eventually gets complete
            // data instead of caching partial state for the process lifetime;
            // a permanently hung handler means the event never sets and we
            // never wait again.
            if (!_abandonedBuild.IsSet) return;
            // Deliberately not Dispose()d: IsSet can be observed a hair before
            // the worker's Set() call has fully unwound, and disposing under it
            // would throw inside the worker. The rare abandoned event is left
            // to the finalizer instead.
            _abandonedBuild = null;
        }
        try { _built = BuildOnce(); }
        catch (Exception ex)
        {
            Log.Error(Cat, "BuildOnce threw", ex);
            _built = true;   // an exception (not a timeout) never retries — unchanged
        }
    }

    private (Dictionary<string, HashSet<string>> Emitted,
             Dictionary<string, string> Icons,
             Dictionary<string, string> Titles) ReadPublished()
    {
        lock (_publishGate) return (_emittedByClsid, _iconByClsid, _titleByClsid);
    }

    /// <summary>Runs the probe worker. Returns true when it completed within the
    /// timeout (result may legitimately be empty); false when the timeout fired
    /// and the worker was abandoned with partial results published.</summary>
    private bool BuildOnce()
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
            return true;
        }

        var done = new ManualResetEventSlim(false);
        bool abandoned = false;   // guarded by _publishGate
        var t = new Thread(() =>
        {
            int initHr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
            // Worker-private accumulators — the worker never touches the
            // published fields directly. After each registration it publishes
            // deep copies under the gate, unless the caller already timed out
            // and abandoned this build, in which case late results are
            // discarded and the worker stops probing.
            var emitted = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var reg in registrations)
                {
                    if (ProbeOverride != null)
                    {
                        try { ProbeOverride(reg.Clsid, reg.TargetPath, emitted, icons, titles); }
                        catch (Exception ex) { Log.Debug(Cat, $"probe override {reg.Clsid} ex={ex.Message}"); }
                    }
                    else
                    {
                        try { InvokeOne(reg.Clsid, reg.TargetPath, emitted); }
                        catch (Exception ex) { Log.Debug(Cat, $"invoke {reg.Clsid} target={reg.TargetPath} ex={ex.Message}"); }
                        try { TryGetIconFromExplorerCommand(reg.Clsid, icons, titles); }
                        catch (Exception ex) { Log.Debug(Cat, $"GetIcon {reg.Clsid} ex={ex.Message}"); }
                    }
                    lock (_publishGate)
                    {
                        if (abandoned) break;
                        _emittedByClsid = CopyEmitted(emitted);
                        _iconByClsid = new Dictionary<string, string>(icons, StringComparer.OrdinalIgnoreCase);
                        _titleByClsid = new Dictionary<string, string>(titles, StringComparer.OrdinalIgnoreCase);
                    }
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
        if (!done.Wait(BuildTimeout))
        {
            lock (_publishGate) abandoned = true;
            _abandonedBuild = done;
            Log.Warn(Cat, $"STA invoker exceeded {BuildTimeout.TotalSeconds:0}s; using partial results");
            return false;
        }

        var published = ReadPublished();
        Log.Info(Cat, $"ShellexInvoker probed registrations clsids={published.Emitted.Count} iconPaths={published.Icons.Count} titles={published.Titles.Count}");
        return true;
    }

    private static Dictionary<string, HashSet<string>> CopyEmitted(Dictionary<string, HashSet<string>> src)
    {
        var copy = new Dictionary<string, HashSet<string>>(src.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (clsid, names) in src)
            copy[clsid] = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>
    /// Asks an IExplorerCommand-implementing CLSID for its icon path. Many modern
    /// shellexes return something like "C:\\Program Files\\Notepad++\\notepad++.exe,0"
    /// even when the registry has no Icon / DefaultIcon hint pointing to that exe.
    /// </summary>
    private static void TryGetIconFromExplorerCommand(string clsid,
        Dictionary<string, string> icons, Dictionary<string, string> titles)
    {
        var key = clsid.Trim().ToUpperInvariant();
        if (icons.ContainsKey(key)) return;

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
                    if (!string.IsNullOrWhiteSpace(iconPath)) icons[key] = iconPath!;
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
                        titles[key] = StripAccelerator(title!);
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
        foreach (var scope in ShellexHandlerWalk.StandardScopes)
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
        // recoverMaskedDefault deliberately FALSE: the probe set is for
        // handlers the shell can currently load; a handler masked by RCMM's
        // HkcuMask still gets probed anyway because MainViewModel registers
        // every registry-scan-recovered CLSID via RegisterExtraClsid before
        // BuildDisplayNameToClsidMap runs.
        foreach (var r in ShellexHandlerWalk.EnumerateHandlers(_reg, handlersRoot, recoverMaskedDefault: false))
        {
            if (r.Clsid == null) continue;
            if (!seen.Add(r.Clsid)) continue;
            result.Add((r.Clsid, targetPath));
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

    private static void InvokeOne(string clsid, string targetPath,
        Dictionary<string, HashSet<string>> emittedByClsid)
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
                if (!emittedByClsid.TryGetValue(key, out var set))
                    emittedByClsid[key] = set = new(StringComparer.OrdinalIgnoreCase);
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

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");
}
