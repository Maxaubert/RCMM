using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class TemplateUpdateServiceTests
{
    private static AdditionTemplates.Template Tpl(
        string name = "X", string cmd = "run",
        AdditionScope scope = AdditionScope.FolderBackground, RunMode mode = RunMode.Background,
        string wd = "%V", string[]? fts = null)
        => new() { Name = name, Command = cmd, Ecosystem = "Test", Scope = scope, RunMode = mode, WorkingDir = wd, FileTypes = fts };

    private static AdditionEntry Ent(
        string id = "id1", string name = "X", string cmd = "run",
        AdditionScope scope = AdditionScope.FolderBackground, RunMode mode = RunMode.Background,
        string wd = "%V", string[]? fts = null,
        string? src = null, string? applied = null, string? skipped = null,
        string? icon = null, string? folder = null, string? term = null)
        => new()
        {
            Id = id, Name = name, Command = cmd, WorkingDir = wd, Scope = scope, RunMode = mode,
            FileTypes = fts, Icon = icon, FolderId = folder, Terminal = term,
            SourceTemplateId = src, AppliedTemplateHash = applied, SkippedTemplateHash = skipped,
        };

    [Fact]
    public void Hash_is_stable_and_ignores_filetype_order()
    {
        var a = TemplateUpdateService.HashFields("run", AdditionScope.File, RunMode.Background, "%V", new[] { "png", "jpg" });
        var b = TemplateUpdateService.HashFields("run", AdditionScope.File, RunMode.Background, "%V", new[] { ".jpg", "PNG" });
        Assert.Equal(a, b);   // order- and case- and dot-insensitive
    }

    [Fact]
    public void Hash_changes_when_a_tracked_field_changes()
    {
        var baseHash = TemplateUpdateService.HashFields("run", AdditionScope.File, RunMode.Background, "%V", new[] { "png" });
        Assert.NotEqual(baseHash, TemplateUpdateService.HashFields("run2", AdditionScope.File, RunMode.Background, "%V", new[] { "png" }));
        Assert.NotEqual(baseHash, TemplateUpdateService.HashFields("run", AdditionScope.File, RunMode.Background, "%V", new[] { "png", "jxl" }));
        Assert.NotEqual(baseHash, TemplateUpdateService.HashFields("run", AdditionScope.AllFilesystemObjects, RunMode.Background, "%V", new[] { "png" }));
    }

    [Fact]
    public void Merge_keeps_user_fields_and_refreshes_tracked_ones()
    {
        var t = Tpl(name: "NewName", cmd: "%selfdir%\\x.ps1", scope: AdditionScope.File, fts: new[] { "png", "jxl" });
        var e = Ent(name: "My rename", icon: "lib:star", folder: "f1", term: "wt", scope: AdditionScope.File, fts: new[] { "png" }, src: "X");

        var merged = TemplateUpdateService.Merge(e, t, expandedCommand: "C:\\app\\x.ps1");

        Assert.Equal("My rename", merged.Name);   // user's
        Assert.Equal("lib:star", merged.Icon);     // user's
        Assert.Equal("f1", merged.FolderId);       // user's
        Assert.Equal("wt", merged.Terminal);       // user's
        Assert.Equal("C:\\app\\x.ps1", merged.Command);          // expanded, not the placeholder
        Assert.Equal(new[] { "png", "jxl" }, merged.FileTypes!);  // refreshed
        Assert.Equal(TemplateUpdateService.Hash(t), merged.AppliedTemplateHash);
        Assert.Null(merged.SkippedTemplateHash);
    }

    [Fact]
    public void MatchesIgnoringCommand_ignores_command_but_not_other_fields()
    {
        var t = Tpl(cmd: "%selfdir%\\x.ps1", scope: AdditionScope.File, fts: new[] { "png", "jxl" });
        Assert.True(TemplateUpdateService.MatchesIgnoringCommand(
            Ent(cmd: "C:\\app\\x.ps1", scope: AdditionScope.File, fts: new[] { "jxl", "png" }), t));
        Assert.False(TemplateUpdateService.MatchesIgnoringCommand(
            Ent(cmd: "C:\\app\\x.ps1", scope: AdditionScope.File, fts: new[] { "png" }), t));
    }

    [Fact]
    public void Stamp_sets_source_id_and_applied_hash()
    {
        var t = Tpl(name: "Z", fts: new[] { "png" });
        var s = TemplateUpdateService.Stamp(Ent(name: "Z"), t);
        Assert.Equal("Z", s.SourceTemplateId);
        Assert.Equal(TemplateUpdateService.Hash(t), s.AppliedTemplateHash);
    }

    [Fact]
    public void FindUpdates_flags_only_stale_unskipped_tracked_entries()
    {
        var t = AdditionTemplates.All.First(x => x.Name == "Change format");
        var live = TemplateUpdateService.Hash(t);

        var inSync = Ent(id: "sync", name: "Change format", src: "Change format", applied: live);
        var stale = Ent(id: "stale", name: "Change format", src: "Change format", applied: "OLD");
        var skipped = Ent(id: "skip", name: "Change format", src: "Change format", applied: "OLD", skipped: live);
        var untracked = Ent(id: "hand", name: "hand-authored", src: null);

        var ups = new TemplateUpdateService().FindUpdates(
            new AdditionState { Entries = new[] { inSync, stale, skipped, untracked } });

        Assert.Single(ups);
        Assert.Equal("stale", ups[0].Entry.Id);
        Assert.False(string.IsNullOrWhiteSpace(ups[0].Summary));
    }

    [Fact]
    public void Migration_v2_to_v3_stamps_only_structural_matches()
    {
        var cf = AdditionTemplates.All.First(x => x.Name == "Change format");

        // Structurally identical to the template -> confidently template-derived.
        var sync = Ent(id: "a", name: "Change format", cmd: "C:\\app\\rcmm-convert.ps1",
                       scope: cf.Scope, mode: cf.RunMode, wd: cf.WorkingDir, fts: cf.FileTypes!.ToArray());
        // Name collides but the structure differs. Could be a drifted legacy entry OR a
        // hand-authored one that merely shares the name; we can't tell, so treat it as
        // hand-authored and DON'T stamp it — the safe choice.
        var collision = Ent(id: "b", name: "Change format", cmd: "C:\\app\\rcmm-convert.ps1",
                            scope: cf.Scope, mode: cf.RunMode, wd: cf.WorkingDir, fts: new[] { "png" });
        var hand = Ent(id: "c", name: "totally custom", cmd: "foo");

        var migrated = AdditionStore.MigrateIfNeeded(
            new AdditionState { SchemaVersion = 2, Entries = new[] { sync, collision, hand } });

        Assert.Equal(AdditionState.CurrentSchemaVersion, migrated.SchemaVersion);
        var a = migrated.Entries.Single(e => e.Id == "a");
        Assert.Equal("Change format", a.SourceTemplateId);
        Assert.Equal(TemplateUpdateService.Hash(cf), a.AppliedTemplateHash);      // in sync
        Assert.Null(migrated.Entries.Single(e => e.Id == "b").SourceTemplateId);  // name-only collision -> hand-authored
        Assert.Null(migrated.Entries.Single(e => e.Id == "c").SourceTemplateId);  // not a template name

        // Nothing surfaces as an update: only the in-sync entry is stamped, and it matches.
        Assert.Empty(new TemplateUpdateService().FindUpdates(migrated));
    }

    [Fact]
    public void Migration_does_not_stamp_a_hand_authored_entry_that_only_shares_a_template_name()
    {
        // The bug: a user's own entry named exactly like a built-in template used to be
        // stamped as template-derived, after which a template change would offer an
        // "update" that overwrites the user's command. It must stay hand-authored.
        var cmd = AdditionTemplates.All.First(x => x.Name == "Command Prompt here");
        var mine = Ent(id: "mine", name: "Command Prompt here", cmd: "my-own-launcher.exe %V",
                       scope: AdditionScope.File, mode: RunMode.Background, wd: "%1", fts: new[] { "txt" });

        var migrated = AdditionStore.MigrateIfNeeded(
            new AdditionState { SchemaVersion = 2, Entries = new[] { mine } });

        var m = migrated.Entries.Single();
        Assert.Null(m.SourceTemplateId);
        Assert.Equal("my-own-launcher.exe %V", m.Command);
        Assert.Empty(new TemplateUpdateService().FindUpdates(migrated));
    }
}
