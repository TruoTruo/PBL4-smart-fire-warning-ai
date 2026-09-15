namespace SmartFireApp.UI;

/// <summary>
/// 4-slot KPI counter HUD strip — maps to the Quick KPI Counters in the status banner.
/// Renders 4 mini counter boxes: Sensors, Fires, Heat Warnings, Response time.
/// </summary>
public sealed class KpiCounterStrip : UserControl
{
    private readonly KpiBox[] _boxes;

    public KpiCounterStrip()
    {
        Height    = 72;
        BackColor = Color.Transparent;

        _boxes = [
            new KpiBox { Icon = "⬡", Label = "Cảm biến",    Value = "32", Sub = "/ 32 On",  Accent = Theme.Tertiary },
            new KpiBox { Icon = "⚑", Label = "Đám cháy",    Value = "00", Sub = "Phát hiện", Accent = Theme.OnSurface },
            new KpiBox { Icon = "⬆", Label = "Cảnh báo nhiệt", Value = "01", Sub = "Điểm cao", Accent = Theme.Secondary },
            new KpiBox { Icon = "◎", Label = "Phản hồi",    Value = "120", Sub = "ms",       Accent = Theme.OnSurface },
        ];

        var flow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            BackColor     = Color.Transparent,
        };

        foreach (var box in _boxes)
        {
            box.Width  = 142;
            box.Height = 64;
            box.Margin = new Padding(0, 0, Theme.SpaceSm, 0);
            flow.Controls.Add(box);
        }

        Controls.Add(flow);
    }

    public void SetKpis(string sensors, string sensorSub,
                        string fires,   string fireSub,
                        string heat,    string heatSub,
                        string ms,      string msSub)
    {
        string[] vals = [sensors, fires, heat, ms];
        string[] subs = [sensorSub, fireSub, heatSub, msSub];
        for (int i = 0; i < _boxes.Length; i++)
        {
            _boxes[i].Value = vals[i];
            _boxes[i].Sub   = subs[i];
            _boxes[i].Invalidate();
        }
    }

    // ── Inner cell ────────────────────────────────────────────────────────
    private sealed class KpiBox : UserControl
    {
        public string Icon   { get; init; } = "";
        public string Label  { get; init; } = "";
        public string Value  { get; set;  } = "";
        public string Sub    { get; set;  } = "";
        public Color  Accent { get; init; }

        public KpiBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint  |
                     ControlStyles.UserPaint, true);
            BackColor = Theme.SurfaceContainer;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Rounded background
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Theme.CornerLg);
            using var bg   = new SolidBrush(BackColor);
            g.FillPath(bg, path);
            Region = new Region(path);

            int px = Theme.SpaceMd, py = Theme.SpaceSm;

            // Label line
            using var labelFont = Theme.LabelCode;
            using var labelBrush = new SolidBrush(Theme.OnSurfaceVariant);
            g.DrawString(Label, labelFont, labelBrush, px, py);

            // Value
            using var valueFont  = Theme.MetricValue;
            using var valueBrush = new SolidBrush(Accent);
            g.DrawString(Value, valueFont, valueBrush, px, py + 16);

            // Sub
            var subSize = g.MeasureString(Value, valueFont);
            using var subFont  = Theme.LabelCode;
            using var subBrush = new SolidBrush(Theme.OnSurfaceVariant);
            g.DrawString(Sub, subFont, subBrush, px + subSize.Width + 2, py + 19);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle rect, int r)
        {
            r = Math.Min(r, Math.Min(rect.Width, rect.Height) / 2);
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            p.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            p.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            p.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}

