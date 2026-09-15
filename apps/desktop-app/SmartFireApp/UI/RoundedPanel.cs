using System.Drawing.Drawing2D;

namespace SmartFireApp.UI;

/// <summary>
/// Custom Panel with rounded corners and optional drop shadow.
/// Pure GDI+ — no third-party dependency required.
/// </summary>
public class RoundedPanel : Panel
{
    private int _cornerRadius = Theme.CornerXl;
    private Color _borderColor = Color.Transparent;
    private bool _hasShadow = false;

    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = value; Invalidate(); }
    }

    public Color BorderColor
    {
        get => _borderColor;
        set { _borderColor = value; Invalidate(); }
    }

    public bool HasShadow
    {
        get => _hasShadow;
        set { _hasShadow = value; Invalidate(); }
    }

    public RoundedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.UserPaint, true);
        BackColor = Theme.SurfaceContainerLow;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(2, 2, Width - 4, Height - 4);

        if (_hasShadow)
        {
            // Soft drop shadow via multiple transparent rectangles
            for (int i = 3; i >= 0; i--)
            {
                var shadowRect = new Rectangle(rect.X + i, rect.Y + i, rect.Width, rect.Height);
                using var shadowBrush = new SolidBrush(Color.FromArgb(20 + i * 5, 0, 0, 0));
                using var shadowPath = RoundedPath(shadowRect);
                e.Graphics.FillPath(shadowBrush, shadowPath);
            }
        }

        // Fill background
        using var bgBrush = new SolidBrush(BackColor);
        using var path = RoundedPath(rect);
        e.Graphics.FillPath(bgBrush, path);

        // Border
        if (_borderColor != Color.Transparent)
        {
            using var pen = new Pen(_borderColor, 1f);
            e.Graphics.DrawPath(pen, path);
        }

        // Clip children to rounded region
        Region = new Region(RoundedPath(new Rectangle(0, 0, Width, Height)));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private GraphicsPath RoundedPath(Rectangle rect)
    {
        int r = Math.Min(_cornerRadius, Math.Min(rect.Width, rect.Height) / 2);
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}

