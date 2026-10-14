using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Core.ViewModels;

/// <summary>
/// The pre-merge attach + rename stage of the rescan pipeline, extracted from
/// MainViewModel.RescanCore so the rename chain has one owner instead of
/// living inline in a 300-line method. Behavior is identical: for each
/// captured item it (a) attaches an OwnerClsid via the name index / fuzzy
/// match / invoker map, (b) replaces technical FileDescription labels with
/// real menu text through the trust-ordered rename chain, and (c) records
/// which CLSIDs were observed on live captures or renamed from a trusted
/// source — state FilterIntoAllEntries later uses to decide row visibility.
/// </summary>
internal sealed class RenamePipeline
{
    private readonly ShellexNameIndex _shellexIndex;
    private readonly IShellexInvoker? _shellexInvoker;
    private readonly CommandStoreVerbIndex? _commandStore;

    // Last-resort DisplayName overrides for shellex handlers whose menu text
    // we can't recover from the registry or COM probes — typically because
    // the handler refuses to populate IContextMenu without admin/admin-elevated
    // context, and it doesn't implement IExplorerCommand. Keyed by the
    // FileDescription that ClassicShellexScanner picks up.
    private static readonly Dictionary<string, string> _technicalDisplayNameOverrides
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Security Client Shell Extension"] = "Scan with Microsoft Defender",
        ["NVIDIA Display Shell Extension"] = "NVIDIA Control Panel",
    };

    internal RenamePipeline(ShellexNameIndex shellexIndex,
                            IShellexInvoker? shellexInvoker,
                            CommandStoreVerbIndex? commandStore)
    {
        _shellexIndex = shellexIndex;
        _shellexInvoker = shellexInvoker;
        _commandStore = commandStore;
    }

    /// <summary>
    /// Pre-merge pass over <paramref name="allItems"/> (mutated in place):
    ///   (a) Attach OwnerClsid to live captures so the merge's clsid-key dedup
    ///       collapses a live "Scan for deleted files" with the registry-derived
    ///       "Recuva shell extensions" row (same CLSID).
    ///   (b) For any item whose DisplayName is a technical FileDescription
    ///       label and whose CLSID was probed, replace DisplayName with the
    ///       first emitted menu text. "Microsoft Security Client Shell
    ///       Extension" → "Scan with Microsoft Defender…" when Defender's
    ///       handler emitted that text during the invoker run.
    /// </summary>
    internal void Run(List<CapturedItem> allItems,
                      IReadOnlyDictionary<string, string> nameIndex,
                      IReadOnlyList<(string Clsid, HashSet<string> Words)> wordIndex,
                      ISet<string> renamedClsids,
                      ISet<string> observedClsids)
    {
        for (int i = 0; i < allItems.Count; i++)
        {
            var item = allItems[i];
            // A live-captured row's DisplayName is whatever Explorer actually
            // rendered — real menu text by definition, however identifier-like
            // it reads ("TeraCopy...", "WinMerge"). Mark the provenance now so
            // FilterIntoAllEntries never discards it as a technical label.
            // Registry/packaged placeholder rows ("<registry:..>", "<packaged:..>")
            // carry fallback names the heuristic is allowed to judge.
            if (!item.TargetPath.StartsWith("<") && !item.DisplayNameIsMenuText)
                item = item with { DisplayNameIsMenuText = true };
            if (string.IsNullOrEmpty(item.OwnerClsid))
            {
                if (nameIndex.TryGetValue(item.DisplayName, out var nameClsid))
                    item = item with { OwnerClsid = nameClsid };
                else
                {
                    var fuzzy = _shellexIndex.FuzzyMatch(item.DisplayName, wordIndex);
                    if (fuzzy != null) item = item with { OwnerClsid = fuzzy };
                    else
                    {
                        var invMap = _shellexInvoker?.BuildDisplayNameToClsidMap();
                        if (invMap != null && invMap.TryGetValue(item.DisplayName, out var invClsid))
                            item = item with { OwnerClsid = invClsid };
                    }
                }
            }

            if (!string.IsNullOrEmpty(item.OwnerClsid) && LooksTechnical(item.DisplayName))
            {
                // Fallback chain — first one with a non-technical, non-empty title wins.
                //   (1) IShellExtInit+IContextMenu probe results ("Scan for deleted files"
                //       for Recuva's CLSID once it emitted that string during probing).
                //   (2) IExplorerCommand::GetTitle — Modern Share's CLSID returns "Share"
                //       here even though the registry only has the COM ProgID
                //       "Ribbon Modern Share Verb".
                //   (3) CommandStore verb-name derivation — covers any OS modern verb
                //       whose handler is registered in CommandStore but doesn't
                //       implement IExplorerCommand.
                //   (4) Static override table for well-known third-party handlers
                //       (Defender, NVIDIA) that don't probe and have no CommandStore
                //       entry; their menu text is well-known and stable enough to
                //       hardcode rather than leave the row hidden.
                //   (5) Last resort: re-try the emitted-names / GetTitle sources
                //       accepting a CamelCase result. When a handler's true menu
                //       title IS a single-token brand name ("TeraCopy...",
                //       "PowerRename"), every earlier candidate was rejected by
                //       the camel-case rule even though the text is exactly what
                //       Explorer renders; only outright class LABELS ("...Shell
                //       Extension") stay rejected. Skipped for rows already
                //       carrying live menu text — replacing a captured string
                //       with a sibling emission would mislabel the row.
                //
                // Trust levels: GetTitle / CommandStore / override renames mark
                // the CLSID as trusted (renamedClsids); emission-only renames
                // don't — see MainViewModel's _renamedClsids comment for why.
                // Emitted names and GetTitle results are real menu text, so they
                // also set the provenance flag that shields the row from the
                // technical-name filter.
                bool renameIsTrusted = false;
                bool renameIsMenuText = false;
                string? renamed = _shellexInvoker?.LookupEmittedNames(item.OwnerClsid)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !LooksTechnical(n));
                if (!string.IsNullOrWhiteSpace(renamed))
                    renameIsMenuText = true;
                if (string.IsNullOrWhiteSpace(renamed))
                {
                    var title = _shellexInvoker?.LookupTitle(item.OwnerClsid);
                    if (!string.IsNullOrWhiteSpace(title) && !LooksTechnical(title!))
                    {
                        renamed = title;
                        renameIsTrusted = true;
                        renameIsMenuText = true;
                    }
                }
                if (string.IsNullOrWhiteSpace(renamed))
                {
                    var cs = _commandStore?.FriendlyTitleForClsid(item.OwnerClsid);
                    if (!string.IsNullOrWhiteSpace(cs) && !LooksTechnical(cs!))
                    {
                        renamed = cs;
                        renameIsTrusted = true;
                    }
                }
                if (string.IsNullOrWhiteSpace(renamed)
                    && _technicalDisplayNameOverrides.TryGetValue(item.DisplayName, out var hardcoded))
                {
                    renamed = hardcoded;
                    renameIsTrusted = true;
                    renameIsMenuText = true;
                }
                if (string.IsNullOrWhiteSpace(renamed) && !item.DisplayNameIsMenuText)
                {
                    renamed = _shellexInvoker?.LookupEmittedNames(item.OwnerClsid)
                        .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !LooksTechnicalLabel(n));
                    if (string.IsNullOrWhiteSpace(renamed))
                    {
                        var title = _shellexInvoker?.LookupTitle(item.OwnerClsid);
                        if (!string.IsNullOrWhiteSpace(title) && !LooksTechnicalLabel(title!))
                        {
                            renamed = title;
                            renameIsTrusted = true;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(renamed))
                        renameIsMenuText = true;
                }

                if (!string.IsNullOrWhiteSpace(renamed))
                {
                    item = item with
                    {
                        DisplayName = renamed!,
                        DisplayNameIsMenuText = item.DisplayNameIsMenuText || renameIsMenuText
                    };
                    if (renameIsTrusted) renamedClsids.Add(item.OwnerClsid!);
                }
            }

            // A CLSID counts as "observed" only when it's attached to a *live*
            // captured menu item (TargetPath is a real path, not a "<registry:..>"
            // or "<packaged:..>" placeholder). Invoker emissions don't qualify
            // because some shellexes (Work Folders, Client Side Caching UI,
            // Launches Sync Center, …) emit informational items during a probe
            // but never contribute to the actual right-click menu the user sees.
            if (!string.IsNullOrEmpty(item.OwnerClsid) && !item.TargetPath.StartsWith("<"))
                observedClsids.Add(item.OwnerClsid!);

            allItems[i] = item;
        }
    }

    /// <summary>
    /// Heuristic for a "technical" DisplayName the user wouldn't recognise as a menu item:
    /// CamelCase identifiers with no spaces, or substrings like "Shell Extension" /
    /// "Common Dll" / "Property Page" / "Context Menu extension" / "verb handler".
    /// Used to decide whether to swap in the publisher's friendly product name.
    /// </summary>
    internal static bool LooksTechnical(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        if (LooksTechnicalLabel(s)) return true;
        // CamelCase identifier (one token, mixed case)
        if (!s.Contains(' ') && s.Length >= 8)
        {
            bool hasLower = false, hasUpper = false;
            foreach (var c in s)
            {
                if (char.IsLower(c)) hasLower = true;
                else if (char.IsUpper(c)) hasUpper = true;
            }
            if (hasLower && hasUpper) return true;
        }
        return false;
    }

    /// <summary>
    /// The substring half of <see cref="LooksTechnical"/>: unambiguous class
    /// LABELS ("…Shell Extension", "…Property Page") that no shell menu ever
    /// renders as an entry. Split out so text with menu provenance (live
    /// captures, probe emissions, GetTitle results) can be screened against
    /// only this — the camel-case rule is a guess about identifiers and must
    /// not veto a brand name the shell demonstrably displays.
    /// </summary>
    internal static bool LooksTechnicalLabel(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        var lower = s.ToLowerInvariant();
        return lower.Contains("shell extension") || lower.Contains("shellext")
            || lower.Contains("shell common dll") || lower.Contains("common dll")
            || lower.Contains("property page") || lower.Contains("context menu extension")
            || lower.Contains("verb handler") || lower.Contains("context menu verb handler");
    }
}
