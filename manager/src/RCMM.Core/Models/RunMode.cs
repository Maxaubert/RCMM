namespace RCMM.Core.Models;

/// <summary>How a user-added entry's command is executed when clicked.</summary>
public enum RunMode
{
    /// <summary>
    /// Wraps the command as "cmd /k &lt;Command&gt;" at registry-write time.
    /// User sees a terminal window with the command output; window stays open
    /// until they close it.
    /// </summary>
    VisibleTerminal,
    /// <summary>
    /// Writes the command as-is. Caller is responsible for making it
    /// windowless (e.g. by using start /B, or pointing at a GUI executable).
    /// </summary>
    Background
}
