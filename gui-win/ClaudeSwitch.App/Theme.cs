namespace ClaudeSwitch.App;

public enum ThemeMode
{
    Light,
    Dark,
}

/// <summary>
/// Design tokens: one YaHei UI type scale, restrained green as brand/primary only,
/// separate success / warn / danger roles, 4px spacing base.
/// </summary>
public static class Theme
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.Light;

    public static event EventHandler? Changed;

    // Surfaces
    public static Color BgApp { get; private set; }
    public static Color BgSurface { get; private set; }
    public static Color BgHeader { get; private set; }
    public static Color BgSidebar { get; private set; }
    public static Color BgRowAlt { get; private set; }
    public static Color BgActive { get; private set; }
    public static Color BgHover { get; private set; }
    public static Color BgDisabled { get; private set; }
    public static Color BgDrawer { get; private set; }
    public static Color BgSelected { get; private set; }
    /// <summary>Slate/blue ring for keyboard/mouse selection (not brand green).</summary>
    public static Color SelectionBorder { get; private set; }

    // Brand / chrome
    public static Color Primary { get; private set; }
    public static Color PrimaryDark { get; private set; }
    public static Color PrimarySoft { get; private set; }
    public static Color Accent { get; private set; }

    // Semantic
    public static Color Danger { get; private set; }
    public static Color DangerSoft { get; private set; }
    public static Color Success { get; private set; }
    public static Color Warning { get; private set; }

    // Text
    public static Color TextPrimary { get; private set; }
    public static Color TextSecondary { get; private set; }
    public static Color TextMuted { get; private set; }
    public static Color TextOnPrimary { get; private set; }
    public static Color TextDisabled { get; private set; }

    public static Color Border { get; private set; }
    public static Color BorderSoft { get; private set; }
    public static Color Overlay { get; private set; }

    public static Color UsageOk { get; private set; }
    public static Color UsageWarn { get; private set; }
    public static Color UsageHigh { get; private set; }

    // ── Type scale (single family; Regular by default — Bold only for brand) ──
    public const string FontFamily = "Microsoft YaHei UI";

    public static readonly Font FontBrand = new(FontFamily, 13f, FontStyle.Bold);
    public static readonly Font FontHeading = new(FontFamily, 11f, FontStyle.Bold);
    /// <summary>Account display name — Regular so glyphs fit measured line boxes.</summary>
    public static readonly Font FontName = new(FontFamily, 10f, FontStyle.Regular);
    public static readonly Font FontBody = new(FontFamily, 9f, FontStyle.Regular);
    public static readonly Font FontSmall = new(FontFamily, 8.25f, FontStyle.Regular);
    public static readonly Font FontCaption = new(FontFamily, 8f, FontStyle.Regular);

    // Spacing (4px base)
    public const int Space1 = 4;
    public const int Space2 = 8;
    public const int Space3 = 12;
    public const int Space4 = 16;
    public const int Space5 = 20;
    public const int Space6 = 24;

    public const int ControlHeight = 30;
    public const int CardRadius = 8;
    public const int ControlRadius = 4;

    /// <summary>
    /// Converts a 96-DPI design length to device pixels for <paramref name="c"/>.
    /// Fixed pixel boxes around text clip their content once Windows scaling is
    /// above 100% — measure with this, or let the control AutoSize.
    /// </summary>
    public static int Scale(Control c, int designPx) =>
        (int)Math.Round(designPx * (c?.DeviceDpi ?? 96) / 96.0);

    // Legacy aliases used by older controls
    public static Font FontTitle => FontName;

    static Theme() => Apply(ThemeMode.Light, raise: false);

    public static void Apply(ThemeMode mode, bool raise = true)
    {
        Mode = mode;
        if (mode == ThemeMode.Dark)
        {
            BgApp = Color.FromArgb(0x16, 0x1C, 0x19);
            BgSurface = Color.FromArgb(0x1E, 0x26, 0x22);
            BgHeader = Color.FromArgb(0x1A, 0x22, 0x1E);
            BgSidebar = Color.FromArgb(0x1A, 0x22, 0x1E);
            BgRowAlt = Color.FromArgb(0x24, 0x2C, 0x28);
            BgActive = Color.FromArgb(0x1E, 0x26, 0x22); // same as surface — active via pill only
            // Selection is slate/blue — never green (green = current-use only).
            BgSelected = Color.FromArgb(0x24, 0x2E, 0x38);
            BgHover = Color.FromArgb(0x26, 0x30, 0x2B);
            BgDisabled = Color.FromArgb(0x1A, 0x20, 0x1C);
            BgDrawer = Color.FromArgb(0x18, 0x20, 0x1C);
            SelectionBorder = Color.FromArgb(0x7A, 0x96, 0xB0);

            Primary = Color.FromArgb(0x5F, 0xA8, 0x7A);
            PrimaryDark = Color.FromArgb(0x4A, 0x90, 0x66);
            PrimarySoft = Color.FromArgb(0x2A, 0x3C, 0x32);
            Accent = Color.FromArgb(0x7B, 0xB8, 0x94);

            Danger = Color.FromArgb(0xE0, 0x7A, 0x6A);
            DangerSoft = Color.FromArgb(0x3A, 0x28, 0x26);
            Success = Color.FromArgb(0x6F, 0xB8, 0x8A);
            Warning = Color.FromArgb(0xE0, 0xB8, 0x55);

            TextPrimary = Color.FromArgb(0xEC, 0xF2, 0xEE);
            TextSecondary = Color.FromArgb(0xB0, 0xBE, 0xB6);
            TextMuted = Color.FromArgb(0x8A, 0x98, 0x90);
            TextOnPrimary = Color.FromArgb(0x10, 0x16, 0x12);
            TextDisabled = Color.FromArgb(0x9A, 0xA8, 0xA0);

            Border = Color.FromArgb(0x3A, 0x48, 0x40);
            BorderSoft = Color.FromArgb(0x2C, 0x38, 0x32);
            Overlay = Color.FromArgb(140, 0, 0, 0);

            UsageOk = Success;
            UsageWarn = Warning;
            UsageHigh = Danger;
        }
        else
        {
            // Neutral-forward surfaces; green reserved for primary actions & status pills.
            BgApp = Color.FromArgb(0xF5, 0xF6, 0xF5);
            BgSurface = Color.FromArgb(0xFF, 0xFF, 0xFF);
            BgHeader = Color.FromArgb(0xFF, 0xFF, 0xFF);
            BgSidebar = Color.FromArgb(0xF0, 0xF2, 0xF1);
            BgRowAlt = Color.FromArgb(0xF7, 0xF8, 0xF7);
            BgActive = Color.FromArgb(0xFF, 0xFF, 0xFF);
            // Soft slate selection fill (not green).
            BgSelected = Color.FromArgb(0xEB, 0xF0, 0xF6);
            BgHover = Color.FromArgb(0xF4, 0xF6, 0xF5);
            BgDisabled = Color.FromArgb(0xF0, 0xF0, 0xF0);
            BgDrawer = Color.FromArgb(0xFC, 0xFC, 0xFC);
            SelectionBorder = Color.FromArgb(0x7A, 0x8F, 0xA8);

            Primary = Color.FromArgb(0x3D, 0x7A, 0x56);
            PrimaryDark = Color.FromArgb(0x2F, 0x62, 0x44);
            PrimarySoft = Color.FromArgb(0xE4, 0xF0, 0xE8);
            Accent = Color.FromArgb(0x5A, 0x9A, 0x72);

            Danger = Color.FromArgb(0xC0, 0x4A, 0x3E);
            DangerSoft = Color.FromArgb(0xF8, 0xEB, 0xE9);
            Success = Color.FromArgb(0x3D, 0x7A, 0x56);
            Warning = Color.FromArgb(0xB8, 0x8A, 0x28);

            TextPrimary = Color.FromArgb(0x1A, 0x1F, 0x1C);
            TextSecondary = Color.FromArgb(0x4A, 0x54, 0x4E);
            TextMuted = Color.FromArgb(0x6B, 0x76, 0x70);
            TextOnPrimary = Color.White;
            // Readable disabled text (not washed-out on soft green).
            TextDisabled = Color.FromArgb(0x5A, 0x64, 0x5E);

            Border = Color.FromArgb(0xC8, 0xD0, 0xCC);
            BorderSoft = Color.FromArgb(0xE2, 0xE6, 0xE4);
            Overlay = Color.FromArgb(90, 20, 30, 24);

            UsageOk = Success;
            UsageWarn = Warning;
            UsageHigh = Danger;
        }

        if (raise) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Toggle() =>
        Apply(Mode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light);

    /// <summary>
    /// Usage color bands: 0–69 green · 70–89 orange · ≥90 red.
    /// </summary>
    public static Color UsageColor(double? pct)
    {
        if (pct is null) return TextMuted;
        if (pct >= 90) return UsageHigh;
        if (pct >= 70) return UsageWarn;
        return UsageOk;
    }

    /// <summary>Percent is utilization (used share of the window).</summary>
    public static string UsageLabel(double? pct) =>
        pct is null ? Loc.T("usage.none") : Loc.T("usage.usedOnly", $"{pct:0.#}");

    /// <summary>Compact used+remain for dense card meters (must fit ~120px).</summary>
    public static string UsageLabelCompact(double? pct, string? status = null)
    {
        if (pct is null) return UsageStatusShort(status);
        double remain = Math.Max(0, 100.0 - pct.Value);
        return Loc.T("usage.used", $"{pct:0.#}", $"{remain:0.#}");
    }

    /// <summary>Full used+remain for detail drawer / tooltips.</summary>
    public static string UsageLabelFull(double? pct, string? status = null)
    {
        if (pct is null) return UsageStatusLong(status);
        double remain = Math.Max(0, 100.0 - pct.Value);
        return Loc.T("usage.usedFull", $"{pct:0.#}", $"{remain:0.#}");
    }

    /// <summary>
    /// Card-width reason for a missing number (kebab status from core). A blank
    /// cell that cannot say why is indistinguishable from a broken app.
    /// </summary>
    public static string UsageStatusShort(string? status) => status switch
    {
        "needs-login" => Loc.T("usage.needsLogin"),
        "no-credential" => Loc.T("usage.noCredential"),
        "no-subscription" => Loc.T("usage.noSubscription"),
        "api-key" => Loc.T("usage.apiKey"),
        "unavailable" => Loc.T("usage.unavailable"),
        _ => Loc.T("usage.none"),
    };

    /// <summary>Drawer/tooltip wording — says what the user should do about it.</summary>
    public static string UsageStatusLong(string? status) => status switch
    {
        "needs-login" => Loc.T("usage.needsLoginLong"),
        "no-credential" => Loc.T("usage.noCredentialLong"),
        // The API reports no windows for either case and gives no way to tell
        // them apart, so the wording covers both instead of guessing.
        "no-subscription" => Loc.T("usage.noSubscriptionLong"),
        "api-key" => Loc.T("usage.apiKeyLong"),
        "unavailable" => Loc.T("usage.unavailableLong"),
        _ => Loc.T("usage.noneLong"),
    };

    /// <summary>
    /// Per-window level: ample 0–69 · watch 70–89 · critical ≥90.
    /// </summary>
    public static string UsageLevel(double? pct) => Loc.T(LevelKey(pct));

    private static string LevelKey(double? pct) =>
        pct is null ? "level.unknown"
        : pct >= 90 ? "level.critical"
        : pct >= 70 ? "level.watch"
        : "level.ample";

    /// <summary>Overall account health from max known window (same bands as UsageLevel).</summary>
    public static string UsageHealth(double? fiveHour, double? sevenDay)
    {
        if (fiveHour is null && sevenDay is null) return Loc.T("level.unknown");
        double max = Math.Max(fiveHour ?? 0, sevenDay ?? 0);
        return UsageLevel(max);
    }

    /// <summary>
    /// Short health tag for the detail banner: healthy / watch / critical / unknown.
    /// </summary>
    /// <remarks>
    /// Derived from the numbers, not from <see cref="UsageHealth"/>'s text: a
    /// translated word must never decide which band an account is in.
    /// </remarks>
    public static string UsageHealthTag(double? fiveHour, double? sevenDay)
    {
        if (fiveHour is null && sevenDay is null) return Loc.T("level.unknown");
        double max = Math.Max(fiveHour ?? 0, sevenDay ?? 0);
        string key = LevelKey(max);
        return Loc.T(key == "level.ample" ? "level.healthy" : key);
    }

    /// <summary>Format remaining seconds for humans: 148 → 2分28秒.</summary>
    public static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        if (totalSeconds < 60) return Loc.T("duration.s", totalSeconds);
        int m = totalSeconds / 60;
        int s = totalSeconds % 60;
        if (m < 60)
            return s == 0 ? Loc.T("duration.m", m) : Loc.T("duration.ms", m, s);
        int h = m / 60;
        m %= 60;
        return m == 0 ? Loc.T("duration.h", h) : Loc.T("duration.hm", h, m);
    }

    /// <summary>
    /// Human remaining time until ISO reset, e.g. "2 小时 18 分后". Null if unknown/past.
    /// </summary>
    public static string? FormatResetsIn(string? isoUtcOrOffset)
    {
        if (string.IsNullOrWhiteSpace(isoUtcOrOffset)) return null;
        if (!DateTimeOffset.TryParse(isoUtcOrOffset, out var when)) return null;
        var rem = when - DateTimeOffset.UtcNow;
        if (rem.TotalSeconds <= 0) return Loc.T("resets.soon");
        if (rem.TotalDays >= 1)
        {
            int d = (int)rem.TotalDays;
            int h = rem.Hours;
            return h > 0 ? Loc.T("resets.dh", d, h) : Loc.T("resets.d", d);
        }
        if (rem.TotalHours >= 1)
        {
            int h = (int)rem.TotalHours;
            int m = rem.Minutes;
            return m > 0 ? Loc.T("resets.hm", h, m) : Loc.T("resets.h", h);
        }
        int mins = Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes));
        return Loc.T("resets.m", mins);
    }


    /// <summary>ISO timestamp → local "5/24" for dense card lines. Null if unparseable.</summary>
    public static string? FormatShortDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, out var when)) return null;
        var local = when.ToLocalTime();
        return $"{local.Month}/{local.Day}";
    }

    /// <summary>ISO timestamp → local "2026-05-24". Null if unparseable.</summary>
    public static string? FormatDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, out var when)) return null;
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Epoch-ms → a list-friendly stamp: "08-05 14:30" for the last week,
    /// "2026-05-24" beyond it. Full precision only where it earns its width.
    /// </summary>
    public static string? FormatCompactEpoch(long? epochMs)
    {
        if (epochMs is null or <= 0) return null;
        try
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(epochMs.Value).ToLocalTime();
            var age = DateTimeOffset.Now - when;
            return age.TotalDays is >= 0 and < 7
                ? when.ToString("MM-dd HH:mm")
                : when.ToString("yyyy-MM-dd");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Epoch-ms → local "2026-05-24 14:30". Null when absent.</summary>
    public static string? FormatEpochMs(long? epochMs)
    {
        if (epochMs is null or <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMs.Value)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Claude's <c>billingType</c>, translated; unknown values pass through raw so
    /// a new payment channel shows up as itself rather than disappearing.
    /// </summary>
    public static string BillingTypeLabel(string? billingType) =>
        string.IsNullOrWhiteSpace(billingType) ? Loc.T("billing.unknown") : billingType switch
        {
            "stripe_subscription" => Loc.T("billing.stripe"),
            "google_play_subscription" => Loc.T("billing.googlePlay"),
            "apple_subscription" or "app_store_subscription" => Loc.T("billing.appStore"),
            "invoice" => Loc.T("billing.invoice"),
            _ => billingType,
        };

    private static string PrefPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeSwitch",
            "ui-prefs.ini");

    public static void LoadPrefs()
    {
        try
        {
            var path = PrefPath;
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("theme=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line[6..].Trim();
                    if (v.Equals("dark", StringComparison.OrdinalIgnoreCase))
                        Apply(ThemeMode.Dark, raise: false);
                }
            }
        }
        catch
        {
            /* ignore corrupt prefs */
        }
    }

    public static void SavePrefs()
    {
        UiPrefs.Save();
    }
}
