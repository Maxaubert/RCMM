using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RCMM.Core.Diagnostics;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using RCMM.Util;
using RCMM.Views;
using Windows.UI;

namespace RCMM;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private Windows.UI.ViewManagement.UISettings? _uiSettings;
    private WindowMinSize? _minSize;
    private bool _busy;
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
        // NOTE: rescans are intentionally NOT protected by a busy guard. That is safe
        // only because the Apply button — the sole other rescan trigger — stays
        // disabled until the first rescan populates PendingChangeIds, so two rescans
        // can't run concurrently against the shared _allRows/_packaged* state. A future
        // "Refresh" trigger would need a real busy/IsBusy guard.
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
        while (ContentFrame.CanGoBack) ContentFrame.GoBack();
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
        FooterApplyButton.IsEnabled = n > 0 || addDirty;
        FooterApplyButton.Content = n > 0 ? $"Apply ({n})" : "Apply";
    }

    private async void FooterApply_Click(object sender, RoutedEventArgs e)
    {
        // _busy guards re-entry even if a click is requeued before the disabled
        // state propagates; the FooterApplyButton.IsEnabled = false below is the
        // visual half of the same guard.
        if (_busy) return;
        _busy = true;
        FooterApplyButton.IsEnabled = false;
        try
        {
            await Task.Run(() => ViewModel.ApplyPending());
            // Honor the "Restart Explorer automatically" setting. Off = write the
            // registry but leave Explorer alone (some changes then show only after
            // the user restarts Explorer or signs out).
            if (new SettingsStore().Load().RestartExplorerOnApply)
                await Task.Run(() => new ExplorerRestart().Restart());
            await ViewModel.RescanAsync();
        }
        catch (Exception ex) { Log.Error("apply", "apply/rescan failed", ex); }
        finally
        {
            _busy = false;
            UpdateFooterApply();
        }
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
                    try
                    {
                        using var ms = new System.IO.MemoryStream(bytes);
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                        rowRef.Icon = bmp;
                    }
                    catch { }
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
                // it on the UI thread to avoid silent cross-thread marshalling errors.
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        using var ms = new System.IO.MemoryStream(pngBytes);
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                        rowRef.Icon = bmp;
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("ui", $"icon load failed for {rowRef.Entry.DisplayName}: {ex.Message}");
                    }
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

    private void HookThemeChange()
    {
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (s, a) =>
                DispatcherQueue.TryEnqueue(UpdateForCurrentTheme);
            UpdateForCurrentTheme();
        }
        catch { }
    }

    private void UpdateForCurrentTheme()
    {
        if (_uiSettings is null) return;
        var bg = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        bool isDark = (bg.R + bg.G + bg.B) < 384;
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
    }
}
