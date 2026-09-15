using System.Drawing.Drawing2D;

namespace SmartFireApp.UI;

/// <summary>
/// Composite metric card — maps to the 4 telemetry cards in the HTML mockup.
/// Layout: node label | icon placeholder | title | big value + unit | progress bar | trend line.
/// </summary>
public sealed class MetricCard : UserControl
{
    // Labels
    private readonly Label _nodeLabel  = new();
    private readonly Label _titleLabel = new();
    private readonly Label _valueLabel = new();
    private readonly Label _unitLabel  = new();
    private readonly Label _hintLabel  = new();
    private readonly Label _trendLabel = new();

    // Progress
    private float   _progress   = 0f;  // 0.0 – 1.0
    private Color   _accent     = Theme.Tertiary;
    private string  _badgeText  = "";
    private Color   _badgeBack  = Color.Transparent;
    private Color   _badgeFore  = Color.Transparent;
    private bool    _showBadge  = false;

    // ── Public API ────────────────────────────────────────────────────────

    public void SetData(
        string nodeLabel,
        string title,
        string value,
        string unit,
        float progressRatio,   // 0–1
        string hint,
        string trendText,
        Color accent,
        string badgeText    = "",
        Color? badgeBack    = null,
        Color? badgeFore    = null)
    {
        _nodeLabel.Text  = nodeLabel.ToUpperInvariant();
        _titleLabel.Text = title;
        _valueLabel.Text = value;
        _unitLabel.Text  = unit;
        _hintLabel.Text  = hint;
        _trendLabel.Text = trendText;
        _progress        = Math.Clamp(progressRatio, 0f, 1f);
        _accent          = accent;

        _nodeLabel.ForeColor  = accent;
        _valueLabel.ForeColor = accent;

        _badgeText = badgeText;
        _badgeBack = badgeBack ?? Color.Transparent;
        _badgeFore = badgeFore ?? accent;
        _showBadge = !string.IsNullOrEmpty(badgeText);

        Invalidate();
    }

    // ── Constructor ───────────────────────────────────────────────────────

    public MetricCard()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.UserPaint, true);

        Size      = new Size(260, 148);
        BackColor = Theme.SurfaceContainerLow;

        // Node label (monospace, top-left)
        _nodeLabel.Font      = Theme.LabelCode;
        _nodeLabel.ForeColor = Theme.Tertiary;
        _nodeLabel.Location  = new Point(Theme.SpaceMd, Theme.SpaceMd);
        _nodeLabel.Size      = new Size(220, 16);
        _nodeLabel.BackColor = Color.Transparent;

        // Title
        _titleLabel.Font      = Theme.TitleMd;
        _titleLabel.ForeColor = Theme.OnSurface;
        _titleLabel.Location  = new Point(Theme.SpaceMd, 36);
        _titleLabel.Size      = new Size(220, 20);
        _titleLabel.BackColor = Color.Transparent;

        // Big metric value
        _valueLabel.Font      = Theme.MetricDisplay;
        _valueLabel.ForeColor = Theme.Tertiary;
        _valueLabel.Location  = new Point(Theme.SpaceMd, 60);
        _valueLabel.Size      = new Size(160, 44);
        _valueLabel.BackColor = Color.Transparent;

        // Unit
        _unitLabel.Font      = Theme.HeadlineSm;
        _unitLabel.ForeColor = Theme.OnSurfaceVariant;
        _unitLabel.Location  = new Point(170, 72);
        _unitLabel.Size      = new Size(70, 28);
        _unitLabel.BackColor = Color.Transparent;

        // Hint
        _hintLabel.Font      = Theme.BodySm;
        _hintLabel.ForeColor = Theme.OnSurfaceVariant;
        _hintLabel.Location  = new Point(Theme.SpaceMd, 108);
        _hintLabel.Size      = new Size(220, 14);
        _hintLabel.BackColor = Color.Transparent;

        // Trend
        _trendLabel.Font      = Theme.BodySm;
        _trendLabel.ForeColor = Theme.OnSurfaceVariant;
        _trendLabel.Location  = new Point(Theme.SpaceMd, 126);
        _trendLabel.Size      = new Size(220, 14);
        _trendLabel.BackColor = Color.Transparent;

        Controls.AddRange([_nodeLabel, _titleLabel, _valueLabel,
                            _unitLabel, _hintLabel, _trendLabel]);

        // Hover effect
        MouseEnter += (_, _) => { BackColor = Theme.SurfaceContainer; Invalidate(); };
        MouseLeave += (_, _) => { BackColor = Theme.SurfaceContainerLow; Invalidate(); };
    }

    // ── Custom paint ──────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Card background rounded
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Theme.CornerXl);
        using var bg   = new SolidBrush(BackColor);
        g.FillPath(bg, path);
        Region = new Region(path);

        // Progress bar track (at y=98, height=6)
        var trackRect = new RectangleF(Theme.SpaceMd, 98, Width - Theme.SpaceMd * 2, 5);
        using var trackBrush = new SolidBrush(Theme.SurfaceContainerHighest);
        g.FillRoundedRect(trackBrush, trackRect, 3);

        // Progress bar fill
        float fillWidth = trackRect.Width * _progress;
        if (fillWidth > 0)
        {
            var fillRect = new RectangleF(trackRect.X, trackRect.Y, fillWidth, trackRect.Height);
            using var fillBrush = new SolidBrush(_accent);
            g.FillRoundedRect(fillBrush, fillRect, 3);
        }

        // Badge (top-right, e.g. "Theo dõi sát")
        if (_showBadge)
        {
            const int badgePad = 6;
            using var badgeFont = Theme.LabelCode;
            var badgeSize = g.MeasureString(_badgeText, badgeFont);
            var badgeRect = new RectangleF(Width - badgeSize.Width - badgePad * 2 - Theme.SpaceSm,
                                           60, badgeSize.Width + badgePad * 2, 18);
            using var badgeBg = new SolidBrush(_badgeBack == Color.Transparent
                                               ? Theme.WithAlpha(_accent, 40) : _badgeBack);
            g.FillRoundedRect(badgeBg, badgeRect, 3);
            using var badgeFg = new SolidBrush(_badgeFore);
            g.DrawString(_badgeText, badgeFont, badgeFg, badgeRect.X + badgePad, badgeRect.Y + 2);
        }

        // Paint children (labels)
        foreach (Control c in Controls)
            c.Invalidate();
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        int r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// Extension for filling rounded rectangles on Graphics
internal static class GraphicsExtensions
{
    public static void FillRoundedRect(this Graphics g, Brush brush, RectangleF rect, float radius)
    {
        float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

