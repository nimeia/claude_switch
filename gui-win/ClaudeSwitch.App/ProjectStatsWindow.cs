using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// Visualized result of one directory's transcript scan.
///
/// Three forms, each picked for its data's job: headline totals are numbers, so
/// they are stat tiles rather than a one-bar chart; activity is change over time,
/// so it is a column per day; per-session weight is magnitude across a few named
/// rows, so it is a horizontal bar list where long titles fit. Every chart plots
/// one measure, so colour encodes magnitude and no legend is needed — each title
/// names what is plotted.
/// </summary>
internal sealed class ProjectStatsWindow : Form
{
    private readonly StatTile _tileSessions = new();
    private readonly StatTile _tileQuestions = new();
    private readonly StatTile _tileOutput = new();
    private readonly StatTile _tileSpan = new();
    private readonly ColumnChart _daily = new();
    private readonly BarListChart _sessions = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _dailyTitle = new();
    private readonly Label _sessionsTitle = new();
    private readonly Label _footnote = new();
    private readonly Panel _header = new();

    public ProjectStatsWindow(string projectPath, JsonNode stats, long scanMs)
    {
        Text = $"用量统计 · {projectPath}";
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.FontBody;
        ShowIcon = false;
        MinimizeBox = false;

        _title.Text = "用量统计";
        _title.Font = Theme.FontBrand;
        _title.AutoSize = true;
        _title.Location = new Point(Theme.Space4, Theme.Space3);

        _subtitle.Text = projectPath;
        _subtitle.Font = Theme.FontSmall;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(Theme.Space4, Theme.Space3 + Theme.FontBrand.Height + 2);

        _header.Dock = DockStyle.Top;
        // Header holds the brand line plus a subtitle; measured, not guessed.
        _header.Height = Theme.FontBrand.Height + Theme.FontSmall.Height + Theme.Space5;
        _header.Controls.AddRange([_title, _subtitle]);

        var tiles = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            // Height comes from the tiles: they measure their own text, and a
            // fixed band clipped the labels once display scaling passed 100%.
            Height = _tileSessions.PreferredHeight + Theme.Space2,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(Theme.Space4, Theme.Space2, Theme.Space4, 0),
        };
        for (int i = 0; i < 4; i++)
            tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        foreach (var t in new[] { _tileSessions, _tileQuestions, _tileOutput, _tileSpan })
        {
            t.Dock = DockStyle.Fill;
            t.Margin = new Padding(0, 0, Theme.Space2, 0);
            tiles.Controls.Add(t);
        }

        _dailyTitle.Text = "每日提问数";
        _sessionsTitle.Text = "各会话输出 token";
        foreach (var l in new[] { _dailyTitle, _sessionsTitle })
        {
            l.Font = Theme.FontHeading;
            l.Dock = DockStyle.Top;
            l.Height = Theme.FontHeading.Height + Theme.Space2;
            l.TextAlign = ContentAlignment.MiddleLeft;
        }

        _daily.Dock = DockStyle.Top;
        _daily.Height = Theme.Scale(this, 150);
        // Sized to its rows, not filling: a bar list stretched to the panel
        // leaves a large empty field below the last bar.
        _sessions.Dock = DockStyle.Top;

        _footnote.Dock = DockStyle.Bottom;
        _footnote.Height = Theme.FontCaption.Height * 2 + Theme.Space2;
        _footnote.Font = Theme.FontCaption;
        _footnote.TextAlign = ContentAlignment.MiddleLeft;
        _footnote.Padding = new Padding(Theme.Space4, 0, Theme.Space4, 0);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space2),
            AutoScroll = true,
        };
        // Fill added last so the stacked bands above claim their height first.
        body.Controls.Add(_sessions);
        body.Controls.Add(_sessionsTitle);
        body.Controls.Add(_daily);
        body.Controls.Add(_dailyTitle);

        Controls.Add(body);
        Controls.Add(tiles);
        Controls.Add(_header);
        Controls.Add(_footnote);

        ApplyTheme();
        Theme.Changed += OnThemeChanged;
        Bind(stats, scanMs);
        Load += (_, _) =>
        {
            MinimumSize = new Size(Theme.Scale(this, 640), Theme.Scale(this, 480));
            Size = new Size(Theme.Scale(this, 860), Theme.Scale(this, 660));
            // Start at the top: a scrolling panel jumps to whichever child takes
            // focus, which hid the first chart's title and peak label.
            body.AutoScrollPosition = Point.Empty;
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        ApplyTheme();
        Invalidate(true);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.BgApp;
        ForeColor = Theme.TextPrimary;
        _header.BackColor = Theme.BgHeader;
        _title.ForeColor = Theme.TextPrimary;
        _subtitle.ForeColor = Theme.TextMuted;
        _dailyTitle.ForeColor = Theme.TextPrimary;
        _sessionsTitle.ForeColor = Theme.TextPrimary;
        _footnote.ForeColor = Theme.TextMuted;
        _footnote.BackColor = Theme.BgHeader;
    }

    private void Bind(JsonNode s, long scanMs)
    {
        long Num(string key) => s[key]?.GetValue<long>() ?? 0;

        _tileSessions.Set("会话数", Num("sessions").ToString());
        _tileQuestions.Set("提问数", Num("userMessages").ToString());
        _tileOutput.Set("输出 token", Compact(Num("outputTokens")));

        long first = Num("firstMs");
        long last = Num("lastMs");
        _tileSpan.Set("活跃跨度", SpanText(first, last));

        // ── Daily activity ──
        var daily = new List<ChartPoint>();
        if (s["daily"] is JsonArray days)
        {
            foreach (var d in days)
            {
                if (d is null) continue;
                string day = d["day"]?.GetValue<string>() ?? "";
                long q = d["userMessages"]?.GetValue<long>() ?? 0;
                long outTok = d["outputTokens"]?.GetValue<long>() ?? 0;
                daily.Add(new ChartPoint(
                    ShortDay(day),
                    q,
                    $"{day}\n提问 {q} 条 · 输出 {Compact(outTok)} token"));
            }
        }
        _daily.SetPoints(daily);

        // ── Per-session weight ──
        var rows = new List<ChartPoint>();
        if (s["sessionsDetail"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is null) continue;
                string id = item["id"]?.GetValue<string>() ?? "";
                string? prompt = item["firstPrompt"]?.GetValue<string>();
                long outTok = item["outputTokens"]?.GetValue<long>() ?? 0;
                long q = item["userMessages"]?.GetValue<long>() ?? 0;
                long start = item["firstMs"]?.GetValue<long>() ?? 0;
                string label = string.IsNullOrWhiteSpace(prompt)
                    ? (Theme.FormatCompactEpoch(start) ?? id[..Math.Min(8, id.Length)])
                    : prompt!;
                rows.Add(new ChartPoint(
                    label,
                    outTok,
                    $"{Theme.FormatEpochMs(start) ?? "时间未知"}\n"
                    + $"提问 {q} 条 · 输出 {Compact(outTok)} token\n会话 {id}"));
            }
        }
        // Magnitude reads best ordered by magnitude; the time order lives in the
        // daily chart and in each row's tooltip.
        rows.Sort((x, y) => y.Value.CompareTo(x.Value));
        // Sessions that produced nothing have no magnitude to compare and were
        // filling half the chart with empty rows. They are counted in the note
        // instead of being silently dropped.
        int silent = rows.Count(r => r.Value <= 0);
        rows.RemoveAll(r => r.Value <= 0);
        _sessions.SetPoints(rows);
        _sessions.Height = _sessions.PreferredHeight;
        _sessionsTitle.Text = silent > 0
            ? $"各会话输出 token（另有 {silent} 个会话无输出，未列出）"
            : "各会话输出 token";

        long skipped = Num("skippedLines");
        string note =
            $"读取 {Bytes(Num("scannedBytes"))} 会话记录，用时 {scanMs} ms。"
            + "缓存读写 token 未计入上方图表（量级差数百倍，同图会淹没其余数据）。";
        if (skipped > 0)
            note += $" {skipped} 行无法解析，数值为下限。";
        _footnote.Text = note;
    }

    private static string SpanText(long firstMs, long lastMs)
    {
        if (firstMs <= 0 || lastMs <= 0) return "—";
        var days = (DateTimeOffset.FromUnixTimeMilliseconds(lastMs)
            - DateTimeOffset.FromUnixTimeMilliseconds(firstMs)).TotalDays;
        return days < 1 ? "当天" : $"{Math.Round(days)} 天";
    }

    /// <summary>`2026-08-05` → `08-05`; the year is in the tooltip.</summary>
    private static string ShortDay(string iso) =>
        iso.Length >= 10 ? iso[5..10] : iso;

    private static string Compact(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
        : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
        : n.ToString();

    private static string Bytes(long b) =>
        b >= 1024 * 1024 ? $"{b / 1024.0 / 1024.0:0.#} MB"
        : b >= 1024 ? $"{b / 1024.0:0} KB"
        : $"{b} B";

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}
