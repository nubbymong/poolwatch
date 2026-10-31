using System.Drawing.Drawing2D;
using System.Globalization;

namespace PoolWatch.Ui.Setup;

/// <summary>
/// Reads the path data of the setup window's outline icons (the approved design's own icons, written into
/// <see cref="SetupIcons"/>) into a <see cref="GraphicsPath"/> in the icon's own units. It reads the commands those icons
/// use, absolute and relative: M, L, H, V, C, S, A and Z, with repeated parameters after a command. It only ever reads the
/// strings compiled into PoolWatch; a path it cannot read throws, so a typo fails the tests, never a window.
/// </summary>
internal static class SvgPath
{
    public static GraphicsPath Parse(string d)
    {
        var path = new GraphicsPath();
        var r = new Reader(d);
        PointF current = default, start = default, lastControl = default;
        char command = ' ', previous = ' ';
        bool figureOpen = false;
        while (r.More())
        {
            if (r.Command() is { } c) command = c;
            else if (command == ' ') throw new FormatException($"path data must start with a command: \"{d}\"");
            bool rel = char.IsLower(command);
            PointF Off(float x, float y) => rel ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    var p = Off(r.Number(), r.Number());
                    path.StartFigure();
                    figureOpen = true;
                    current = start = p;
                    // Further pairs after a move are lines.
                    command = rel ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    var p = Off(r.Number(), r.Number());
                    path.AddLine(current, p);
                    current = p;
                    break;
                }
                case 'H':
                {
                    float x = r.Number();
                    var p = new PointF(rel ? current.X + x : x, current.Y);
                    path.AddLine(current, p);
                    current = p;
                    break;
                }
                case 'V':
                {
                    float y = r.Number();
                    var p = new PointF(current.X, rel ? current.Y + y : y);
                    path.AddLine(current, p);
                    current = p;
                    break;
                }
                case 'C':
                {
                    var c1 = Off(r.Number(), r.Number());
                    var c2 = Off(r.Number(), r.Number());
                    var p = Off(r.Number(), r.Number());
                    path.AddBezier(current, c1, c2, p);
                    lastControl = c2;
                    current = p;
                    break;
                }
                case 'S':
                {
                    // The first control point mirrors the last curve's second one (or is the current point after anything else).
                    var c1 = char.ToUpperInvariant(previous) is 'C' or 'S' ? new PointF(2 * current.X - lastControl.X, 2 * current.Y - lastControl.Y) : current;
                    var c2 = Off(r.Number(), r.Number());
                    var p = Off(r.Number(), r.Number());
                    path.AddBezier(current, c1, c2, p);
                    lastControl = c2;
                    current = p;
                    break;
                }
                case 'A':
                {
                    float rx = r.Number(), ry = r.Number(), rotation = r.Number();
                    bool large = r.Flag(), sweep = r.Flag();
                    var p = Off(r.Number(), r.Number());
                    Arc(path, current, rx, ry, rotation, large, sweep, p);
                    current = p;
                    break;
                }
                case 'Z':
                    if (figureOpen) path.CloseFigure();
                    figureOpen = false;
                    current = start;
                    break;
                default:
                    throw new FormatException($"path command '{command}' is not one the setup icons use: \"{d}\"");
            }
            previous = command;
        }
        return path;
    }

    /// <summary>
    /// An elliptical arc from <paramref name="from"/> to <paramref name="to"/> as cubic Bezier curves, by the SVG
    /// specification's conversion from endpoint to centre parameters (SVG 1.1, appendix F.6.5 and F.6.6).
    /// </summary>
    private static void Arc(GraphicsPath path, PointF from, float rx, float ry, float rotationDegrees, bool large, bool sweep, PointF to)
    {
        if (from == to) return;
        if (rx == 0 || ry == 0) { path.AddLine(from, to); return; }
        double phi = rotationDegrees * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (from.X - to.X) / 2.0, dy = (from.Y - to.Y) / 2.0;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double a = Math.Abs(rx), b = Math.Abs(ry);
        double lambda = x1 * x1 / (a * a) + y1 * y1 / (b * b);
        if (lambda > 1) { a *= Math.Sqrt(lambda); b *= Math.Sqrt(lambda); }
        double num = a * a * b * b - a * a * y1 * y1 - b * b * x1 * x1, den = a * a * y1 * y1 + b * b * x1 * x1;
        double coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
        double cx1 = coef * a * y1 / b, cy1 = coef * -b * x1 / a;
        double cx = cos * cx1 - sin * cy1 + (from.X + to.X) / 2.0, cy = sin * cx1 + cos * cy1 + (from.Y + to.Y) / 2.0;
        double theta = Angle(1, 0, (x1 - cx1) / a, (y1 - cy1) / b);
        double delta = Angle((x1 - cx1) / a, (y1 - cy1) / b, (-x1 - cx1) / a, (-y1 - cy1) / b);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9);
        double step = delta / segments, k = 4.0 / 3.0 * Math.Tan(step / 4);
        PointF On(double t) => new((float)(cx + a * Math.Cos(t) * cos - b * Math.Sin(t) * sin), (float)(cy + a * Math.Cos(t) * sin + b * Math.Sin(t) * cos));
        PointF Tangent(double t) => new((float)(-a * Math.Sin(t) * cos - b * Math.Cos(t) * sin), (float)(-a * Math.Sin(t) * sin + b * Math.Cos(t) * cos));
        var p0 = from;
        for (int i = 0; i < segments; i++)
        {
            double t0 = theta + i * step, t1 = t0 + step;
            var d0 = Tangent(t0);
            var d1 = Tangent(t1);
            var p1 = i == segments - 1 ? to : On(t1);
            path.AddBezier(p0, new PointF(p0.X + (float)k * d0.X, p0.Y + (float)k * d0.Y), new PointF(p1.X - (float)k * d1.X, p1.Y - (float)k * d1.Y), p1);
            p0 = p1;
        }

        static double Angle(double ux, double uy, double vx, double vy)
        {
            double angle = Math.Acos(Math.Clamp((ux * vx + uy * vy) / (Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy)), -1, 1));
            return ux * vy - uy * vx < 0 ? -angle : angle;
        }
    }

    /// <summary>Path data, read one command letter, number or flag at a time.</summary>
    private sealed class Reader(string d)
    {
        private int _i;

        public bool More()
        {
            Skip();
            return _i < d.Length;
        }

        /// <summary>The command letter here, or null when a number comes next (a repeated command).</summary>
        public char? Command()
        {
            Skip();
            if (_i < d.Length && char.IsAsciiLetter(d[_i]) && d[_i] is not ('e' or 'E')) return d[_i++];
            return null;
        }

        public float Number()
        {
            Skip();
            int start = _i;
            if (_i < d.Length && d[_i] is '-' or '+') _i++;
            bool dot = false;
            while (_i < d.Length && (char.IsAsciiDigit(d[_i]) || (d[_i] == '.' && !dot)))
            {
                if (d[_i] == '.') dot = true;
                _i++;
            }
            if (_i < d.Length && d[_i] is 'e' or 'E')
            {
                _i++;
                if (_i < d.Length && d[_i] is '-' or '+') _i++;
                while (_i < d.Length && char.IsAsciiDigit(d[_i])) _i++;
            }
            if (_i == start || !float.TryParse(d.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                throw new FormatException($"a number was expected at {start} in \"{d}\"");
            return value;
        }

        /// <summary>An arc's flag: one digit, 0 or 1, which may stand right before the next number.</summary>
        public bool Flag()
        {
            Skip();
            if (_i < d.Length && d[_i] is '0' or '1') return d[_i++] == '1';
            throw new FormatException($"an arc flag was expected at {_i} in \"{d}\"");
        }

        private void Skip()
        {
            while (_i < d.Length && (char.IsWhiteSpace(d[_i]) || d[_i] == ',')) _i++;
        }
    }
}
