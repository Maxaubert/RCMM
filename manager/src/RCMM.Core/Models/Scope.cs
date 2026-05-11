namespace RCMM.Core.Models;

public enum Scope
{
    Files,
    Folders,
    Drives,
    Background
}

public static class ScopeExtensions
{
    public static string ToRegistryRoot(this Scope scope) => scope switch
    {
        Scope.Files      => @"*",
        Scope.Folders    => @"Directory",
        Scope.Drives     => @"Drive",
        Scope.Background => @"Directory\Background",
        _ => throw new System.ArgumentOutOfRangeException(nameof(scope))
    };
}
