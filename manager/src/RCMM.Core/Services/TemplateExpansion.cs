using System;

namespace RCMM.Core.Services;

/// <summary>
/// Resolves a template's placeholders to the concrete (command, icon) used when
/// writing an entry. Lives in Core next to <see cref="AdditionTemplates"/> and
/// <see cref="TemplateUpdateService"/> because it is pure business logic — it
/// used to sit as a static on the TemplatesPage UI class, which meant Core's
/// TemplateUpdateService.Merge had to take the expanded command as a parameter
/// and any headless consumer would have needed a WinUI reference to expand
/// correctly. Shared by +Add and the template-update merge so both produce
/// identical commands:
///   • <c>%bin%</c> → the resolved target binary's absolute path (or the literal
///     binary name if it can't be found, so the user sees what's missing).
///   • <c>%selfdir%</c> → the directory of RCMM.exe (where the shipped scripts live).
///   • Icon priority: explicit template.Icon (e.g. "lib:claude") wins; else the
///     resolved binary (Windows extracts its .exe icon); else null.
/// </summary>
public static class TemplateExpansion
{
    public static (string Command, string? Icon) Expand(AdditionTemplates.Template t)
    {
        string command = t.Command;
        string? icon = t.Icon;
        if (!string.IsNullOrEmpty(t.IconBinary))
        {
            var resolved = BinaryResolver.Find(t.IconBinary!, t.IconBinaryFallbacks);
            if (resolved != null)
            {
                if (string.IsNullOrEmpty(icon)) icon = resolved;
                command = command.Replace("%bin%", resolved);
            }
            else
            {
                command = command.Replace("%bin%", t.IconBinary!);
            }
        }
        var selfDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
                      ?? AppContext.BaseDirectory.TrimEnd('\\');
        command = command.Replace("%selfdir%", selfDir);
        return (command, icon);
    }
}
