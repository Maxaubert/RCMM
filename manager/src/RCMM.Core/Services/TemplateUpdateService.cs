using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Detects when a built-in template has changed since the user added it, and
/// merges those changes into the user's entry on demand.
///
/// Only the fields an update would actually rewrite are tracked — Command,
/// FileTypes, Scope, RunMode, WorkingDir. The entry's Name, Icon, folder
/// placement and Terminal are the user's and are preserved across an update,
/// so a template's display-name change is intentionally NOT a trigger.
///
/// The originating template is identified by <see cref="AdditionEntry.SourceTemplateId"/>
/// (the template's Name). A content hash of the tracked fields, stored on the
/// entry as <see cref="AdditionEntry.AppliedTemplateHash"/>, decides "changed vs
/// in-sync"; <see cref="AdditionEntry.SkippedTemplateHash"/> suppresses a prompt
/// the user dismissed until the template moves on again.
/// </summary>
public sealed class TemplateUpdateService
{
    /// <summary>One available update: the user's entry, the live template it came
    /// from, and a human-readable summary of what applying it would change.</summary>
    public sealed record Update(AdditionEntry Entry, AdditionTemplates.Template Template, string Summary);

    /// <summary>Stable content hash of the fields an update rewrites.</summary>
    public static string HashFields(string command, AdditionScope scope, RunMode runMode, string workingDir, IReadOnlyList<string>? fileTypes)
    {
        var fts = fileTypes == null
            ? ""
            : string.Join(",", fileTypes.Select(NormExt).OrderBy(f => f, StringComparer.Ordinal));
        // Length-prefix each field ("<len>:<value>;") so no delimiter char can ever
        // cause a collision — "ab"+"c" and "a"+"bc" produce different canonical text.
        var sb = new StringBuilder();
        foreach (var f in new[] { command ?? "", scope.ToString(), runMode.ToString(), workingDir ?? "", fts })
            sb.Append(f.Length).Append(':').Append(f).Append(';');
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string Hash(AdditionTemplates.Template t) => HashFields(t.Command, t.Scope, t.RunMode, t.WorkingDir, t.FileTypes);
    public static string Hash(AdditionEntry e) => HashFields(e.Command, e.Scope, e.RunMode, e.WorkingDir, e.FileTypes);

    /// <summary>The live template a tracked entry came from, or null when the entry
    /// is hand-authored or its source template no longer exists.</summary>
    public static AdditionTemplates.Template? TemplateFor(AdditionEntry e)
        => e.SourceTemplateId == null
            ? null
            : AdditionTemplates.All.FirstOrDefault(t => t.Name == e.SourceTemplateId);

    /// <summary>Entries whose source template has changed since the entry was added
    /// or last updated, excluding any change the user already chose to Skip.</summary>
    public IReadOnlyList<Update> FindUpdates(AdditionState state)
    {
        var updates = new List<Update>();
        foreach (var e in state.Entries)
        {
            var t = TemplateFor(e);
            if (t == null) continue;
            var live = Hash(t);
            if (live == e.AppliedTemplateHash) continue;   // in sync
            if (live == e.SkippedTemplateHash) continue;    // dismissed this version
            updates.Add(new Update(e, t, Summarize(e, t)));
        }
        return updates;
    }

    /// <summary>Merged entry: refresh the tracked fields from the template, keep the
    /// user's Name / Icon / folder / Terminal, and stamp the new applied hash.
    /// <paramref name="expandedCommand"/> is the template's Command with %selfdir% /
    /// %bin% already resolved to absolute paths — the form the registry needs — since
    /// the template itself only carries placeholders.</summary>
    public static AdditionEntry Merge(AdditionEntry e, AdditionTemplates.Template t, string expandedCommand)
        => e with
        {
            Command = expandedCommand,
            Scope = t.Scope,
            RunMode = t.RunMode,
            WorkingDir = t.WorkingDir,
            FileTypes = t.FileTypes,
            AppliedTemplateHash = Hash(t),
            SkippedTemplateHash = null,
        };

    /// <summary>True when the entry's non-command tracked fields match the template.
    /// The v3 migration uses this to baseline a pre-feature entry without comparing
    /// the command — the entry's command is path-expanded while the template's still
    /// carries %selfdir% / %bin% placeholders, so a raw command compare is meaningless.</summary>
    public static bool MatchesIgnoringCommand(AdditionEntry e, AdditionTemplates.Template t)
        => e.Scope == t.Scope
           && e.RunMode == t.RunMode
           && string.Equals(e.WorkingDir, t.WorkingDir, StringComparison.Ordinal)
           && SameFileTypes(e.FileTypes, t.FileTypes);

    private static bool SameFileTypes(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        => (a ?? Array.Empty<string>()).Select(NormExt).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual((b ?? Array.Empty<string>()).Select(NormExt).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Record the live template hash as skipped, so this same change won't
    /// prompt again until the template changes further.</summary>
    public static AdditionEntry MarkSkipped(AdditionEntry e, AdditionTemplates.Template t)
        => e with { SkippedTemplateHash = Hash(t) };

    /// <summary>Stamp a freshly template-cloned entry so it starts in-sync.</summary>
    public static AdditionEntry Stamp(AdditionEntry e, AdditionTemplates.Template t)
        => e with { SourceTemplateId = t.Name, AppliedTemplateHash = Hash(t), SkippedTemplateHash = null };

    /// <summary>Human-readable summary of what applying an update would change.</summary>
    public static string Summarize(AdditionEntry e, AdditionTemplates.Template t)
    {
        var parts = new List<string>();

        var cur = (e.FileTypes ?? Array.Empty<string>()).Select(NormExt).ToHashSet(StringComparer.Ordinal);
        var neu = (t.FileTypes ?? Array.Empty<string>()).Select(NormExt).ToHashSet(StringComparer.Ordinal);
        var added = neu.Except(cur).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var removed = cur.Except(neu).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (added.Count > 0 || removed.Count > 0)
        {
            var sb = new StringBuilder("file types:");
            foreach (var a in added) sb.Append(" +").Append(a);
            foreach (var r in removed) sb.Append(" -").Append(r);
            parts.Add(sb.ToString());
        }
        if (!string.Equals(e.Command, t.Command, StringComparison.Ordinal)) parts.Add("command updated");
        if (e.Scope != t.Scope) parts.Add($"scope -> {t.Scope}");
        if (e.RunMode != t.RunMode) parts.Add($"run mode -> {t.RunMode}");
        if (!string.Equals(e.WorkingDir, t.WorkingDir, StringComparison.Ordinal)) parts.Add("working dir updated");

        return parts.Count == 0 ? "updated" : string.Join("; ", parts);
    }

    private static string NormExt(string ext) => ext.Trim().TrimStart('.').ToLowerInvariant();
}
