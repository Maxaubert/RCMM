namespace RCMM.Core.Models;

public enum Scope
{
    Files,
    Folders,
    Drives,
    Background,
    AllObjects,
    Folder
}

public static class ScopeExtensions
{
    public static string ToRegistryRoot(this Scope scope) => scope switch
    {
        Scope.Files       => @"*",
        Scope.Folders     => @"Directory",
        Scope.Drives      => @"Drive",
        Scope.Background  => @"Directory\Background",
        Scope.AllObjects  => @"AllFilesystemObjects",
        Scope.Folder      => @"Folder",
        _ => throw new System.ArgumentOutOfRangeException(nameof(scope))
    };
}
