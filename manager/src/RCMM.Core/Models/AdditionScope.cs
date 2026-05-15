namespace RCMM.Core.Models;

/// <summary>
/// Where a user-added entry shows up in the Windows right-click menu.
/// Maps to the registry path segment under HKCU\Software\Classes\.
/// </summary>
public enum AdditionScope
{
    /// <summary>Right-click empty space inside a folder. Path: Directory\Background\shell.</summary>
    FolderBackground,
    /// <summary>Right-click on a folder from its parent view. Path: Directory\shell.</summary>
    Folder,
    /// <summary>Right-click on a file. With no FileTypes set, registers under *\shell; otherwise one registration per extension under &lt;.ext&gt;\shell.</summary>
    File,
    /// <summary>Right-click on a drive root. Path: Drive\shell.</summary>
    Drive,
    /// <summary>Every file and folder (broad). Path: AllFilesystemObjects\shell.</summary>
    AllFilesystemObjects
}
