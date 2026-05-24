using System;
using Microsoft.UI.Xaml;
using RCMM.Core.Diagnostics;

namespace RCMM;

public partial class App : Application
{
    private Window? _window;

    /// <summary>The single top-level window. Exposed so pages can obtain the HWND
    /// that WinUI 3 file/folder pickers must be initialized against (they have no
    /// implicit owner in an unpackaged app).</summary>
    public static Window? MainWindow { get; private set; }

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Info("app", $"launched os={Environment.OSVersion} 64bit={Environment.Is64BitProcess}");
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Error("app", "unhandled exception", e.ExceptionObject as Exception);
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }
}
