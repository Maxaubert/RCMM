using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class EntryScanner
{
    private static readonly Scope[] AllScopes =
        { Scope.Files, Scope.Folders, Scope.Drives, Scope.Background };

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
}
