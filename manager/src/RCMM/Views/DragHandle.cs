using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RCMM.Views;

/// <summary>
/// ContentControl subclass that flips the cursor to a 4-way move while the
/// pointer is over it. <see cref="UIElement.ProtectedCursor"/> is protected
/// so it can only be set from a subclass. Border is sealed, ContentControl
/// is not — so this is the lightest WinUI 3 vehicle for a non-Control element
/// with a custom hover cursor.
///
/// Used as the drag grip on each row of the AddPage's lists. The drag itself
/// is driven by PointerPressed/Moved/Released handlers in AddPage.xaml.cs;
/// this control's only job is the cursor change. Background defaults to
/// transparent so the whole padded area is hit-testable.
/// </summary>
public sealed class DragHandle : ContentControl
{
    public DragHandle()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        IsTabStop = false;
        PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        PointerExited  += (_, _) => ProtectedCursor = null;
    }
}
