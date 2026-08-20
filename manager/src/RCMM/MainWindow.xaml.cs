using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RCMM.Core.Diagnostics;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using RCMM.Util;
using RCMM.Views;

namespace RCMM;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private WindowMinSize? _minSize;
    private bool _busy;
    /// <summary>Serializes the apply → Explorer-restart → rescan orchestrations
    /// started from the footer button, the template-update dialog and the
    /// Settings page. MainViewModel's work gate only protects the shared VM
    /// state; without this gate two orchestrations interleave at the step
    /// level (two Explorer restarts, doubled rescans).</summary>
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private bool _templateUpdatesChecked;
    private bool _settingsLeaveGuard;

    public MainWindow()
    {
        InitializeComponent();

        var registry = new Win32Registry();
        var capture = new ContextMenuCaptureService();
        var targets = new TargetProvider();
        var mapper = new VerbToRegistryMapper(registry);
        var hide = new HideService(registry);
        var files = new Win32FileVersionReader();
        var mui = new Win32MuiStringResolver();
        var resolver = new ClsidResolver(registry);
        var shellexIndex = new ShellexNameIndex(registry, resolver, files);
        var verbScanner = new ClassicVerbScanner(registry, mui);
        var shellexScanner = new ClassicShellexScanner(registry, resolver, files);
        var entryScanner = new EntryScanner(verbScanner, shellexScanner);
        var packagedScanner = new PackagedShellExtScanner(registry, mui);
        var commandStore = new CommandStoreVerbIndex(registry);
        var shellexKeyIndex = new ShellexKeyNameIndex(registry);
        var shellexInvoker = new ShellexInvoker(registry, targets);

        var iconMaterializer = new IconMaterializer(IconMaterializer.DefaultDir());
        var additionApplier = new AdditionApplier(registry, iconMaterializer);
        var addStore = new AdditionStore(AdditionStore.DefaultPath());
        var addPage = new AddPageViewModel(addStore);
        addPage.Load();

        var cascadeProtector = new CascadeProtectionService(registry);

        ViewModel = new MainViewModel(capture, targets, mapper, hide, registry, files, shellexIndex, entryScanner, packagedScanner, commandStore, shellexKeyIndex, shellexInvoker, addPage, additionApplier, cascadeProtector,
            postToUi: action => DispatcherQueue.TryEnqueue(() => action()));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // The system caption buttons default to the compact (~32px) strip while
        // AppTitleBar is 48px, so our title-bar buttons — vertically centered in
        // the row — sat visibly LOWER than minimize/maximize/close. Tall makes
        // the system strip 48px too, giving both sets the same vertical center.
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        // SetTitleBar's automatic pass-through for interactive children is unreliable —
        // clicks on the nav / settings buttons get swallowed by the title bar's drag
        // region and need a second click. Explicitly mark each button's rectangle as a
        // pass-through (interactive) region so the first click always lands. Refreshed
        // on layout because the rects are in physical pixels and depend on size/DPI.
        AppTitleBar.Loaded += (_, __) => SetTitleBarInteractiveRegions();
        AppTitleBar.SizeChanged += (_, __) => SetTitleBarInteractiveRegions();
        TryRemoveWindowBorder();
        _minSize = WindowMinSize.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), 600, 480);
        CenterOnPrimaryDisplay();
        ApplyAppIcon();

        // Force dark mode — the new design is dark-only.
        RootGrid.RequestedTheme = ElementTheme.Dark;
        ViewModel.RescanComplete += () => DispatcherQueue.TryEnqueue(LoadIconsForAllEntries);
        ViewModel.PendingChangeIds.CollectionChanged += (_, __) => DispatcherQueue.TryEnqueue(UpdateFooterApply);
        if (ViewModel.AddPage != null)
        {
            ViewModel.AddPage.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AddPageViewModel.HasPendingChanges))
                    DispatcherQueue.TryEnqueue(UpdateFooterApply);
            };
        }
        // Fire-and-forget: RescanAsync never faults (it wraps the guarded Rescan),
        // so this can't leave an unobserved faulted Task. The window paints now and
        // rows populate when the scan's UI tail marshals back via _post/RescanComplete.
        // Rescan and ApplyPending serialize against each other via MainViewModel's work
        // gate, so this startup scan and the template-update dialog's Apply below can't
        // race the shared _allRows/_packaged* state even if the dialog is accepted first.
        _ = ViewModel.RescanAsync();
        UpdateFooterApply();

        ContentFrame.Navigated += (_, e) => {
            UpdateFooterApplyVisibility(e.SourcePageType);
            UpdateNavButtons();
        };
        ContentFrame.Navigating += ContentFrame_Navigating;
        ContentFrame.Navigate(typeof(LandingPage), new NavArgs(ViewModel));

        // Once the visual tree is up (XamlRoot ready), check whether any added
        // entries came from templates we've since updated, and offer to apply.
        // One-shot — RootGrid.Loaded can fire more than once.
        RootGrid.Loaded += async (_, __) =>
        {
            if (_templateUpdatesChecked) return;
            _templateUpdatesChecked = true;
            // additions.json unreadable at startup: warn before anything else. The
            // Add page shows a stub, and Apply refuses additions until a restart
            // reloads the file — the user must hear that from us, not discover it
            // after their edits appear to vanish.
            if (ViewModel.AddPage?.LoadFailed == true)
            {
                try
                {
                    await new ContentDialog
                    {
                        Title = "Your added entries could not be loaded",
                        Content = new TextBlock
                        {
                            Text = "additions.json exists but could not be read, so the Add page looks empty. "
                                 + "Your data is untouched (a backup copy was saved as additions.json.bad), and "
                                 + "Apply will refuse to write added entries this session to protect it.\n\n"
                                 + "Restart RCMM to retry loading.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        CloseButtonText = "OK",
                        XamlRoot = RootGrid.XamlRoot,
                        RequestedTheme = ElementTheme.Dark,
                    }.ShowAsync();
                }
                catch (Exception ex) { Log.Error("addstore", "load-failure warning dialog failed", ex); }
                return;   // skip the template-update offer — its Apply path is refused anyway
            }
            try { await TemplateUpdatesDialog.RunAsync(ViewModel, RootGrid.XamlRoot, manual: false); }
            catch (Exception ex) { Log.Error("tplupd", "startup template-update check failed", ex); }
        };
    }

    private void UpdateNavButtons()
    {
        NavBackButton.IsEnabled = ContentFrame.CanGoBack;
        NavHomeButton.IsEnabled = ContentFrame.CanGoBack;
    }

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void NavHome_Click(object sender, RoutedEventArgs e)
    {
        // Bounded walk to the back-stack root. A dirty Settings page cancels
        // navigation synchronously (ContentFrame_Navigating sets e.Cancel before
        // its first await), which leaves CanGoBack true — an unconditional
        // while (CanGoBack) GoBack() would then spin forever on the UI thread.
        // Stop as soon as a GoBack is refused (back-stack depth unchanged); the
        // leave-guard's dialog takes over from there.
        while (ContentFrame.CanGoBack)
        {
            int depth = ContentFrame.BackStack.Count;
            ContentFrame.GoBack();
            if (ContentFrame.BackStack.Count == depth) break;   // navigation cancelled
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
            ContentFrame.Navigate(typeof(SettingsPage), new NavArgs(ViewModel));
    }

    /// <summary>Guard navigation away from a dirty Settings page: cancel the move,
    /// ask Apply / Discard / Cancel, then re-issue the navigation per the choice. The
    /// re-issued navigation is let through by <see cref="_settingsLeaveGuard"/>.</summary>
    private async void ContentFrame_Navigating(object sender, Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
    {
        if (_settingsLeaveGuard) { _settingsLeaveGuard = false; return; }
        if (ContentFrame.Content is not SettingsPage sp || !sp.IsDirty) return;

        var target = e.SourcePageType;
        var param = e.Parameter;
        var mode = e.NavigationMode;
        e.Cancel = true;   // must be set before the first await

        // async void: an exception escaping this handler rethrows on the
        // dispatcher and kills the process (ContentDialog.ShowAsync can throw
        // COMException when another dialog is already open), so everything past
        // the first await must be guarded.
        try
        {
            var choice = await sp.ConfirmLeaveAsync();
            if (choice == SettingsLeaveChoice.Cancel) return;
            if (choice == SettingsLeaveChoice.Apply)
            {
                try { await sp.ApplyAsync(); }
                catch (Exception ex) { Log.Error("settings", "apply-on-leave failed", ex); }
            }
            else sp.Revert();

            _settingsLeaveGuard = true;
            if (mode == Microsoft.UI.Xaml.Navigation.NavigationMode.Back && ContentFrame.CanGoBack)
                ContentFrame.GoBack();
            else if (target != null)
                ContentFrame.Navigate(target, param);
        }
        catch (Exception ex) { Log.Error("settings", "leave-guard navigation failed", ex); }
    }

    private void UpdateFooterApplyVisibility(Type pageType)
    {
        bool needsApply = pageType == typeof(ScopePage) || pageType == typeof(AddPage);
        FooterApplyButton.Visibility = needsApply ? Visibility.Visible : Visibility.Collapsed;
        if (needsApply)
        {
            FooterContainer.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["FooterBackground"];
            FooterContainer.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorder"];
            FooterContainer.BorderThickness = new Thickness(0, 1, 0, 0);
        }
        else
        {
            FooterContainer.Background = null;
            FooterContainer.BorderBrush = null;
            FooterContainer.BorderThickness = new Thickness(0);
        }
    }

    private void UpdateFooterApply()
    {
        int n = ViewModel.PendingChangeIds.Count;
        bool addDirty = ViewModel.AddPage?.HasPendingChanges == true;
        // !_busy: a pending-count change mid-chain (ApplyPending clears the ids,
        // a row toggle re-adds one) must not re-enable the button while an
        // apply/restart/rescan orchestration is still running.
        FooterApplyButton.IsEnabled = !_busy && (n > 0 || addDirty);
        FooterApplyButton.Content = n > 0 ? $"Apply ({n})" : "Apply";
    }

    /// <summary>Run an apply → Explorer-restart → rescan orchestration under the
    /// shared gate. Chains queue rather than overlap, so two Explorer restarts
    /// can never run concurrently and rescans stay sequential. The footer button
    /// is disabled for the duration.</summary>
    public async Task RunApplyChainAsync(Func<Task> work)
    {
        await _applyGate.WaitAsync();
        _busy = true;
        UpdateFooterApply();
        try { await work(); }
        finally
        {
            _busy = false;
            UpdateFooterApply();
            _applyGate.Release();
        }
    }

    private async void FooterApply_Click(object sender, RoutedEventArgs e)
    {
        // _busy guards re-entry even if a click is requeued before the disabled
        // state propagates — a footer click during a running chain is dropped,
        // not queued.
        if (_busy) return;
        try
        {
            System.Collections.Generic.IReadOnlyList<string> errors = System.Array.Empty<string>();
            await RunApplyChainAsync(async () =>
            {
                errors = await Task.Run(() => ViewModel.ApplyPending());
                // Honor the "Restart Explorer automatically" setting. Off = write the
                // registry but leave Explorer alone (some changes then show only after
                // the user restarts Explorer or signs out).
                if (new SettingsStore().Load().RestartExplorerOnApply)
                    await Task.Run(() => new ExplorerRestart().Restart());
                await ViewModel.RescanAsync();
            });
            // After the chain (never inside it — the gate must release first):
            // a failed write used to vanish into rcmm.log while Explorer restarted
            // and the UI looked freshly applied.
            await ShowApplyErrorsAsync(errors);
        }
        catch (Exception ex) { Log.Error("apply", "apply/rescan failed", ex); }
    }

    /// <summary>Tell the user which apply operations failed. No-op on success.</summary>
    public async Task ShowApplyErrorsAsync(System.Collections.Generic.IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) return;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Some changes were not applied",
                Content = new TextBlock { Text = string.Join("\n\n", errors), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };
            await dialog.ShowAsync();
        }
        // ShowAsync throws COMException when another dialog is already open; the
        // errors are still in rcmm.log, so degrade to logging rather than crashing.
        catch (Exception ex) { Log.Error("apply", "apply-error dialog failed", ex); }
    }

private void LoadIconsForAllEntries()
    {
        foreach (var row in ViewModel.AllEntries)
        {
            var rowRef = row;
            var bytes = row.Entry.IconBytes;
            if (bytes != null && bytes.Length > 0)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var bmp = await IconPainter.DecodeAsync(bytes);
                    if (bmp != null) rowRef.Icon = bmp;
                });
                continue;
            }

            var path = row.Entry.IconPath;
            if (string.IsNullOrEmpty(path)) continue;
            _ = Task.Run(async () =>
            {
                var pngBytes = await IconHelper.LoadIconBytesAsync(path);
                if (pngBytes == null) return;
                // BitmapImage is COM-tied to the dispatcher that created it; build
                // it on the UI thread (IconPainter logs decode failures).
                DispatcherQueue.TryEnqueue(async () =>
                {
                    var bmp = await IconPainter.DecodeAsync(pngBytes);
                    if (bmp != null) rowRef.Icon = bmp;
                });
            });
        }
    }

    private void ApplyAppIcon()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath)) appWindow?.SetIcon(icoPath);
        }
        catch (Exception ex) { Log.Error("ui", "ApplyAppIcon failed", ex); }
    }

    private void CenterOnPrimaryDisplay()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        if (appWindow == null || display == null) return;
        int x = display.WorkArea.X + (display.WorkArea.Width - appWindow.Size.Width) / 2;
        int y = display.WorkArea.Y + (display.WorkArea.Height - appWindow.Size.Height) / 2;
        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    /// <summary>Declare the title-bar buttons' rectangles as pass-through (interactive)
    /// regions so the window's non-client hit-testing delivers clicks to them on the
    /// first press instead of swallowing it. Rects are physical pixels (scaled by the
    /// rasterization scale), so this must re-run whenever the title bar lays out.</summary>
    private void SetTitleBarInteractiveRegions()
    {
        if (AppTitleBar.XamlRoot is null) return;
        double scale = AppTitleBar.XamlRoot.RasterizationScale;
        var buttons = new FrameworkElement[] { NavBackButton, NavHomeButton, SettingsButton };
        var rects = new System.Collections.Generic.List<Windows.Graphics.RectInt32>(buttons.Length);
        foreach (var b in buttons)
        {
            if (b.ActualWidth <= 0 || b.ActualHeight <= 0) continue;
            var bounds = b.TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight));
            rects.Add(new Windows.Graphics.RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }
        if (rects.Count == 0) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var src = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(windowId);
            src.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception ex) { Log.Error("ui", "title-bar passthrough regions failed", ex); }
    }

    private void TryRemoveWindowBorder()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint color = Win32.DWMWA_COLOR_NONE;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref color, sizeof(uint));
        }
        catch { }
    }

}
