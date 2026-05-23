using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Lucide-style outline icons bundled with RCMM. Icons are stored as raw SVG
/// child fragments (lines / rects / polylines / paths) and converted on first
/// resolve to a single Geometry-friendly Path Data string. The UI layer parses
/// that string into a Microsoft.UI.Xaml.Media.Geometry via XamlReader and
/// renders it through a <c>Microsoft.UI.Xaml.Shapes.Path</c>.
///
/// Storage convention: an AdditionEntry/Folder's Icon field holds either
///   - <c>lib:&lt;name&gt;</c> — a library icon resolved here, or
///   - a raw Windows path / <c>path,index</c> string — passed through verbatim.
/// </summary>
public static class IconLibrary
{
    private const string LibPrefix = "lib:";

    // ----- Catalogue -----

    public static readonly IReadOnlyList<IconCategory> Categories = new[]
    {
        new IconCategory("Shells & code",       new[] { "terminal","terminal-square","command","code","code-square" }),
        new IconCategory("Files & folders",     new[] { "folder","folder-open","folder-tree","file","file-text","files","archive" }),
        new IconCategory("Editing",             new[] { "copy","clipboard","scissors","trash","edit","pen","search" }),
        new IconCategory("Tools",               new[] { "wrench","hammer","settings","cog","sparkles" }),
        new IconCategory("Run & power",         new[] { "play","zap","rocket","power" }),
        new IconCategory("Identity & security", new[] { "shield","lock","key","user" }),
        new IconCategory("Programming",         new[] { "github","git-branch","package","database" }),
        new IconCategory("AI",                  new[] { "claude","openai" }),
        new IconCategory("Misc",                new[] { "hash","link","external-link","globe","download","upload","eye","refresh","repeat","monitor" }),
    };

    /// <summary>
    /// Icons whose source SVGs are filled shapes rather than Lucide-style
    /// strokes. Renderers (both the WinUI Path in the picker and the GDI+
    /// path in IconMaterializer) check this flag and switch from
    /// Stroke+StrokeThickness to Fill / FillPath. Brand marks like Claude
    /// and OpenAI live here.
    /// </summary>
    private static readonly HashSet<string> _filled = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "openai",
    };

    public static bool IsFilled(string name) => _filled.Contains(name);

    public static IReadOnlyList<string> AllNames { get; } =
        Categories.SelectMany(c => c.Icons).Distinct().ToList();

    // ----- Raw SVG fragments (24x24 viewBox, Lucide outline style) -----
    // Each value is the children of an SVG element. Multiple shapes per icon
    // are combined into one PathGeometry string by SvgFragmentToPathData().

    private static readonly Dictionary<string, string> _svg = new(StringComparer.OrdinalIgnoreCase)
    {
        ["terminal"]        = "<polyline points='4 17 10 11 4 5'/><line x1='12' y1='19' x2='20' y2='19'/>",
        ["terminal-square"] = "<path d='M 7 11 l 3 -3 l -3 -3'/><line x1='13' y1='8' x2='17' y2='8'/><rect x='3' y='3' width='18' height='18' rx='2'/>",
        ["command"]         = "<path d='M 18 3 a 3 3 0 0 0 -3 3 v 12 a 3 3 0 0 0 3 3 a 3 3 0 0 0 3 -3 a 3 3 0 0 0 -3 -3 H 6 a 3 3 0 0 0 -3 3 a 3 3 0 0 0 3 3 a 3 3 0 0 0 3 -3 V 6 a 3 3 0 0 0 -3 -3 a 3 3 0 0 0 -3 3 a 3 3 0 0 0 3 3 h 12 a 3 3 0 0 0 3 -3 a 3 3 0 0 0 -3 -3 z'/>",
        ["code"]            = "<polyline points='16 18 22 12 16 6'/><polyline points='8 6 2 12 8 18'/>",
        ["code-square"]     = "<rect x='3' y='3' width='18' height='18' rx='2'/><path d='M 10 9 l -3 3 l 3 3'/><path d='M 14 9 l 3 3 l -3 3'/>",

        ["folder"]          = "<path d='M 20 20 a 2 2 0 0 0 2 -2 V 8 a 2 2 0 0 0 -2 -2 h -7.93 a 2 2 0 0 1 -1.66 -0.9 l -0.82 -1.2 A 2 2 0 0 0 7.93 3 H 4 a 2 2 0 0 0 -2 2 v 13 a 2 2 0 0 0 2 2 Z'/>",
        ["folder-open"]     = "<path d='M 6 14 L 4.5 19 a 2 2 0 0 0 1.9 2.5 h 11.2 a 2 2 0 0 0 1.9 -1.4 L 22 11 H 7 a 2 2 0 0 0 -1.9 1.4 Z'/><path d='M 2 8 a 2 2 0 0 1 2 -2 h 3.93 a 2 2 0 0 1 1.66 0.9 l 0.82 1.2 a 2 2 0 0 0 1.66 0.9 H 18 a 2 2 0 0 1 2 2 v 2'/>",
        ["folder-tree"]     = "<path d='M 20 10 a 1 1 0 0 0 1 -1 V 6 a 1 1 0 0 0 -1 -1 h -2.5 a 1 1 0 0 1 -0.8 -0.4 l -0.9 -1.2 A 1 1 0 0 0 15 3 h -2 a 1 1 0 0 0 -1 1 v 5 a 1 1 0 0 0 1 1 Z'/><path d='M 20 21 a 1 1 0 0 0 1 -1 v -3 a 1 1 0 0 0 -1 -1 h -2.9 a 1 1 0 0 1 -0.88 -0.55 l -0.42 -0.85 a 1 1 0 0 0 -0.92 -0.6 H 13 a 1 1 0 0 0 -1 1 v 5 a 1 1 0 0 0 1 1 Z'/><path d='M 3 5 a 2 2 0 0 0 2 2 h 3'/><path d='M 3 3 v 13 a 2 2 0 0 0 2 2 h 3'/>",
        ["file"]            = "<path d='M 14 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 8 z'/><polyline points='14 2 14 8 20 8'/>",
        ["file-text"]       = "<path d='M 14 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 8 z'/><polyline points='14 2 14 8 20 8'/><line x1='16' y1='13' x2='8' y2='13'/><line x1='16' y1='17' x2='8' y2='17'/><polyline points='10 9 9 9 8 9'/>",
        ["files"]           = "<path d='M 15.5 2 H 8.6 c -0.4 0 -0.8 0.2 -1.1 0.5 c -0.3 0.3 -0.5 0.7 -0.5 1.1 v 12.8 c 0 0.4 0.2 0.8 0.5 1.1 c 0.3 0.3 0.7 0.5 1.1 0.5 h 9.8 c 0.4 0 0.8 -0.2 1.1 -0.5 c 0.3 -0.3 0.5 -0.7 0.5 -1.1 V 6.5 L 15.5 2 z'/><path d='M 3 7.6 v 12.8 c 0 0.4 0.2 0.8 0.5 1.1 c 0.3 0.3 0.7 0.5 1.1 0.5 h 9.8'/><path d='M 15 2 v 5 h 5'/>",
        ["archive"]         = "<rect x='2' y='3' width='20' height='5' rx='1'/><path d='M 4 8 v 11 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 V 8'/><path d='M 10 12 h 4'/>",

        ["copy"]             = "<rect x='9' y='9' width='13' height='13' rx='2'/><path d='M 5 15 H 4 a 2 2 0 0 1 -2 -2 V 4 a 2 2 0 0 1 2 -2 h 9 a 2 2 0 0 1 2 2 v 1'/>",
        ["clipboard"]        = "<rect x='8' y='2' width='8' height='4' rx='1'/><path d='M 16 4 h 2 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 H 6 a 2 2 0 0 1 -2 -2 V 6 a 2 2 0 0 1 2 -2 h 2'/>",
        ["scissors"]         = "<circle cx='6' cy='6' r='3'/><circle cx='6' cy='18' r='3'/><line x1='20' y1='4' x2='8.12' y2='15.88'/><line x1='14.47' y1='14.48' x2='20' y2='20'/><line x1='8.12' y1='8.12' x2='12' y2='12'/>",
        ["trash"]            = "<polyline points='3 6 5 6 21 6'/><path d='M 19 6 l -2 14 a 2 2 0 0 1 -2 2 H 9 a 2 2 0 0 1 -2 -2 L 5 6'/><path d='M 10 11 v 6'/><path d='M 14 11 v 6'/>",
        ["edit"]             = "<path d='M 11 4 H 4 a 2 2 0 0 0 -2 2 v 14 a 2 2 0 0 0 2 2 h 14 a 2 2 0 0 0 2 -2 v -7'/><path d='M 18.5 2.5 a 2.121 2.121 0 0 1 3 3 L 12 15 l -4 1 l 1 -4 z'/>",
        ["pen"]              = "<path d='M 21.174 6.812 a 1 1 0 0 0 -3.986 -3.987 L 3.842 16.174 a 2 2 0 0 0 -0.5 0.83 l -1.321 4.352 a 0.5 0.5 0 0 0 0.623 0.622 l 4.353 -1.32 a 2 2 0 0 0 0.83 -0.497 Z'/>",
        ["search"]           = "<circle cx='11' cy='11' r='8'/><line x1='21' y1='21' x2='16.65' y2='16.65'/>",

        ["wrench"]           = "<path d='M 14.7 6.3 a 1 1 0 0 0 0 1.4 l 1.6 1.6 a 1 1 0 0 0 1.4 0 l 3.77 -3.77 a 6 6 0 0 1 -7.94 7.94 l -6.91 6.91 a 2.12 2.12 0 0 1 -3 -3 l 6.91 -6.91 a 6 6 0 0 1 7.94 -7.94 l -3.76 3.76 z'/>",
        ["hammer"]           = "<path d='M 15 12 l -8.373 8.373 a 1 1 0 1 1 -3 -3 L 12 9'/><path d='M 18 15 l 4 -4'/><path d='M 21.5 11.5 l -1.914 -1.914 A 2 2 0 0 1 19 8.172 V 7 l -2.26 -2.26 a 6 6 0 0 0 -4.202 -1.756 L 9 2.96 l 0.92 0.82 A 6.18 6.18 0 0 1 12 8.4 V 10 l 2 2 h 1.172 a 2 2 0 0 1 1.414 0.586 L 18.5 14.5'/>",
        ["settings"]         = "<path d='M 12.22 2 h -0.44 a 2 2 0 0 0 -2 2 v 0.18 a 2 2 0 0 1 -1 1.73 l -0.43 0.25 a 2 2 0 0 1 -2 0 l -0.15 -0.08 a 2 2 0 0 0 -2.73 0.73 l -0.22 0.38 a 2 2 0 0 0 0.73 2.73 l 0.15 0.1 a 2 2 0 0 1 1 1.72 v 0.51 a 2 2 0 0 1 -1 1.74 l -0.15 0.09 a 2 2 0 0 0 -0.73 2.73 l 0.22 0.38 a 2 2 0 0 0 2.73 0.73 l 0.15 -0.08 a 2 2 0 0 1 2 0 l 0.43 0.25 a 2 2 0 0 1 1 1.73 V 20 a 2 2 0 0 0 2 2 h 0.44 a 2 2 0 0 0 2 -2 v -0.18 a 2 2 0 0 1 1 -1.73 l 0.43 -0.25 a 2 2 0 0 1 2 0 l 0.15 0.08 a 2 2 0 0 0 2.73 -0.73 l 0.22 -0.39 a 2 2 0 0 0 -0.73 -2.73 l -0.15 -0.08 a 2 2 0 0 1 -1 -1.74 v -0.5 a 2 2 0 0 1 1 -1.74 l 0.15 -0.09 a 2 2 0 0 0 0.73 -2.73 l -0.22 -0.38 a 2 2 0 0 0 -2.73 -0.73 l -0.15 0.08 a 2 2 0 0 1 -2 0 l -0.43 -0.25 a 2 2 0 0 1 -1 -1.73 V 4 a 2 2 0 0 0 -2 -2 z'/><circle cx='12' cy='12' r='3'/>",
        ["cog"]              = "<circle cx='12' cy='12' r='3'/><circle cx='12' cy='12' r='8.5'/><path d='M 12 3.5 V 6'/><path d='M 12 18 v 2.5'/><path d='M 3.5 12 H 6'/><path d='M 18 12 h 2.5'/>",
        ["sparkles"]         = "<path d='M 9.937 15.5 A 2 2 0 0 0 8.5 14.063 l -6.135 -1.582 a 0.5 0.5 0 0 1 0 -0.962 L 8.5 9.936 A 2 2 0 0 0 9.937 8.5 l 1.582 -6.135 a 0.5 0.5 0 0 1 0.963 0 L 14.063 8.5 A 2 2 0 0 0 15.5 9.937 l 6.135 1.581 a 0.5 0.5 0 0 1 0 0.964 L 15.5 14.063 a 2 2 0 0 0 -1.437 1.437 l -1.582 6.135 a 0.5 0.5 0 0 1 -0.963 0 z'/>",

        ["play"]             = "<polygon points='6 3 20 12 6 21 6 3'/>",
        ["zap"]              = "<polygon points='13 2 3 14 12 14 11 22 21 10 12 10 13 2'/>",
        ["rocket"]           = "<path d='M 4.5 16.5 c -1.5 1.26 -2 5 -2 5 s 3.74 -0.5 5 -2 c 0.71 -0.84 0.7 -2.13 -0.09 -2.91 a 2.18 2.18 0 0 0 -2.91 -0.09 z'/><path d='M 12 15 l -3 -3 a 22 22 0 0 1 2 -3.95 A 12.88 12.88 0 0 1 22 2 c 0 2.72 -0.78 7.5 -6 11 a 22.35 22.35 0 0 1 -4 2 z'/><path d='M 9 12 H 4 s 0.55 -3.03 2 -4 c 1.62 -1.08 5 0 5 0'/><path d='M 12 15 v 5 s 3.03 -0.55 4 -2 c 1.08 -1.62 0 -5 0 -5'/>",
        ["power"]            = "<path d='M 18.36 6.64 a 9 9 0 1 1 -12.73 0'/><line x1='12' y1='2' x2='12' y2='12'/>",

        ["shield"]           = "<path d='M 12 22 s 8 -4 8 -10 V 5 l -8 -3 -8 3 v 7 c 0 6 8 10 8 10 z'/>",
        ["lock"]             = "<rect x='3' y='11' width='18' height='11' rx='2'/><path d='M 7 11 V 7 a 5 5 0 0 1 10 0 v 4'/>",
        ["key"]              = "<path d='M 21 2 l -2 2 m -7.61 7.61 a 5.5 5.5 0 1 1 -7.778 7.778 a 5.5 5.5 0 0 1 7.777 -7.777 z m 0 0 L 15.5 7.5 m 0 0 l 3 3 L 22 7 l -3 -3 m -3.5 3.5 L 19 4'/>",
        ["user"]             = "<path d='M 20 21 v -2 a 4 4 0 0 0 -4 -4 H 8 a 4 4 0 0 0 -4 4 v 2'/><circle cx='12' cy='7' r='4'/>",

        ["github"]           = "<path d='M 9 19 c -5 1.5 -5 -2.5 -7 -3 m 14 6 v -3.87 a 3.37 3.37 0 0 0 -0.94 -2.61 c 3.14 -0.35 6.44 -1.54 6.44 -7 A 5.44 5.44 0 0 0 20 4.77 A 5.07 5.07 0 0 0 19.91 1 s -1.18 -0.35 -3.91 1.48 a 13.38 13.38 0 0 0 -7 0 C 6.27 0.65 5.09 1 5.09 1 A 5.07 5.07 0 0 0 5 4.77 a 5.44 5.44 0 0 0 -1.5 3.78 c 0 5.42 3.3 6.61 6.44 7 A 3.37 3.37 0 0 0 9 18.13 V 22'/>",
        ["git-branch"]       = "<line x1='6' y1='3' x2='6' y2='15'/><circle cx='18' cy='6' r='3'/><circle cx='6' cy='18' r='3'/><path d='M 18 9 a 9 9 0 0 1 -9 9'/>",
        ["package"]          = "<path d='M 16.5 9.4 L 7.55 4.24'/><path d='M 21 16 V 8 a 2 2 0 0 0 -1 -1.73 l -7 -4 a 2 2 0 0 0 -2 0 l -7 4 A 2 2 0 0 0 3 8 v 8 a 2 2 0 0 0 1 1.73 l 7 4 a 2 2 0 0 0 2 0 l 7 -4 A 2 2 0 0 0 21 16 z'/><polyline points='3.27 6.96 12 12.01 20.73 6.96'/><line x1='12' y1='22.08' x2='12' y2='12'/>",
        ["database"]         = "<ellipse cx='12' cy='5' rx='9' ry='3'/><path d='M 3 5 v 14 a 9 3 0 0 0 18 0 V 5'/><path d='M 3 12 a 9 3 0 0 0 18 0'/>",

        ["hash"]             = "<line x1='4' y1='9' x2='20' y2='9'/><line x1='4' y1='15' x2='20' y2='15'/><line x1='10' y1='3' x2='8' y2='21'/><line x1='16' y1='3' x2='14' y2='21'/>",
        ["link"]             = "<path d='M 10 13 a 5 5 0 0 0 7.54 0.54 l 3 -3 a 5 5 0 0 0 -7.07 -7.07 l -1.72 1.71'/><path d='M 14 11 a 5 5 0 0 0 -7.54 -0.54 l -3 3 a 5 5 0 0 0 7.07 7.07 l 1.71 -1.71'/>",
        ["external-link"]    = "<path d='M 18 13 v 6 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 V 8 a 2 2 0 0 1 2 -2 h 6'/><polyline points='15 3 21 3 21 9'/><line x1='10' y1='14' x2='21' y2='3'/>",
        ["globe"]            = "<circle cx='12' cy='12' r='10'/><line x1='2' y1='12' x2='22' y2='12'/><path d='M 12 2 a 15.3 15.3 0 0 1 4 10 a 15.3 15.3 0 0 1 -4 10 a 15.3 15.3 0 0 1 -4 -10 a 15.3 15.3 0 0 1 4 -10 z'/>",
        ["download"]         = "<path d='M 21 15 v 4 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 v -4'/><polyline points='7 10 12 15 17 10'/><line x1='12' y1='15' x2='12' y2='3'/>",
        ["upload"]           = "<path d='M 21 15 v 4 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 v -4'/><polyline points='17 8 12 3 7 8'/><line x1='12' y1='3' x2='12' y2='15'/>",
        ["eye"]              = "<path d='M 2 12 s 3 -7 10 -7 s 10 7 10 7 s -3 7 -10 7 s -10 -7 -10 -7 z'/><circle cx='12' cy='12' r='3'/>",
        ["refresh"]          = "<polyline points='23 4 23 10 17 10'/><polyline points='1 20 1 14 7 14'/><path d='M 3.51 9 a 9 9 0 0 1 14.85 -3.36 L 23 10 M 1 14 l 4.64 4.36 A 9 9 0 0 0 20.49 15'/>",
        ["monitor"]          = "<rect x='2' y='3' width='20' height='14' rx='2' ry='2'/><line x1='8' y1='21' x2='16' y2='21'/><line x1='12' y1='17' x2='12' y2='21'/>",
        // Lucide "repeat" — convert / change-format loop (spaced to match the
        // other entries so both the XAML and GDI+ path parsers handle it).
        ["repeat"]           = "<path d='m 17 2 l 4 4 l -4 4'/><path d='M 3 11 v -1 a 4 4 0 0 1 4 -4 h 14'/><path d='m 7 22 l -4 -4 l 4 -4'/><path d='M 21 13 v 1 a 4 4 0 0 1 -4 4 H 3'/>",

        // Brand marks from Simple Icons (MIT-licensed). Filled shapes, not
        // strokes — flagged in _filled above so the renderers paint them
        // with Fill / FillPath instead of Stroke.
        ["claude"] = "<path d='m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z'/>",
        ["openai"] = "<path d='M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z'/>",
    };

    // ----- Lookup -----

    public static bool IsLibraryName(string? icon)
        => icon is not null && icon.StartsWith(LibPrefix, StringComparison.Ordinal);

    public static string? StripPrefix(string? icon)
        => IsLibraryName(icon) ? icon!.Substring(LibPrefix.Length) : null;

    public static string MakeLibValue(string name) => LibPrefix + name;

    /// <summary>Returns the raw SVG-child XML fragment for a library icon, or
    /// null if the name isn't known. Used by IconMaterializer to render the
    /// icon to a .ico file for the Windows shell at Apply time.</summary>
    public static string? RawSvgFragment(string name)
        => _svg.TryGetValue(name, out var f) ? f : null;

    private static readonly ConcurrentDictionary<string, string> _pathDataCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a library name to a combined Path Data string ready for the
    /// XAML Geometry mini-language. Returns null if the name is unknown.
    /// Cached after first call.
    /// </summary>
    public static string? TryGetPathData(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_pathDataCache.TryGetValue(name, out var cached)) return cached;
        if (!_svg.TryGetValue(name, out var raw)) return null;
        var data = SvgFragmentToPathData(raw);
        _pathDataCache[name] = data;
        return data;
    }

    /// <summary>
    /// Convert a fragment of SVG element children (line / rect / circle /
    /// ellipse / polyline / polygon / path) into a single Path Data string
    /// suitable for <c>Microsoft.UI.Xaml.Media.Geometry</c>. Each shape becomes
    /// its own M-prefixed sub-path so multiple disconnected strokes still
    /// render in one Path. Visible for tests.
    /// </summary>
    public static string SvgFragmentToPathData(string fragment)
    {
        // Wrap in a synthetic <svg> with the SVG namespace so XLinq can parse it.
        var wrapped = "<svg xmlns='http://www.w3.org/2000/svg'>" + fragment + "</svg>";
        var doc = XDocument.Parse(wrapped);
        var root = doc.Root ?? throw new InvalidOperationException("empty svg");
        var sb = new StringBuilder();
        foreach (var el in root.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "line":     AppendLine(el, sb); break;
                case "rect":     AppendRect(el, sb); break;
                case "circle":   AppendCircle(el, sb); break;
                case "ellipse":  AppendEllipse(el, sb); break;
                case "polyline": AppendPolyPoints(el, sb, close: false); break;
                case "polygon":  AppendPolyPoints(el, sb, close: true); break;
                case "path":     AppendPath(el, sb); break;
            }
            sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static double D(XElement el, string attr, double def = 0)
    {
        var v = (string?)el.Attribute(attr);
        return string.IsNullOrEmpty(v) ? def : double.Parse(v, CultureInfo.InvariantCulture);
    }
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static void AppendLine(XElement el, StringBuilder sb)
    {
        sb.Append($"M {N(D(el,"x1"))} {N(D(el,"y1"))} L {N(D(el,"x2"))} {N(D(el,"y2"))}");
    }

    private static void AppendRect(XElement el, StringBuilder sb)
    {
        double x = D(el, "x"), y = D(el, "y"), w = D(el, "width"), h = D(el, "height");
        double rx = D(el, "rx", 0), ry = D(el, "ry", rx);
        if (rx <= 0 && ry <= 0)
        {
            sb.Append($"M {N(x)} {N(y)} H {N(x+w)} V {N(y+h)} H {N(x)} Z");
        }
        else
        {
            sb.Append(
                $"M {N(x+rx)} {N(y)} H {N(x+w-rx)} A {N(rx)} {N(ry)} 0 0 1 {N(x+w)} {N(y+ry)} " +
                $"V {N(y+h-ry)} A {N(rx)} {N(ry)} 0 0 1 {N(x+w-rx)} {N(y+h)} " +
                $"H {N(x+rx)} A {N(rx)} {N(ry)} 0 0 1 {N(x)} {N(y+h-ry)} " +
                $"V {N(y+ry)} A {N(rx)} {N(ry)} 0 0 1 {N(x+rx)} {N(y)} Z");
        }
    }

    private static void AppendCircle(XElement el, StringBuilder sb)
    {
        double cx = D(el, "cx"), cy = D(el, "cy"), r = D(el, "r");
        sb.Append($"M {N(cx-r)} {N(cy)} A {N(r)} {N(r)} 0 1 0 {N(cx+r)} {N(cy)} A {N(r)} {N(r)} 0 1 0 {N(cx-r)} {N(cy)} Z");
    }

    private static void AppendEllipse(XElement el, StringBuilder sb)
    {
        double cx = D(el, "cx"), cy = D(el, "cy"), rx = D(el, "rx"), ry = D(el, "ry");
        sb.Append($"M {N(cx-rx)} {N(cy)} A {N(rx)} {N(ry)} 0 1 0 {N(cx+rx)} {N(cy)} A {N(rx)} {N(ry)} 0 1 0 {N(cx-rx)} {N(cy)} Z");
    }

    private static void AppendPolyPoints(XElement el, StringBuilder sb, bool close)
    {
        var pts = ((string?)el.Attribute("points") ?? "").Trim();
        if (pts.Length == 0) return;
        // points may be comma- or space-separated. Normalise to spaces then split.
        var nums = pts.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < nums.Length; i += 2)
        {
            sb.Append(i == 0 ? "M " : " L ");
            sb.Append(nums[i]).Append(' ').Append(nums[i+1]);
        }
        if (close) sb.Append(" Z");
    }

    private static void AppendPath(XElement el, StringBuilder sb)
    {
        var d = (string?)el.Attribute("d");
        if (!string.IsNullOrEmpty(d)) sb.Append(d);
    }
}
