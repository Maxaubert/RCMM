using System;

namespace RCMM.Core.Util;

public static class EntryFilters
{
    public static bool IsLikelyUserVisible(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var s = displayName.Trim();
        if (s.StartsWith("@", StringComparison.Ordinal)) return false;
        if (s.Length >= 2 && s[0] == '{' && s[^1] == '}') return false;
        if (s.Contains('\\') || s.Contains('/')) return false;
        if (EndsWithExtension(s, ".dll") || EndsWithExtension(s, ".exe")
            || EndsWithExtension(s, ".ocx") || EndsWithExtension(s, ".ico"))
            return false;
        return true;
    }

    private static bool EndsWithExtension(string s, string ext)
        => s.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
}
