using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Core.ViewModels;

/// <summary>
/// The apply-sequencing half of MainViewModel, extracted so the ~200-line
/// orchestration (cascade protection → hides → unhides → protection sweep →
/// additions apply / re-assert) has one owner. Behavior is identical to the
/// inline original. MainViewModel keeps ownership of the pending dictionaries
/// and badge list: it snapshots them, calls <see cref="Run"/>, and un-badges
/// afterwards — failures are handed back through the requeue callbacks so the
/// pending-retry semantics stay where the pending state lives.
/// </summary>
internal sealed class ApplyOrchestrator
{
    private readonly HideService _hideService;
    private readonly IRegistry _reg;
    private readonly CascadeProtectionService? _cascadeProtector;
    private readonly AdditionApplier? _additionApplier;
    private readonly AddPageViewModel? _addPage;
    private readonly Action<Action> _post;

    internal ApplyOrchestrator(HideService hideService,
                               IRegistry reg,
                               CascadeProtectionService? cascadeProtector,
                               AdditionApplier? additionApplier,
                               AddPageViewModel? addPage,
                               Action<Action> post)
    {
        _hideService = hideService;
        _reg = reg;
        _cascadeProtector = cascadeProtector;
        _additionApplier = additionApplier;
        _addPage = addPage;
        _post = post;
    }

    /// <summary>Applies one snapshotted batch of pending hides/unhides plus any
    /// dirty additions. Returns user-facing error lines (empty = all applied).
    /// A failed hide/unhide is reported through the matching requeue callback
    /// so its badge survives and the next Apply retries it.</summary>
    internal List<string> Run(
        IReadOnlyDictionary<string, IReadOnlyList<HideTarget>> pendingHide,
        IReadOnlyDictionary<string, IReadOnlyList<HideTarget>> pendingUnhide,
        IReadOnlyDictionary<string, PackagedShellExt> backgroundExtsByClsid,
        Action<string, IReadOnlyList<HideTarget>> requeueFailedHide,
        Action<string, IReadOnlyList<HideTarget>> requeueFailedUnhide)
    {
        var errors = new List<string>();

        // Cascade-protection pass — runs BEFORE any HKCU\…\Shell Extensions\Blocked
        // write so the protective classic verbs exist by the time Windows decides
        // whether to silently de-list other Directory\Background packaged-COM
        // extensions. We only act when (a) the protection service is wired AND
        // (b) the current hide batch actually adds a Background-scoped packaged
        // CLSID to Blocked. See CLAUDE.md "packaged-COM Directory\Background
        // cascade" for the underlying Windows behaviour we're guarding against.
        if (_cascadeProtector != null && backgroundExtsByClsid.Count > 0)
        {
            // Collect every Background packaged CLSID this batch adds to Blocked
            // FIRST, then plan once for the whole set. Planning per-CLSID (the
            // original shape) excluded only the one CLSID being iterated, so
            // hiding two Background extensions in one batch installed a
            // resurrection verb for each from the other's pass.
            var bgClsidsBeingHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var targets in pendingHide.Values)
                foreach (var t in targets)
                    if (t.Kind == HideKind.BlockedShellExt
                        && !string.IsNullOrEmpty(t.ValueName)
                        && backgroundExtsByClsid.ContainsKey(t.ValueName!))
                        bgClsidsBeingHidden.Add(t.ValueName!);

            if (bgClsidsBeingHidden.Count > 0)
            {
                // A CLSID being hidden now may still own a protection verb from an
                // earlier Apply (installed back when a DIFFERENT extension was the
                // one being hidden). That verb is a real rendered menu row, so left
                // in place it would keep the newly-hidden extension visible.
                try
                {
                    var stale = _cascadeProtector.RemoveProtectionsFor(bgClsidsBeingHidden);
                    if (stale > 0)
                        Log.Info("apply", $"cascade-protection: removed {stale} protection verbs for extensions being hidden in this batch");
                }
                catch (Exception ex) { Log.Error("apply", "cascade-protection stale-verb removal failed", ex); }

                var plans = _cascadeProtector.PlanProtections(bgClsidsBeingHidden, backgroundExtsByClsid.Values);
                if (plans.Count > 0)
                {
                    Log.Info("apply", $"cascade-protection: installing {plans.Count} classic-verb fallbacks for hide of [{string.Join(", ", bgClsidsBeingHidden)}]");
                    try { _cascadeProtector.Install(plans); }
                    catch (Exception ex) { Log.Error("apply", "cascade-protection install failed", ex); }
                }
            }
        }

        foreach (var (id, targets) in pendingHide)
        {
            if (TryRouteOwnedHide(targets, hidden: true))
            {
                Log.Debug("apply", $"hide id='{id}' routed to additions store (RCMM-owned entry)");
                continue;
            }
            Log.Debug("apply", $"hide id='{id}' targets={targets.Count}");
            foreach (var t in targets)
                Log.Debug("apply", $"  hide  kind={t.Kind} hive={t.Hive} path='{t.Path}' value='{t.ValueName}'");
            try { _hideService.Hide(targets); }
            catch (Exception ex)
            {
                Log.Error("apply", $"hide id={id} failed", ex);
                errors.Add($"Hiding '{id}' failed: {ex.Message}");
                requeueFailedHide(id, targets);
            }
        }
        foreach (var (id, targets) in pendingUnhide)
        {
            if (TryRouteOwnedHide(targets, hidden: false))
            {
                Log.Debug("apply", $"unhide id='{id}' routed to additions store (RCMM-owned entry)");
                continue;
            }
            Log.Debug("apply", $"unhide id='{id}' targets={targets.Count}");
            foreach (var t in targets)
                Log.Debug("apply", $"  unhide kind={t.Kind} hive={t.Hive} path='{t.Path}' value='{t.ValueName}'");
            try { _hideService.Unhide(targets); }
            catch (Exception ex)
            {
                Log.Error("apply", $"unhide id={id} failed", ex);
                errors.Add($"Un-hiding '{id}' failed: {ex.Message}");
                requeueFailedUnhide(id, targets);
            }
        }

        // Post-unhide cleanup: when no Background-scoped packaged CLSID remains in
        // HKCU Blocked, the cascade can't trigger, so we can sweep the protective
        // verbs back out. The sweep is namespace-scoped (RcmmProtect_ prefix only)
        // so user-authored classic verbs are untouched.
        if (_cascadeProtector != null && pendingUnhide.Count > 0 && backgroundExtsByClsid.Count > 0)
        {
            try
            {
                bool anyBgStillBlocked = backgroundExtsByClsid.Keys.Any(clsid =>
                    _reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, clsid) != null);
                if (!anyBgStillBlocked)
                {
                    var removed = _cascadeProtector.UninstallAll();
                    if (removed > 0)
                        Log.Info("apply", $"cascade-protection: removed {removed} protection verbs (no Background ext remains blocked)");
                }
            }
            catch (Exception ex) { Log.Error("apply", "cascade-protection sweep failed", ex); }
        }

        // Always purge any Directory\shell\RcmmProtect_* verbs left behind by
        // earlier builds that protected the folder-selected scope too. Those
        // produced a duplicate "Open in Terminal" / "AMD Software" row on
        // right-click of a selected folder. Cheap and idempotent.
        if (_cascadeProtector != null)
        {
            try
            {
                var stale = _cascadeProtector.PurgeStaleDirectoryScopeProtections();
                if (stale > 0)
                    Log.Info("apply", $"cascade-protection: purged {stale} stale Directory-scope protection verbs");
            }
            catch (Exception ex) { Log.Error("apply", "cascade-protection Directory purge failed", ex); }
        }
        if (_addPage != null && _additionApplier != null && _addPage.HasPendingChanges)
        {
            if (_addPage.LoadFailed)
            {
                // additions.json existed but couldn't be read at startup, so the
                // page buffer is a stub, not the user's data. Applying would purge
                // every RCMM verb and rewrite registry + JSON from that stub —
                // permanently destroying the entries the unreadable file (parked
                // at additions.json.bad) still holds. Refuse; HasPendingChanges
                // stays lit so the user's edits aren't silently discarded either.
                Log.Error("apply", "refusing additions apply: additions.json failed to load at startup — applying would purge real entries based on a stub state");
                errors.Add("Added entries were NOT applied: additions.json could not be read when RCMM started, "
                         + "so applying now would erase your previously added entries. "
                         + "A backup was saved as additions.json.bad — restart RCMM to retry loading.");
            }
            else
            {
                // Snapshot enumerates the UI-bound Entries/Folders collections, and the
                // user can still drag-reorder on the Add page while this worker runs —
                // take the snapshot on the UI thread so it can't tear mid-edit.
                var state = RunOnUi(_addPage.Snapshot);
                Log.Info("apply", $"additions begin entries={state.Entries.Count} folders={state.Folders.Count}");
                try
                {
                    _additionApplier.Apply(state);
                    // Persist only after registry write succeeds so a failed Apply leaves the JSON on the previous state.
                    _addPage.Save(state);
                    _post(() => _addPage.MarkClean());
                }
                catch (Exception ex)
                {
                    Log.Error("apply", "additions failed", ex);
                    // MarkClean deliberately not reached: the Apply button stays lit.
                    errors.Add($"Applying your added entries to the registry failed: {ex.Message}");
                }
            }
        }
        else if (_additionApplier != null && (_addPage == null || !_addPage.LoadFailed)
                 && (pendingHide.Count > 0 || pendingUnhide.Count > 0))
        {
            // A hide/unhide batch ran with no addition edits of its own. Re-assert
            // the user's existing added entries as part of the same Apply: after a
            // large Blocked-list hide + Explorer restart, Explorer can stop rendering
            // user-added Directory\Background verbs even though their keys are intact
            // (RCMM still captures them on rescan). A fresh registry write under
            // …\shell is what forces the classic menu to rebuild — exactly the manual
            // "re-apply additions" workaround, now automatic. Idempotent purge+rewrite;
            // nothing is dirtied or persisted because the state is unchanged.
            try
            {
                var state = _addPage != null
                    ? RunOnUi(_addPage.Snapshot)   // UI-bound collections — see above
                    : new AdditionStore(AdditionStore.DefaultPath()).Load();
                if (state.Entries.Count > 0 || state.Folders.Count > 0)
                {
                    Log.Info("apply", $"re-asserting {state.Entries.Count} added entries after hide change (menu-cache refresh)");
                    _additionApplier.Apply(state);
                }
            }
            catch (Exception ex)
            {
                Log.Error("apply", "additions re-assert failed", ex);
                errors.Add($"Re-writing your added entries after the hide change failed: {ex.Message}");
            }
        }

        return errors;
    }

    /// <summary>
    /// A hide for one of RCMM's own added entries cannot be written as a registry
    /// marker. The addition re-assert later in this same apply calls
    /// AdditionApplier.Apply → PurgeOwnedKeys, which deletes every RCMM.-prefixed
    /// verb key and rewrites it from additions.json — erasing any LegacyDisable we
    /// just wrote. So the intent has to live in the store, where the rewrite can
    /// reproduce it. Returns false for foreign verbs (Tabby, WinRAR, Defender…),
    /// which take the normal HideService path unchanged. See issue #10.
    /// </summary>
    private bool TryRouteOwnedHide(IReadOnlyList<HideTarget> targets, bool hidden)
    {
        if (_addPage == null || _additionApplier == null || targets.Count == 0) return false;

        // Every target must resolve to the same owned entry. A File-scope entry
        // fans out to one target per extension, so multiple targets are normal —
        // but a mixed or non-LegacyDisable set means this isn't a plain owned verb.
        string? entryId = null;
        foreach (var t in targets)
        {
            if (t.Kind != HideKind.LegacyDisable) return false;
            var keyName = t.Path[(t.Path.LastIndexOf('\\') + 1)..];
            if (!AdditionApplier.TryParseOwnedVerb(keyName, out var parsed)) return false;
            if (entryId != null && entryId != parsed) return false;
            entryId = parsed;
        }
        // SetEntryHidden replaces an item in the UI-bound Entries collection
        // (CollectionChanged → AddPage.RefreshAll → XAML), so it must run on the
        // UI thread — Run is called from the Task.Run apply worker.
        return entryId != null && RunOnUi(() => _addPage.SetEntryHidden(entryId, hidden));
    }

    /// <summary>
    /// Runs <paramref name="func"/> through the injected UI dispatcher and blocks
    /// until it completes, returning its result. The apply flow needs this for
    /// the Add-page operations (SetEntryHidden, Snapshot): they mutate/enumerate
    /// the page's UI-bound ObservableCollections, which per the threading design
    /// must only ever be touched on the UI thread — but their result decides what
    /// the apply worker does next, so fire-and-forget _post isn't enough. Safe
    /// against deadlock because every ApplyPending call site invokes it via
    /// Task.Run, never on the UI thread itself (and the inline default post used
    /// by tests runs synchronously). The bounded wait means a dead dispatcher
    /// (app teardown) aborts the apply with a logged error instead of hanging
    /// the worker forever.
    /// </summary>
    private T RunOnUi<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        using var completed = new ManualResetEventSlim(false);
        _post(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
            finally { completed.Set(); }
        });
        if (!completed.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("UI dispatcher did not run a posted operation within 30s");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}
