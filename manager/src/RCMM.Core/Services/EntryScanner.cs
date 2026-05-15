using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class EntryScanner
{
    private static readonly Scope[] AllScopes =
        { Scope.Files, Scope.Folders, Scope.Drives, Scope.Background, Scope.AllObjects, Scope.Folder };

    private readonly ClassicVerbScanner _verbs;
    private readonly ClassicShellexScanner _shellex;

    public EntryScanner(ClassicVerbScanner verbs, ClassicShellexScanner shellex)
    {
        _verbs = verbs;
        _shellex = shellex;
    }

    public IEnumerable<ContextMenuEntry> ScanAll()
        => AllScopes.SelectMany(ScanScope);

    public IEnumerable<ContextMenuEntry> ScanScope(Scope scope)
        => _verbs.Scan(scope).Concat(_shellex.Scan(scope));

    /// <summary>
    /// Yields each registered shell entry as a synthetic CapturedItem so the rescan
    /// pipeline can merge it with live captures. Live captures take precedence when
    /// keys collide (their display names are what the user actually sees).
    /// </summary>
    public IEnumerable<CapturedItem> ScanAsCaptures()
    {
        int pos = 0;
        foreach (var entry in ScanAll())
        {
            yield return new CapturedItem
            {
                TargetPath = $"<registry:{entry.Scope}:{entry.Kind}>",
                Position = pos++,
                DisplayName = entry.DisplayName,
                Verb = entry.Kind == EntryKind.ShellVerb ? entry.OriginalKeyName : null,
                OwnerClsid = entry.Kind == EntryKind.ShellExtension ? entry.Clsid : null,
                IconHint = entry.IconPath,
                IsSeparator = false,
                IsSubmenu = false
            };
        }
    }
}
