using System.Drawing.Drawing2D;

namespace SmartFireApp.Forms;

/// <summary>
/// Upgraded GDI+ telemetry chart — area fill gradients, animated pulse dot,
/// crosshair at peak, floating tooltip, and X-axis time labels.
/// Maps to section 3 of the HTML mockup.
/// </summary>
internal sealed class TelemetryChart : Control
{
    // ── Demo series (normalized 0–1, bottom=0, top=1) ─────────────────────
    private readonly float[] _smoke       = [0.10f, .17f, .22f, .31f, .38f, .55f, .47f, .52f];
    private readonly float[] _temperature = [.30f, .32f, .35f, .39f, .43f, .49f, .46f, .50f];
    private readonly float[] _humidity    = [.62f, .59f, .56f, .53f, .49f, .45f, .46f, .43f];

    private readonly string[] _xLabels =
        ["14:00", "14:15", "14:30", "14:45", "15:00", "15:15 (Tăng nhiệt)", "15:20 (Khói đỉnh)", "Hiện tại"];

    private readonly string[] _yLabels =
        ["250 ppm / 60°C", "180 ppm / 45°C", "100 ppm / 30°C", "0 ppm / 15°C"];

    // Pulse animation
    private float _pulseRadius = 0f;
    private bool  _pulseGrow   = true;
    private readonly System.Windows.Forms.Timer _pulseTimer;

    public TelemetryChart()
    {
        DoubleBuffered = true;
        BackColor      = Color.FromArgb(10, 14, 23);
        MinimumSize    = new Size(500, 220);

        _pulseTimer          = new System.Windows.Forms.Timer { Interval = 50 };
        _pulseTimer.Tick    += OnPulseTick;
        _pulseTimer.Start();
    }

    private void OnPulseTick(object? s, EventArgs e)
    {
        if (_pulseGrow) { _pulseRadius += 0.7f; if (_pulseRadius > 9f) _pulseGrow = false; }
        else            { _pulseRadius -= 0.7f; if (_pulseRadius < 0f) _pulseGrow = true;  }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pulseTimer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Chart area — leave space for Y labels left, X labels bottom
        const int marginLeft = 110, marginTop = 12, marginRight = 12, marginBottom = 28;
        var area = new RectangleF(
            marginLeft, marginTop,
            Width - marginLeft - marginRight,
            Height - marginTop - marginBottom);

        DrawGrid(g, area);
        DrawYLabels(g, area);
        DrawAreaFill(g, area, _smoke,  UI.Theme.PrimaryContainer, 60);
        DrawSeries(g, area, _smoke,       UI.Theme.PrimaryContainer, 2.5f, false);
        DrawSeries(g, area, _temperature, UI.Theme.Secondary,        2.5f, false);
        DrawSeries(g, area, _humidity,    UI.Theme.Tertiary,         2.2f, true);
        DrawPeakIndicators(g, area);
        DrawXLabels(g, area);
        DrawFloatingTooltip(g, area);
    }

    // ── Grid ──────────────────────────────────────────────────────────────
    private void DrawGrid(Graphics g, RectangleF area)
    {
        using var gridPen = new Pen(Color.FromArgb(49, 53, 63), 1f)
            { DashStyle = DashStyle.Dash };
        using var basePen = new Pen(Color.FromArgb(49, 53, 63), 1f);

        for (int row = 0; row < 4; row++)
        {
            float y = area.Top + row * area.Height / 3f;
            var pen = row == 3 ? basePen : gridPen;
            g.DrawLine(pen, area.Left, y, area.Right, y);
        }
    }

    // ── Y Labels ─────────────────────────────────────────────────────────
    private void DrawYLabels(Graphics g, RectangleF area)
    {
        using var brush = new SolidBrush(UI.Theme.Outline);
        using var font  = UI.Theme.LabelCode;
        for (int i = 0; i < _yLabels.Length; i++)
        {
            float y = area.Top + i * area.Height / 3f;
            g.DrawString(_yLabels[i], font, brush, 2, y - 8);
        }
    }

    // ── Area fill under smoke curve ───────────────────────────────────────
    private static void DrawAreaFill(Graphics g, RectangleF area, float[] pts, Color color, int alpha)
    {
        var points = SeriesPoints(area, pts);
        var fillPoints = new PointF[points.Length + 2];
        fillPoints[0] = new PointF(points[0].X, area.Bottom);
        for (int i = 0; i < points.Length; i++) fillPoints[i + 1] = points[i];
        fillPoints[^1] = new PointF(points[^1].X, area.Bottom);

        using var path = new GraphicsPath();
        path.AddPolygon(fillPoints);

        using var brush = new LinearGradientBrush(
            new PointF(0, area.Top), new PointF(0, area.Bottom),
            Color.FromArgb(alpha, color), Color.FromArgb(0, color));
        g.FillPath(brush, path);
    }

    // ── Curve series ──────────────────────────────────────────────────────
    private static void DrawSeries(Graphics g, RectangleF area, float[] pts, Color color, float width, bool dashed)
    {
        var points = SeriesPoints(area, pts);
        if (points.Length < 2) return;
        using var pen = new Pen(color, width)
            { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (dashed) pen.DashPattern = [3f, 3f];
        g.DrawLines(pen, points);
    }

    // ── Peak dot + crosshair ──────────────────────────────────────────────
    private void DrawPeakIndicators(Graphics g, RectangleF area)
    {
        // Peak is at index 5 of _smoke
        int peakIdx = 5;
        var pt = SeriesPoint(area, _smoke, peakIdx);

        // Vertical crosshair
        using var dashPen = new Pen(Color.FromArgb(150, UI.Theme.PrimaryContainer), 1f)
            { DashStyle = DashStyle.Dash };
        g.DrawLine(dashPen, pt.X, area.Top, pt.X, area.Bottom);

        // Pulsing outer ring
        float pr = 5f + _pulseRadius;
        using var pulseBrush = new SolidBrush(Color.FromArgb(
            Math.Max(0, 80 - (int)(_pulseRadius * 8)), UI.Theme.PrimaryContainer));
        g.FillEllipse(pulseBrush, pt.X - pr, pt.Y - pr, pr * 2, pr * 2);

        // Solid core dot — smoke
        using var coreBrush = new SolidBrush(UI.Theme.PrimaryContainer);
        g.FillEllipse(coreBrush, pt.X - 5, pt.Y - 5, 10, 10);
        using var coreOutline = new Pen(Color.FromArgb(15, 19, 28), 2f);
        g.DrawEllipse(coreOutline, pt.X - 5, pt.Y - 5, 10, 10);

        // Temperature dot
        var tPt = SeriesPoint(area, _temperature, peakIdx);
        using var tBrush = new SolidBrush(UI.Theme.Secondary);
        g.FillEllipse(tBrush, tPt.X - 4, tPt.Y - 4, 9, 9);
        g.DrawEllipse(coreOutline, tPt.X - 4, tPt.Y - 4, 9, 9);

        // Humidity dot
        var hPt = SeriesPoint(area, _humidity, peakIdx);
        using var hBrush = new SolidBrush(UI.Theme.Tertiary);
        g.FillEllipse(hBrush, hPt.X - 3, hPt.Y - 3, 8, 8);
        g.DrawEllipse(coreOutline, hPt.X - 3, hPt.Y - 3, 8, 8);
    }

    // ── X-axis labels ─────────────────────────────────────────────────────
    private void DrawXLabels(Graphics g, RectangleF area)
    {
        using var font  = UI.Theme.LabelCode;
        using var brush = new SolidBrush(UI.Theme.OnSurfaceVariant);
        using var peak1 = new SolidBrush(UI.Theme.Secondary);
        using var peak2 = new SolidBrush(UI.Theme.PrimaryContainer);

        for (int i = 0; i < _xLabels.Length; i++)
        {
            float x = area.Left + i * area.Width / (_xLabels.Length - 1f);
            var b   = i == 5 ? peak1 : (i == 6 ? peak2 : brush);
            var size = g.MeasureString(_xLabels[i], font);
            g.DrawString(_xLabels[i], font, b, x - size.Width / 2f, area.Bottom + 4);
        }
    }

    // ── Floating tooltip ──────────────────────────────────────────────────
    private static void DrawFloatingTooltip(Graphics g, RectangleF area)
    {
        // Fixed tooltip near peak (index 5)
        float peakX = area.Left + 5f * area.Width / 7f;
        float tx = peakX - 90f;
        float ty = area.Top + 8f;

        const int tw = 160, th = 80;
        var box = new RectangleF(tx, ty, tw, th);

        // Background
        using var bgBrush = new SolidBrush(Color.FromArgb(220, 49, 53, 63));
        using var path    = RoundedRectPath(box, 8);
        g.FillPath(bgBrush, path);
        using var border  = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);
        g.DrawPath(border, path);

        using var labelFont  = UI.Theme.LabelCode;
        using var valueFont  = UI.Theme.LabelCode;
        using var cyan       = new SolidBrush(UI.Theme.Tertiary);
        using var red        = new SolidBrush(UI.Theme.PrimaryContainer);
        using var orange     = new SolidBrush(UI.Theme.Secondary);
        using var white      = new SolidBrush(UI.Theme.OnSurface);

        float lx = tx + 8, ly = ty + 6;
        g.DrawString("Mốc 15:20:00", labelFont, cyan, lx, ly);
        g.DrawString("Điểm cảnh báo", labelFont, red, lx + 70, ly);

        ly += 16;
        DrawTooltipRow(g, lx, ly, "Khói:", "210 ppm", red, white, labelFont);
        ly += 16;
        DrawTooltipRow(g, lx, ly, "Nhiệt độ:", "35.6 °C", orange, white, labelFont);
        ly += 16;
        DrawTooltipRow(g, lx, ly, "Độ ẩm:", "47.0 %", cyan, white, labelFont);
    }

    private static void DrawTooltipRow(Graphics g, float x, float y,
        string label, string value, SolidBrush valueBrush, SolidBrush labelBrush, Font font)
    {
        // Dot
        using var dot = new SolidBrush(valueBrush.Color);
        g.FillEllipse(dot, x, y + 3, 6, 6);
        g.DrawString(label, font, labelBrush, x + 10, y);
        var lSize = g.MeasureString(label + "   ", font);
        g.DrawString(value, font, valueBrush, x + lSize.Width + 4, y);
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static PointF[] SeriesPoints(RectangleF area, float[] pts)
    {
        var result = new PointF[pts.Length];
        for (int i = 0; i < pts.Length; i++)
            result[i] = SeriesPoint(area, pts, i);
        return result;
    }

    private static PointF SeriesPoint(RectangleF area, float[] pts, int i) =>
        new(area.Left + i * area.Width / (pts.Length - 1f),
            area.Bottom - pts[i] * area.Height);

    private static GraphicsPath RoundedRectPath(RectangleF rect, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
