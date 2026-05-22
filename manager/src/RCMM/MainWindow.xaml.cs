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
        ContentFrame.Navigate(typeof(LandingPage), new NavArgs(ViewModel));
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
