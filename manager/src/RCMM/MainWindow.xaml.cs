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

        ViewModel = new MainViewModel(capture, targets, mapper, hide, registry, files, shellexIndex, entryScanner, packagedScanner);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TryRemoveWindowBorder();
        _minSize = WindowMinSize.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), 600, 480);

        HookThemeChange();
        ViewModel.PropertyChanged += OnVmPropertyChanged;
        ViewModel.PendingChangeIds.CollectionChanged += (_, __) => RefreshFooter();
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
        StatusLabel.Text = $"{ViewModel.AllEntries.Count} entries · {ViewModel.PendingChangeIds.Count} pending";
        ApplyButton.IsEnabled = ViewModel.PendingChangeIds.Count > 0;
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

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Log.Folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("ui", "OpenLogButton failed", ex);
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
                var bmp = await IconHelper.LoadIconAsync(path);
                if (bmp != null)
                    DispatcherQueue.TryEnqueue(() => rowRef.Icon = bmp);
            });
        }
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
