using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RCMM.Core.Diagnostics;
using RCMM.Core.Services;

namespace RCMM.Views;

/// <summary>
/// Lightweight "smart action" converter window, launched via
/// <c>RCMM.exe convert "&lt;file&gt;"</c>. Detects the file type, offers the
/// valid target formats, resolves the external tool (ImageMagick / ffmpeg) and
/// offers a winget install if it's missing, then runs the conversion. The
/// type/target/command logic lives in <see cref="FormatConverter"/> (unit-tested);
/// this window owns the UI and process execution.
/// </summary>
public sealed partial class ConvertWindow : Window
{
    private readonly string _file;
    private ConvertPlan? _plan;
    private ConvertTarget? _pending;

    public ConvertWindow(string filePath)
    {
        InitializeComponent();
        _file = filePath;
        RootGrid.RequestedTheme = ElementTheme.Dark;
        Title = "Convert";
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 300)); } catch { }
        Build();
    }

    private void Build()
    {
        FileText.Text = Path.GetFileName(_file);
        _plan = FormatConverter.PlanFor(_file);
        if (_plan == null || _plan.Targets.Count == 0)
        {
            PromptText.Text = "RCMM can't convert this file type yet.";
            return;
        }
        PromptText.Text = "Convert to:";
        foreach (var target in _plan.Targets)
        {
            var btn = new Button
            {
                Content = target.Label,
                Tag = target,
                Style = (Style)Application.Current.Resources["SurfaceButton"],
            };
            btn.Click += Target_Click;
            TargetsPanel.Children.Add(btn);
        }
    }

    private async void Target_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || sender is not Button b || b.Tag is not ConvertTarget target) return;
        _pending = target;
        SetBusy(true);
        Status($"Looking for {_plan.Tool}…");
        var toolPath = await Task.Run(() => BinaryResolver.Find(_plan.Tool + ".exe", null));
        if (toolPath == null)
        {
            Status($"{_plan.Tool} isn't installed.");
            InstallButton.Content = $"Install {_plan.Tool}";
            InstallButton.Visibility = Visibility.Visible;
            SetBusy(false);
            return;
        }
        await RunConversion(toolPath, target);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _pending == null) return;
        InstallButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        Status($"Installing {_plan.Tool} via winget… (a UAC prompt may appear)");
        int code = await Task.Run(() => RunProcess("winget",
            $"install --id {_plan.WingetId} -e --accept-source-agreements --accept-package-agreements"));
        if (code != 0)
        {
            Status($"winget install failed (exit {code}). Install {_plan.Tool} manually, then retry.");
            SetBusy(false);
            return;
        }
        RefreshPathFromRegistry();
        var toolPath = await Task.Run(() => BinaryResolver.Find(_plan.Tool + ".exe", null));
        if (toolPath == null)
        {
            Status($"Installed {_plan.Tool}, but it isn't on PATH in this session yet — re-run the convert action.");
            SetBusy(false);
            return;
        }
        await RunConversion(toolPath, _pending);
    }

    private async Task RunConversion(string toolPath, ConvertTarget target)
    {
        SetBusy(true);
        var output = FormatConverter.OutputPathFor(_file, target);
        Status($"Converting → {Path.GetFileName(output)}…");
        var args = FormatConverter.BuildArguments(_plan!, _file, target);
        int code = await Task.Run(() => RunProcess(toolPath, args));
        if (code == 0 && File.Exists(output))
        {
            Status($"Done → {Path.GetFileName(output)}");
            try { Process.Start("explorer.exe", $"/select,\"{output}\""); } catch { }
        }
        else
        {
            Status($"Conversion failed (exit {code}). See %LOCALAPPDATA%\\RCMM\\logs.");
        }
        SetBusy(false);
    }

    private static int RunProcess(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            string err = p.StandardError.ReadToEnd();
            _ = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) Log.Error("convert", $"{Path.GetFileName(exe)} exit {p.ExitCode}: {err.Trim()}");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("convert", $"failed to run {exe}", ex);
            return -1;
        }
    }

    // winget updates the machine/user PATH, but this process captured PATH at
    // launch — re-read it so a freshly-installed tool resolves without a restart.
    private static void RefreshPathFromRegistry()
    {
        try
        {
            var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? string.Empty;
            var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
            Environment.SetEnvironmentVariable("Path", $"{machine};{user}");
        }
        catch (Exception ex) { Log.Warn("convert", $"PATH refresh failed: {ex.Message}"); }
    }

    private void SetBusy(bool busy)
    {
        foreach (var child in TargetsPanel.Children)
            if (child is Button b) b.IsEnabled = !busy;
    }

    private void Status(string text)
    {
        if (DispatcherQueue.HasThreadAccess) StatusText.Text = text;
        else DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
    }
}
