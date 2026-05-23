using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.Versioning;

namespace RCMM.Core.Services;

/// <summary>
/// Minimal SVG path-mini-language parser → <see cref="GraphicsPath"/>. Supports
/// the commands used by the full Lucide icon set:
///     M m L l H h V v C c S s Q q T t A a Z z
/// (plus implicit continuation: numbers after an M command default to L, etc.)
///
/// Smooth curves (S/s, T/t) reflect the previous curve's control point.
/// Quadratics (Q/q/T/t) are elevated to cubics, since GDI+ has no native
/// quadratic. Rotated arcs (phi != 0) are ignored — Lucide doesn't use them.
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
        float lc2x = 0, lc2y = 0; // previous cubic's 2nd control point (S/s reflection)
        float lqx = 0, lqy = 0;   // previous quadratic's control point (T/t reflection)
        int lastKind = 0;         // command just executed: 0 none, 1 cubic-family, 2 quad-family
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
                    lastKind = 0;
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
                    lc2x = x2; lc2y = y2; cx = x; cy = y;
                    break;
                }
                case 'c':
                {
                    var x1 = cx + F(tokens[idx++]); var y1 = cy + F(tokens[idx++]);
                    var x2 = cx + F(tokens[idx++]); var y2 = cy + F(tokens[idx++]);
                    var x  = cx + F(tokens[idx++]); var y  = cy + F(tokens[idx++]);
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    lc2x = x2; lc2y = y2; cx = x; cy = y;
                    break;
                }
                case 'S':
                case 's':
                {
                    float x2, y2, x, y;
                    if (cmd == 'S')
                    {
                        x2 = F(tokens[idx++]); y2 = F(tokens[idx++]);
                        x  = F(tokens[idx++]); y  = F(tokens[idx++]);
                    }
                    else
                    {
                        x2 = cx + F(tokens[idx++]); y2 = cy + F(tokens[idx++]);
                        x  = cx + F(tokens[idx++]); y  = cy + F(tokens[idx++]);
                    }
                    // First control point = reflection of the previous cubic's
                    // 2nd control point about the current point (else = current).
                    float x1 = (lastKind == 1) ? 2 * cx - lc2x : cx;
                    float y1 = (lastKind == 1) ? 2 * cy - lc2y : cy;
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    lc2x = x2; lc2y = y2; cx = x; cy = y;
                    break;
                }
                case 'Q':
                case 'q':
                {
                    float qx, qy, x, y;
                    if (cmd == 'Q')
                    {
                        qx = F(tokens[idx++]); qy = F(tokens[idx++]);
                        x  = F(tokens[idx++]); y  = F(tokens[idx++]);
                    }
                    else
                    {
                        qx = cx + F(tokens[idx++]); qy = cy + F(tokens[idx++]);
                        x  = cx + F(tokens[idx++]); y  = cy + F(tokens[idx++]);
                    }
                    AddQuadratic(path, cx, cy, qx, qy, x, y);
                    lqx = qx; lqy = qy; cx = x; cy = y;
                    break;
                }
                case 'T':
                case 't':
                {
                    float x, y;
                    if (cmd == 'T') { x = F(tokens[idx++]); y = F(tokens[idx++]); }
                    else { x = cx + F(tokens[idx++]); y = cy + F(tokens[idx++]); }
                    // Control point = reflection of the previous quadratic's
                    // control point about the current point (else = current).
                    float qx = (lastKind == 2) ? 2 * cx - lqx : cx;
                    float qy = (lastKind == 2) ? 2 * cy - lqy : cy;
                    AddQuadratic(path, cx, cy, qx, qy, x, y);
                    lqx = qx; lqy = qy; cx = x; cy = y;
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

            // Track whether the command just executed was a cubic- or quadratic-
            // family curve, so a following S/T can reflect its control point.
            // (Z resets lastKind via the continue above.)
            lastKind = (cmd == 'C' || cmd == 'c' || cmd == 'S' || cmd == 's') ? 1
                     : (cmd == 'Q' || cmd == 'q' || cmd == 'T' || cmd == 't') ? 2
                     : 0;
        }
        return path;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Append a quadratic Bézier by elevating it to a cubic (GDI+ has
    /// no native quadratic). Standard degree-elevation of the control point.</summary>
    private static void AddQuadratic(GraphicsPath path,
        float x0, float y0, float qx, float qy, float x, float y)
    {
        float c1x = x0 + 2f / 3f * (qx - x0);
        float c1y = y0 + 2f / 3f * (qy - y0);
        float c2x = x  + 2f / 3f * (qx - x);
        float c2y = y  + 2f / 3f * (qy - y);
        path.AddBezier(x0, y0, c1x, c1y, c2x, c2y, x, y);
    }

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
