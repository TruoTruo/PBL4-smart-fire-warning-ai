/*
 * THESIS: SOC fire-watch dashboard for a distributed IoT sensor network.
 *   Refuses the "big number on a white card" default; puts continuous telemetry front and center.
 *
 * OWN-WORLD: Near-black ground (#0f131c), 3-layer surface system,
 *   tricolor semantic roles (Red=alarm, Orange=heat/warning, Cyan=online/OK),
 *   JetBrains Mono for all metrics/codes, Segoe UI for body/title text.
 *
 * STORY: SOC operator opens app → reads system status banner → checks 4 metric cards →
 *   scans telemetry chart → reviews event log → triggers quick action if needed.
 *
 * FIRST VIEWPORT: Sidebar 256px (darkest surface) fixed left | Header 64px fixed top |
 *   Scrollable content: status banner → 4-card metric row → chart → event log + action cockpit.
 *
 * FORM: Operate mode. Familiar sidebar+top-bar shell. Native WinForms, pure GDI+.
 *
 * FINISH: unreviewed and undocumented is unfinished;
 *   this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance.
 */

using SmartFireApp.Models;
using SmartFireApp.Services;
using SmartFireApp.UI;

namespace SmartFireApp.Forms;

public sealed class DashboardForm : Form
{
    // ── Services ──────────────────────────────────────────────────────────
    private readonly DashboardDataService _data = new();

    // ── Live update labels ────────────────────────────────────────────────
    private readonly Label _clock       = new();
    private readonly Label _tempValue   = new();
    private readonly Label _humValue    = new();
    private readonly Label _gasValue    = new();

    // ── Custom controls ───────────────────────────────────────────────────
    private readonly MetricCard       _cardTemp  = new();
    private readonly MetricCard       _cardHum   = new();
    private readonly MetricCard       _cardGas   = new();
    private readonly MetricCard       _cardRisk  = new();
    private readonly KpiCounterStrip  _kpiStrip  = new();
    private readonly FlowLayoutPanel  _eventFlow = new();

    // ── Pulse dot animation (sidebar WebSocket indicator) ─────────────────
    private bool  _pulseGrow = true;
    private float _pulseR    = 0f;
    private Panel? _pulseDot;

    // ── Timers ────────────────────────────────────────────────────────────
    private readonly System.Windows.Forms.Timer _clockTimer  = new() { Interval = 1_000 };
    private readonly System.Windows.Forms.Timer _dataTimer   = new() { Interval = 2_000 };
    private readonly System.Windows.Forms.Timer _pulseTimer  = new() { Interval = 60 };

    // ── Constructor ───────────────────────────────────────────────────────
    public DashboardForm()
    {
        Text         = "FireGuard IoT — PBL System Monitor";
        BackColor    = Theme.Background;
        ForeColor    = Theme.OnSurface;
        Font         = Theme.BodyMd;
        MinimumSize  = new Size(1280, 780);
        WindowState  = FormWindowState.Maximized;
        Icon         = SystemIcons.Shield;

        BuildLayout();
        LoadInitialData();

        _clockTimer.Tick  += (_, _) => UpdateClock();
        _dataTimer.Tick   += (_, _) => RefreshLiveValues();
        _pulseTimer.Tick  += OnPulseTick;

        _clockTimer.Start();
        _dataTimer.Start();
        _pulseTimer.Start();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  LAYOUT BUILDER
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildLayout()
    {
        SuspendLayout();

        // ── Sidebar ───────────────────────────────────────────────────────
        var sidebar = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 256,
            BackColor = Theme.SurfaceContainerLowest,
            Padding   = new Padding(0),
        };
        sidebar.Controls.Add(BuildSidebar());
        Controls.Add(sidebar);

        // ── Header ────────────────────────────────────────────────────────
        var header = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 64,
            BackColor = Theme.SurfaceContainerLowest,
            Padding   = new Padding(Theme.SpaceLg, 0, Theme.SpaceLg, 0),
        };
        header.Controls.Add(BuildHeader());
        Controls.Add(header);

        // ── Content scroll area ───────────────────────────────────────────
        var scroll = new Panel
        {
            Dock        = DockStyle.Fill,
            BackColor   = Theme.Background,
            AutoScroll  = true,
            Padding     = new Padding(Theme.SpaceLg, Theme.SpaceLg, Theme.SpaceMd, Theme.SpaceLg),
        };
        scroll.Controls.Add(BuildContent());
        Controls.Add(scroll);

        ResumeLayout();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  SIDEBAR
    // ─────────────────────────────────────────────────────────────────────
    private Control BuildSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) };

        // ── Brand bar ────────────────────────────────────────────────────
        var brandBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 64,
            BackColor = Theme.SurfaceContainerLowest,
            Padding   = new Padding(Theme.SpaceMd, 0, Theme.SpaceMd, 0),
        };
        var brandName = MakeLabel("FireGuard IoT", Theme.TitleMd, Theme.OnSurface);
        brandName.Location = new Point(Theme.SpaceMd, 14);
        brandName.AutoSize = true;
        var brandSub = MakeLabel("PBL SYSTEM MONITOR", Theme.LabelCode, Theme.Tertiary);
        brandSub.Location = new Point(Theme.SpaceMd, 36);
        brandSub.AutoSize = true;
        brandBar.Controls.AddRange([brandName, brandSub]);
        panel.Controls.Add(brandBar);

        // ── Network status badge ──────────────────────────────────────────
        var networkBadge = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 36,
            BackColor = Theme.SurfaceContainer,
            Margin    = new Padding(Theme.SpaceMd),
            Padding   = new Padding(Theme.SpaceSm),
        };
        var networkLeft  = MakeLabel("MẠNG CẢM BIẾN", Theme.LabelCode, Theme.OnSurfaceVariant);
        networkLeft.Location = new Point(12, 10);
        networkLeft.AutoSize = true;
        var networkRight = MakeLabel("CH-01 • ONLINE", Theme.LabelCode, Theme.Secondary);
        networkRight.Location = new Point(256 - 110, 10);
        networkRight.AutoSize = true;
        networkBadge.Controls.AddRange([networkLeft, networkRight]);
        // Wrap badge in a panel with margin
        var badgeWrap = new Panel
        {
            Dock    = DockStyle.Top,
            Height  = 48,
            BackColor = Theme.SurfaceContainerLowest,
            Padding = new Padding(Theme.SpaceMd, Theme.SpaceSm, Theme.SpaceMd, Theme.SpaceSm),
        };
        badgeWrap.Controls.Add(networkBadge);
        networkBadge.Dock = DockStyle.Fill;
        panel.Controls.Add(badgeWrap);

        // ── Navigation ────────────────────────────────────────────────────
        var navItems = new[]
        {
            ("Tổng quan",           true),
            ("Danh sách cảm biến",  false),
            ("Lịch sử cảnh báo",    false),
            ("Bản đồ khu vực",      false),
            ("Báo cáo & Phân tích", false),
            ("Cài đặt ngưỡng",      false),
        };

        var navFlow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = navItems.Length * 44 + Theme.SpaceSm * 2,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            BackColor     = Theme.SurfaceContainerLowest,
            Padding       = new Padding(Theme.SpaceMd, Theme.SpaceSm, Theme.SpaceMd, Theme.SpaceSm),
        };

        foreach (var (text, active) in navItems)
        {
            var btn = new Button
            {
                Text       = "  " + text,
                FlatStyle  = FlatStyle.Flat,
                TextAlign  = ContentAlignment.MiddleLeft,
                Font       = active ? Theme.TitleSm : Theme.BodyMd,
                ForeColor  = active ? Theme.OnPrimaryContainer : Theme.OnSurfaceVariant,
                BackColor  = active ? Theme.PrimaryContainer    : Color.Transparent,
                Size       = new Size(224, 40),
                Margin     = new Padding(0, 0, 0, 4),
                Cursor     = Cursors.Hand,
            };
            btn.FlatAppearance.BorderSize    = 0;
            btn.FlatAppearance.MouseOverBackColor =
                active ? Theme.Blend(Theme.PrimaryContainer, Color.White, 0.05f)
                       : Theme.SurfaceContainerHigh;

            if (!active)
            {
                btn.Click += (_, _) =>
                    MessageBox.Show(
                        "Màn hình này sẽ được triển khai ở sprint tiếp theo.",
                        "FireGuard IoT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            navFlow.Controls.Add(btn);
        }
        panel.Controls.Add(navFlow);

        // ── WebSocket live indicator (bottom) ─────────────────────────────
        var liveBox = new RoundedPanel
        {
            Dock         = DockStyle.Bottom,
            Height       = 80,
            BackColor    = Theme.SurfaceContainerLow,
            CornerRadius = Theme.CornerXl,
            Margin       = new Padding(Theme.SpaceMd),
        };
        // Wrap with margin
        var liveWrap = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 96,
            BackColor = Theme.SurfaceContainerLowest,
            Padding   = new Padding(Theme.SpaceMd),
        };
        liveBox.Dock = DockStyle.Fill;

        _pulseDot = new Panel
        {
            Size      = new Size(8, 8),
            Location  = new Point(Theme.SpaceMd, (80 - 8) / 2 - 8),
            BackColor = Theme.Tertiary,
        };
        // Make it circular via Paint
        _pulseDot.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Theme.WithAlpha(Theme.Tertiary, (int)(80 - _pulseR * 8)));
            pe.Graphics.FillEllipse(b, -(_pulseR), -(_pulseR), 8 + _pulseR * 2, 8 + _pulseR * 2);
            using var core = new SolidBrush(Theme.Tertiary);
            pe.Graphics.FillEllipse(core, 0, 0, 8, 8);
        };

        var liveName = MakeLabel("WebSocket Live", Theme.LabelCode, Theme.OnSurface);
        liveName.Location = new Point(Theme.SpaceMd + 14, 12);
        liveName.AutoSize = true;
        var liveLatency = MakeLabel("24ms", Theme.LabelCode, Theme.Tertiary);
        liveLatency.Location = new Point(256 - 50, 12);
        liveLatency.AutoSize = true;
        var liveDetail = MakeLabel("Hệ thống: Trực tuyến ổn định\nQua giao thức MQTT/WSS", Theme.BodySm, Theme.OnSurfaceVariant);
        liveDetail.Location = new Point(Theme.SpaceMd, 34);
        liveDetail.Size     = new Size(220, 36);

        liveBox.Controls.AddRange([_pulseDot, liveName, liveLatency, liveDetail]);
        liveWrap.Controls.Add(liveBox);
        panel.Controls.Add(liveWrap);

        return panel;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  HEADER
    // ─────────────────────────────────────────────────────────────────────
    private Control BuildHeader()
    {
        var container = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 1,
            BackColor   = Color.Transparent,
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // ── Left: search ──────────────────────────────────────────────────
        var leftFlow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor     = Color.Transparent,
            Padding       = new Padding(0, 14, 0, 0),
        };
        var searchBox = new TextBox
        {
            Width            = 260,
            Height           = 32,
            BackColor        = Theme.SurfaceContainerHigh,
            ForeColor        = Theme.OnSurface,
            BorderStyle      = BorderStyle.None,
            Font             = Theme.BodySm,
            PlaceholderText  = "  Tìm Node ID, Zone, Tầng...",
            Padding          = new Padding(6),
        };
        leftFlow.Controls.Add(searchBox);

        // ── Right: clock, alerts, button, user ───────────────────────────
        var rightFlow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor     = Color.Transparent,
            Padding       = new Padding(0, 14, 0, 0),
        };

        // User avatar chip
        var userChip = new Panel
        {
            Width     = 160,
            Height    = 36,
            BackColor = Color.Transparent,
            Margin    = new Padding(0, 0, Theme.SpaceSm, 0),
        };
        var avatar = new Panel
        {
            Size      = new Size(32, 32),
            Location  = new Point(128, 2),
            BackColor = Theme.Primary,
        };
        avatar.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Theme.Primary);
            pe.Graphics.FillEllipse(b, 0, 0, 32, 32);
            using var fb = new SolidBrush(Theme.OnPrimary);
            using var f  = Theme.TitleSm;
            var s = pe.Graphics.MeasureString("A", f);
            pe.Graphics.DrawString("A", f, fb, (32 - s.Width) / 2, (32 - s.Height) / 2);
        };
        var userName = MakeLabel("Nguyễn Văn An", Theme.TitleSm, Theme.OnSurface);
        userName.Location = new Point(0, 2);
        userName.AutoSize = true;
        var userRole = MakeLabel("Trực ban SOC • Ca 01", Theme.LabelCode, Theme.Secondary);
        userRole.Location = new Point(0, 20);
        userRole.AutoSize = true;
        userChip.Controls.AddRange([avatar, userName, userRole]);

        // Silence button
        var silenceBtn = MakeButton("Tắt chuông khẩn cấp", Theme.SecondaryContainer, Theme.OnSecondaryContainer,
            (_, _) => MessageBox.Show("Chuông cảnh báo đã được tắt trên giao diện.", "FireGuard IoT"));
        silenceBtn.Width  = 175;
        silenceBtn.Height = 36;
        silenceBtn.Margin = new Padding(0, 0, Theme.SpaceSm, 0);

        // Alert badge
        var alertBadge = new Panel
        {
            Width     = 170,
            Height    = 36,
            BackColor = Theme.ErrorContainer,
            Margin    = new Padding(0, 0, Theme.SpaceSm, 0),
        };
        alertBadge.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b    = new SolidBrush(Theme.OnErrorContainer);
            using var font = Theme.LabelCode;
            var text = "⚠  Cảnh báo: 0 Sự cố";
            var size = pe.Graphics.MeasureString(text, font);
            pe.Graphics.DrawString(text, font, b, (alertBadge.Width - size.Width) / 2f, (36 - size.Height) / 2f);
        };

        // Clock
        _clock.Font      = Theme.LabelCode;
        _clock.ForeColor = Theme.OnSurface;
        _clock.BackColor = Theme.SurfaceContainer;
        _clock.Width     = 180;
        _clock.Height    = 36;
        _clock.TextAlign = ContentAlignment.MiddleCenter;
        _clock.Margin    = new Padding(0, 0, Theme.SpaceSm, 0);
        UpdateClock();

        rightFlow.Controls.AddRange([userChip, silenceBtn, alertBadge, _clock]);
        container.Controls.Add(leftFlow, 0, 0);
        container.Controls.Add(rightFlow, 1, 0);
        return container;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  CONTENT
    // ─────────────────────────────────────────────────────────────────────
    private Control BuildContent()
    {
        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            BackColor     = Theme.Background,
        };

        col.Controls.Add(BuildStatusBanner());
        col.Controls.Add(Spacer(0, Theme.SpaceMd));
        col.Controls.Add(BuildMetricCards());
        col.Controls.Add(Spacer(0, Theme.SpaceMd));
        col.Controls.Add(BuildChartSection());
        col.Controls.Add(Spacer(0, Theme.SpaceMd));
        col.Controls.Add(BuildBottomSection());
        col.Controls.Add(Spacer(0, Theme.SpaceLg));

        return col;
    }

    // ── Section 1: Status Banner ──────────────────────────────────────────
    private Control BuildStatusBanner()
    {
        var banner = new RoundedPanel
        {
            Width        = EffectiveContentWidth(),
            Height       = 100,
            BackColor    = Theme.SurfaceContainerLow,
            CornerRadius = Theme.CornerXl,
            Margin       = new Padding(0),
        };

        // Status badge (left)
        var statusDot = new Panel { Size = new Size(10, 10), Location = new Point(16, 44), BackColor = Theme.Secondary };
        statusDot.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Theme.Secondary);
            pe.Graphics.FillEllipse(b, 0, 0, 10, 10);
        };

        var statusGroup = new Panel
        {
            Location  = new Point(10, Theme.SpaceMd),
            Size      = new Size(200, 66),
            BackColor = Theme.SurfaceContainerHighest,
        };
        statusGroup.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bp = RoundedGPath(new Rectangle(0, 0, statusGroup.Width - 1, statusGroup.Height - 1), Theme.CornerLg);
            using var b  = new SolidBrush(Theme.SurfaceContainerHighest);
            pe.Graphics.FillPath(b, bp);
            statusGroup.Region = new Region(bp);
        };
        var statusCaption = MakeLabel("Tình trạng hệ thống", Theme.LabelCode, Theme.OnSurfaceVariant);
        statusCaption.Location = new Point(Theme.SpaceMd + 14, 8);
        statusCaption.AutoSize = true;
        var statusValue = MakeLabel("BÌNH THƯỜNG • GIÁM SÁT 24/7", Theme.TitleSm, Theme.OnSurface);
        statusValue.Location = new Point(Theme.SpaceMd, 28);
        statusValue.AutoSize = true;
        var statusDot2 = new Panel { Location = new Point(Theme.SpaceMd, 14), Size = new Size(8, 8), BackColor = Theme.Secondary };
        statusDot2.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Theme.Secondary);
            pe.Graphics.FillEllipse(b, 0, 0, 8, 8);
        };
        statusGroup.Controls.AddRange([statusDot2, statusCaption, statusValue]);

        // Zone badge
        var zoneBadge = new Panel
        {
            Location  = new Point(220, Theme.SpaceMd),
            Size      = new Size(310, 36),
            BackColor = Theme.SurfaceContainer,
        };
        zoneBadge.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bp = RoundedGPath(new Rectangle(0, 0, zoneBadge.Width - 1, zoneBadge.Height - 1), Theme.CornerLg);
            using var b  = new SolidBrush(Theme.SurfaceContainer);
            pe.Graphics.FillPath(b, bp);
            zoneBadge.Region = new Region(bp);
            using var font   = Theme.BodySm;
            using var normal = new SolidBrush(Theme.OnSurface);
            using var accent = new SolidBrush(Theme.Secondary);
            pe.Graphics.DrawString("Vùng trọng điểm:", font, normal, Theme.SpaceSm, 10);
            pe.Graphics.DrawString("Xưởng Gia Công A - Block C2", font, accent, 108, 10);
        };

        // Safety level badge
        var safetyBadge = new Panel
        {
            Location  = new Point(540, Theme.SpaceMd),
            Size      = new Size(210, 36),
            BackColor = Theme.SurfaceContainer,
        };
        safetyBadge.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bp = RoundedGPath(new Rectangle(0, 0, safetyBadge.Width - 1, safetyBadge.Height - 1), Theme.CornerLg);
            using var b  = new SolidBrush(Theme.SurfaceContainer);
            pe.Graphics.FillPath(b, bp);
            safetyBadge.Region = new Region(bp);
            using var font   = Theme.LabelCode;
            using var accent = new SolidBrush(Theme.Tertiary);
            pe.Graphics.DrawString("Mức An Toàn: Cấp 1 (Ổn Định)", font, accent, Theme.SpaceSm, 11);
        };

        // KPI strip (right)
        _kpiStrip.Location = new Point(760, Theme.SpaceMd);
        _kpiStrip.Width    = 620;
        _kpiStrip.Height   = 64;

        banner.Controls.AddRange([statusGroup, zoneBadge, safetyBadge, _kpiStrip]);
        return banner;
    }

    // ── Section 2: Metric Cards ───────────────────────────────────────────
    private Control BuildMetricCards()
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            BackColor     = Theme.Background,
            Height        = 160,
            Width         = EffectiveContentWidth(),
            Margin        = new Padding(0),
        };

        // Card sizing
        int cardW = (EffectiveContentWidth() - Theme.SpaceMd * 3) / 4;
        foreach (var card in new[] { _cardTemp, _cardHum, _cardGas, _cardRisk })
        {
            card.Width  = cardW;
            card.Height = 152;
            card.Margin = new Padding(0, 0, Theme.SpaceMd, 0);
        }

        // Temperature (secondary/orange accent)
        _cardTemp.SetData(
            nodeLabel:     "Node #T-104 • Xưởng A",
            title:         "Nhiệt độ môi trường",
            value:         "34.8",
            unit:          "°C",
            progressRatio: 34.8f / 55f,
            hint:          "Ngưỡng nguy hiểm: > 55.0 °C",
            trendText:     "Tăng nhẹ +1.2°C so với 10p trước",
            accent:        Theme.Secondary);

        // Humidity (tertiary/cyan accent)
        _cardHum.SetData(
            nodeLabel:     "Node #H-202 • Xưởng A",
            title:         "Độ ẩm không khí",
            value:         "48.5",
            unit:          "% RH",
            progressRatio: 0.485f,
            hint:          "Tiêu chuẩn: 40% - 65% RH",
            trendText:     "Khô ráo, nguy cơ bắt lửa trung bình",
            accent:        Theme.Tertiary);

        // Gas (primary/red accent + badge)
        _cardGas.SetData(
            nodeLabel:     "Node #MQ-02 • Trạm Hút",
            title:         "Khói & Khí Gas MQ-2",
            value:         "185",
            unit:          "ppm",
            progressRatio: 185f / 600f,
            hint:          "Ngưỡng >300 cảnh báo | >600 Báo Động",
            trendText:     "MQ-2 Sensor ổn định • Khí lưu thông tốt",
            accent:        Theme.PrimaryContainer,
            badgeText:     "Theo dõi sát",
            badgeBack:     Theme.ErrorContainer,
            badgeFore:     Theme.OnErrorContainer);

        // Fire risk index (on-surface, multi-segment gauge)
        _cardRisk.SetData(
            nodeLabel:     "Thuật toán AI PBL",
            title:         "Chỉ số nguy cơ cháy nổ",
            value:         "18",
            unit:          "/ 100",
            progressRatio: 0.18f,
            hint:          "Thang đo rủi ro: An toàn (0 - 35)",
            trendText:     "Mức THẤP — Hệ thống ổn định",
            accent:        Theme.Tertiary);

        row.Controls.AddRange([_cardTemp, _cardHum, _cardGas, _cardRisk]);
        return row;
    }

    // ── Section 3: Chart ──────────────────────────────────────────────────
    private Control BuildChartSection()
    {
        var card = new RoundedPanel
        {
            Width        = EffectiveContentWidth(),
            Height       = 390,
            BackColor    = Theme.SurfaceContainerLow,
            CornerRadius = Theme.CornerXl,
            Margin       = new Padding(0),
        };

        // Header row
        var titleLabel = MakeLabel("Biểu đồ biến thiên Nhiệt độ, Độ ẩm & Nồng độ khói",
                                   Theme.HeadlineSm, Theme.OnSurface);
        titleLabel.Location = new Point(Theme.SpaceLg, Theme.SpaceMd);
        titleLabel.AutoSize = true;

        var descLabel = MakeLabel("Dòng đo Telemetry thời gian thực từ các trạm cảm biến phân tán (MQTT/WebSocket)",
                                  Theme.BodySm, Theme.OnSurfaceVariant);
        descLabel.Location = new Point(Theme.SpaceLg, 46);
        descLabel.AutoSize = true;

        // Legend chips
        var legendFlow = new FlowLayoutPanel
        {
            Location      = new Point(Theme.SpaceLg, 68),
            Height        = 20,
            Width         = 360,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor     = Color.Transparent,
            AutoSize      = true,
        };
        legendFlow.Controls.AddRange([
            LegendChip("Khói (ppm)",      Theme.PrimaryContainer),
            LegendChip("Nhiệt độ (°C)",   Theme.Secondary),
            LegendChip("Độ ẩm (%)",        Theme.Tertiary),
        ]);

        // Time filter tabs
        var filterFlow = new FlowLayoutPanel
        {
            Location      = new Point(EffectiveContentWidth() - 220, Theme.SpaceMd + 4),
            Height        = 36,
            Width         = 206,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor     = Theme.SurfaceContainer,
            Padding       = new Padding(2),
        };
        filterFlow.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bp = RoundedGPath(new Rectangle(0, 0, filterFlow.Width - 1, filterFlow.Height - 1), Theme.CornerLg);
            using var b  = new SolidBrush(Theme.SurfaceContainer);
            pe.Graphics.FillPath(b, bp);
            filterFlow.Region = new Region(bp);
        };
        foreach (var (label, active) in new[] { ("1 giờ", false), ("6 giờ", true), ("24 giờ", false) })
        {
            var fb = new Button
            {
                Text      = label,
                Width     = 54,
                Height    = 30,
                FlatStyle = FlatStyle.Flat,
                Font      = Theme.LabelCode,
                BackColor = active ? Theme.SurfaceContainerHighest : Color.Transparent,
                ForeColor = active ? Theme.OnSurface : Theme.OnSurfaceVariant,
                Margin    = new Padding(0),
            };
            fb.FlatAppearance.BorderSize = 0;
            filterFlow.Controls.Add(fb);
        }
        // Live indicator tab
        var liveTab = new Button
        {
            Text      = "● Live",
            Width     = 62,
            Height    = 30,
            FlatStyle = FlatStyle.Flat,
            Font      = Theme.LabelCode,
            ForeColor = Theme.Tertiary,
            BackColor = Color.Transparent,
            Margin    = new Padding(0),
        };
        liveTab.FlatAppearance.BorderSize = 0;
        filterFlow.Controls.Add(liveTab);

        // Chart canvas
        var chart = new TelemetryChart
        {
            Location = new Point(Theme.SpaceMd, 96),
            Size     = new Size(EffectiveContentWidth() - Theme.SpaceMd * 2, 260),
            Anchor   = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        };

        // Peak annotation
        var peakNote = MakeLabel("Đỉnh nhiệt độ ca: 36.2 °C lúc 15:18",
                                  Theme.LabelCode, Theme.Secondary);
        peakNote.Location = new Point(EffectiveContentWidth() - 270, 72);
        peakNote.AutoSize = true;

        card.Controls.AddRange([titleLabel, descLabel, legendFlow, filterFlow, chart, peakNote]);
        return card;
    }

    // ── Section 4: Event Log + Action Cockpit ─────────────────────────────
    private Control BuildBottomSection()
    {
        int totalW   = EffectiveContentWidth();
        int logW     = (int)(totalW * 0.64f);
        int cockpitW = totalW - logW - Theme.SpaceMd;

        var row = new Panel
        {
            Width     = totalW,
            Height    = 340,
            BackColor = Theme.Background,
            Margin    = new Padding(0),
        };

        // ── Event log panel ───────────────────────────────────────────────
        var logCard = new RoundedPanel
        {
            Location     = new Point(0, 0),
            Size         = new Size(logW, 336),
            BackColor    = Theme.SurfaceContainerLow,
            CornerRadius = Theme.CornerXl,
        };

        var logTitle = MakeLabel("Nhật ký sự kiện & Cảnh báo gần đây", Theme.TitleMd, Theme.OnSurface);
        logTitle.Location = new Point(Theme.SpaceMd, Theme.SpaceMd);
        logTitle.AutoSize = true;

        var autoLabel = MakeLabel("Cập nhật tự động (WSS)", Theme.LabelCode, Theme.OnSurfaceVariant);
        autoLabel.Location = new Point(logW - 180, Theme.SpaceMd + 2);
        autoLabel.AutoSize = true;

        _eventFlow.Location      = new Point(Theme.SpaceMd, 48);
        _eventFlow.Size          = new Size(logW - Theme.SpaceMd * 2, 248);
        _eventFlow.FlowDirection = FlowDirection.TopDown;
        _eventFlow.WrapContents  = false;
        _eventFlow.AutoScroll    = true;
        _eventFlow.BackColor     = Color.Transparent;

        var seeAll = new Button
        {
            Text      = "Xem toàn bộ lịch sử →",
            Location  = new Point(logW - 180, 302),
            FlatStyle = FlatStyle.Flat,
            Font      = Theme.LabelCode,
            ForeColor = Theme.Tertiary,
            BackColor = Color.Transparent,
            AutoSize  = true,
        };
        seeAll.FlatAppearance.BorderSize = 0;
        seeAll.Click += (_, _) =>
            MessageBox.Show("Màn hình Lịch sử cảnh báo sẽ được triển khai sprint tiếp theo.", "FireGuard IoT");

        var countLabel = MakeLabel("Hiển thị 4 trên 48 sự kiện trong ca", Theme.BodySm, Theme.OnSurfaceVariant);
        countLabel.Location = new Point(Theme.SpaceMd, 306);
        countLabel.AutoSize = true;

        logCard.Controls.AddRange([logTitle, autoLabel, _eventFlow, countLabel, seeAll]);
        row.Controls.Add(logCard);

        // ── Action cockpit ────────────────────────────────────────────────
        var cockpit = new RoundedPanel
        {
            Location     = new Point(logW + Theme.SpaceMd, 0),
            Size         = new Size(cockpitW, 336),
            BackColor    = Theme.SurfaceContainerLow,
            CornerRadius = Theme.CornerXl,
        };

        var cockpitTitle = MakeLabel("Tác vụ khẩn cấp & Điều hành", Theme.TitleMd, Theme.OnSurface);
        cockpitTitle.Location = new Point(Theme.SpaceMd, Theme.SpaceMd);
        cockpitTitle.AutoSize = true;

        var cockpitDesc = MakeLabel(
            "Chỉ huy trực tiếp thiết bị ngoại vi,\nvan ngắt gas và hệ thống loa còi.",
            Theme.BodySm, Theme.OnSurfaceVariant);
        cockpitDesc.Location = new Point(Theme.SpaceMd, 40);
        cockpitDesc.Size     = new Size(cockpitW - Theme.SpaceMd * 2, 36);

        // Siren button
        var sirenBtn = BuildActionButton(
            "Kiểm tra chuông báo",
            "Kích hoạt còi thử nghiệm 3s",
            Theme.SecondaryContainer,
            (_, _) => HandleSirenTest());
        sirenBtn.Location = new Point(Theme.SpaceMd, 84);
        sirenBtn.Size     = new Size(cockpitW - Theme.SpaceMd * 2, 58);

        // Export button
        var exportBtn = BuildActionButton(
            "Xuất báo cáo ca trực",
            "Định dạng Excel / PDF có chữ ký số",
            Theme.TertiaryContainer,
            (_, _) => ExportReport());
        exportBtn.Location = new Point(Theme.SpaceMd, 150);
        exportBtn.Size     = new Size(cockpitW - Theme.SpaceMd * 2, 58);

        // Emergency safety zone
        var safetyZone = new Panel
        {
            Location  = new Point(Theme.SpaceMd, 220),
            Size      = new Size(cockpitW - Theme.SpaceMd * 2, 100),
            BackColor = Theme.WithAlpha(Theme.ErrorContainer, 40),
        };
        safetyZone.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bp = RoundedGPath(new Rectangle(0, 0, safetyZone.Width - 1, safetyZone.Height - 1), Theme.CornerLg);
            using var b  = new SolidBrush(Theme.WithAlpha(Theme.ErrorContainer, 40));
            pe.Graphics.FillPath(b, bp);
            safetyZone.Region = new Region(bp);
        };

        var safetyLabel = MakeLabel("⚠  CƠ CHẾ AN TOÀN KHẨN CẤP", Theme.LabelCode, Theme.PrimaryContainer);
        safetyLabel.Location = new Point(Theme.SpaceSm, Theme.SpaceSm);
        safetyLabel.AutoSize = true;

        var emergencyBtn = MakeButton(
            "Ngắt nguồn điện & Khóa Van Gas",
            Theme.PrimaryContainer, Theme.OnPrimaryContainer,
            (_, _) => ConfirmEmergency());
        emergencyBtn.Location = new Point(Theme.SpaceSm, 28);
        emergencyBtn.Size     = new Size(safetyZone.Width - Theme.SpaceSm * 2, 42);

        var authNote = MakeLabel("Yêu cầu xác thực khóa bảo vệ hai lớp", Theme.BodySm, Theme.OnSurfaceVariant);
        authNote.Location = new Point(Theme.SpaceSm, 76);
        authNote.AutoSize = true;

        safetyZone.Controls.AddRange([safetyLabel, emergencyBtn, authNote]);

        // Broker status
        var brokerBar = new Panel
        {
            Location  = new Point(Theme.SpaceMd, 326),
            Size      = new Size(cockpitW - Theme.SpaceMd * 2, 0), // hidden, cockpit height fits
            BackColor = Color.Transparent,
        };

        cockpit.Controls.AddRange([cockpitTitle, cockpitDesc, sirenBtn, exportBtn, safetyZone]);
        row.Controls.Add(cockpit);

        return row;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA & EVENTS
    // ═══════════════════════════════════════════════════════════════════════

    private void LoadInitialData()
    {
        RefreshLiveValues();
        UpdateClock();

        foreach (var alert in _data.GetRecentAlerts())
        {
            var row = new EventLogRow { Width = _eventFlow.Width - 8 };
            row.SetAlert(alert);
            _eventFlow.Controls.Add(row);
        }
    }

    private void RefreshLiveValues()
    {
        var r   = _data.GetLatestReading();
        var kpi = _data.GetKpiSnapshot();

        // Update metric cards
        _cardTemp.SetData("Node #T-104 • Xưởng A", "Nhiệt độ môi trường",
            $"{r.Temperature:0.0}", "°C", (float)(r.Temperature / 55.0),
            "Ngưỡng nguy hiểm: > 55.0 °C",
            r.Temperature > 40 ? $"⚠ Cao: {r.Temperature:0.0}°C" : $"Tăng nhẹ {r.Temperature - 33:+0.0;-0.0}°C so với 10p",
            Theme.Secondary);

        _cardHum.SetData("Node #H-202 • Xưởng A", "Độ ẩm không khí",
            $"{r.Humidity:0.0}", "% RH", (float)(r.Humidity / 100.0),
            "Tiêu chuẩn: 40% - 65% RH",
            r.Humidity is > 40 and < 65 ? "Khô ráo, nguy cơ bắt lửa trung bình" : "Ngoài ngưỡng khuyến nghị",
            Theme.Tertiary);

        _cardGas.SetData("Node #MQ-02 • Trạm Hút", "Khói & Khí Gas MQ-2",
            $"{r.GasPpm:0}", "ppm", (float)(r.GasPpm / 600.0),
            "Ngưỡng >300 cảnh báo | >600 Báo Động",
            "MQ-2 Sensor ổn định • Khí lưu thông tốt",
            Theme.PrimaryContainer,
            r.GasPpm > 300 ? "CẢNH BÁO" : "Theo dõi sát",
            Theme.WithAlpha(Theme.ErrorContainer, 200), Theme.OnErrorContainer);

        _kpiStrip.SetKpis(
            $"{kpi.SensorsOnline}", $"/ {kpi.SensorsTotal} On",
            kpi.FireDetected.ToString("D2"), "Phát hiện",
            kpi.HeatWarnings.ToString("D2"), "Điểm cao",
            $"{kpi.ResponseMs}", "ms");
    }

    private void UpdateClock() =>
        _clock.Text = DateTime.Now.ToString("HH:mm:ss • dd/MM/yyyy");

    private void OnPulseTick(object? s, EventArgs e)
    {
        if (_pulseGrow) { _pulseR += 0.8f; if (_pulseR > 5f) _pulseGrow = false; }
        else            { _pulseR -= 0.8f; if (_pulseR < 0f) _pulseGrow = true;  }
        _pulseDot?.Invalidate();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  QUICK ACTIONS
    // ─────────────────────────────────────────────────────────────────────
    private void HandleSirenTest()
    {
        MessageBox.Show(
            "Đã gửi lệnh thử còi cảnh báo (demo mode).\nActuator sẽ được tích hợp sau khi nhóm thống nhất MQTT control topic.",
            "Kiểm tra chuông", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ConfirmEmergency()
    {
        var result = MessageBox.Show(
            "CẢNH BÁO NGUY CẤP\n\nBạn có chắc chắn muốn:\n" +
            "  • Ngắt nguồn điện toàn khu\n  • Kích hoạt khóa van gas khẩn cấp?\n\n" +
            "Thao tác này yêu cầu xác thực hai lớp khi kết nối actuator.",
            "Cơ chế an toàn khẩn cấp",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
            MessageBox.Show(
                "Chức năng này chưa phát lệnh MQTT vì dự án chưa có control topic/actuator.\n" +
                "Không có thiết bị nào bị điều khiển.",
                "Thao tác bị chặn", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ExportReport()
    {
        using var dialog = new SaveFileDialog
        {
            Filter   = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"bao-cao-ca-truc-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        var lines = new[] { "AlertId,DeviceId,Zone,AlertType,Severity,AlertTime,Acknowledged,SmsSent" }
            .Concat(_data.GetRecentAlerts().Select(x =>
                $"{x.AlertId},{x.DeviceId},{x.ZoneName},{x.AlertType},{x.Severity},{x.AlertTime:O},{x.Acknowledged},{x.SmsSent}"));
        File.WriteAllLines(dialog.FileName, lines);
        MessageBox.Show($"Đã xuất báo cáo CSV:\n{dialog.FileName}", "FireGuard IoT");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FACTORY HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static Label MakeLabel(string text, Font font, Color color) =>
        new() { Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent, AutoSize = true };

    private static Button MakeButton(string text, Color back, Color fore, EventHandler handler)
    {
        var btn = new Button
        {
            Text      = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font      = Theme.TitleSm,
            Cursor    = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += handler;
        return btn;
    }

    /// <summary>Compound action button with title + description lines.</summary>
    private static Panel BuildActionButton(string title, string desc, Color iconBg, EventHandler handler)
    {
        var panel = new Panel { BackColor = Theme.SurfaceContainer, Cursor = Cursors.Hand };
        panel.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bp = RoundedGPath(new Rectangle(0, 0, panel.Width - 1, panel.Height - 1), Theme.CornerLg);
            using var b  = new SolidBrush(panel.BackColor);
            pe.Graphics.FillPath(b, bp);
            panel.Region = new Region(bp);
        };

        // Icon box
        var iconBox = new Panel
        {
            Location  = new Point(Theme.SpaceMd, 10),
            Size      = new Size(36, 36),
            BackColor = Theme.WithAlpha(iconBg, 80),
        };
        iconBox.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bp = RoundedGPath(new Rectangle(0, 0, 35, 35), Theme.CornerLg);
            using var b  = new SolidBrush(iconBox.BackColor);
            pe.Graphics.FillPath(b, bp);
            iconBox.Region = new Region(bp);
        };

        var titleLabel = MakeLabel(title, Theme.TitleSm, Theme.OnSurface);
        titleLabel.Location = new Point(Theme.SpaceMd + 44, 12);
        var descLabel  = MakeLabel(desc,  Theme.BodySm,  Theme.OnSurfaceVariant);
        descLabel.Location  = new Point(Theme.SpaceMd + 44, 30);

        var chevron = MakeLabel("›", Theme.HeadlineMd, Theme.OnSurfaceVariant);
        chevron.TextAlign = ContentAlignment.MiddleRight;

        panel.Controls.AddRange([iconBox, titleLabel, descLabel, chevron]);
        panel.MouseEnter += (_, _) => { panel.BackColor = Theme.SurfaceContainerHigh; };
        panel.MouseLeave += (_, _) => { panel.BackColor = Theme.SurfaceContainer; };
        panel.Click      += handler;
        foreach (Control c in panel.Controls) c.Click += handler;

        panel.Resize += (_, _) =>
        {
            chevron.Location = new Point(panel.Width - 24, (panel.Height - 24) / 2);
            chevron.Size = new Size(20, 24);
        };

        return panel;
    }

    private static Panel LegendChip(string label, Color color)
    {
        var chip = new Panel { Width = 105, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 0, Theme.SpaceMd, 0) };
        chip.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(color);
            pe.Graphics.FillRectangle(b, 0, 7, 12, 6);
            using var tb = new SolidBrush(Theme.OnSurfaceVariant);
            using var f  = Theme.BodySm;
            pe.Graphics.DrawString(label, f, tb, 16, 2);
        };
        return chip;
    }

    private static Panel Spacer(int w, int h) =>
        new() { Width = w == 0 ? 1200 : w, Height = h, BackColor = Color.Transparent };

    private static System.Drawing.Drawing2D.GraphicsPath RoundedGPath(Rectangle rect, int r)
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

    /// <summary>Effective drawable width for content panels (accounts for scroll padding).</summary>
    private static int EffectiveContentWidth() => 1024 - Theme.SpaceMd * 2;

    // ── Cleanup ───────────────────────────────────────────────────────────
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockTimer.Dispose();
            _dataTimer.Dispose();
            _pulseTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
