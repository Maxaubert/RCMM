using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.Versioning;

namespace RCMM.Core.Services;

/// <summary>
/// Minimal SVG path-mini-language parser → <see cref="GraphicsPath"/>. Supports
/// the commands actually used by the bundled Lucide icons:
///     M m L l H h V v C c A a Z z
/// (plus implicit continuation: numbers after an M command default to L, etc.)
///
/// Rotated arcs (phi != 0) are ignored — none of the Lucide source uses them.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SvgPathParser
{
    public static GraphicsPath Parse(string d)
    {
        var path = new GraphicsPath();
        if (string.IsNullOrWhiteSpace(d)) return path;

        var tokens = Tokenize(d);
        int idx = 0;
        float cx = 0, cy = 0;     // current point
        float sx = 0, sy = 0;     // subpath start
        char cmd = '\0';

        while (idx < tokens.Count)
        {
            var tok = tokens[idx];
            if (tok.Length == 1 && char.IsLetter(tok[0]))
            {
                cmd = tok[0];
                idx++;
                if (cmd == 'Z' || cmd == 'z')
                {
                    path.CloseFigure();
                    cx = sx; cy = sy;
                    continue;
                }
            }
            else if (cmd == '\0')
            {
                throw new InvalidOperationException("SVG path starts with a number");
            }

            switch (cmd)
            {
                case 'M':
                    cx = F(tokens[idx++]); cy = F(tokens[idx++]);
                    sx = cx; sy = cy;
                    path.StartFigure();
                    cmd = 'L'; // subsequent number pairs are implicit line-to
                    break;
                case 'm':
                    cx += F(tokens[idx++]); cy += F(tokens[idx++]);
                    sx = cx; sy = cy;
                    path.StartFigure();
                    cmd = 'l';
                    break;
                case 'L':
                {
                    var x = F(tokens[idx++]); var y = F(tokens[idx++]);
                    path.AddLine(cx, cy, x, y);
                    cx = x; cy = y;
                    break;
                }
                case 'l':
                {
                    var x = cx + F(tokens[idx++]); var y = cy + F(tokens[idx++]);
                    path.AddLine(cx, cy, x, y);
                    cx = x; cy = y;
                    break;
                }
                case 'H':
                {
                    var x = F(tokens[idx++]);
                    path.AddLine(cx, cy, x, cy);
                    cx = x;
                    break;
                }
                case 'h':
                {
                    var x = cx + F(tokens[idx++]);
                    path.AddLine(cx, cy, x, cy);
                    cx = x;
                    break;
                }
                case 'V':
                {
                    var y = F(tokens[idx++]);
                    path.AddLine(cx, cy, cx, y);
                    cy = y;
                    break;
                }
                case 'v':
                {
                    var y = cy + F(tokens[idx++]);
                    path.AddLine(cx, cy, cx, y);
                    cy = y;
                    break;
                }
                case 'C':
                {
                    var x1 = F(tokens[idx++]); var y1 = F(tokens[idx++]);
                    var x2 = F(tokens[idx++]); var y2 = F(tokens[idx++]);
                    var x  = F(tokens[idx++]); var y  = F(tokens[idx++]);
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    cx = x; cy = y;
                    break;
                }
                case 'c':
                {
                    var x1 = cx + F(tokens[idx++]); var y1 = cy + F(tokens[idx++]);
                    var x2 = cx + F(tokens[idx++]); var y2 = cy + F(tokens[idx++]);
                    var x  = cx + F(tokens[idx++]); var y  = cy + F(tokens[idx++]);
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    cx = x; cy = y;
                    break;
                }
                case 'A':
                {
                    var rx = F(tokens[idx++]); var ry = F(tokens[idx++]);
                    var phi = F(tokens[idx++]);
                    var laf = (int)F(tokens[idx++]);
                    var sf  = (int)F(tokens[idx++]);
                    var x = F(tokens[idx++]); var y = F(tokens[idx++]);
                    AppendArc(path, cx, cy, rx, ry, phi, laf, sf, x, y);
                    cx = x; cy = y;
                    break;
                }
                case 'a':
                {
                    var rx = F(tokens[idx++]); var ry = F(tokens[idx++]);
                    var phi = F(tokens[idx++]);
                    var laf = (int)F(tokens[idx++]);
                    var sf  = (int)F(tokens[idx++]);
                    var x = cx + F(tokens[idx++]); var y = cy + F(tokens[idx++]);
                    AppendArc(path, cx, cy, rx, ry, phi, laf, sf, x, y);
                    cx = x; cy = y;
                    break;
                }
                default:
                    // Unknown command — skip a single token to advance.
                    idx++;
                    break;
            }
        }
        return path;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>SVG endpoint→center arc conversion, then write as a GDI+ arc.
    /// Rotation (phi) is ignored — the Lucide icon set doesn't use it.</summary>
    private static void AppendArc(GraphicsPath path,
        float x1, float y1, float rx, float ry, float _phi,
        int laf, int sf, float x2, float y2)
    {
        if (rx == 0 || ry == 0)
        {
            path.AddLine(x1, y1, x2, y2);
            return;
        }
        rx = Math.Abs(rx); ry = Math.Abs(ry);

        // Step 1: translate origin to midpoint of (x1,y1)-(x2,y2)
        double x1p = (x1 - x2) / 2.0;
        double y1p = (y1 - y2) / 2.0;

        // Step 2: enlarge radii if necessary
        double rxSq = rx * rx, rySq = ry * ry;
        double x1pSq = x1p * x1p, y1pSq = y1p * y1p;
        double lambda = x1pSq / rxSq + y1pSq / rySq;
        if (lambda > 1)
        {
            double s = Math.Sqrt(lambda);
            rx = (float)(rx * s); ry = (float)(ry * s);
            rxSq = rx * rx; rySq = ry * ry;
        }

        // Step 3: compute (cx', cy')
        double sign = (laf == sf) ? -1.0 : 1.0;
        double num = rxSq * rySq - rxSq * y1pSq - rySq * x1pSq;
        double den = rxSq * y1pSq + rySq * x1pSq;
        double factor = sign * Math.Sqrt(Math.Max(0, num / den));
        double cxp = factor * (rx * y1p / ry);
        double cyp = factor * (-ry * x1p / rx);

        // Step 4: untranslate
        double cx = cxp + (x1 + x2) / 2.0;
        double cy = cyp + (y1 + y2) / 2.0;

        // Step 5: angles
        double theta1 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
        double theta2 = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
        double dTheta = theta2 - theta1;
        if (sf == 0 && dTheta > 0) dTheta -= 2 * Math.PI;
        else if (sf == 1 && dTheta < 0) dTheta += 2 * Math.PI;

        var rect = new System.Drawing.RectangleF((float)(cx - rx), (float)(cy - ry), 2 * rx, 2 * ry);
        float startDeg  = (float)(theta1 * 180.0 / Math.PI);
        float sweepDeg  = (float)(dTheta * 180.0 / Math.PI);
        path.AddArc(rect, startDeg, sweepDeg);
    }

    /// <summary>Split a path-d string into individual tokens (commands or
    /// numbers). Commas / whitespace are equivalent separators; signs and
    /// dot-prefixed fragments split numbers without an intervening separator.</summary>
    private static List<string> Tokenize(string d)
    {
        var list = new List<string>(64);
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c)) { list.Add(c.ToString()); i++; continue; }
            // Read a number: sign, digits, dot, digits, e/E, sign, digits.
            int start = i;
            if (c == '+' || c == '-') i++;
            bool dot = false;
            while (i < d.Length)
            {
                char k = d[i];
                if (char.IsDigit(k)) { i++; continue; }
                if (k == '.' && !dot) { dot = true; i++; continue; }
                if (k == 'e' || k == 'E')
                {
                    i++;
                    if (i < d.Length && (d[i] == '+' || d[i] == '-')) i++;
                    while (i < d.Length && char.IsDigit(d[i])) i++;
                    break;
                }
                break;
            }
            if (i == start)
            {
                // Hit a character we can't parse; skip to avoid infinite loop.
                i++;
                continue;
            }
            list.Add(d.Substring(start, i - start));
        }
        return list;
    }
}
