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

        // CLI mode: `RCMM.exe convert "<file>"` launches the lightweight
        // smart-actions converter window instead of the main app. This is how
        // the "Convert / Change format" right-click verb invokes RCMM (reusing
        // the one installed exe — no separate helper to ship).
        var argv = Environment.GetCommandLineArgs();
        if (argv.Length >= 3 && string.Equals(argv[1], "convert", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("convert", $"convert mode for '{argv[2]}'");
            _window = new Views.ConvertWindow(argv[2]);
            _window.Activate();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
