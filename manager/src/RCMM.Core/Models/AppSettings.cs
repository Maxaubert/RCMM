namespace RCMM.Core.Models;

/// <summary>User-facing app preferences (Settings page). Persisted as settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>When true (default), the footer Apply restarts Explorer so changes
    /// take effect immediately. When false, Apply writes the registry but leaves
    /// Explorer alone — the user restarts it (or signs out) when they choose.</summary>
    public bool RestartExplorerOnApply { get; set; } = true;

    /// <summary>The terminal new entries open in. Same vocabulary as
    /// <see cref="AdditionEntry.Terminal"/>: a known key
    /// (<c>wt</c>/<c>powershell</c>/<c>pwsh</c>/<c>wsl</c>), <c>""</c> for Command
    /// Prompt, or a custom terminal exe path. <c>null</c> = the user hasn't chosen,
    /// so the effective default is resolved at use-time
    /// (<see cref="Services.TerminalCatalog.DefaultPreferred"/>): Windows Terminal
    /// if installed, else Command Prompt. Seeds the Terminal field of entries
    /// created on the Add page and added from templates; existing entries are only
    /// rewritten if the user opts in on the Settings page.</summary>
    public string? DefaultTerminal { get; set; }
}
