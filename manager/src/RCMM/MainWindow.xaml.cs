using System;
using System.IO;
using System.Threading.Tasks;
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
        var files = new Win32FileVersionReader();
        var mui = new Win32MuiStringResolver();
        var scanner = new EntryScanner(
            new ClassicVerbScanner(registry, mui),
            new ClassicShellexScanner(registry, resolver, files));
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
        LoadIconsForAllEntries();
        RefreshFooter();

        ContentFrame.Navigate(typeof(ScopePage), ViewModel);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RequiresExplorerRestart))
            RefreshFooter();
    }

    private void RefreshFooter()
    {
        StatusLabel.Text = $"{ViewModel.AllEntries.Count} entries · {ViewModel.PendingChanges.Count} pending";
        ApplyButton.IsEnabled = ViewModel.PendingChanges.Count > 0;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var needsRestart = ViewModel.RequiresExplorerRestart;
        ViewModel.ApplyPending();
        if (needsRestart) new ExplorerRestart().Restart();
        ViewModel.Rescan();
        LoadIconsForAllEntries();
        RefreshFooter();
    }

    private void LoadIconsForAllEntries()
    {
        foreach (var row in ViewModel.AllEntries)
        {
            var path = row.Entry.IconPath ?? ExtractExeFromCommand(row.Entry.CommandLine);
            if (string.IsNullOrEmpty(path)) continue;
            var rowRef = row;
            _ = Task.Run(async () =>
            {
                var bmp = await IconHelper.LoadIconAsync(path);
                if (bmp != null)
                {
                    DispatcherQueue.TryEnqueue(() => rowRef.Icon = bmp);
                }
            });
        }
    }

    private static string? ExtractExeFromCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 1) return cmd[1..end];
        }
        var space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
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
