using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Xml.Linq;
using RCMM.Core.Diagnostics;

namespace RCMM.Core.Services;

/// <summary>
/// Renders library icons (the <c>lib:&lt;name&gt;</c> values produced by the
/// icon picker) into actual <c>.ico</c> files on disk that the Windows shell
/// can show in the right-click menu. Without this, the registry's <c>Icon</c>
/// value contains <c>lib:terminal</c> which Explorer can't resolve, so the
/// menu falls back to a blank document icon.
///
/// Output: <c>%LOCALAPPDATA%\RCMM\icons\&lt;name&gt;.ico</c>, with embedded PNG
/// images at multiple sizes (16/24/32/48/256). Cached on disk — a generated
/// file is only re-rendered if missing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IconMaterializer
{
    private const string Cat = "iconmat";
    private readonly string _dir;

    public IconMaterializer(string dir) { _dir = dir; }

    public static string DefaultDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "RCMM", "icons");

    /// <summary>If <paramref name="iconValue"/> is a library reference,
    /// ensure the .ico file exists on disk and return its path. Otherwise
    /// pass through verbatim (custom DLL,index or .ico paths).</summary>
    public string? Materialize(string? iconValue)
    {
        if (string.IsNullOrWhiteSpace(iconValue)) return iconValue;
        if (!IconLibrary.IsLibraryName(iconValue)) return iconValue;
        var name = IconLibrary.StripPrefix(iconValue);
        if (string.IsNullOrEmpty(name)) return null;
        var raw = IconLibrary.RawSvgFragment(name);
        if (raw == null) { Log.Warn(Cat, $"unknown library icon: {name}"); return null; }
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, name + ".ico");
            if (!File.Exists(path)) Render(name, raw, path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"Materialize {name} failed", ex);
            return null;
        }
    }

    private static readonly int[] _sizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static void Render(string iconName, string svgFragment, string outPath)
    {
        Log.Info(Cat, $"rendering {Path.GetFileName(outPath)}");
        bool filled = IconLibrary.IsFilled(iconName);
        var pngs = new List<(int size, byte[] data)>(_sizes.Length);
        foreach (var size in _sizes)
        {
            using var bmp = RenderToBitmap(svgFragment, size, filled);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs.Add((size, ms.ToArray()));
        }
        WriteIcoFile(outPath, pngs);
    }

    /// <summary>Render the SVG fragment to a transparent ARGB bitmap.
    /// Lucide outline icons are stroked at 1.75px on a 24x24 viewBox; brand
    /// marks (claude, openai) are filled solids. White ink either way so the
    /// glyph is legible against any Explorer menu background — Windows uses
    /// only the alpha channel for compositing.</summary>
    private static Bitmap RenderToBitmap(string svgFragment, int size, bool filled)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        float scale = size / 24f;
        g.ScaleTransform(scale, scale);

        using var pen = new Pen(Color.White, 1.75f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        using var fillBrush = new SolidBrush(Color.White);

        var doc = XDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg'>" + svgFragment + "</svg>");
        foreach (var el in doc.Root!.Elements())
        {
            using var path = ToGraphicsPath(el);
            if (path == null) continue;
            if (filled)
            {
                // SVG default is fill-rule=nonzero; GDI+ defaults to Alternate
                // (even-odd), so set Winding explicitly. Brand marks rely on
                // nonzero for sub-shapes to fill correctly.
                path.FillMode = FillMode.Winding;
                g.FillPath(fillBrush, path);
            }
            else
            {
                g.DrawPath(pen, path);
            }
        }
        return bmp;
    }

    private static GraphicsPath? ToGraphicsPath(XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "line":
            {
                var p = new GraphicsPath();
                p.AddLine(D(el, "x1"), D(el, "y1"), D(el, "x2"), D(el, "y2"));
                return p;
            }
            case "rect":
            {
                float x = D(el, "x"), y = D(el, "y"), w = D(el, "width"), h = D(el, "height");
                float rx = D(el, "rx", 0), ry = D(el, "ry", rx);
                var p = new GraphicsPath();
                if (rx <= 0 && ry <= 0)
                {
                    p.AddRectangle(new RectangleF(x, y, w, h));
                }
                else
                {
                    float dx = rx * 2, dy = ry * 2;
                    p.AddArc(x, y, dx, dy, 180, 90);
                    p.AddLine(x + rx, y, x + w - rx, y);
                    p.AddArc(x + w - dx, y, dx, dy, 270, 90);
                    p.AddLine(x + w, y + ry, x + w, y + h - ry);
                    p.AddArc(x + w - dx, y + h - dy, dx, dy, 0, 90);
                    p.AddLine(x + w - rx, y + h, x + rx, y + h);
                    p.AddArc(x, y + h - dy, dx, dy, 90, 90);
                    p.AddLine(x, y + h - ry, x, y + ry);
                    p.CloseFigure();
                }
                return p;
            }
            case "circle":
            {
                float cx = D(el, "cx"), cy = D(el, "cy"), r = D(el, "r");
                var p = new GraphicsPath();
                p.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                return p;
            }
            case "ellipse":
            {
                float cx = D(el, "cx"), cy = D(el, "cy"), rx = D(el, "rx"), ry = D(el, "ry");
                var p = new GraphicsPath();
                p.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
                return p;
            }
            case "polyline":
            case "polygon":
            {
                var pts = ParsePoints(el.Attribute("points")?.Value ?? "");
                if (pts.Length < 2) return null;
                var p = new GraphicsPath();
                p.StartFigure();
                for (int i = 0; i < pts.Length - 1; i++)
                {
                    p.AddLine(pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y);
                }
                if (el.Name.LocalName == "polygon") p.CloseFigure();
                return p;
            }
            case "path":
            {
                return SvgPathParser.Parse(el.Attribute("d")?.Value ?? "");
            }
        }
        return null;
    }

    private static float D(XElement el, string attr, float def = 0)
    {
        var v = (string?)el.Attribute(attr);
        return string.IsNullOrEmpty(v) ? def : float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (float x, float y)[] ParsePoints(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return Array.Empty<(float, float)>();
        var nums = s.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pts = new (float, float)[nums.Length / 2];
        for (int i = 0; i + 1 < nums.Length; i += 2)
        {
            pts[i / 2] = (
                float.Parse(nums[i],     System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(nums[i + 1], System.Globalization.CultureInfo.InvariantCulture));
        }
        return pts;
    }

    /// <summary>Write a multi-image ICO file. Each entry holds a full PNG —
    /// supported by Windows Vista+. Layout:
    /// <c>ICONDIR (6 bytes) | ICONDIRENTRY * N (16 bytes each) | image data</c>.
    /// </summary>
    private static void WriteIcoFile(string path, IReadOnlyList<(int size, byte[] png)> imgs)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // ICONDIR header
        bw.Write((ushort)0);          // reserved
        bw.Write((ushort)1);          // type = 1 (icon)
        bw.Write((ushort)imgs.Count); // image count

        int headerSize = 6 + 16 * imgs.Count;
        int offset = headerSize;
        for (int i = 0; i < imgs.Count; i++)
        {
            var (size, png) = imgs[i];
            bw.Write((byte)(size == 256 ? 0 : size)); // width  (0 ⇒ 256)
            bw.Write((byte)(size == 256 ? 0 : size)); // height (0 ⇒ 256)
            bw.Write((byte)0);                        // palette count
            bw.Write((byte)0);                        // reserved
            bw.Write((ushort)1);                      // color planes
            bw.Write((ushort)32);                     // bits per pixel
            bw.Write((uint)png.Length);               // image size
            bw.Write((uint)offset);                   // image offset
            offset += png.Length;
        }
        for (int i = 0; i < imgs.Count; i++) bw.Write(imgs[i].png);
    }
}
