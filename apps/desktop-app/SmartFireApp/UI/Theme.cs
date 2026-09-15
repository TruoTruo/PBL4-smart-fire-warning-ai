namespace SmartFireApp.UI;

/// <summary>
/// Centralized design tokens mapped from the HTML/CSS design system.
/// All colors, fonts, and spacing live here — never hardcoded elsewhere.
/// </summary>
public static class Theme
{
    // ── Surfaces ──────────────────────────────────────────────────────────
    public static readonly Color Background              = Color.FromArgb(0x0F, 0x13, 0x1C);
    public static readonly Color SurfaceDim              = Color.FromArgb(0x0F, 0x13, 0x1C);
    public static readonly Color SurfaceContainerLowest  = Color.FromArgb(0x0A, 0x0E, 0x17);
    public static readonly Color SurfaceContainerLow     = Color.FromArgb(0x18, 0x1B, 0x25);
    public static readonly Color SurfaceContainer        = Color.FromArgb(0x1C, 0x1F, 0x29);
    public static readonly Color SurfaceContainerHigh    = Color.FromArgb(0x26, 0x2A, 0x34);
    public static readonly Color SurfaceContainerHighest = Color.FromArgb(0x31, 0x35, 0x3F);
    public static readonly Color SurfaceBright           = Color.FromArgb(0x35, 0x39, 0x43);

    // ── Semantic ──────────────────────────────────────────────────────────
    // Primary = fire red
    public static readonly Color Primary              = Color.FromArgb(0xFF, 0xB3, 0xAD);
    public static readonly Color PrimaryContainer     = Color.FromArgb(0xFF, 0x54, 0x51);
    public static readonly Color OnPrimary            = Color.FromArgb(0x68, 0x00, 0x0A);
    public static readonly Color OnPrimaryContainer   = Color.FromArgb(0x5C, 0x00, 0x08);
    // Secondary = orange/heat
    public static readonly Color Secondary            = Color.FromArgb(0xFF, 0xB9, 0x5F);
    public static readonly Color SecondaryContainer   = Color.FromArgb(0xEE, 0x98, 0x00);
    public static readonly Color OnSecondary          = Color.FromArgb(0x47, 0x2A, 0x00);
    public static readonly Color OnSecondaryContainer = Color.FromArgb(0x5B, 0x38, 0x00);
    // Tertiary = cyan/online
    public static readonly Color Tertiary             = Color.FromArgb(0x4C, 0xD7, 0xF6);
    public static readonly Color TertiaryContainer    = Color.FromArgb(0x00, 0x9E, 0xB9);
    public static readonly Color OnTertiary           = Color.FromArgb(0x00, 0x36, 0x40);
    // Error
    public static readonly Color Error                = Color.FromArgb(0xFF, 0xB4, 0xAB);
    public static readonly Color ErrorContainer       = Color.FromArgb(0x93, 0x00, 0x0A);
    public static readonly Color OnError              = Color.FromArgb(0x69, 0x00, 0x05);
    public static readonly Color OnErrorContainer     = Color.FromArgb(0xFF, 0xDA, 0xD6);

    // ── Text ──────────────────────────────────────────────────────────────
    public static readonly Color OnSurface            = Color.FromArgb(0xDF, 0xE2, 0xEF);
    public static readonly Color OnSurfaceVariant     = Color.FromArgb(0xE4, 0xBE, 0xBA);
    public static readonly Color Outline              = Color.FromArgb(0xAB, 0x89, 0x86);
    public static readonly Color OutlineVariant       = Color.FromArgb(0x5B, 0x40, 0x3E);

    // ── Grid Lines ───────────────────────────────────────────────────────
    public static readonly Color GridLine             = Color.FromArgb(0x31, 0x35, 0x3F);

    // ── Fonts ─────────────────────────────────────────────────────────────
    // UI text — system font stack similar to Inter
    public static Font BodySm    => new("Segoe UI", 8f,  FontStyle.Regular);
    public static Font BodyMd    => new("Segoe UI", 9f,  FontStyle.Regular);
    public static Font BodyLg    => new("Segoe UI", 10f, FontStyle.Regular);
    public static Font TitleSm   => new("Segoe UI", 9f,  FontStyle.Bold);
    public static Font TitleMd   => new("Segoe UI", 10f, FontStyle.Bold);
    public static Font HeadlineSm => new("Segoe UI", 13f, FontStyle.Bold);
    public static Font HeadlineMd => new("Segoe UI", 16f, FontStyle.Bold);

    // Monospace — JetBrains Mono or fallback to Consolas
    private static readonly string _monoFace = IsFontInstalled("JetBrains Mono") ? "JetBrains Mono" : "Consolas";
    public static Font LabelCode    => new(_monoFace, 8f,  FontStyle.Regular);
    public static Font MetricValue  => new(_monoFace, 13f, FontStyle.Bold);
    public static Font MetricDisplay => new(_monoFace, 24f, FontStyle.Bold);

    // ── Spacing ───────────────────────────────────────────────────────────
    public const int SpaceXs = 4;
    public const int SpaceSm = 8;
    public const int SpaceMd = 16;
    public const int SpaceLg = 24;
    public const int SpaceXl = 32;
    public const int CornerDefault = 4;
    public const int CornerLg     = 8;
    public const int CornerXl     = 12;

    // ── Helpers ───────────────────────────────────────────────────────────
    private static bool IsFontInstalled(string name)
    {
        using var fc = new System.Drawing.Text.InstalledFontCollection();
        return fc.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static Color WithAlpha(Color c, int alpha) =>
        Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Blend two colors by a 0–1 factor (0 = base, 1 = overlay).</summary>
    public static Color Blend(Color base_, Color overlay, float t) => Color.FromArgb(
        (int)(base_.R + (overlay.R - base_.R) * t),
        (int)(base_.G + (overlay.G - base_.G) * t),
        (int)(base_.B + (overlay.B - base_.B) * t));

    /// <summary>Severity → accent color mapping.</summary>
    public static Color SeverityColor(string severity) => severity switch
    {
        "High"   => PrimaryContainer,
        "Medium" => Secondary,
        _        => Tertiary
    };
}

