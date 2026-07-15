using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace RCMM.Views;

public sealed partial class LandingPage : Page
{
    private NavArgs _args = null!;

    public LandingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        // Read now (covers the case where the rescan already finished) AND
        // subscribe, because the startup rescan runs async and usually finishes
        // *after* this page first renders — without the event the donut would
        // sit at 0 until the user navigated away and back. RescanComplete is
        // raised on the UI thread (see MainViewModel), so this is safe.
        RefreshCounts();
        _args.ViewModel.RescanComplete += RefreshCounts;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_args?.ViewModel != null) _args.ViewModel.RescanComplete -= RefreshCounts;
    }

    private void RefreshCounts()
    {
        var vm = _args.ViewModel;
        int total = vm.AllEntries.Count;
        int builtin = vm.AllEntries.Count(r => r.IsBuiltIn);
        int app = total - builtin;

        ShowHideCount.Text = total.ToString();
        TotalNumber.Text = total.ToString();
        AppCountLabel.Text = app.ToString();
        WinCountLabel.Text = builtin.ToString();
        DrawDonut(app, builtin);

        AddCountLabel.Text = (vm.AddPage?.Entries.Count ?? 0).ToString();
    }

    private void DrawDonut(int app, int win)
    {
        int total = app + win;
        if (total == 0) { AppArcPath.Data = null; WinArcPath.Data = null; return; }

        const double cx = 60, cy = 60, rOuter = 54, rInner = 36;
        double appFrac = (double)app / total;
        double splitAngle = appFrac * 2 * Math.PI;

        // App segment: 0 to splitAngle
        AppArcPath.Data = BuildDonutSegment(cx, cy, rOuter, rInner, 0, splitAngle);
        // Win segment: splitAngle to 2π
        WinArcPath.Data = BuildDonutSegment(cx, cy, rOuter, rInner, splitAngle, 2 * Math.PI);
    }

    private static Geometry BuildDonutSegment(double cx, double cy, double rOuter, double rInner,
                                              double startAngle, double endAngle)
    {
        // 0 at top, clockwise
        double a1 = startAngle - Math.PI / 2;
        double a2 = endAngle - Math.PI / 2;
        bool isLargeArc = (endAngle - startAngle) > Math.PI;

        // Full circle: render two halves as a single donut without an angular gap
        if (Math.Abs(endAngle - startAngle - 2 * Math.PI) < 1e-6)
        {
            var pg = new PathGeometry();
            pg.Figures.Add(MakeFullRing(cx, cy, rOuter, isOuter: true));
            pg.Figures.Add(MakeFullRing(cx, cy, rInner, isOuter: false));
            return pg;
        }

        var p1 = new Point(cx + rOuter * Math.Cos(a1), cy + rOuter * Math.Sin(a1));
        var p2 = new Point(cx + rOuter * Math.Cos(a2), cy + rOuter * Math.Sin(a2));
        var p3 = new Point(cx + rInner * Math.Cos(a2), cy + rInner * Math.Sin(a2));
        var p4 = new Point(cx + rInner * Math.Cos(a1), cy + rInner * Math.Sin(a1));

        var figure = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment { Point = p2, Size = new Size(rOuter, rOuter), SweepDirection = SweepDirection.Clockwise, IsLargeArc = isLargeArc });
        figure.Segments.Add(new LineSegment { Point = p3 });
        figure.Segments.Add(new ArcSegment { Point = p4, Size = new Size(rInner, rInner), SweepDirection = SweepDirection.Counterclockwise, IsLargeArc = isLargeArc });

        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        return geom;
    }

    private static PathFigure MakeFullRing(double cx, double cy, double r, bool isOuter)
    {
        // Two half-arcs to draw a full circle as a closed figure
        var top = new Point(cx, cy - r);
        var bottom = new Point(cx, cy + r);
        var dir = isOuter ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        var f = new PathFigure { StartPoint = top, IsClosed = true, IsFilled = true };
        f.Segments.Add(new ArcSegment { Point = bottom, Size = new Size(r, r), SweepDirection = dir, IsLargeArc = false });
        f.Segments.Add(new ArcSegment { Point = top, Size = new Size(r, r), SweepDirection = dir, IsLargeArc = false });
        return f;
    }

    private void ShowHide_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ScopePage), _args);
    }

    private void AddToMenu_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(AddPage), _args);
    }
}
