using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// Everything this machine has done with Claude Code, in one view.
///
/// The per-directory stats answered "what did this project cost"; this answers
/// "what have I built" — the question with a number worth leading on. A hero
/// figure carries it, a calendar heatmap carries the rhythm, and a bar per
/// project carries where the effort went.
/// </summary>
internal sealed class OverviewWindow : Form
{
    private readonly Label _heroValue = new();
    private readonly Label _heroLabel = new();
    private readonly StatTile _tileProjects = new();
    private readonly StatTile _tileSessions = new();
    private readonly StatTile _tileQuestions = new();
    private readonly StatTile _tileStreak = new();
    private readonly CalendarHeatmap _heatmap = new();
    private readonly Panel _heatmapBand = new();
    private readonly BarListChart _projects = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _heatTitle = new();
    private readonly Label _projectsTitle = new();
    private readonly Label _footnote = new();
    private readonly Panel _header = new();

    public OverviewWindow(JsonNode stats, long scanMs)
    {
        Text = "用量总览";
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.FontBody;
        ShowIcon = false;
        MinimizeBox = false;

        _title.Text = "用量总览";
        _title.Font = Theme.FontBrand;
        _title.AutoSize = true;
        _title.Location = new Point(Theme.Space4, 14);
        _subtitle.Font = Theme.FontSmall;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(Theme.Space4, 44);
        _header.Dock = DockStyle.Top;
        _header.Height = 72;
        _header.Controls.AddRange([_title, _subtitle]);

        // Hero figure — the one number the view leads with, same sans as the rest.
        _heroValue.Font = new Font(Theme.FontFamily, 34f, FontStyle.Bold);
        _heroValue.AutoSize = true;
        _heroValue.Location = new Point(Theme.Space4, Theme.Space2);
        _heroLabel.Font = Theme.FontBody;
        _heroLabel.AutoSize = true;
        // Height follows the figure: a fixed band let the 34pt number push its
        // own caption under the tiles below.
        var hero = new Panel { Dock = DockStyle.Top };
        hero.Controls.AddRange([_heroValue, _heroLabel]);
        hero.Resize += (_, _) => PlaceHero();
        _heroValue.SizeChanged += (_, _) => PlaceHero();

        var tiles = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scale(this, 64),
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(Theme.Space4, 0, Theme.Space4, Theme.Space2),
        };
        for (int i = 0; i < 4; i++)
            tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        foreach (var t in new[] { _tileProjects, _tileSessions, _tileQuestions, _tileStreak })
        {
            t.Dock = DockStyle.Fill;
            t.Margin = new Padding(0, 0, Theme.Space2, 0);
            tiles.Controls.Add(t);
        }

        _heatTitle.Text = "活动日历";
        _projectsTitle.Text = "各项目输出 token";
        foreach (var l in new[] { _heatTitle, _projectsTitle })
        {
            l.Font = Theme.FontHeading;
            l.Dock = DockStyle.Top;
            l.Height = 26;
            l.TextAlign = ContentAlignment.MiddleLeft;
        }

        // Grid on top with its own height; the legend paints in the strip left
        // below it. Docking the grid to Fill hid the legend behind it.
        _heatmap.Dock = DockStyle.Top;
        _heatmapBand.Dock = DockStyle.Top;
        _heatmapBand.Controls.Add(_heatmap);
        _heatmapBand.Paint += (_, e) => _heatmap.PaintLegend(
            e.Graphics,
            new Rectangle(0, _heatmapBand.Height - 22, _heatmapBand.Width, 20));

        _projects.Dock = DockStyle.Top;

        _footnote.Dock = DockStyle.Bottom;
        _footnote.Height = 40;
        _footnote.Font = Theme.FontCaption;
        _footnote.TextAlign = ContentAlignment.MiddleLeft;
        _footnote.Padding = new Padding(Theme.Space4, 0, Theme.Space4, 0);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.Space4, 0, Theme.Space4, Theme.Space2),
            AutoScroll = true,
        };
        body.Controls.Add(_projects);
        body.Controls.Add(_projectsTitle);
        body.Controls.Add(_heatmapBand);
        body.Controls.Add(_heatTitle);

        Controls.Add(body);
        Controls.Add(tiles);
        Controls.Add(hero);
        Controls.Add(_header);
        Controls.Add(_footnote);

        ApplyTheme();
        Theme.Changed += OnThemeChanged;
        Bind(stats, scanMs);
        Load += (_, _) =>
        {
            MinimumSize = new Size(Theme.Scale(this, 720), Theme.Scale(this, 560));
            Size = new Size(Theme.Scale(this, 940), Theme.Scale(this, 720));
            body.AutoScrollPosition = Point.Empty;
            PlaceHero();
        };
    }

    private void PlaceHero()
    {
        if (_heroValue.Parent is not { } hero) return;
        hero.Height = _heroValue.Height + Theme.Space3;
        _heroLabel.Location = new Point(
            _heroValue.Right + Theme.Space2,
            _heroValue.Bottom - _heroLabel.Height - Theme.Space2);
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
        _heroValue.ForeColor = Theme.Primary;
        _heroLabel.ForeColor = Theme.TextSecondary;
        _heatTitle.ForeColor = Theme.TextPrimary;
        _projectsTitle.ForeColor = Theme.TextPrimary;
        _footnote.ForeColor = Theme.TextMuted;
        _footnote.BackColor = Theme.BgHeader;
        _heatmapBand.BackColor = Theme.BgSurface;
    }

    private void Bind(JsonNode s, long scanMs)
    {
        long Num(string key) => s[key]?.GetValue<long>() ?? 0;

        _heroValue.Text = Compact(Num("outputTokens"));
        _heroLabel.Text = "个 token 由 Claude 为你写出";

        _tileProjects.Set("项目", Num("projects").ToString());
        _tileSessions.Set("会话", Num("sessions").ToString());
        _tileQuestions.Set("提问", Num("userMessages").ToString());
        _tileStreak.Set("最长连续", $"{Num("longestStreak")} 天");

        long first = Num("firstMs");
        long last = Num("lastMs");
        _subtitle.Text = first > 0
            ? $"{Theme.FormatDate(Iso(first))} 至 {Theme.FormatDate(Iso(last))}"
              + $" · 其中 {Num("activeDays")} 天有活动"
            : "尚无会话记录";

        // ── Calendar heatmap ──
        var cells = new List<ChartPoint>();
        int firstWeekday = 0;
        if (s["daily"] is JsonArray days)
        {
            foreach (var d in days)
            {
                if (d is null) continue;
                string day = d["day"]?.GetValue<string>() ?? "";
                long q = d["userMessages"]?.GetValue<long>() ?? 0;
                long outTok = d["outputTokens"]?.GetValue<long>() ?? 0;
                cells.Add(new ChartPoint(
                    day,
                    q,
                    q > 0
                        ? $"{day}\n提问 {q} 条 · 输出 {Compact(outTok)} token"
                        : $"{day}\n没有活动"));
            }
            if (cells.Count > 0 && DateTime.TryParse(cells[0].Label, out var start))
            {
                // Monday-first, so the grid lines up with a real week.
                firstWeekday = ((int)start.DayOfWeek + 6) % 7;
            }
        }
        _heatmap.FirstWeekday = firstWeekday;
        _heatmap.SetPoints(cells);
        _heatmap.Height = _heatmap.PreferredHeight;
        _heatmapBand.Height = _heatmap.PreferredHeight + 26;
        // Cell size depends on width, so the band must re-measure on resize.
        _heatmapBand.Resize += (_, _) =>
        {
            _heatmap.Height = _heatmap.PreferredHeight;
            _heatmapBand.Height = _heatmap.PreferredHeight + 26;
        };

        // ── Where the effort went ──
        var rows = new List<ChartPoint>();
        if (s["perProject"] is JsonArray list)
        {
            foreach (var p in list)
            {
                if (p is null) continue;
                long outTok = p["outputTokens"]?.GetValue<long>() ?? 0;
                if (outTok <= 0) continue;
                rows.Add(new ChartPoint(
                    p["name"]?.GetValue<string>() ?? "",
                    outTok,
                    $"{p["path"]?.GetValue<string>()}\n"
                    + $"{p["sessions"]?.GetValue<long>() ?? 0} 个会话 · "
                    + $"{p["userMessages"]?.GetValue<long>() ?? 0} 条提问 · "
                    + $"输出 {Compact(outTok)} token"));
            }
        }
        _projects.SetPoints(rows);
        _projects.Height = _projects.PreferredHeight;

        long busiest = s["busiestDay"]?["userMessages"]?.GetValue<long>() ?? 0;
        string busiestDay = s["busiestDay"]?["day"]?.GetValue<string>() ?? "";
        string note = busiest > 0
            ? $"最忙的一天是 {busiestDay}，提问 {busiest} 条。"
            : "";
        note += $"读取 {Bytes(Num("scannedBytes"))} 会话记录，用时 {scanMs} ms。";
        long skipped = Num("skippedLines");
        if (skipped > 0) note += $" {skipped} 行无法解析，数值为下限。";
        note += " 此统计与账号无关——Claude Code 不记录会话属于哪个账号。";
        _footnote.Text = note;
    }

    private static string Iso(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");

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
