using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    public MainWindow()
    {
        InitializeComponent();

        var registry = new Win32Registry();
        var resolver = new ClsidResolver(registry);
        var scanner = new EntryScanner(
            new ClassicVerbScanner(registry),
            new ClassicShellexScanner(registry, resolver));
        var hide = new HideService(registry);
        ViewModel = new MainViewModel(scanner, hide);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TryRemoveWindowBorder();
        _minSize = WindowMinSize.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), 600, 480);

        HookThemeChange();
        ViewModel.PropertyChanged += OnVmPropertyChanged;
        ViewModel.PendingChanges.CollectionChanged += (_, __) => RefreshFooter();
        ViewModel.Rescan();
        RefreshFooter();

        ContentFrame.Navigate(typeof(LandingPage), ViewModel);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RequiresExplorerRestart))
            RefreshFooter();
    }

    private void RefreshFooter()
    {
        var total = 0;
        foreach (var scope in new[] {
            RCMM.Core.Models.Scope.Files,
            RCMM.Core.Models.Scope.Folders,
            RCMM.Core.Models.Scope.Drives,
            RCMM.Core.Models.Scope.Background })
        {
            total += ViewModel.GetScope(scope).Entries.Count;
        }
        StatusLabel.Text = $"{total} entries · {ViewModel.PendingChanges.Count} pending";
        ApplyButton.IsEnabled = ViewModel.PendingChanges.Count > 0;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var needsRestart = ViewModel.RequiresExplorerRestart;
        ViewModel.ApplyPending();
        if (needsRestart) new ExplorerRestart().Restart();
        ViewModel.Rescan();
        RefreshFooter();
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
