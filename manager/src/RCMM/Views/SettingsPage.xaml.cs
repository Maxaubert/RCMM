using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Windows.Storage.Pickers;

namespace RCMM.Views;

/// <summary>The user's choice when leaving the Settings page with unsaved changes.</summary>
public enum SettingsLeaveChoice { Apply, Discard, Cancel }

/// <summary>
/// Settings page. Options (Context menu, Restart Explorer, Default terminal) are
/// STAGED — changing a control doesn't touch the registry or disk. The docked Apply
/// bar commits them together; navigating away while dirty prompts via
/// <see cref="ConfirmLeaveAsync"/> (driven from MainWindow's navigation guard).
/// </summary>
public sealed partial class SettingsPage : Page
{
    private NavArgs _args = null!;
    private readonly ContextMenuModeService _ctxMode = new(new Win32Registry());
    private readonly SettingsStore _settings = new();

    /// <summary>Suppresses change handlers while we set control values programmatically.</summary>
    private bool _initializing;

    // Last-committed (loaded) values — the baseline dirty is measured against.
    private bool _loadedClassic;
    private bool _loadedRestartOnApply;
    private string _loadedDefaultTerminal = "";

    // Staged values — what Apply will commit.
    private bool _stagedClassic;
    private bool _stagedRestartOnApply;
    private string _stagedDefaultTerminal = "";
    private bool _stagedApplyToExisting;   // "also set it for existing entries?" answer

    private bool _dirty;

    // Terminal dropdown options (Command Prompt / installed shells / Custom…), plus an
    // optional synthetic "Custom: <name>" item for a chosen exe path.
    private List<TerminalCatalog.Option> _baseOptions = new();

    public SettingsPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        // Read the version from the assembly so About always matches the build —
        // the single source is <Version> in RCMM.csproj.
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "Version" : $"Version {v.Major}.{v.Minor}.{v.Build}";

        _baseOptions = TerminalCatalog.OptionsFor(RunMode.VisibleTerminal, BinaryResolver.Find).ToList();

        var s = _settings.Load();
        _loadedRestartOnApply = s.RestartExplorerOnApply;
        // null = never chosen → resolve the preferred default (Windows Terminal if installed).
        _loadedDefaultTerminal = s.DefaultTerminal ?? TerminalCatalog.DefaultPreferred(BinaryResolver.Find);
        _loadedClassic = _ctxMode.IsClassic();
        ResetStagedToLoaded();
    }

    public bool IsDirty => _dirty;

    // ---- staging handlers ---------------------------------------------------

    private void RestartExplorer_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _stagedRestartOnApply = RestartExplorerToggle.IsOn;
        RecomputeDirty();
    }

    private void ContextMenu_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _stagedClassic = ContextMenuBox.SelectedIndex == 0;   // 0 = Classic, 1 = Win 11
        RecomputeDirty();
    }

    private async void DefaultTerminal_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (DefaultTerminalBox.SelectedItem is not TerminalCatalog.Option opt) return;

        if (opt.Value == TerminalCatalog.Custom) { await BrowseForTerminalAsync(); return; }

        if (TerminalEquals(opt.Value, _stagedDefaultTerminal)) return;   // no real change
        _stagedDefaultTerminal = opt.Value;
        RecomputeDirty();
        await MaybePromptApplyExistingAsync();
    }

    /// <summary>"Browse for .exe…" → file picker. A pick becomes a custom default; a
    /// cancel snaps the dropdown back to whatever was staged.</summary>
    private async Task BrowseForTerminalAsync()
    {
        string? path = null;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            // WinUI 3 unpackaged: pickers must be initialized against the window HWND.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            path = file?.Path;
        }
        catch (Exception ex) { Log.Error("settings", "terminal browse failed", ex); }

        if (string.IsNullOrEmpty(path)) { SelectTerminalValue(_stagedDefaultTerminal); return; }   // cancelled

        SetOptions(path);                 // adds + selects the "Custom: <name>" item
        if (TerminalEquals(path!, _stagedDefaultTerminal)) return;
        _stagedDefaultTerminal = path!;
        RecomputeDirty();
        await MaybePromptApplyExistingAsync();
    }

    /// <summary>Ask whether the new default should also rewrite existing entries.
    /// Only asked when the default actually differs from the last-committed one.</summary>
    private async Task MaybePromptApplyExistingAsync()
    {
        if (TerminalEquals(_stagedDefaultTerminal, _loadedDefaultTerminal)) { _stagedApplyToExisting = false; return; }

        var dialog = new ContentDialog
        {
            Title = "Set default terminal",
            Content = new TextBlock
            {
                Text = "Set this as the terminal for entries you've already added?\n\n" +
                       "Yes updates your existing terminal entries. No keeps them as they are — " +
                       "only new entries will use the new default.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Yes, update them",
            CloseButtonText = "No",
            XamlRoot = XamlRoot,
        };
        DialogTheme.Apply(dialog);
        _stagedApplyToExisting = await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---- Apply / Reset / leave-guard ---------------------------------------

    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyAsync();

    /// <summary>Commit every staged setting. Public so MainWindow's leave-guard can call it.</summary>
    public async Task ApplyAsync()
    {
        if (!_dirty) return;
        ApplyButton.IsEnabled = false;
        try
        {
            bool restartExplorer = false;

            // 1. Context-menu mode flips the legacy-menu registry; it always needs an
            //    Explorer restart to take visible effect (regardless of the toggle).
            if (_stagedClassic != _ctxMode.IsClassic())
            {
                _ctxMode.SetClassic(_stagedClassic);
                Log.Info("settings", $"context menu -> {(_stagedClassic ? "classic" : "win11")}");
                restartExplorer = true;
            }

            // 2. Persist app prefs.
            var s = _settings.Load();
            s.RestartExplorerOnApply = _stagedRestartOnApply;
            s.DefaultTerminal = _stagedDefaultTerminal;
            _settings.Save(s);

            // 3. Default terminal → existing entries (opt-in). Rewrite the in-memory
            //    additions, then reuse the normal apply path so they stay live.
            bool additionsTouched = false;
            var add = _args.ViewModel.AddPage;
            if (_stagedApplyToExisting && !TerminalEquals(_stagedDefaultTerminal, _loadedDefaultTerminal) && add != null)
            {
                var updated = TerminalDefaults.ApplyToExisting(add.Entries.ToList(), _stagedDefaultTerminal);
                for (int i = 0; i < updated.Count; i++)
                {
                    if (!ReferenceEquals(updated[i], add.Entries[i]))   // ApplyToExisting preserves order
                        add.ReplaceEntry(updated[i]);
                }
                additionsTouched = add.HasPendingChanges;
            }

            if (additionsTouched)
            {
                await Task.Run(() => _args.ViewModel.ApplyPending());   // writes registry + saves additions.json
                if (_stagedRestartOnApply) restartExplorer = true;
            }

            if (restartExplorer)
                await Task.Run(() => new ExplorerRestart().Restart());

            if (additionsTouched)
                await _args.ViewModel.RescanAsync();

            // Re-baseline: staged is now the committed state.
            _loadedClassic = _stagedClassic;
            _loadedRestartOnApply = _stagedRestartOnApply;
            _loadedDefaultTerminal = _stagedDefaultTerminal;
            _stagedApplyToExisting = false;
            _dirty = false;
            UpdateApplyBar();
        }
        catch (Exception ex) { Log.Error("settings", "apply failed", ex); }
        finally { ApplyButton.IsEnabled = _dirty; }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Reset the app preferences to their defaults (AppSettings defaults). The
        // context-menu mode is a system state, not an app default, so it's left alone.
        var def = new AppSettings();
        _initializing = true;
        _stagedRestartOnApply = def.RestartExplorerOnApply;
        RestartExplorerToggle.IsOn = _stagedRestartOnApply;
        // The default-terminal default is "not chosen" → the preferred terminal
        // (Windows Terminal if installed, else Command Prompt).
        _stagedDefaultTerminal = TerminalCatalog.DefaultPreferred(BinaryResolver.Find);
        SetOptions(null);
        SelectTerminalValue(_stagedDefaultTerminal);
        _initializing = false;
        _stagedApplyToExisting = false;
        RecomputeDirty();
    }

    /// <summary>Revert staged edits back to the last-committed state (Discard on leave).</summary>
    public void Revert() => ResetStagedToLoaded();

    /// <summary>Prompt when leaving with unsaved changes. Returns the user's choice.</summary>
    public async Task<SettingsLeaveChoice> ConfirmLeaveAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Unsaved settings",
            Content = new TextBlock
            {
                Text = "You have unsaved settings. Apply them before leaving?",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Apply",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        DialogTheme.Apply(dialog);
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => SettingsLeaveChoice.Apply,
            ContentDialogResult.Secondary => SettingsLeaveChoice.Discard,
            _ => SettingsLeaveChoice.Cancel,
        };
    }

    // ---- helpers ------------------------------------------------------------

    private void ResetStagedToLoaded()
    {
        _initializing = true;
        _stagedClassic = _loadedClassic;
        _stagedRestartOnApply = _loadedRestartOnApply;
        _stagedDefaultTerminal = _loadedDefaultTerminal;
        _stagedApplyToExisting = false;

        RestartExplorerToggle.IsOn = _stagedRestartOnApply;
        ContextMenuBox.SelectedIndex = _stagedClassic ? 0 : 1;

        bool isKnown = _baseOptions.Any(o => TerminalEquals(o.Value, _stagedDefaultTerminal));
        SetOptions(isKnown ? null : _stagedDefaultTerminal);
        SelectTerminalValue(_stagedDefaultTerminal);

        _initializing = false;
        _dirty = false;
        UpdateApplyBar();
    }

    /// <summary>Rebuild the terminal dropdown's items, optionally inserting a synthetic
    /// custom entry (a chosen exe path or a stored value not offered on this PC) just
    /// before "Custom…".</summary>
    private void SetOptions(string? customValue)
    {
        bool wasInit = _initializing;
        _initializing = true;
        var list = new List<TerminalCatalog.Option>(_baseOptions);   // ends with Custom…
        if (!string.IsNullOrEmpty(customValue))
        {
            bool looksPath = customValue!.Contains('\\') || customValue.Contains('/') ||
                             customValue.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var display = looksPath ? "Custom: " + Path.GetFileName(customValue) : customValue;
            list.Insert(list.Count - 1, new TerminalCatalog.Option(display, customValue));
        }
        DefaultTerminalBox.ItemsSource = list;
        _initializing = wasInit;
    }

    private void SelectTerminalValue(string value)
    {
        bool wasInit = _initializing;
        _initializing = true;
        var items = (List<TerminalCatalog.Option>)DefaultTerminalBox.ItemsSource;
        DefaultTerminalBox.SelectedItem =
            items.FirstOrDefault(o => TerminalEquals(o.Value, value)) ?? items.FirstOrDefault();
        _initializing = wasInit;
    }

    private void RecomputeDirty()
    {
        _dirty = _stagedClassic != _loadedClassic
              || _stagedRestartOnApply != _loadedRestartOnApply
              || !TerminalEquals(_stagedDefaultTerminal, _loadedDefaultTerminal);
        UpdateApplyBar();
    }

    private void UpdateApplyBar()
    {
        ApplyButton.IsEnabled = _dirty;
        DirtyHint.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Treats null/"" (Command Prompt) as equal; otherwise ordinal.</summary>
    private static bool TerminalEquals(string? a, string? b)
        => (string.IsNullOrEmpty(a) ? "" : a) == (string.IsNullOrEmpty(b) ? "" : b);

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RCMM", "logs");
            if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(dir)!;   // fall back to %LOCALAPPDATA%\RCMM
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }
}
