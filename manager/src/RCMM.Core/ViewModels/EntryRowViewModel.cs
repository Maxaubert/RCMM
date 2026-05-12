using System;
using RCMM.Core.Models;

namespace RCMM.Core.ViewModels;

public sealed class EntryRowViewModel : ObservableObject
{
    private bool _isHidden;
    public ContextMenuEntry Entry { get; }
    public Action<EntryRowViewModel, bool>? HiddenChanged;

    public EntryRowViewModel(ContextMenuEntry entry)
    {
        Entry = entry;
        _isHidden = entry.IsHidden;
    }

    public string DisplayName => Entry.DisplayName;
    public string Source => Entry.Source;
    public string KindLabel => Entry.Kind switch
    {
        EntryKind.ShellVerb      => "Verb",
        EntryKind.ShellExtension => "Shell extension",
        _ => "?"
    };
    public bool IsBuiltIn => Entry.IsBuiltIn;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (SetField(ref _isHidden, value))
                HiddenChanged?.Invoke(this, value);
        }
    }
}
