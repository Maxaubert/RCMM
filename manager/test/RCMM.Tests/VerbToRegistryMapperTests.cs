using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class VerbToRegistryMapperTests
{
    [Fact]
    public void Map_verb_finds_no_targets_when_unregistered()
    {
        var reg = new FakeRegistry();
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("nonexistent").ToList();

        Assert.Empty(targets);
    }

    [Fact]
    public void MapClsid_still_finds_target_when_HKCU_mask_shadows_HKCR()
    {
        // FakeRegistry models HKCR by reading HKLM\Software\Classes — but to
        // exercise the HKLM fallback path we set HKLM directly and HKCR
        // separately to mimic the merged view: HKCR shows the HKCU shadow's
        // empty default, while the underlying HKLM key still carries the CLSID.
        var reg = new FakeRegistry();
        var clsid = "{F81E9010-6EA4-11CE-A7FF-00AA003CA9F6}";
        // HKLM original registration
        reg.SetValue(RegistryHive.LocalMachine,
            "Software\\Classes\\Directory\\shellex\\ContextMenuHandlers\\Sharing", "", clsid);
        // HKCR shows the HKCU mask's empty value (simulated)
        reg.SetValue(RegistryHive.ClassesRoot,
            "Directory\\shellex\\ContextMenuHandlers\\Sharing", "", "");

        var sut = new VerbToRegistryMapper(reg);
        var targets = sut.MapClsid(clsid).ToList();

        Assert.Single(targets);
        Assert.Equal(HideKind.HkcuMask, targets[0].Kind);
        Assert.Equal("Software\\Classes\\Directory\\shellex\\ContextMenuHandlers\\Sharing", targets[0].Path);
    }

    [Fact]
    public void Map_verb_finds_single_registry_location()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("git_shell").ToList();

        Assert.Single(targets);
        Assert.Equal(HideKind.LegacyDisable, targets[0].Kind);
        // Writes go to per-user HKCU\Software\Classes; the HKCR discovery key
        // is converted to its per-user shadow so Apply doesn't need admin.
        Assert.Equal(RegistryHive.CurrentUser, targets[0].Hive);
        Assert.Equal(@"Software\Classes\*\shell\git_shell", targets[0].Path);
        Assert.Equal("LegacyDisable", targets[0].ValueName);
    }

    [Fact]
    public void Map_verb_finds_all_scope_root_locations()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\open", "", "Open");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\open", "", "Open");
        reg.SetValue(RegistryHive.ClassesRoot, @"Drive\shell\open", "", "Open");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("open").ToList();

        Assert.Equal(3, targets.Count);
        Assert.All(targets, t => Assert.Equal(RegistryHive.CurrentUser, t.Hive));
        Assert.Contains(targets, t => t.Path == @"Software\Classes\*\shell\open");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\Directory\shell\open");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\Drive\shell\open");
    }

    [Fact]
    public void Map_verb_finds_progid_registration_via_extension_default()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, ".mp4", "", "VLC.mp4");
        reg.SetValue(RegistryHive.ClassesRoot, @"VLC.mp4\shell\PlayWithVLC", "", "Play with VLC media player");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("PlayWithVLC", new[] { ".mp4" }).ToList();

        var t = Assert.Single(targets);
        Assert.Equal(HideKind.LegacyDisable, t.Kind);
        Assert.Equal(RegistryHive.CurrentUser, t.Hive);
        Assert.Equal(@"Software\Classes\VLC.mp4\shell\PlayWithVLC", t.Path);
        Assert.Equal("LegacyDisable", t.ValueName);
    }

    [Fact]
    public void Map_verb_finds_progid_registration_via_OpenWithProgids()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @".mkv\OpenWithProgids", "VLC.mkv", "");
        reg.SetValue(RegistryHive.ClassesRoot, @"VLC.mkv\shell\PlayWithVLC", "", "Play with VLC media player");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("PlayWithVLC", new[] { ".mkv" }).ToList();

        var t = Assert.Single(targets);
        Assert.Equal(@"Software\Classes\VLC.mkv\shell\PlayWithVLC", t.Path);
    }

    [Fact]
    public void Map_verb_finds_progid_registration_via_UserChoice()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp4\UserChoice", "ProgId", "VLC.mp4");
        reg.SetValue(RegistryHive.ClassesRoot, @"VLC.mp4\shell\PlayWithVLC", "", "Play with VLC media player");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("PlayWithVLC", new[] { ".mp4" }).ToList();

        var t = Assert.Single(targets);
        Assert.Equal(@"Software\Classes\VLC.mp4\shell\PlayWithVLC", t.Path);
    }

    [Fact]
    public void Map_verb_finds_verb_registered_directly_on_extension_key()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @".mp4\shell\grabframe", "", "Grab frame");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("grabframe", new[] { ".mp4" }).ToList();

        var t = Assert.Single(targets);
        Assert.Equal(@"Software\Classes\.mp4\shell\grabframe", t.Path);
    }

    [Fact]
    public void Map_verb_does_not_duplicate_progid_shared_by_multiple_extensions()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, ".mp4", "", "VLC.mp4");
        reg.SetValue(RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp4\UserChoice", "ProgId", "VLC.mp4");
        reg.SetValue(RegistryHive.ClassesRoot, @".m4v\OpenWithProgids", "VLC.mp4", "");
        reg.SetValue(RegistryHive.ClassesRoot, @"VLC.mp4\shell\PlayWithVLC", "", "Play with VLC media player");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("PlayWithVLC", new[] { ".mp4", ".m4v" }).ToList();

        Assert.Single(targets);
    }

    [Fact]
    public void Map_verb_association_cache_rebuilds_after_invalidate()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, ".mp4", "", "VLC.mp4");
        reg.SetValue(RegistryHive.ClassesRoot, @"VLC.mp4\shell\PlayWithVLC", "", "Play");
        var sut = new VerbToRegistryMapper(reg);
        Assert.Single(sut.MapVerb("PlayWithVLC", new[] { ".mp4", ".avi" }));

        reg.SetValue(RegistryHive.ClassesRoot, ".avi", "", "VLC.avi");
        reg.SetValue(RegistryHive.ClassesRoot, @"VLC.avi\shell\PlayWithVLC", "", "Play");
        sut.InvalidateAssociationCache();

        Assert.Equal(2, sut.MapVerb("PlayWithVLC", new[] { ".mp4", ".avi" }).Count());
    }

    // --- Fan-out narrowing: a hide may only touch the registrations that ---
    // --- produced the visible row, never every bucket sharing the name.  ---

    [Fact]
    public void Map_verb_does_not_fan_out_to_unrelated_progids_sharing_the_verb_name()
    {
        // "print" exists under txtfile, batfile and jpegfile. The row was captured
        // on the .txt sample only — hiding it must not touch .bat or .jpg types.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, ".txt", "", "txtfile");
        reg.SetValue(RegistryHive.ClassesRoot, @"txtfile\shell\print", "", "Print");
        reg.SetValue(RegistryHive.ClassesRoot, ".bat", "", "batfile");
        reg.SetValue(RegistryHive.ClassesRoot, @"batfile\shell\print", "", "Print");
        reg.SetValue(RegistryHive.ClassesRoot, ".jpg", "", "jpegfile");
        reg.SetValue(RegistryHive.ClassesRoot, @"jpegfile\shell\print", "", "Print");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("print", new[] { ".txt" }).ToList();

        var t = Assert.Single(targets);
        Assert.Equal(@"Software\Classes\txtfile\shell\print", t.Path);
    }

    [Fact]
    public void Map_verb_without_source_extensions_maps_scope_roots_only()
    {
        // No source attribution (registry-scanned scope verb) → association and
        // SystemFileAssociations buckets must not be guessed from the verb name.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\mytool", "", "My Tool");
        reg.SetValue(RegistryHive.ClassesRoot, ".txt", "", "txtfile");
        reg.SetValue(RegistryHive.ClassesRoot, @"txtfile\shell\mytool", "", "My Tool");
        reg.SetValue(RegistryHive.ClassesRoot, @"SystemFileAssociations\text\shell\mytool", "", "My Tool");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("mytool").ToList();

        var t = Assert.Single(targets);
        Assert.Equal(@"Software\Classes\Directory\shell\mytool", t.Path);
    }

    [Fact]
    public void Map_verb_finds_SystemFileAssociations_for_source_extension_and_its_perceived_type()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, ".png", "PerceivedType", "image");
        reg.SetValue(RegistryHive.ClassesRoot, @"SystemFileAssociations\.png\shell\edittool", "", "Edit tool");
        reg.SetValue(RegistryHive.ClassesRoot, @"SystemFileAssociations\image\shell\edittool", "", "Edit tool");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("edittool", new[] { ".png" }).ToList();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Path == @"Software\Classes\SystemFileAssociations\.png\shell\edittool");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\SystemFileAssociations\image\shell\edittool");
    }

    [Fact]
    public void Map_verb_does_not_fan_out_to_SystemFileAssociations_of_other_perceived_types()
    {
        // Same verb name registered for both image and video perceived types;
        // observed on the .png sample only → the video bucket stays untouched.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, ".png", "PerceivedType", "image");
        reg.SetValue(RegistryHive.ClassesRoot, @"SystemFileAssociations\image\shell\rotate", "", "Rotate");
        reg.SetValue(RegistryHive.ClassesRoot, @"SystemFileAssociations\video\shell\rotate", "", "Rotate");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("rotate", new[] { ".png" }).ToList();

        var t = Assert.Single(targets);
        Assert.Equal(@"Software\Classes\SystemFileAssociations\image\shell\rotate", t.Path);
    }

    [Fact]
    public void MapClsid_recovers_masked_per_user_only_handler_via_SavedDefault_stash()
    {
        // A per-user-only handler (no HKLM twin, key name not a CLSID) that the
        // user hid: the merged HKCR default is empty (the mask), and the only
        // surviving copy of the CLSID is the RCMM.SavedDefault stash Hide wrote.
        // MapClsid must read it, or the hidden handler loses its HkcuMask target
        // after a rescan and becomes unrecoverable through the UI.
        var reg = new FakeRegistry();
        var clsid = "{11111111-2222-3333-4444-555555555555}";
        reg.SetValue(RegistryHive.ClassesRoot,
            @"Directory\shellex\ContextMenuHandlers\MyTool", "", "");
        reg.SetValue(RegistryHive.CurrentUser,
            @"Software\Classes\Directory\shellex\ContextMenuHandlers\MyTool",
            HideService.SavedDefaultValueName, clsid);

        var sut = new VerbToRegistryMapper(reg);
        var targets = sut.MapClsid(clsid).ToList();

        var t = Assert.Single(targets);
        Assert.Equal(HideKind.HkcuMask, t.Kind);
        Assert.Equal(@"Software\Classes\Directory\shellex\ContextMenuHandlers\MyTool", t.Path);
    }

    [Fact]
    public void Map_clsid_finds_shellex_handler_locations_via_default_or_keyname_match()
    {
        var reg = new FakeRegistry();
        // Handler registered with CLSID in default value:
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");
        // Handler registered with CLSID as key name:
        reg.CreateKey(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\{ABC}");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapClsid("{ABC}").ToList();

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal(HideKind.HkcuMask, t.Kind));
        Assert.All(targets, t => Assert.Equal(RegistryHive.CurrentUser, t.Hive));
        Assert.Contains(targets, t => t.Path == @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\Directory\shellex\ContextMenuHandlers\{ABC}");
    }
}
