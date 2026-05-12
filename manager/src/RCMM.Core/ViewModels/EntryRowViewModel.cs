using System;
using RCMM.Core.Models;

namespace RCMM.Core.ViewModels;

public sealed class EntryRowViewModel : ObservableObject
{
    private bool _isHidden;
    private object? _icon;

    public MenuEntry Entry { get; }
    public Action<EntryRowViewModel, bool>? HiddenChanged;

    public EntryRowViewModel(MenuEntry entry)
    {
        Entry = entry;
        _isHidden = entry.IsHidden;
    }

    public string DisplayName => Entry.DisplayName;
    public string Source => string.IsNullOrEmpty(Entry.Source) ? "Unknown" : Entry.Source!;
    public string KindLabel => Entry.IsSubmenu ? "Submenu" : "Item";
    public bool IsBuiltIn => Entry.IsBuiltIn;
    public bool CanHide => Entry.CanHide;
    public byte[]? IconBytes => Entry.IconBytes;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (!CanHide) return;
            if (SetField(ref _isHidden, value))
                HiddenChanged?.Invoke(this, value);
        }
    }

    public object? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }
}
