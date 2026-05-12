using System.Collections.ObjectModel;
using RCMM.Core.Models;

namespace RCMM.Core.ViewModels;

public sealed class ScopeListViewModel : ObservableObject
{
    public Scope Scope { get; }
    public ObservableCollection<EntryRowViewModel> Entries { get; } = new();

    public ScopeListViewModel(Scope scope) { Scope = scope; }
}
