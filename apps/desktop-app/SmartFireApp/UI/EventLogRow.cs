using System.Drawing.Drawing2D;
using SmartFireApp.Models;

namespace SmartFireApp.UI;

/// <summary>
/// Event log row — maps to the Incident Row items in the HTML event log section.
/// Shows: timestamp badge (severity-colored) | body text (2 lines) | status badge.
/// Hover → surface lightens.
/// </summary>
public sealed class EventLogRow : UserControl
{
    private Color  _accent  = Theme.Tertiary;
    private string _time    = "";
    private string _title   = "";
    private string _detail  = "";
    private string _status  = "";
    private bool   _isHover = false;

    public EventLogRow()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.UserPaint, true);
        Height    = 68;
        BackColor = Theme.SurfaceContainer;
        Cursor    = Cursors.Hand;
        Margin    = new Padding(0, 0, 0, Theme.SpaceSm);

        MouseEnter += (_, _) => { _isHover = true;  Invalidate(); };
        MouseLeave += (_, _) => { _isHover = false; Invalidate(); };
    }

    public void SetAlert(AlertRecord alert)
    {
        _time   = alert.AlertTime.ToString("HH:mm:ss");
        _title  = $"{alert.ZoneName} — {alert.AlertType}";
        _detail = $"{alert.DeviceId} • {(alert.Acknowledged ? "Đã xử lý" : "Chưa xác nhận")}";
        _status = alert.SmsSent ? "SMS đã gửi" : (alert.Acknowledged ? "Đã xử lý" : "Chờ xác nhận");
        _accent = Theme.SeverityColor(alert.Severity);
        Invalidate();
    }

    public void SetSystem(string time, string title, string detail, string status)
    {
        _time   = time;
        _title  = title;
        _detail = detail;
        _status = status;
        _accent = Theme.Tertiary;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Background
        var bg = _isHover ? Theme.SurfaceContainerHigh : Theme.SurfaceContainer;
        using var bgBrush = new SolidBrush(bg);
        using var path    = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Theme.CornerLg);
        g.FillPath(bgBrush, path);
        Region = new Region(path);

        int py = 12;

        // Timestamp badge (left, 80px wide)
        const int badgeW = 84;
        var badgeRect = new RectangleF(Theme.SpaceSm, py, badgeW, 22);
        using var badgeBg = new SolidBrush(Theme.WithAlpha(_accent, 50));
        g.FillRoundedRect(badgeBg, badgeRect, 4);
        using var timeFg   = new SolidBrush(_accent);
        using var timeFont = Theme.LabelCode;
        g.DrawString(_time, timeFont, timeFg,
                     badgeRect.X + 6, badgeRect.Y + 4);

        // Title text
        int bodyX = Theme.SpaceSm + badgeW + Theme.SpaceMd;
        int statusW = 100;
        int bodyW   = Width - bodyX - statusW - Theme.SpaceMd;

        using var titleFg   = new SolidBrush(Theme.OnSurface);
        using var titleFont = Theme.TitleSm;
        g.DrawString(_title, titleFont, titleFg, bodyX, py - 1);

        // Detail line
        using var detailFg   = new SolidBrush(Theme.OnSurfaceVariant);
        using var detailFont = Theme.BodySm;
        g.DrawString(_detail, detailFont, detailFg, bodyX, py + 18);

        // Status badge (right-aligned)
        using var statusFont = Theme.LabelCode;
        var statusSize = g.MeasureString(_status, statusFont);
        float sx = Width - statusSize.Width - Theme.SpaceMd - Theme.SpaceSm;
        float sy = (Height - 20) / 2f;
        var statusRect = new RectangleF(sx - 6, sy, statusSize.Width + 12, 20);
        using var statusBg = new SolidBrush(Theme.WithAlpha(_accent, 30));
        g.FillRoundedRect(statusBg, statusRect, 4);
        using var statusFg = new SolidBrush(_accent);
        g.DrawString(_status, statusFont, statusFg, sx, sy + 3);
    }

    private static GraphicsPath RoundedPath(Rectangle rect, int r)
    {
        r = Math.Min(r, Math.Min(rect.Width, rect.Height) / 2);
        var p = new GraphicsPath();
        p.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        p.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        p.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }
}

