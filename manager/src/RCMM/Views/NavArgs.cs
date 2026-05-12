using RCMM.Core.ViewModels;

namespace RCMM.Views;

public enum ListFilter
{
    All,
    ApplicationSpecific,  // IsBuiltIn = false
    WindowsSpecific       // IsBuiltIn = true
}

/// <summary>
/// Frame.Navigate parameter — every page in the new layout takes the same
/// ViewModel reference. The list page also takes a filter so one ScopePage
/// instance can render either the application-specific or Windows-specific
/// subset.
/// </summary>
public sealed record NavArgs(MainViewModel ViewModel, ListFilter Filter = ListFilter.All);
