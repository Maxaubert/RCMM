using RCMM.Core.ViewModels;

namespace RCMM.Views;

/// <summary>
/// Frame.Navigate parameter — every page in the layout takes the same
/// ViewModel reference. (The old ListFilter went away with ShowHidePage:
/// the unified list filters via page-local chips, not navigation args.)
/// </summary>
public sealed record NavArgs(MainViewModel ViewModel);
