using System;
using Microsoft.UI.Xaml;
using RCMM.Core.Diagnostics;

namespace RCMM;

public partial class App : Application
{
    private Window? _window;

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Info("app", $"launched os={Environment.OSVersion} 64bit={Environment.Is64BitProcess}");
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Error("app", "unhandled exception", e.ExceptionObject as Exception);
        _window = new MainWindow();
        _window.Activate();
    }
}
