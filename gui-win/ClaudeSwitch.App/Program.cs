using System.Text;
using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        UiPrefs.Load();

        string? fixture = null;
        bool forceOnboarding = false;
        bool skipOnboarding = false;
        bool warmupOnce = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--fixture" or "-f" && i + 1 < args.Length)
                fixture = args[++i];
            if (args[i] is "--onboarding")
                forceOnboarding = true;
            if (args[i] is "--skip-onboarding")
                skipOnboarding = true;
            if (args[i] is "--warmup-once")
                warmupOnce = true;
        }

        // Layout probe mode always skips onboarding dialog.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LAYOUT_DIR")))
            skipOnboarding = true;

        // One instance per data set, not one per machine. A --fixture run reads a
        // separate demo home and touches none of the real credentials, so it must
        // not be blocked by — or block — the instance managing the real accounts.
        string instanceKey = string.IsNullOrWhiteSpace(fixture)
            ? @"Local\ClaudeSwitch.Gui"
            : @"Local\ClaudeSwitch.Gui.fixture."
              + Convert.ToHexString(
                  System.Security.Cryptography.MD5.HashData(
                      Encoding.UTF8.GetBytes(Path.GetFullPath(fixture))));
        using var mutex = new Mutex(true, instanceKey, out bool created);
        if (!created)
        {
            // Scheduled headless warmup: the live GUI's poll already owns the
            // guardian — exit quietly instead of popping "already running".
            if (warmupOnce) return;
            MessageBox.Show(
                Loc.T("app.alreadyRunning"),
                "Claude Switch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (warmupOnce)
            {
                RunWarmupOnce(fixture);
                return;
            }

            var form = new MainForm(fixture);
            if (!skipOnboarding && (forceOnboarding || !UiPrefs.OnboardingDone))
            {
                form.Shown += (_, _) =>
                {
                    using var wiz = new OnboardingWizard(form.Engine, () => form.ReloadPublic());
                    wiz.ShowDialog(form);
                    form.ReloadPublic();
                };
            }
            Application.Run(form);
        }
        catch (Exception ex)
        {
            // Headless path must not block a scheduled task on a dialog.
            if (warmupOnce) return;
            MessageBox.Show(ex.ToString(), Loc.T("app.startFailed"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Scheduled-task entry: no UI. Uses <b>scheduled</b> stagger (not force) so
    /// multi-account phases Aᵢ = A₀ + i·(5/N) stay intact. N is only subscribed
    /// + healthy accounts. Short catch-up loop covers later anchors the same morning.
    /// </summary>
    static void RunWarmupOnce(string? fixtureRoot)
    {
        try
        {
            using var eng = string.IsNullOrWhiteSpace(fixtureRoot)
                ? new Engine()
                : new Engine(fixtureRoot);

            // Stay long enough for N≤4 last phase (e.g. #3 @ 09:45 when A₀=06:00).
            var deadline = DateTime.UtcNow.AddHours(4);
            while (DateTime.UtcNow < deadline)
            {
                JsonNode? tick = null;
                try
                {
                    // refresh_usage fetches status (defines N) then runs warmup_tick.
                    tick = eng.Call("refresh_usage");
                }
                catch
                {
                    try { tick = eng.Call("warmup_tick"); } catch { /* offline */ }
                }

                // Nested warmup on refresh, or top-level on warmup_tick.
                var warm = tick?["warmup"] ?? tick;
                bool waiting = false;
                if (warm?["skipped"] is JsonArray skipped)
                {
                    foreach (var s in skipped)
                    {
                        if (s?["reason"]?.GetValue<string>() == "before-anchor")
                        {
                            waiting = true;
                            break;
                        }
                    }
                }
                if (!waiting) break;
                Thread.Sleep(30_000);
            }
        }
        catch
        {
            // Silent: Task Scheduler would surface a message box as a failure popup.
        }
    }
}

/// <summary>
/// Card dashboard + usage drawer + switch confirm + light/dark sage theme.
/// </summary>
sealed class MainForm : Form
{
    internal Engine Engine => _engine;

    private readonly Engine _engine;
    /// <summary>Journal of supervised runs; survives the app for resume.</summary>
    private AgentRunStore _agentRuns = null!;
    private readonly FlowLayoutPanel _cardHost;
    private readonly Panel _emptyState;
    private readonly Panel _listOuter;
    private readonly ActivityStrip _activityStrip;
    private readonly ToolStripDropDownButton _btnResume;
    private readonly Panel _header;
    private readonly Panel _actionBar;
    private readonly Panel _settingsStrip;
    private readonly Label _status;
    /// <summary>Right half of the status band — the countdown, always in the same spot.</summary>
    private readonly Label _statusRight;
    private readonly Panel _statusBand;
    private readonly Label _activeChip;
    /// <summary>Green dot beside the live account; the chip itself stays unpainted.</summary>
    private readonly Label _activeDot;
    private readonly Label _countLabel;
    private readonly Label _brand;
    private readonly Label _subtitle;
    private readonly NotifyIcon _tray;
    private readonly CheckBox _autoEnabled;
    private readonly CheckBox _startupEnabled;
    private readonly CheckBox _warmupEnabled;
    private readonly CheckBox _warmupTask;
    private readonly Button _warmupNowBtn;
    private readonly NumericUpDown _threshold;
    private readonly Label _thrLabel;
    private readonly Label _thrUnit;
    private readonly Label _thrWindow;
    private readonly Label _autoTargetLabel;
    private readonly Label _autoTargetValue;
    private readonly Label _advancedLabel;
    private readonly Label _workHoursLabel;
    private readonly NumericUpDown _workStartHour;
    private readonly Label _workHoursSep;
    private readonly NumericUpDown _workEndHour;
    private readonly ToolStripButton _btnSwitch;
    private readonly ToolStripMenuItem _btnTheme;
    private readonly ToolStripButton _btnRefresh;
    private readonly ToolStripButton _btnAdd;
    private readonly ToolStripMenuItem _btnProjects;
    private readonly ToolStripMenuItem _btnOverview;
    private readonly ToolStripDropDownButton _btnTools;
    private readonly ToolStripDropDownButton _btnRuns;

    /// <summary>
    /// Supervised work, on the main window rather than only behind a dropdown.
    /// </summary>
    /// <remarks>
    /// Auto-continue resumes sessions with nobody watching; without a visible
    /// band the first thing a user hears about a task is that it already failed.
    /// </remarks>
    private readonly TaskBoard _taskBoard;
    private readonly ToolStripMenuItem _btnLang;
    private bool _fittingSearch;
    private readonly ToolStripTextBox _search;
    /// <summary>The ✕ drawn inside the search box; hidden while the query is empty.</summary>
    private Control? _searchClear;
    private readonly ToolStripLabel _searchMatchLabel;
    private readonly ToolStrip _toolStrip;

    // ── Collapsible automation section (root row 2) ──────────────────────
    private readonly TableLayoutPanel _root;
    private readonly Panel _settingsHeader;
    private readonly TableLayoutPanel _settingsBody;
    private readonly Label _settingsToggle;
    /// <summary>What the section says about itself while it is folded away.</summary>
    private readonly Label _settingsSummary;
    private readonly UsageDrawer _drawer;
    private Form? _detailForm;
    private readonly System.Windows.Forms.Timer _pollTimer;

    /// <summary>How often to look for stalled terminal sessions.</summary>
    /// <remarks>
    /// Two minutes against a ten-minute idle threshold: nothing can become a
    /// candidate between sweeps that would not still be one at the next.
    /// </remarks>
    private const int StalledSweepMs = 2 * 60 * 1000;

    private readonly System.Windows.Forms.Timer _stalledTimer;
    private StalledWatch _stalled = null!;
    private readonly PollCoordinator _poll = new();
    private readonly Label _autoHint;
    private readonly CheckBox _hideEmail;
    private readonly Label _settingsSaved;
    private AccountCard? _selected;
    private List<AccountCardModel> _models = [];
    /// <summary>Watermark shown while the search box is empty (never in Text).</summary>
    private static string SearchHint => Loc.T("toolbar.search.hint");
    private bool _switching;
    private bool _settingsLoading;
    private double _nextPollSeconds = 60;
    private DateTime _nextPollUtc = DateTime.UtcNow.AddSeconds(60);
    private string _baseStatus = "";
    /// <summary>Live Claude login that no managed slot holds, if any.</summary>
    private string? _unmanagedLogin;
    /// <summary>A rebuild was requested during a drag and still owes a redraw.</summary>
    private bool _rebuildDeferred;

    /// <summary>Detail is a separate tool window — main list width is never reduced.</summary>
    private bool DetailOpen =>
        _detailForm is { IsDisposed: false, Visible: true };

    public MainForm(string? fixtureRoot)
    {
        Text = "Claude Switch";
        Icon = AppIcon.Get();
        // List-only chrome; detail opens in a separate window so nothing is covered.
        // Provisional; ApplyWindowMetrics re-sizes for the display once the
        // handle exists and the real DPI is known.
        Width = DesignWidth;
        Height = DesignHeight;
        MinimumSize = new Size(DesignMinWidth, DesignMinHeight);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.FontBody;
        DoubleBuffered = true;
        // Prefer DPI-aware scaling for chrome; cards measure fonts themselves.
        AutoScaleMode = AutoScaleMode.Dpi;

        if (fixtureRoot is not null)
        {
            Directory.CreateDirectory(fixtureRoot);
            Directory.CreateDirectory(Path.Combine(fixtureRoot, ".claude"));
            SeedFixture(fixtureRoot);
            _engine = new Engine(fixtureRoot);
            try { _engine.SwitchTo("1"); } catch { /* first boot */ }
            try { _engine.Call("refresh_usage"); } catch { /* offline ok */ }
        }
        else
        {
            _engine = new Engine(null);
        }
        _agentRuns = new AgentRunStore(_engine);
        _stalled = new StalledWatch(_engine, _agentRuns, AccountLabelOrUnknown, AskPermissionAsync);

        // ═══════════════════════════════════════════════════════════
        // Root chrome: ONE TableLayout (5 rows). No stacked Dock.Top —
        // eliminates band bleed/overlap under DPI.
        // ═══════════════════════════════════════════════════════════
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        // Bands sized for measured control height (30) + padding — no text clip.
        // Values are placeholders; ApplyBandHeights sets the real, DPI-scaled
        // ones once the form knows which display it is on.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderBandH));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ToolbarBandH));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsCollapsedH));
        // Supervised work. Zero-height until there is some: auto-continue runs
        // unattended, so this is the only place its tasks are visible — but an
        // empty band above the account list would cost every user height to
        // tell most of them nothing.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // content (list)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, StatusBandH));
        _root = root;

        // ── Row 0: Header — one line. The window's own title bar already says
        // "Claude Switch", so this band is the app's *state*, not its name: the
        // wordmark shrinks to a label and the account count sits on the same
        // line, leaving the height for the list instead.
        _header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space4, 0, Theme.Space4, 0), Margin = new Padding(0) };
        var headerGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
        };
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var brandRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        _brand = new Label
        {
            Text = "Claude Switch",
            Font = Theme.FontHeading,
            AutoSize = true,
            Margin = new Padding(0, 1, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _subtitle = new Label
        {
            Text = "",
            Visible = false,
            AutoSize = false,
            Size = new Size(1, 1),
        };
        _countLabel = new Label
        {
            AutoSize = true,
            Font = Theme.FontSmall,
            Text = CountText(0),
            Margin = new Padding(Theme.Space2, 3, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        brandRow.Controls.Add(_brand);
        brandRow.Controls.Add(_countLabel);

        // A dot and a line of text, not a filled pill: this is a readout, and a
        // block of colour up here competed with the card that says the same
        // thing in the list below.
        var activeRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        _activeDot = new Label
        {
            Text = "●",
            AutoSize = true,
            Font = Theme.FontCaption,
            Margin = new Padding(0, 4, Theme.Space1, 0),
            TextAlign = ContentAlignment.MiddleRight,
        };
        _activeChip = new Label
        {
            AutoSize = true,
            Font = Theme.FontSmall,
            Margin = new Padding(0, 2, 0, 0),
            Text = Loc.T("header.active", Loc.T("status.none")),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        activeRow.Controls.Add(_activeDot);
        activeRow.Controls.Add(_activeChip);

        headerGrid.Controls.Add(brandRow, 0, 0);
        headerGrid.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 1, 0);
        headerGrid.Controls.Add(activeRow, 2, 0);
        _header.Controls.Add(headerGrid);

        // ── Row 1: Toolbar ───────────────────────────────────────────────
        // Three tiers, not one flat row of nine peers: the primary switch, the
        // handful of things done often (add / refresh / resume / runs), and a
        // 工具 menu for the reference windows that are opened once in a while.
        // Appearance controls sit apart on the right, shrunk to a word each.
        // Per-account ops (alias / disable / detail / delete) live on each card's ⋮ menu.
        _actionBar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space3, Theme.Space1, Theme.Space3, Theme.Space1), Margin = new Padding(0) };
        _toolStrip = new ToolStrip
        {
            Dock = DockStyle.Fill,
            GripStyle = ToolStripGripStyle.Hidden,
            CanOverflow = true,
            Stretch = true,
            Font = Theme.FontBody,
            Padding = new Padding(2),
            AutoSize = false,
            Height = StripItemH + 4,
        };

        _btnSwitch = MakeStripBtn(Loc.T("toolbar.switch.select"), "primary");
        _btnSwitch.ToolTipText = Loc.T("toolbar.switch.tip");
        _btnRefresh = MakeStripBtn(Loc.T("toolbar.refresh"), "secondary");
        var btnRefresh = _btnRefresh;
        btnRefresh.ToolTipText = Loc.T("toolbar.refresh.tip");
        _btnAdd = MakeStripBtn(Loc.T("toolbar.add"), "secondary");
        var btnAdd = _btnAdd;
        btnAdd.ToolTipText = Loc.T("toolbar.add.tip");
        _btnResume = MakeStripDrop(Loc.T("toolbar.resume"), Loc.T("toolbar.resume.tip"));
        // Built on open, not on a timer: the list is only interesting at the
        // moment it is read, and building it costs a directory listing.
        _btnResume.DropDownOpening += (_, _) => FillResumeMenu(_btnResume.DropDownItems);

        _btnRuns = MakeStripDrop(Loc.T("toolbar.runs"), Loc.T("toolbar.runs.tip"));
        var btnRuns = _btnRuns;
        // Built on open: a run's state changes under it, and the list is only
        // interesting at the moment it is read.
        _btnRuns.DropDownOpening += (_, _) => FillRunsMenu(_btnRuns.DropDownItems);

        _btnProjects = new ToolStripMenuItem(Loc.T("toolbar.projects"))
        {
            ToolTipText = Loc.T("toolbar.projects.tip"),
        };
        _btnOverview = new ToolStripMenuItem(Loc.T("toolbar.overview"))
        {
            ToolTipText = Loc.T("toolbar.overview.tip"),
        };
        // Appearance changes the shell, not the data. On the row it cost
        // permanent width for something touched about twice a year — and it was
        // the first thing cut off when the labels grew. In the menu it costs
        // nothing and gains room for the full wording.
        _btnTheme = new ToolStripMenuItem(ThemeToggleText())
        {
            ToolTipText = Loc.T("toolbar.theme.tip"),
        };
        _btnLang = BuildLanguageButton();
        _btnTools = MakeStripDrop(Loc.T("toolbar.tools"), Loc.T("toolbar.tools.tip"));
        _btnTools.DropDownItems.AddRange([
            _btnProjects, _btnOverview, new ToolStripSeparator(), _btnTheme, _btnLang,
        ]);
        _search = new ToolStripTextBox("search")
        {
            AutoSize = false,
            // Wider so placeholder and queries are readable without clipping.
            Width = 280,
            // No native border: the OS draws it square and unthemed, which made
            // this the one hard-edged box on a window of rounded surfaces. The
            // renderer paints a rounded one behind the hosted text box, and the
            // padding is what leaves it room to show.
            BorderStyle = BorderStyle.None,
            Padding = new Padding(3, 2, 3, 2),
            Font = Theme.FontBody,
            // Hint is a native watermark, never Text — see SearchBox.
            Text = "",
            Alignment = ToolStripItemAlignment.Right,
            Overflow = ToolStripItemOverflow.Never,
            ToolTipText = Loc.T("toolbar.search.tip"),
        };
        _searchMatchLabel = new ToolStripLabel("")
        {
            Alignment = ToolStripItemAlignment.Right,
            Overflow = ToolStripItemOverflow.Never,
            Font = Theme.FontCaption,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, Theme.Space2, 0),
        };
        // The right-hand cluster is pinned; the working actions are allowed to
        // fall into the chevron. Before this every item was Never, which does
        // not mean "always fits" — it means "cut off at the edge", and that is
        // exactly how the last two buttons vanished on a narrow window with no
        // way to reach them.
        foreach (var item in new ToolStripItem[] { _search, _searchMatchLabel })
            item.Overflow = ToolStripItemOverflow.Never;
        foreach (var item in new ToolStripItem[]
                 { _btnSwitch, btnRefresh, btnAdd, _btnResume, btnRuns, _btnTools })
            item.Overflow = ToolStripItemOverflow.AsNeeded;

        _btnSwitch.Click += (_, _) => DoSwitch();
        btnRefresh.Click += (_, _) => Reload();
        btnAdd.Click += (_, _) => DoAdd();
        _btnProjects.Click += (_, _) => ShowProjectsWindow();
        _btnOverview.Click += (_, _) => ShowOverviewWindow();
        // Refreshing the band refreshes the toolbar count with it, since the
        // count is the band's own total.
        void OnSupervisedWorkChanged() => UpdateTaskBoard();

        BackgroundRuns.Changed += OnSupervisedWorkChanged;
        _stalled.Changed += OnSupervisedWorkChanged;
        _stalled.TakeoverFinished += OnTakeoverFinished;
        FormClosed += (_, _) =>
        {
            BackgroundRuns.Changed -= OnSupervisedWorkChanged;
            _stalled.Changed -= OnSupervisedWorkChanged;
            _stalled.TakeoverFinished -= OnTakeoverFinished;
        };
        _btnTheme.Click += (_, _) =>
        {
            Theme.Toggle();
            Theme.SavePrefs();
            _btnTheme.Text = ThemeToggleText();
            ApplyShellTheme();
            RebuildCards();
            if (DetailOpen && _selected is not null) _drawer.Bind(_selected.Model);
        };
        SearchBox.AttachCueBanner(_search.TextBox, SearchHint);
        _search.TextChanged += (_, _) => RebuildCards();
        // Esc clears without having to reach for the toolbar button.
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            ClearSearch();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        // The ✕ lives in the box it clears, so the toolbar spends no width on a
        // button that is dead most of the time.
        _searchClear = SearchBox.AttachInlineClear(
            _search.TextBox, Loc.T("toolbar.search.clear.tip"), ClearSearch);

        // The surround is drawn by the strip's renderer, so focus moving in and
        // out of the hosted text box has to invalidate the strip — the text box
        // repainting itself leaves the border it does not own untouched.
        if (_search.TextBox is { } searchBox)
        {
            searchBox.Enter += (_, _) => _toolStrip.Invalidate();
            searchBox.Leave += (_, _) => _toolStrip.Invalidate();
        }

        // Painted here rather than by the renderer: a ToolStripControlHost does
        // not route its background through one, so a render override drew
        // nothing. The strip's Paint runs after the items, and the hosted text
        // box is shorter than its item, so the surround shows around it.
        _toolStrip.Paint += (_, e) =>
        {
            if (!_search.Visible || _search.Bounds.Width <= 0) return;
            SageToolStripRenderer.PaintTextBoxSurround(
                e.Graphics,
                Rectangle.Inflate(_search.Bounds, -1, -1),
                _search.TextBox?.Focused == true);
        };

        // Right cluster first, then left: primary switch + global actions only.
        _toolStrip.Items.Add(_searchMatchLabel);
        _toolStrip.Items.Add(_search);
        _toolStrip.Items.AddRange([
            _btnSwitch,
            new ToolStripSeparator(),
            btnRefresh, btnAdd,
            new ToolStripSeparator(),
            _btnResume, btnRuns, _btnTools,
        ]);
        _actionBar.Controls.Add(_toolStrip);
        // Every item has Overflow = Never, so anything that does not fit is simply
        // cut off the right edge — which is what happened the first time the UI
        // was rendered in English, where the same labels are ~1.7× wider. The
        // search box is the one item with slack, so it gives that width back.
        _toolStrip.Layout += (_, _) => FitSearchBox();

        // ── Row 2: Automation settings ───────────────────────────────────
        // One collapsible section. These are set-and-forget options, so the
        // default is folded away with a one-line summary of what they are
        // currently doing — the space goes to the account list, which is what
        // the window is for. Inside, every option is a labelled field rather
        // than a control embedded mid-sentence.
        _settingsStrip = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.Space4, 0, Theme.Space4, 0),
            Margin = new Padding(0),
        };
        _settingsToggle = new Label
        {
            AutoSize = true,
            Font = Theme.FontBody,
            Margin = new Padding(0, 6, Theme.Space3, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
        };
        _settingsSummary = new Label
        {
            AutoSize = true,
            Font = Theme.FontSmall,
            Margin = new Padding(0, 8, Theme.Space3, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
        };
        _settingsSaved = new Label
        {
            Text = "",
            AutoSize = true,
            Margin = new Padding(Theme.Space2, 8, 0, 0),
            Font = Theme.FontSmall,
            ForeColor = SystemColors.GrayText,
        };
        var headerFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        headerFlow.Controls.Add(_settingsToggle);
        headerFlow.Controls.Add(_settingsSummary);
        headerFlow.Controls.Add(_settingsSaved);
        _settingsHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = (int)SettingsCollapsedH,
            Margin = new Padding(0),
        };
        _settingsHeader.Controls.Add(headerFlow);
        foreach (Control c in new Control[] { _settingsToggle, _settingsSummary })
            c.Click += (_, _) => ToggleSettings();

        _settingsBody = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        _settingsBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 3; i++)
            _settingsBody.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsRowH));

        static FlowLayoutPanel SettingsRow() => new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        var settingsFlow = SettingsRow();
        var warmupFlow = SettingsRow();
        var advancedFlow = SettingsRow();

        // Field label: names the value beside it, in the quieter of the two
        // weights so the eye lands on the setting rather than on its caption.
        static Label FieldLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 7, Theme.Space2, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontSmall,
        };
        static Label FieldValue(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 6, Theme.Space4, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
        };
        // ThemedCheckBox, not the stock control: the OS renderer draws the box
        // from the system visual style, so in dark mode it stayed a bright white
        // square no matter what colours were set around it.
        static CheckBox OptionBox(string text, bool wide = false) => new ThemedCheckBox
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 4, wide ? Theme.Space5 : Theme.Space4, 0),
            Padding = new Padding(0, 2, 0, 2),
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
        };

        _autoEnabled = OptionBox(Loc.T("settings.autoswitch"), wide: true);
        _thrLabel = FieldLabel(Loc.T("settings.autoswitch.trigger"));
        _thrWindow = FieldValue(Loc.T("settings.autoswitch.window"));
        _thrWindow.Margin = new Padding(0, 6, Theme.Space2, 0);
        _threshold = new ThemedNumericUpDown
        {
            Minimum = 50,
            Maximum = 99,
            Value = 90,
            Width = ThresholdBoxW,
            Height = Theme.ControlHeight - 2,
            Margin = new Padding(0, 4, Theme.Space1, 0),
            Font = Theme.FontBody,
            TextAlign = HorizontalAlignment.Center,
        };
        _thrUnit = FieldValue(Loc.T("settings.autoswitch.pct"));
        _autoTargetLabel = FieldLabel(Loc.T("settings.autoswitch.target"));
        _autoTargetValue = FieldValue(Loc.T("settings.autoswitch.targetValue"));
        _advancedLabel = FieldLabel(Loc.T("settings.advanced"));
        _autoHint = new Label
        {
            Text = "",
            AutoSize = true,
            Visible = false,
            Margin = new Padding(0),
        };
        _startupEnabled = OptionBox(Loc.T("settings.startup"));
        _startupEnabled.Checked = StartupHelper.IsEnabled();
        _startupEnabled.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupHelper.SetEnabled(_startupEnabled.Checked);
                FlashSettingsSaved(_startupEnabled.Checked
                    ? Loc.T("settings.startup.on")
                    : Loc.T("settings.startup.off"));
            }
            catch (Exception ex)
            {
                _startupEnabled.Checked = StartupHelper.IsEnabled();
                MessageBox.Show(this, ex.Message, Loc.T("settings.startup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _hideEmail = OptionBox(Loc.T("settings.hideEmail"));
        _hideEmail.Checked = UiPrefs.HideEmail;
        _hideEmail.CheckedChanged += (_, _) =>
        {
            if (_settingsLoading) return;
            UiPrefs.HideEmail = _hideEmail.Checked;
            UiPrefs.Save();
            RebuildCards();
            if (DetailOpen && _selected is not null)
                _drawer.Bind(_selected.Model);
            FlashSettingsSaved(_hideEmail.Checked ? Loc.T("settings.hideEmail.on") : Loc.T("settings.hideEmail.off"));
        };

        _warmupEnabled = OptionBox(Loc.T("settings.warmup"), wide: true);
        _workHoursLabel = FieldLabel(Loc.T("settings.warmup.hours"));
        _workStartHour = new ThemedNumericUpDown
        {
            Minimum = 0,
            Maximum = 22,
            Value = 9,
            Width = HourBoxW,
            Height = Theme.ControlHeight - 2,
            Margin = new Padding(0, 4, Theme.Space1, 0),
            Font = Theme.FontBody,
            TextAlign = HorizontalAlignment.Center,
        };
        _workHoursSep = new Label
        {
            Text = "—",
            AutoSize = true,
            Margin = new Padding(0, 6, Theme.Space1, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
        };
        _workEndHour = new ThemedNumericUpDown
        {
            Minimum = 1,
            Maximum = 23,
            Value = 18,
            Width = HourBoxW,
            Height = Theme.ControlHeight - 2,
            Margin = new Padding(0, 4, Theme.Space4, 0),
            Font = Theme.FontBody,
            TextAlign = HorizontalAlignment.Center,
        };
        // The shell's own button, not a raw system one: same height, corner and
        // padding as everything else on the row.
        _warmupNowBtn = new SecondaryButton
        {
            Text = Loc.T("settings.warmup.now"),
            Margin = new Padding(0, 3, Theme.Space2, 0),
        };
        // Scheduling is an advanced fallback, not part of the everyday warmup
        // setting — it lives on the "other" row with the rest of the plumbing.
        _warmupTask = OptionBox(Loc.T("settings.warmup.task"));
        _warmupTask.Checked = WarmupTaskHelper.IsEnabled();

        _autoEnabled.CheckedChanged += (_, _) =>
        {
            SyncAutoSwitchUiEnabled();
            ApplyThresholdMarker();
            if (DetailOpen && _selected is not null)
                _drawer.Bind(_selected.Model);
            if (!_settingsLoading) SaveSettings(auto: true);
        };
        _threshold.ValueChanged += (_, _) =>
        {
            ApplyThresholdMarker();
            if (!_settingsLoading)
            {
                foreach (Control c in _cardHost.Controls)
                    c.Invalidate();
                if (DetailOpen && _selected is not null)
                    _drawer.Bind(_selected.Model);
                SaveSettings(auto: true);
            }
        };
        _warmupEnabled.CheckedChanged += (_, _) =>
        {
            // Turning the guardian off must not leave a daily schtask behind —
            // --warmup-once would keep waking the app for nothing.
            if (!_settingsLoading && !_warmupEnabled.Checked && _warmupTask.Checked)
            {
                _settingsLoading = true;
                _warmupTask.Checked = false;
                _settingsLoading = false;
                try { WarmupTaskHelper.SetEnabled(false, 0, 0); }
                catch { /* delete is best-effort; next load re-syncs from schtasks */ }
            }
            SyncWarmupUiEnabled();
            if (!_settingsLoading) SaveSettings(auto: true);
        };
        _warmupTask.CheckedChanged += (_, _) =>
        {
            if (_settingsLoading) return;
            try
            {
                SyncWarmupScheduledTask();
                // After uncheck, confirm the OS task is gone (not only our checkbox).
                if (!_warmupTask.Checked && WarmupTaskHelper.IsEnabled())
                {
                    WarmupTaskHelper.SetEnabled(false, 0, 0);
                    if (WarmupTaskHelper.IsEnabled())
                        throw new InvalidOperationException(
                            Loc.T("settings.warmup.task.failed", "task still present after delete"));
                }
                FlashSettingsSaved(_warmupTask.Checked
                    ? Loc.T("settings.warmup.task.on", WarmupTaskHelper.FormatBaseAnchor(
                        (int)_workStartHour.Value, (int)_workEndHour.Value))
                    : Loc.T("settings.warmup.task.off"));
            }
            catch (Exception ex)
            {
                _settingsLoading = true;
                _warmupTask.Checked = WarmupTaskHelper.IsEnabled();
                _settingsLoading = false;
                MessageBox.Show(this, ex.Message, Loc.T("settings.warmup.task"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _warmupNowBtn.Click += (_, _) => DoWarmupNow(onlyNumber: null);
        void OnWorkHoursChanged(object? _, EventArgs __)
        {
            if (_settingsLoading) return;
            // Keep end strictly after start so core clamp does not reset defaults.
            if (_workEndHour.Value <= _workStartHour.Value)
                _workEndHour.Value = Math.Min(23, _workStartHour.Value + 1);
            SaveSettings(auto: true);
        }
        _workStartHour.ValueChanged += OnWorkHoursChanged;
        _workEndHour.ValueChanged += OnWorkHoursChanged;

        // Switch: the toggle, then what triggers it, then where it goes. Each is
        // a caption with its value beside it, so the threshold reads as a field
        // rather than as a hole punched in the middle of a sentence.
        settingsFlow.Controls.Add(_autoEnabled);
        settingsFlow.Controls.Add(_thrLabel);
        settingsFlow.Controls.Add(_thrWindow);
        settingsFlow.Controls.Add(_threshold);
        settingsFlow.Controls.Add(_thrUnit);
        settingsFlow.Controls.Add(_autoTargetLabel);
        settingsFlow.Controls.Add(_autoTargetValue);
        settingsFlow.Controls.Add(_autoHint);

        warmupFlow.Controls.Add(_warmupEnabled);
        warmupFlow.Controls.Add(_workHoursLabel);
        warmupFlow.Controls.Add(_workStartHour);
        warmupFlow.Controls.Add(_workHoursSep);
        warmupFlow.Controls.Add(_workEndHour);
        warmupFlow.Controls.Add(_warmupNowBtn);

        // Everything that is configured once and then forgotten.
        advancedFlow.Controls.Add(_advancedLabel);
        advancedFlow.Controls.Add(_startupEnabled);
        advancedFlow.Controls.Add(_hideEmail);
        advancedFlow.Controls.Add(_warmupTask);

        _settingsBody.Controls.Add(settingsFlow, 0, 0);
        _settingsBody.Controls.Add(warmupFlow, 0, 1);
        _settingsBody.Controls.Add(advancedFlow, 0, 2);
        // Fill goes in first so the docked header above claims its height first.
        _settingsStrip.Controls.Add(_settingsBody);
        _settingsStrip.Controls.Add(_settingsHeader);
        ApplySettingsExpansion(UiPrefs.SettingsExpanded, save: false);

        // ── Row 3: Content (account list only — detail is a separate tool window) ──
        _drawer = new UsageDrawer();
        _drawer.SwitchRequested += (_, _) => DoSwitch();
        _drawer.AliasRequested += (_, _) => DoEditAlias();
        _drawer.DeleteRequested += (_, _) => DoDelete();
        _drawer.ToggleDisableRequested += (_, _) => DoToggleDisable();
        _drawer.WarmupRequested += (_, _) =>
        {
            if (_selected is not null)
                DoWarmupNow(_selected.Model.Number);
        };
        _drawer.ClosedByUser += (_, _) => CloseDetailWindow();
        _drawer.PreferredSizeChanged += (_, _) => FitDetailFormToContent();

        // Card list scrolls on the FlowLayoutPanel itself (Dock=Fill + AutoScroll).
        _listOuter = new ClipScrollPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 8, 4),
            Margin = new Padding(0),
        };
        _cardHost = new ClipFlowPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            // Bottom pad so the last card can scroll fully above the status band.
            Padding = new Padding(0, 0, 4, 12),
            Margin = new Padding(0),
        };
        _cardHost.Resize += (_, _) => LayoutCardHost();

        // The strip below the last card would otherwise be a dead zone: dragging
        // to the bottom of the list is the natural way to say "put it last".
        _cardHost.AllowDrop = true;
        static void AllowCardMove(object? _, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(typeof(AccountCard)) == true)
                e.Effect = DragDropEffects.Move;
        }
        _cardHost.DragEnter += AllowCardMove;
        _cardHost.DragOver += AllowCardMove;
        _cardHost.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(typeof(AccountCard)) is not AccountCard source) return;
            var last = _cardHost.Controls.OfType<AccountCard>().LastOrDefault();
            if (last is not null)
                OnCardReorder(new CardDrop(source, last, After: true));
        };

        _emptyState = new Panel { Dock = DockStyle.Fill, Visible = false };
        var emptyInner = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Text = Loc.T("empty.noAccounts"),
        };
        _emptyState.Controls.Add(emptyInner);
        _listOuter.Controls.Add(_cardHost);
        _listOuter.Controls.Add(_emptyState);

        // Activity strip sits under the list and above the status band. Added
        // before the Fill so it claims its height first.
        _activityStrip = new ActivityStrip();
        _activityStrip.OpenRequested += (_, _) => ShowOverviewWindow();

        var body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        body.Controls.Add(_listOuter);
        body.Controls.Add(_activityStrip);
        _listOuter.BringToFront();

        // ── Row 4: Status (dynamic state only — no interaction tutorials) ──
        // Two cells, not one sentence: what just happened on the left, the
        // countdown pinned right. The clock stops jumping sideways every time
        // the message beside it changes length.
        var statusGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Theme.Space4, 0, Theme.Space2, 0),
            Font = Theme.FontSmall,
            Text = Loc.T("status.ready"),
            Margin = new Padding(0),
            AutoEllipsis = true,
        };
        _statusRight = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(Theme.Space2, 0, Theme.Space4, 0),
            Font = Theme.FontSmall,
            Margin = new Padding(0),
        };
        statusGrid.Controls.Add(_status, 0, 0);
        statusGrid.Controls.Add(_statusRight, 1, 0);
        _statusBand = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _statusBand.Controls.Add(statusGrid);
        var statusBand = _statusBand;

        _taskBoard = new TaskBoard { Visible = false };
        // Expanding and paging happen in place, so the band has to be able to
        // ask the form for a different height.
        _taskBoard.LayoutChanged += ApplyBandHeights;

        root.Controls.Add(_header, 0, 0);
        root.Controls.Add(_actionBar, 0, 1);
        root.Controls.Add(_settingsStrip, 0, 2);
        root.Controls.Add(_taskBoard, 0, 3);
        root.Controls.Add(body, 0, 4);
        root.Controls.Add(statusBand, 0, 5);
        Controls.Add(root);

        _tray = new NotifyIcon
        {
            Text = "Claude Switch",
            Icon = AppIcon.Get(),
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                _tray.ShowBalloonTip(1600, Loc.T("app.name"), Loc.T("tray.minimized"), ToolTipIcon.Info);
            }
        };

        // Live poll loop: 1s UI timer drives countdown + due refresh/autoswitch_tick.
        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += (_, _) => OnPollUiTick();

        // Terminal sessions that stalled on a quota wall or a network failure.
        // Slower than the poll loop on purpose: nothing can qualify until it has
        // been silent for the idle threshold, so sweeping faster only re-reads
        // the same transcripts.
        _stalledTimer = new System.Windows.Forms.Timer { Interval = StalledSweepMs };
        _stalledTimer.Tick += (_, _) => SweepStalledSessions();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && (ModifierKeys & Keys.Shift) == 0)
            {
                e.Cancel = true;
                CloseDetailWindow();
                Hide();
                return;
            }
            // Real exit — dispose detail tool window.
            if (_detailForm is { IsDisposed: false })
            {
                try
                {
                    _detailForm.Owner = null;
                    _detailForm.Controls.Remove(_drawer);
                    _detailForm.Dispose();
                }
                catch { /* ignore */ }
                _detailForm = null;
            }
            // Supervised agents are child processes: exiting kills them. Mark
            // them resumable first, so the next launch can offer to continue.
            BackgroundRuns.ShutdownAll(_agentRuns);
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _stalledTimer.Stop();
            _stalledTimer.Dispose();
            _tray.Visible = false;
            Theme.SavePrefs();
            _engine.Dispose();
        };

        // Keep detail window aligned when main window moves (optional nicety).
        LocationChanged += (_, _) =>
        {
            if (DetailOpen) PositionDetailForm();
        };
        ResizeEnd += (_, _) =>
        {
            if (DetailOpen) PositionDetailForm();
        };

        Theme.Changed += (_, _) =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                ApplyShellTheme();
                RebuildCards();
            });
        };

        // Native scrollbars can only be restyled once their control has a
        // handle, which is after the form is shown — and again whenever the
        // list is rebuilt, because those are new windows.
        Shown += (_, _) => NativeScrollbars.Apply(this);

        ApplyBandHeights();
        ApplyShellTheme();
        LoadSettingsUi();
        Reload();
        LoadActivityStripAsync();
        UpdateSwitchEnabled();
        UpdateTaskBoard();
        NoticeResumableRuns();
        _pollTimer.Start();
        _stalledTimer.Start();
        // A session that stalled while the app was closed is the case this
        // exists for; waiting a whole sweep interval to notice would be odd.
        SweepStalledSessions();

        // Visual acceptance: bounds dump + screenshot when CLAUDE_SWITCH_LAYOUT_DIR is set.
        // ToolStrip items aren't Controls — probe host bands + text-box control.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_DETAIL") == "1")
        {
            LayoutProbe.ExtraWindows.Add(("gui-detail.png", () =>
            {
                OpenDetailSafe();
                return _detailForm;
            }));
        }
        // Switching language re-labels the shell in place. That path has no
        // other test — a stale label would only ever be noticed by a user.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_LANG") is { Length: > 0 } probeLang)
        {
            LayoutProbe.Overlays.Add(("gui-lang-switched.png", () => SetLanguage(probeLang)));
        }
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_RESUME") == "1")
        {
            LayoutProbe.Overlays.Add(("gui-resume-menu.png", () => _btnResume.ShowDropDown()));
        }
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_PROJECTS") == "1")
        {
            LayoutProbe.ExtraWindows.Add(("gui-projects.png", () =>
            {
                ShowProjectsWindow();
                return _projectsWindow;
            }));
            LayoutProbe.ExtraWindows.Add(("gui-stats.png", () =>
                _projectsWindow?.ProbeRunStats()));
        }
        // The agent window is built entirely in code and has no other visual
        // test; a clipped status strip or an unreadable transcript would only
        // ever be found by a user mid-run.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_AGENT") == "1")
        {
            LayoutProbe.ExtraWindows.Add(("gui-agent.png", () =>
            {
                Form w = NewAgentWindow(
                    Environment.CurrentDirectory,
                    configDir: null,
                    accountNumber: 1,
                    accountLabel: "demo@example.com");
                w.Show(this);
                return w;
            }));
        }
        // The runs dropdown is the only place a background run is visible once
        // its window is closed, and an overlay is the only way to capture it.
        // The expanded task board is a different layout, not just more rows, so
        // it needs its own capture.
        // Toggle the palette before the main capture, so a themed screenshot
        // does not depend on what the machine happens to be set to. Uses an
        // ExtraCheck rather than an overlay because overlays are screen copies,
        // which come back black on a session with no composited desktop.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_FLIPTHEME") == "1")
        {
            LayoutProbe.ExtraChecks.Add(() =>
            {
                Theme.Toggle();
                Application.DoEvents();
                return $"theme flipped to {Theme.Mode}";
            });
        }
        // Answers "did the strip paint from cache, and if not why" from inside
        // the real process, which is the only place the real native engine is.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_PEEK") == "1")
        {
            LayoutProbe.ExtraChecks.Add(() =>
            {
                try
                {
                    var v = _engine.Call("overview_peek");
                    return $"overview_peek found={v["found"]} stale={v["stale"]} "
                        + $"statsNull={v["stats"] is null} strip_loaded={_activityStrip.LoadedForProbe}";
                }
                catch (Exception ex)
                {
                    return $"overview_peek THREW {ex.GetType().Name}: {ex.Message}";
                }
            });
        }
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_BOARD") == "1")
        {
            LayoutProbe.ExtraChecks.Add(() =>
            {
                _taskBoard.ToggleForTest();
                ApplyBandHeights();
                PerformLayout();
                Application.DoEvents();
                return $"task_board expanded rows={_taskBoard.RowCount}";
            });
        }
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_RUNS") == "1")
        {
            LayoutProbe.Overlays.Add(("gui-runs-menu.png", () => _btnRuns.ShowDropDown()));
            // A dropdown is its own top-level window, so the overlay's screen
            // copy only works on a desktop that is actually being composited —
            // on a headless or disconnected session it captures black. Rendering
            // the control itself needs no desktop at all, and dumping the rows
            // as text makes the contents reviewable from a log.
            LayoutProbe.ExtraChecks.Add(ProbeRunsMenu);
        }
        // The automation panel ships collapsed, so its open state is never in
        // the default capture — and it is three rows of controls whose only
        // other check is that they compile.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_SETTINGS") == "1")
        {
            LayoutProbe.Overlays.Add((
                "gui-settings-open.png", () => ApplySettingsExpansion(true, save: false)));
        }
        // Two states that only exist after an interaction: the ✕ that appears
        // inside the search box, and the menu that now holds the appearance
        // controls the toolbar used to show.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_TOOLS") == "1")
        {
            LayoutProbe.Overlays.Add(("gui-search-active.png", () =>
            {
                _search.Focus();
                _search.Text = "e";
            }));
            LayoutProbe.Overlays.Add(("gui-tools-menu.png", () => _btnTools.ShowDropDown()));
        }
        // Dark is a full second palette, and the colour roles — green for the
        // account in use, slate for focus, amber for attention — have to survive
        // it. Toggled back afterwards so a probe run never rewrites the shared
        // preference file in the other mode.
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_DARK") == "1")
        {
            LayoutProbe.Overlays.Add(("gui-dark.png", Theme.Toggle));
            LayoutProbe.Overlays.Add(("gui-dark-restored.png", Theme.Toggle));
        }
        if (Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_OVERVIEW") == "1")
        {
            LayoutProbe.ExtraWindows.Add(("gui-overview.png", () =>
            {
                ShowOverviewWindow();
                return _overviewWindow;
            }));
        }
        LayoutProbe.RunIfRequested(
            this,
            ("header", _header),
            ("action", _actionBar),
            ("settings", _settingsStrip),
            ("list", _listOuter),
            ("scroll", _cardHost),
            ("cards", _cardHost),
            ("status", _status),
            ("strip", _activityStrip),
            ("toolstrip", _toolStrip),
            ("search", _search.TextBox));
        // ToolStrip items are not Controls, so the probe cannot measure them
        // from the target list — these report themselves into the same dump.
        LayoutProbe.ExtraChecks.Add(() =>
            $"switch_toolstrip_text=\"{_btnSwitch.Text}\" "
            + $"switch_visible={_btnSwitch.Visible} switch_w={_btnSwitch.Width}\n"
            + (IsValidSwitchLabel(_btnSwitch.Text)
                ? "OK switch_label_complete"
                : "FAIL switch_label_incomplete"));
        LayoutProbe.ExtraChecks.Add(() =>
            $"items={_toolStrip.Items.Count} overflow={_toolStrip.OverflowButton.Visible}\n"
            + string.Join(
                "\n",
                _toolStrip.Items.OfType<ToolStripItem>()
                    .Where(i => i.Text is { Length: > 0 })
                    .Select(i => $"  item \"{i.Text}\" placement={i.Placement} w={i.Width}")));
        LayoutProbe.ExtraChecks.Add(SearchClearReport);
    }

    /// <summary>
    /// Where the search box's ✕ actually landed.
    /// </summary>
    /// <remarks>
    /// "Inside the box" is the whole point of moving it off the toolbar, and it
    /// is the one claim a screenshot cannot settle — the border is drawn by the
    /// ToolStrip renderer, not by the edit control, so the glyph can sit a few
    /// pixels past a line that is not the box's real edge. This asserts against
    /// the client rectangle instead of against a picture of it.
    /// </remarks>
    private string SearchClearReport()
    {
        var box = _search.TextBox;
        if (_searchClear is null || box is null) return "FAIL search_clear_missing";

        // Hidden controls are skipped by layout, so an unmeasured glyph sits at
        // (0,0) and would pass "inside the box" without ever having been placed.
        bool was = _searchClear.Visible;
        _searchClear.Visible = true;
        box.PerformLayout();
        var r = _searchClear.Bounds;
        var client = box.ClientSize;
        _searchClear.Visible = was;

        bool inside = ReferenceEquals(_searchClear.Parent, box)
            && r.Left >= 0 && r.Right <= client.Width
            && r.Top >= 0 && r.Bottom <= client.Height
            && r.Width > 0;
        return $"search_clear={r} box_client={client}\n"
            + (inside ? "OK search_clear_inside_box" : "FAIL search_clear_outside_box");
    }

    private static bool IsValidSwitchLabel(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(Loc.T("toolbar.switch.select"), StringComparison.Ordinal)
            || text.Contains(Loc.T("toolbar.switch.current"), StringComparison.Ordinal)
            || text.Contains(Loc.T("toolbar.switch.go"), StringComparison.Ordinal)
            || text.Contains(Loc.T("toolbar.switch.busy"), StringComparison.Ordinal));

    /// <summary>
    /// Gives the search box whatever width the buttons leave, within limits.
    /// </summary>
    /// <remarks>
    /// Below <c>MinSearch</c> the box is too small to read a query in, so it
    /// stops shrinking and the toolbar clips instead — a visibly broken toolbar
    /// is better than a search box that silently cannot be used.
    /// </remarks>
    private void FitSearchBox()
    {
        // Design pixels: a fixed device width is half a box on a 200% display,
        // and the placeholder it has to hold grew with the display.
        int MinSearch = (int)(150 * (DeviceDpi / 96f));
        int MaxSearch = (int)(280 * (DeviceDpi / 96f));
        if (_fittingSearch) return;

        int used = 0;
        foreach (ToolStripItem item in _toolStrip.Items)
        {
            if (!item.Available) continue;
            used += (ReferenceEquals(item, _search) ? 0 : item.Width)
                + item.Margin.Horizontal;
        }
        int free = _toolStrip.DisplayRectangle.Width - used;
        int want = Math.Clamp(free, MinSearch, MaxSearch);
        if (want == _search.Width) return;

        // Setting Width re-triggers Layout; the guard keeps that to one pass.
        _fittingSearch = true;
        try { _search.Width = want; }
        finally { _fittingSearch = false; }
    }

    /// <summary>
    /// Compact language menu. The label is the language's own name (English,
    /// 简体中文) — the one string a user always recognises even when the rest of
    /// the UI is in a language they cannot read.
    /// </summary>
    private ToolStripMenuItem BuildLanguageButton()
    {
        var btn = new ToolStripMenuItem(Loc.T("toolbar.language"))
        {
            ToolTipText = Loc.T("toolbar.language.tip"),
        };
        foreach (var (code, name) in Loc.Available)
        {
            var item = new ToolStripMenuItem(name) { Tag = code, Checked = code == Loc.Current };
            item.Click += (_, _) => SetLanguage(code);
            btn.DropDownItems.Add(item);
        }
        return btn;
    }

    private void SetLanguage(string code)
    {
        if (code == Loc.Current) return;
        Loc.Use(code);
        UiPrefs.Language = code;
        UiPrefs.Save();
        foreach (ToolStripMenuItem item in _btnLang.DropDownItems)
            item.Checked = (string?)item.Tag == code;
        ApplyTexts();
    }

    /// <summary>
    /// Re-labels the shell after a language change. Only the persistent chrome
    /// needs this — cards, the drawer and every secondary window read their
    /// strings when they are built, and are rebuilt or reopened after this.
    /// </summary>
    private void ApplyTexts()
    {
        _btnLang.Text = Loc.T("toolbar.language");
        _btnLang.ToolTipText = Loc.T("toolbar.language.tip");

        _btnRefresh.Text = Loc.T("toolbar.refresh");
        _btnRefresh.ToolTipText = Loc.T("toolbar.refresh.tip");
        _btnAdd.Text = Loc.T("toolbar.add");
        _btnAdd.ToolTipText = Loc.T("toolbar.add.tip");
        _btnResume.Text = Loc.T("toolbar.resume");
        _btnResume.ToolTipText = Loc.T("toolbar.resume.tip");
        _btnTools.Text = Loc.T("toolbar.tools");
        _btnTools.ToolTipText = Loc.T("toolbar.tools.tip");
        _btnProjects.Text = Loc.T("toolbar.projects");
        _btnProjects.ToolTipText = Loc.T("toolbar.projects.tip");
        _btnOverview.Text = Loc.T("toolbar.overview");
        _btnOverview.ToolTipText = Loc.T("toolbar.overview.tip");
        _btnTheme.Text = ThemeToggleText();
        _btnTheme.ToolTipText = Loc.T("toolbar.theme.tip");
        _search.ToolTipText = Loc.T("toolbar.search.tip");
        SearchBox.AttachCueBanner(_search.TextBox, SearchHint);

        _autoEnabled.Text = Loc.T("settings.autoswitch");
        _thrLabel.Text = Loc.T("settings.autoswitch.trigger");
        _thrWindow.Text = Loc.T("settings.autoswitch.window");
        _thrUnit.Text = Loc.T("settings.autoswitch.pct");
        _autoTargetLabel.Text = Loc.T("settings.autoswitch.target");
        _autoTargetValue.Text = Loc.T("settings.autoswitch.targetValue");
        _advancedLabel.Text = Loc.T("settings.advanced");
        _startupEnabled.Text = Loc.T("settings.startup");
        _hideEmail.Text = Loc.T("settings.hideEmail");
        _warmupEnabled.Text = Loc.T("settings.warmup");
        _workHoursLabel.Text = Loc.T("settings.warmup.hours");
        _warmupNowBtn.Text = Loc.T("settings.warmup.now");
        _warmupTask.Text = Loc.T("settings.warmup.task");
        ApplySettingsExpansion(UiPrefs.SettingsExpanded, save: false);

        // The status line is rebuilt from live state, so a stale sentence in the
        // previous language would otherwise sit there until the next poll.
        _baseStatus = "";
        _activityStrip.ApplyTexts();
        // Card geometry is measured from translated labels, so the cache from
        // the previous language would size the meter column wrong.
        AccountCard.InvalidateMetrics();
        RebuildCards();
        ApplyHeaderTexts();
        ComposeStatusLine();
        if (DetailOpen && _selected is not null) _drawer.Bind(_selected.Model);
    }

    /// <summary>
    /// The appearance toggle's label — a word, not a sentence.
    /// </summary>
    /// <remarks>
    /// It changes the shell rather than the data, so it earns a fraction of the
    /// width the working actions get. The tooltip carries the full wording.
    /// </remarks>
    private static string ThemeToggleText() =>
        Theme.Mode == ThemeMode.Dark
            ? Loc.T("toolbar.theme.light")
            : Loc.T("toolbar.theme.dark");

    // ── Band heights (96-DPI design px; see ApplyBandHeights) ────────────
    private const float HeaderBandH = 34f;
    private const float ToolbarBandH = 42f;
    private const float StatusBandH = 28f;
    private const float SettingsCollapsedH = 28f;
    private const float SettingsRowH = 34f;
    private const float SettingsExpandedH = SettingsCollapsedH + SettingsRowH * 3 + 6f;
    /// <summary>
    /// One height for every toolbar item, so buttons, dropdowns and the search
    /// box share a baseline instead of each finding its own.
    /// </summary>
    private const int StripItemH = 32;
    private const int StripPadY = 6;

    /// <param name="role">primary | secondary | danger — read by SageToolStripRenderer.</param>
    private static ToolStripButton MakeStripBtn(string text, string role = "secondary")
    {
        return new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, Theme.Space1, 0),
            Padding = new Padding(Theme.Space3, StripPadY, Theme.Space3, StripPadY),
            Font = Theme.FontBody,
            Tag = role,
            Overflow = ToolStripItemOverflow.Never,
        };
    }

    /// <summary>A dropdown that measures and paints exactly like <see cref="MakeStripBtn"/>.</summary>
    private static ToolStripDropDownButton MakeStripDrop(string text, string tip)
    {
        return new ToolStripDropDownButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, Theme.Space1, 0),
            Padding = new Padding(Theme.Space3, StripPadY, Theme.Space2, StripPadY),
            Font = Theme.FontBody,
            Tag = "secondary",
            Overflow = ToolStripItemOverflow.Never,
            ToolTipText = tip,
        };
    }

    /// <summary>The account count as it reads beside the wordmark.</summary>
    private static string CountText(int n) => "· " + Loc.T("header.count", n);

    private void ToggleSettings() =>
        ApplySettingsExpansion(!UiPrefs.SettingsExpanded, save: true);

    /// <summary>
    /// Fold the automation section away, or open it.
    /// </summary>
    /// <remarks>
    /// Collapsed is the default because these are decisions made once. The
    /// summary line is what makes that safe: folding a setting out of sight is
    /// only acceptable while the header still says what it is currently doing.
    /// </remarks>
    private void ApplySettingsExpansion(bool expanded, bool save)
    {
        UiPrefs.SettingsExpanded = expanded;
        if (save) UiPrefs.Save();
        _settingsBody.Visible = expanded;
        _settingsToggle.Text = (expanded ? "▾  " : "▸  ") + Loc.T("settings.section");
        ApplyBandHeights();
        UpdateSettingsSummary();
    }

    /// <summary>
    /// Sizes the fixed chrome bands for the current display.
    /// </summary>
    /// <remarks>
    /// <see cref="TableLayoutPanel"/> row styles are device pixels and are not
    /// rescaled for us, but the fonts inside them are points and grow with the
    /// display. A band written as a bare constant therefore fits at 100% and
    /// clips its own text at 125% — which is what happened to the settings
    /// header. Everything here is a design pixel put through the same scale.
    /// </remarks>
    private void ApplyBandHeights()
    {
        float s = DeviceDpi / 96f;
        float S(float designPx) => designPx * s;

        _root.RowStyles[0] = new RowStyle(SizeType.Absolute, S(HeaderBandH));
        _root.RowStyles[1] = new RowStyle(SizeType.Absolute, S(ToolbarBandH));
        _root.RowStyles[2] = new RowStyle(
            SizeType.Absolute,
            S(UiPrefs.SettingsExpanded ? SettingsExpandedH : SettingsCollapsedH));
        _root.RowStyles[3] = new RowStyle(SizeType.Absolute, TaskBoardHeight(S));
        _root.RowStyles[5] = new RowStyle(SizeType.Absolute, S(StatusBandH));

        for (int i = 0; i < _settingsBody.RowStyles.Count; i++)
            _settingsBody.RowStyles[i] = new RowStyle(SizeType.Absolute, S(SettingsRowH));

        _settingsHeader.Height = (int)S(SettingsCollapsedH);
        _toolStrip.Height = (int)S(StripItemH + 4);

        // Spin boxes are fixed-width by necessity (AutoSize ignores the spin
        // buttons), so they need the same treatment: at 200% the threshold box
        // rendered "90" as "9(" — the digits grew and the box did not.
        _threshold.Width = (int)S(ThresholdBoxW);
        foreach (var box in new[] { _workStartHour, _workEndHour })
            box.Width = (int)S(HourBoxW);
        foreach (var box in new[] { _threshold, _workStartHour, _workEndHour })
            box.Height = (int)S(Theme.ControlHeight - 2);
    }

    /// <summary>
    /// Height the supervised-work band needs, scaled; zero when it has nothing.
    /// </summary>
    /// <remarks>
    /// The band computes its own figure — it is the only thing that knows
    /// whether it is expanded and whether a pager is showing.
    /// </remarks>
    private float TaskBoardHeight(Func<float, float> scale)
    {
        if (_taskBoard is null) return 0f;

        // This app is an account list first, so the band is told how many rows
        // the window can spare before it decides how tall to be. Fitting by row
        // count rather than by clipping means it never needs a scrollbar — which
        // on Windows is native, unthemed, and glaring in a dark window.
        float shareDesignPx = ClientSize.Height * TaskBoard.MaxHeightShare / scale(1f);
        _taskBoard.SetMaxVisibleRows(TaskBoard.RowsThatFit(shareDesignPx));

        return scale(_taskBoard.DesiredHeight);
    }

    // Wide enough for the digits plus the spin buttons, at 96 DPI.
    private const int ThresholdBoxW = 62;
    private const int HourBoxW = 62;

    // Window size in 96-DPI design px — see ApplyWindowMetrics.
    private const int DesignWidth = 1200;
    private const int DesignHeight = 800;
    private const int DesignMinWidth = 1000;
    private const int DesignMinHeight = 640;

    /// <summary>
    /// Sizes the window for the display it opened on.
    /// </summary>
    /// <remarks>
    /// <c>Width = 1200</c> in the constructor is 1200 <em>device</em> pixels:
    /// nothing scales a code-built form's own bounds. On a 200% display that is
    /// a 600×400 window holding text sized for 1200×800, and the toolbar ran out
    /// of room before the settings row even loaded. The design size is scaled
    /// here and clamped to the work area, so the window is the same apparent
    /// size on every display and never opens larger than the screen.
    /// </remarks>
    private void ApplyWindowMetrics()
    {
        float s = DeviceDpi / 96f;
        var work = Screen.FromHandle(Handle).WorkingArea;
        Size Fit(int w, int h) => new(
            Math.Min((int)(w * s), (int)(work.Width * 0.94)),
            Math.Min((int)(h * s), (int)(work.Height * 0.94)));

        // Minimum first: a MinimumSize above the incoming Size grows the window.
        MinimumSize = Fit(DesignMinWidth, DesignMinHeight);
        var want = Fit(DesignWidth, DesignHeight);
        if (Size.Width < want.Width || Size.Height < want.Height)
            Size = want;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowMetrics();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyBandHeights();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyBandHeights();
        AccountCard.InvalidateMetrics();
        RebuildCards();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // The strip is a glance under the list, never the reason the list has
        // nowhere to go: at the small end of the window it gives its height back.
        _activityStrip?.CapHeight(ClientSize.Height);
    }

    /// <summary>
    /// One line naming what automation is on. Shown only while collapsed — open,
    /// the controls themselves are the summary, and repeating it would be noise.
    /// </summary>
    private void UpdateSettingsSummary()
    {
        if (UiPrefs.SettingsExpanded)
        {
            _settingsSummary.Text = "";
            return;
        }
        string auto = _autoEnabled.Checked
            ? Loc.T("settings.summary.autoOn", (int)_threshold.Value)
            : Loc.T("settings.summary.autoOff");
        string warm = _warmupEnabled.Checked
            ? Loc.T("settings.summary.warmOn", (int)_workStartHour.Value, (int)_workEndHour.Value)
            : Loc.T("settings.summary.warmOff");
        _settingsSummary.Text = $"{auto}   ·   {warm}";
    }

    private void ApplyShellTheme()
    {
        BackColor = Theme.BgApp;
        ForeColor = Theme.TextPrimary;
        _header.BackColor = Theme.BgHeader;
        _actionBar.BackColor = Theme.BgApp;
        _settingsStrip.BackColor = Theme.BgSidebar;
        _listOuter.BackColor = Theme.BgApp;
        _cardHost.BackColor = Theme.BgApp;
        _emptyState.BackColor = Theme.BgApp;
        _taskBoard.ApplyTheme();
        // Scrollbars are drawn by the OS, so they need asking separately or a
        // dark window keeps a white stripe down its edge. Only reaches controls
        // that already have a handle, which is why it is also applied on Shown
        // and after the card list is rebuilt.
        NativeScrollbars.Apply(this);
        _statusBand.BackColor = Theme.BgHeader;
        _status.BackColor = Theme.BgHeader;
        _status.ForeColor = Theme.TextSecondary;
        _statusRight.BackColor = Theme.BgHeader;
        _statusRight.ForeColor = Theme.TextMuted;
        _brand.ForeColor = Theme.PrimaryDark;
        _subtitle.ForeColor = Theme.TextSecondary;
        // A readout, not a badge: the dot carries the colour so the text can
        // stay the same weight as everything else in the band.
        _activeDot.ForeColor = _models.Any(a => a.Active) ? Theme.Primary : Theme.TextMuted;
        _activeChip.ForeColor = Theme.TextSecondary;
        _countLabel.ForeColor = Theme.TextMuted;
        _autoEnabled.ForeColor = Theme.TextPrimary;
        _startupEnabled.ForeColor = Theme.TextPrimary;
        _hideEmail.ForeColor = Theme.TextPrimary;
        _warmupEnabled.ForeColor = Theme.TextPrimary;
        _warmupTask.ForeColor = Theme.TextPrimary;
        _settingsSaved.ForeColor = Theme.TextMuted;
        _settingsToggle.ForeColor = Theme.TextPrimary;
        _settingsSummary.ForeColor = Theme.TextMuted;
        _advancedLabel.ForeColor = Theme.TextMuted;
        _autoTargetValue.ForeColor = Theme.TextPrimary;
        _thrWindow.ForeColor = Theme.TextPrimary;
        SyncAutoSwitchUiEnabled();
        SyncWarmupUiEnabled();
        _toolStrip.BackColor = Theme.BgApp;
        _toolStrip.ForeColor = Theme.TextPrimary;
        _toolStrip.Renderer = new SageToolStripRenderer();
        ApplySearchColors();
        foreach (Control c in _emptyState.Controls)
        {
            c.ForeColor = Theme.TextSecondary;
            c.BackColor = Theme.BgApp;
        }
        // Walk settings children once for background (no aggressive TintTree).
        void Tint(Control c, Color bg)
        {
            if (c is CheckBox or NumericUpDown or Button) return;
            c.BackColor = bg;
            foreach (Control ch in c.Controls) Tint(ch, bg);
        }
        Tint(_settingsStrip, Theme.BgSidebar);
        Tint(_header, Theme.BgHeader);
        Tint(_actionBar, Theme.BgApp);
        Tint(_statusBand, Theme.BgHeader);
        Icon = AppIcon.Get();
        _tray.Icon = AppIcon.Get();
        Invalidate(true);
    }

    /// <summary>
    /// Field caption colour. Explanatory text stays at
    /// <see cref="Theme.TextSecondary"/>; only a control that is genuinely
    /// switched off drops to <see cref="Theme.TextDisabled"/>, so "grey" always
    /// means the one thing.
    /// </summary>
    private static Color CaptionColor(bool enabled) =>
        enabled ? Theme.TextSecondary : Theme.TextDisabled;

    private static Color ValueColor(bool enabled) =>
        enabled ? Theme.TextPrimary : Theme.TextDisabled;

    private void SyncAutoSwitchUiEnabled()
    {
        bool on = _autoEnabled.Checked;
        foreach (Control c in new Control[]
                 { _threshold, _thrLabel, _thrWindow, _thrUnit, _autoTargetLabel, _autoTargetValue })
            c.Enabled = on;
        _thrLabel.ForeColor = CaptionColor(on);
        _autoTargetLabel.ForeColor = CaptionColor(on);
        _thrWindow.ForeColor = ValueColor(on);
        _thrUnit.ForeColor = ValueColor(on);
        _autoTargetValue.ForeColor = ValueColor(on);
        _threshold.BackColor = on ? Theme.BgSurface : Theme.BgDisabled;
        _threshold.ForeColor = ValueColor(on);
        UpdateSettingsSummary();
    }

    private void SyncWarmupUiEnabled()
    {
        bool on = _warmupEnabled.Checked;
        _workHoursLabel.Enabled = on;
        _workStartHour.Enabled = on;
        _workHoursSep.Enabled = on;
        _workEndHour.Enabled = on;
        // Manual fire and the daily task still work when the guardian toggle is
        // off (force path); grey them only when there is nothing useful to do.
        _warmupNowBtn.Enabled = true;
        _warmupTask.Enabled = true;
        _workHoursLabel.ForeColor = CaptionColor(on);
        _workHoursSep.ForeColor = ValueColor(on);
        _workStartHour.BackColor = on ? Theme.BgSurface : Theme.BgDisabled;
        _workEndHour.BackColor = on ? Theme.BgSurface : Theme.BgDisabled;
        _workStartHour.ForeColor = ValueColor(on);
        _workEndHour.ForeColor = ValueColor(on);
        _warmupTask.ForeColor = Theme.TextPrimary;
        UpdateSettingsSummary();
        var (ah, am) = WarmupTaskHelper.BaseAnchor((int)_workStartHour.Value, (int)_workEndHour.Value);
        _warmupTask.Text = Loc.T("settings.warmup.task");
        _toolTipWarmup ??= new ToolTip();
        _toolTipWarmup.SetToolTip(_warmupTask,
            Loc.T("settings.warmup.task.tip", $"{ah}:{am:D2}"));
        _toolTipWarmup.SetToolTip(_warmupNowBtn, Loc.T("settings.warmup.now.tip"));
    }

    private ToolTip? _toolTipWarmup;

    /// <summary>
    /// Register or refresh the daily schtask at the base anchor time, or remove
    /// it. Uncheck always deletes <c>ClaudeSwitch-Warmup</c>; there is no other
    /// on-disk flag — Task Scheduler is the sole source of truth.
    /// </summary>
    private void SyncWarmupScheduledTask()
    {
        if (!_warmupTask.Checked || !_warmupEnabled.Checked)
        {
            WarmupTaskHelper.SetEnabled(false, 0, 0);
            return;
        }
        var (h, m) = WarmupTaskHelper.BaseAnchor((int)_workStartHour.Value, (int)_workEndHour.Value);
        WarmupTaskHelper.SetEnabled(true, h, m);
    }

    /// <param name="onlyNumber">
    /// Warm a single slot; null warms every eligible OAuth account.
    /// </param>
    private void DoWarmupNow(int? onlyNumber)
    {
        try
        {
            // Fresh usage so open windows are reported as window-active skips.
            // Pass force path only — do not let scheduled guardian double-fire first.
            try
            {
                // Snapshot-only refresh of usage numbers without nested guardian
                // would be ideal; refresh_usage runs guardian when enabled, so we
                // accept that and still call warmup_now (cooldown protects doubles).
                _engine.Call("refresh_usage");
            }
            catch { /* offline ok */ }

            object payload = onlyNumber is { } n
                ? new { id = n.ToString() }
                : new { };
            var result = _engine.Call("warmup_now", payload);
            int fired = result["fired"]?.AsArray()?.Count ?? 0;
            int skipped = result["skipped"]?.AsArray()?.Count ?? 0;
            int ok = WarmupNotice.SuccessfulFires(result).Count;
            string msg = ok > 0
                ? (onlyNumber is { } slot
                    ? Loc.T("settings.warmup.now.slot", slot)
                    : Loc.T("settings.warmup.now.done", ok, fired, skipped))
                : Loc.T("settings.warmup.now.none", skipped);
            FlashSettingsSaved(msg);
            NotifyIfWarmed(result);
            // Pull cards so warmupAnchor / resetsAt update after a fire.
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("settings.warmup.now"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Tray balloon for successful window opens (manual or guardian poll).
    /// </summary>
    private void NotifyIfWarmed(JsonNode? result)
    {
        var fires = WarmupNotice.SuccessfulFires(result);
        if (fires.Count == 0) return;

        var labels = new List<string>(fires.Count);
        foreach (var f in fires)
        {
            var m = _models.FirstOrDefault(x => x.Number == f.Number);
            labels.Add(WarmupNotice.LabelFor(
                f.Number,
                m?.Alias,
                string.IsNullOrEmpty(f.Email) ? (m?.Email ?? "") : f.Email));
        }

        try
        {
            _tray.ShowBalloonTip(
                5000,
                WarmupNotice.Title,
                WarmupNotice.Body(labels),
                ToolTipIcon.Info);
        }
        catch
        {
            // Balloon can fail when the tray is not ready; status flash is enough.
        }
    }

    /// <summary>
    /// When autoswitch is off, hide threshold ticks (0) so users don't think they still apply.
    /// </summary>
    private void ApplyThresholdMarker()
    {
        AccountCard.ThresholdMarkerPct = _autoEnabled.Checked
            ? (double)_threshold.Value
            : 0;
    }

    private void FlashSettingsSaved(string msg)
    {
        _settingsSaved.Text = msg;
        _baseStatus = msg;
        ComposeStatusLine();
        // Clear the inline "saved" flash after a few seconds.
        var t = new System.Windows.Forms.Timer { Interval = 2500 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            if (!_settingsSaved.IsDisposed && _settingsSaved.Text == msg)
                _settingsSaved.Text = "";
        };
        t.Start();
    }

    private void ApplySearchColors()
    {
        _search.BackColor = Theme.BgSurface;
        _search.ForeColor = Theme.TextPrimary;
        if (_search.TextBox is not null)
        {
            _search.TextBox.BackColor = Theme.BgSurface;
            _search.TextBox.ForeColor = Theme.TextPrimary;
        }
    }

    /// <summary>
    /// Keep card widths matched to the scroll viewport. Vertical extent is
    /// owned by FlowLayoutPanel (AutoScroll + TopDown + WrapContents=false).
    /// </summary>
    private void LayoutCardHost()
    {
        if (_cardHost.IsDisposed)
            return;

        // ClientSize already excludes the vertical scrollbar when it is shown.
        int w = Math.Max(280, _cardHost.ClientSize.Width - 4);
        if (w <= 0) return;

        _cardHost.SuspendLayout();
        foreach (Control c in _cardHost.Controls)
        {
            if (c.Width != w)
                c.Width = w;
        }
        _cardHost.ResumeLayout(true);
    }

    /// <summary>
    /// Populate a "continue session" menu from the most recent session per
    /// directory. Shared by the toolbar button and the tray so the two lists
    /// cannot drift apart.
    /// </summary>
    private void FillResumeMenu(ToolStripItemCollection items)
    {
        items.Clear();
        var recent = RecentSessions.Load(_engine, 8);

        if (recent.Count == 0)
        {
            items.Add(new ToolStripMenuItem(Loc.T("resume.none")) { Enabled = false });
        }
        else
        {
            foreach (var session in recent)
            {
                var item = new ToolStripMenuItem(RecentSessions.MenuLabel(session))
                {
                    ToolTipText = Loc.T("resume.tooltip", session.Path, session.SessionId),
                };
                item.Click += (_, _) => ResumeSession(session);
                items.Add(item);
            }
        }

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Loc.T("resume.all"), null, (_, _) => ShowProjectsWindow()));
    }

    /// <summary>
    /// Reopen a conversation, under the directory's bound account when it has one.
    /// </summary>
    /// <remarks>
    /// A binding is the answer to "which account does this directory belong to",
    /// so resuming there without honouring it would use the wrong one. An
    /// unbound directory keeps the original behaviour exactly: plain
    /// <c>claude --resume</c> on the default login.
    /// </remarks>
    private void ResumeSession(RecentSession session)
    {
        if (SessionMode.BoundAccount(_engine, session.Path) is { } bound)
        {
            var result = SessionMode.Launch(
                _engine, bound.Number.ToString(), session.Path, session.SessionId);
            if (!result.Launched)
            {
                MessageBox.Show(
                    this,
                    result.Problem,
                    Loc.T("resume.failed.title"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            _baseStatus = Loc.T("status.resumedAs", session.Name, Pii.MaskEmail(bound.Email));
            ComposeStatusLine();
            Reload();
            return;
        }

        if (ClaudeCli.Resume(session.Path, session.SessionId) is { } problem)
        {
            MessageBox.Show(this, problem, Loc.T("resume.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _baseStatus = Loc.T("status.resumed", session.Name);
        ComposeStatusLine();
    }

    /// <summary>
    /// Open a terminal running one account, leaving the default login alone.
    /// </summary>
    /// <remarks>
    /// Picks from recent working directories first (continue last chat or start
    /// new), with browse-for-folder as a fallback — not a bare directory dialog.
    /// </remarks>
    private void DoOpenSessionTerminal(AccountCardModel model)
    {
        string label = string.IsNullOrWhiteSpace(model.Alias)
            ? Pii.MaskEmail(model.Email)
            : $"{model.Alias} · {Pii.MaskEmail(model.Email)}";
        if (SessionLaunchDialog.Pick(this, _engine, model.Number, label) is not { } pick)
        {
            return;
        }

        var result = SessionMode.Launch(
            _engine, model.Number.ToString(), pick.Directory, pick.SessionId);
        if (!result.Launched)
        {
            MessageBox.Show(
                this,
                result.Problem,
                Loc.T("session.failed.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string dirName = System.IO.Path.GetFileName(pick.Directory.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(dirName)) dirName = pick.Directory;
        _baseStatus = result.Note
            ?? (pick.SessionId is null
                ? Loc.T("status.sessionOpenedNew", model.Number, dirName)
                : Loc.T("status.sessionOpenedResume", model.Number, dirName));
        ComposeStatusLine();
        // The new terminal writes its PID file as it starts, so the card's
        // session count only becomes true on the next snapshot.
        Reload();
    }

    /// <summary>
    /// Open a supervised ACP run for one account.
    /// </summary>
    /// <remarks>
    /// Shares the directory picker with the terminal path — the choice is the
    /// same one, only the thing that gets launched differs. The account profile
    /// comes from <c>session_prepare</c>, so this run is authenticated as the
    /// named account exactly as a session-mode terminal would be.
    /// </remarks>
    private void DoOpenAgentWindow(AccountCardModel model)
    {
        if (AcpLaunch.FindAdapter() is null)
        {
            MessageBox.Show(
                this,
                Loc.T("acp.err.adapterSetup", AcpLaunch.AdapterPackage),
                Loc.T("acp.window.plain"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string label = string.IsNullOrWhiteSpace(model.Alias)
            ? Pii.MaskEmail(model.Email)
            : $"{model.Alias} · {Pii.MaskEmail(model.Email)}";

        if (SessionLaunchDialog.Pick(this, _engine, model.Number, label) is not { } pick)
        {
            return;
        }

        string? configDir;
        try
        {
            var prepared = _engine.Call("session_prepare", new { id = model.Number.ToString() });
            // "Use the default login" means this account already *is* the active
            // one; a second profile copy of its token would drift from it.
            configDir = prepared["useDefaultLogin"]?.GetValue<bool>() == true
                ? null
                : prepared["configDir"]?.GetValue<string>();
        }
        catch (EngineException ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                Loc.T("session.failed.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var window = NewAgentWindow(pick.Directory, configDir, model.Number, label);
        // Modeless: a supervised run can take a long time, and the main window
        // has to stay usable while it does.
        window.Show(this);
    }

    /// <summary>
    /// Build a run view with this app's engine, journal and permission handler.
    /// </summary>
    private AgentWindow NewAgentWindow(
        string workDir, string? configDir, int? accountNumber, string accountLabel) =>
        new(_engine, _agentRuns, workDir, configDir, accountNumber, accountLabel, AskPermissionAsync);

    /// <summary>
    /// Answer an agent's permission request with a dialog.
    /// </summary>
    /// <remarks>
    /// Owned by the main window rather than a run's own view: a background run
    /// can ask while its view is closed, and the question still has to reach
    /// somebody. If nobody is there the turn simply waits, which is the honest
    /// outcome — the alternative is approving on a user's behalf.
    /// </remarks>
    private Task<string?> AskPermissionAsync(JsonNode request) =>
        AcpPermissionDialog.AskAsync(this, request);

    /// <summary>Open a window that resumes a journaled run.</summary>
    private AgentWindow? OpenAgentRun(AgentRunRecord record)
    {
        if (!Directory.Exists(record.Cwd))
        {
            MessageBox.Show(
                this,
                Loc.T("acp.err.workDir", record.Cwd),
                Loc.T("acp.window.plain"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        string label = record.AccountNumber is { } n
            ? LabelForAccount(n)
            : Loc.T("acp.runs.unknownAccount");

        var window = NewAgentWindow(record.Cwd, record.ConfigDir, record.AccountNumber, label);
        if (record.IsResumable) window.PrepareResume(record);
        return window;
    }

    /// <summary>
    /// Render the Runs dropdown offscreen and report its rows.
    /// </summary>
    /// <remarks>
    /// The visual check for the stalled-session section. Unlike the overlay
    /// capture this does not need a composited desktop, so it also works over a
    /// remote or headless session — which is where the screen copy comes back
    /// black.
    /// </remarks>
    private string ProbeRunsMenu()
    {
        FillRunsMenu(_btnRuns.DropDownItems);
        var drop = _btnRuns.DropDown;
        drop.PerformLayout();
        Application.DoEvents();

        var rows = _btnRuns.DropDownItems
            .OfType<ToolStripItem>()
            .Select(i => i is ToolStripSeparator ? "  ---" : $"  [{i.Text}]")
            .ToList();

        string? dir = Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LAYOUT_DIR");
        string saved = "(not saved)";
        if (!string.IsNullOrWhiteSpace(dir) && drop.Width > 0 && drop.Height > 0)
        {
            try
            {
                using var bmp = new Bitmap(drop.Width, drop.Height);
                drop.DrawToBitmap(bmp, new Rectangle(0, 0, drop.Width, drop.Height));
                string path = Path.Combine(dir, "gui-runs-menu-rendered.png");
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                saved = path;
            }
            catch (Exception ex)
            {
                saved = $"(render failed: {ex.GetType().Name})";
            }
        }

        return $"runs_menu items={rows.Count} render={saved}\n{string.Join("\n", rows)}";
    }

    /// <summary>Account label for a run whose slot may not be recorded.</summary>
    private string AccountLabelOrUnknown(int? number) =>
        number is { } n ? LabelForAccount(n) : Loc.T("acp.runs.unknownAccount");

    /// <summary>
    /// Look for terminal sessions that stalled, and resume them.
    /// </summary>
    /// <remarks>
    /// Runs on the UI timer but does its work off it: the scan reads transcripts
    /// and the takeover spawns agents, neither of which belongs on the thread
    /// painting the window.
    /// </remarks>
    private void SweepStalledSessions()
    {
        if (IsDisposed || Disposing) return;
        _ = Task.Run(() =>
        {
            int started;
            try
            {
                started = _stalled.SweepAndResume();
            }
            catch (Exception ex) when (ex is EngineException or ObjectDisposedException
                                          or IOException or InvalidOperationException)
            {
                // A sweep is opportunistic; failing one must not take the app
                // down or stop later sweeps from running.
                return;
            }
            if (started == 0) return;

            if (IsDisposed || Disposing) return;
            try
            {
                BeginInvoke(() =>
                {
                    _baseStatus = Loc.T("stalled.status.resumed", started);
                    ComposeStatusLine();
                    UpdateTaskBoard();
                });
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // Shutting down mid-sweep.
            }
        });
    }

    /// <summary>
    /// Say what a takeover did, once it is settled.
    /// </summary>
    /// <remarks>
    /// A balloon rather than a dialog: this happens while the user is elsewhere,
    /// which is the entire point of the feature. A modal would be waiting for
    /// them instead of the work being done.
    /// </remarks>
    private void OnTakeoverFinished(StalledRecord record, bool ok, string? detail)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            BeginInvoke(() =>
            {
                string kind = Loc.T($"stalled.kind.{record.Reason}");
                _tray.BalloonTipTitle = Loc.T("stalled.notice.title");
                _tray.BalloonTipText = ok
                    ? Loc.T("stalled.notice.resumed.single", kind)
                    : Loc.T("stalled.notice.failed.single", detail ?? kind);
                _tray.ShowBalloonTip(8000);
                UpdateTaskBoard();
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Shutting down as a run reported.
        }
    }

    private string LabelForAccount(int number)
    {
        var model = _models.FirstOrDefault(m => m.Number == number);
        if (model is null) return Loc.T("alias.account", number);
        return string.IsNullOrWhiteSpace(model.Alias)
            ? Pii.MaskEmail(model.Email)
            : $"{model.Alias} · {Pii.MaskEmail(model.Email)}";
    }

    /// <summary>
    /// Show how many agents are working, on the button that opens them.
    /// </summary>
    /// <remarks>
    /// Background runs are invisible by construction — this is the only place
    /// the app admits one is happening once its window is closed.
    /// </remarks>
    private void UpdateRunsButton()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(UpdateRunsButton);
                return;
            }
            // The same total the task band shows, not a subset of it. Counting
            // only live runs here while the band counted everything put two
            // different figures for "tasks" in one window.
            int total = _taskBoard?.TaskCount ?? 0;
            _btnRuns.Text = total == 0
                ? Loc.T("toolbar.runs")
                : Loc.T("toolbar.runs.active", total);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Shutting down while a run reported.
        }
    }

    /// <summary>
    /// Refresh the supervised-work band on the main window.
    /// </summary>
    /// <remarks>
    /// Ordered by what needs a person soonest: sessions a takeover could not
    /// rescue, then agents still working, then work that can be picked up. The
    /// band hides itself when all three are empty, so it costs nothing on a
    /// machine that never uses supervised runs.
    /// </remarks>
    private void UpdateTaskBoard()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(UpdateTaskBoard);
                return;
            }

            var entries = new List<TaskEntry>();

            // A live run has no dismiss: hiding work that is still happening is
            // how a supervised run quietly becomes an unsupervised one.
            foreach (var run in BackgroundRuns.Active)
            {
                var captured = run;
                bool blocked = run.AwaitingPermission;
                entries.Add(new TaskEntry(
                    blocked ? TaskState.AwaitingPermission : TaskState.Running,
                    Title(run.Prompt),
                    AccountText(run),
                    run.Cwd,
                    Elapsed(DateTime.UtcNow - run.StartedUtc),
                    () => OpenLiveRun(captured),
                    // A run parked on a question needs answering, not watching,
                    // and the answer is in the window this opens.
                    blocked ? Loc.T("board.action.continue") : Loc.T("board.action.view"),
                    Stop: () => captured.Cancel(),
                    OpenFolder: FolderAction(captured.Cwd)));
            }

            foreach (var failure in _stalled.Failures)
            {
                var record = failure.Record;
                entries.Add(new TaskEntry(
                    TaskState.NeedsAttention,
                    Loc.T($"stalled.kind.{record.Reason}"),
                    record.AccountNumber is { } n ? LabelForAccount(n) : null,
                    record.Cwd,
                    Elapsed(TimeSpan.FromMilliseconds(record.IdleMs)),
                    () => OpenStalledSession(record),
                    Loc.T("board.action.retry"),
                    // Dismissing drops the warning, not the session: the
                    // transcript stays where it is and the terminal is untouched.
                    Ignore: () => _stalled.Forget(record.SessionId),
                    OpenFolder: FolderAction(record.Cwd),
                    Note: failure.Detail));
            }

            foreach (var record in _agentRuns.Resumable())
            {
                var captured = record;
                entries.Add(new TaskEntry(
                    StateOf(record.Status),
                    record.Title,
                    record.AccountNumber is { } n ? LabelForAccount(n) : null,
                    record.Cwd,
                    Since(record.UpdatedMs),
                    () => OpenAgentRun(captured)?.Show(this),
                    Loc.T("board.action.continue"),
                    Ignore: () => ForgetRun(captured.Id),
                    OpenFolder: FolderAction(captured.Cwd)));
            }

            // Most urgent first, so the top of the band is what actually wants a
            // person rather than whatever happened to be created last.
            entries.Sort((a, b) => a.State.CompareTo(b.State));

            _taskBoard.Show(entries);
            _taskBoard.Visible = entries.Count > 0;
            ApplyBandHeights();
            // The toolbar quotes the band's total, so it is refreshed from here
            // rather than by each caller — one of them would eventually forget,
            // and the window would state two totals again.
            UpdateRunsButton();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Shutting down while a run reported.
        }
    }

    /// <summary>
    /// The account a run is spending, showing a handover when there was one.
    /// </summary>
    /// <remarks>
    /// A run that changed account did so because the first one hit its quota
    /// wall. In an app whose whole job is juggling accounts, that is the most
    /// useful thing a row can say about a long run.
    /// </remarks>
    private static string AccountText(LiveRun run) =>
        run.PreviousAccountLabel is { Length: > 0 } from
            ? Loc.T("board.switched", from, run.AccountLabel)
            : run.AccountLabel;

    /// <summary>Map a journal status onto what the board shows.</summary>
    private static TaskState StateOf(string status) => status switch
    {
        "failed" => TaskState.Failed,
        "cancelled" => TaskState.Cancelled,
        _ => TaskState.Interrupted,
    };

    /// <summary>A duration a person can read at a glance.</summary>
    private static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) return "";
        if (span.TotalMinutes < 1) return Loc.T("board.elapsed.justNow");
        if (span.TotalHours < 1) return Loc.T("board.elapsed.minutes", (int)span.TotalMinutes);
        if (span.TotalDays < 1) return Loc.T("board.elapsed.hours", (int)span.TotalHours);
        return Loc.T("board.elapsed.days", (int)span.TotalDays);
    }

    private static string Since(long epochMs) =>
        Elapsed(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(epochMs));

    /// <summary>
    /// An "open folder" action, or null when there is no folder to open.
    /// </summary>
    /// <remarks>
    /// A row that says <i>working directory is gone</i> used to offer the button
    /// anyway, and clicking it produced a dialog restating what the row already
    /// said. Withholding the action is the honest form of that message.
    /// </remarks>
    private Action? FolderAction(string path) =>
        Directory.Exists(path) ? () => OpenFolder(path) : null;

    /// <summary>Show a task's working directory in Explorer.</summary>
    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            // Still guarded: the directory can vanish between the board being
            // built and the button being clicked.
            MessageBox.Show(
                this,
                Loc.T("acp.err.workDir", path),
                Loc.T("acp.window.plain"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Explorer refused; not worth taking the window down over.
        }
    }

    /// <summary>
    /// Drop a run's journal entry, so it stops being offered.
    /// </summary>
    /// <remarks>
    /// The record goes; the conversation does not. Its transcript is still on
    /// disk and still reachable from <b>Resume</b>, which is what makes this
    /// safe to do without a confirmation on every row — the journal is a list of
    /// reminders, and a reminder you have read is one you should be able to
    /// clear in one click.
    /// </remarks>
    private void ForgetRun(string id)
    {
        _agentRuns.Remove(id);
        UpdateTaskBoard();
    }

    /// <summary>
    /// List supervised runs: the ones still going, then the ones worth resuming.
    /// </summary>
    /// <remarks>
    /// A dropdown rather than a window. Background runs need <em>somewhere</em>
    /// to be reachable — closing a run's view must not hide work that is still
    /// happening — but a handful of rows does not earn a form of its own.
    /// </remarks>
    private void FillRunsMenu(ToolStripItemCollection items)
    {
        items.Clear();

        var live = BackgroundRuns.Active;
        foreach (var run in live)
        {
            var item = new ToolStripMenuItem(Loc.T("acp.runs.item.live", Title(run.Prompt)))
            {
                ToolTipText = run.Cwd,
            };
            var captured = run;
            item.Click += (_, _) => OpenLiveRun(captured);
            items.Add(item);
        }

        // Resumable records are what a crash or a quit leaves behind. Without a
        // way to act on them here they would only ever be a startup balloon.
        var resumable = _agentRuns.Resumable();
        if (live.Count > 0 && resumable.Count > 0)
        {
            items.Add(new ToolStripSeparator());
        }
        foreach (var record in resumable.Take(8))
        {
            var item = new ToolStripMenuItem(Loc.T("acp.runs.item.resume", record.Title))
            {
                ToolTipText = record.LastError is { Length: > 0 } why
                    ? $"{record.Cwd}\n{why}"
                    : record.Cwd,
            };
            var captured = record;
            item.Click += (_, _) => OpenAgentRun(captured)?.Show(this);
            items.Add(item);
        }

        // Stalled terminals a takeover could not rescue. They are somebody
        // else's window, so the only thing offered is a way to look at them —
        // the automatic attempt already happened and did not work.
        var failures = _stalled.Failures;
        if (failures.Count > 0)
        {
            if (items.Count > 0) items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem(Loc.T("stalled.section")) { Enabled = false });

            foreach (var failure in failures.Take(8))
            {
                var record = failure.Record;
                // The reason is in the label, not only the tooltip: two sessions
                // stalled in the same directory are otherwise the same row twice.
                var item = new ToolStripMenuItem(Loc.T(
                    "stalled.item.failed",
                    Loc.T($"stalled.kind.{record.Reason}"),
                    Title(record.Cwd)))
                {
                    ToolTipText = Loc.T(
                        "stalled.item.tip",
                        record.Cwd,
                        record.IdleMinutes,
                        Loc.T($"stalled.kind.{record.Reason}"),
                        failure.Detail),
                };
                item.Click += (_, _) => OpenStalledSession(record);
                items.Add(item);
            }
        }

        if (items.Count == 0)
        {
            items.Add(new ToolStripMenuItem(Loc.T("acp.runs.empty")) { Enabled = false });
        }
    }

    /// <summary>
    /// Open a window on a stalled session the automatic takeover could not fix.
    /// </summary>
    /// <remarks>
    /// Dismisses the row on the way: the user is now looking at it, and a
    /// warning that outlives the thing it warned about trains people to ignore
    /// the next one.
    /// </remarks>
    private void OpenStalledSession(StalledRecord record)
    {
        if (!Directory.Exists(record.Cwd))
        {
            MessageBox.Show(
                this,
                Loc.T("acp.err.workDir", record.Cwd),
                Loc.T("acp.window.plain"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var window = NewAgentWindow(
            record.Cwd,
            record.ConfigDir,
            record.AccountNumber,
            AccountLabelOrUnknown(record.AccountNumber));
        _stalled.Forget(record.SessionId);
        window.Show(this);
        UpdateTaskBoard();
    }

    /// <summary>First line of a prompt, bounded for a menu row.</summary>
    private static string Title(string prompt)
    {
        string first = prompt
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        return first.Length <= 60 ? first : string.Concat(first.AsSpan(0, 59), "\u2026");
    }

    /// <summary>Open a window already watching a run that is in flight.</summary>
    private void OpenLiveRun(LiveRun live)
    {
        var window = NewAgentWindow(
            live.Cwd,
            configDir: null,
            accountNumber: live.Runner.AccountNumber,
            accountLabel: live.AccountLabel);
        window.Attach(live);
        window.Show(this);
    }

    /// <summary>
    /// Point out runs left unfinished by a previous session.
    /// </summary>
    /// <remarks>
    /// A balloon rather than a modal: the app has just started, and a dialog
    /// demanding a decision about work from yesterday is the wrong way to open.
    /// The runs window is where the decision belongs.
    /// </remarks>
    private void NoticeResumableRuns()
    {
        var resumable = _agentRuns.Resumable();
        if (resumable.Count == 0) return;

        _tray.BalloonTipTitle = Loc.T("acp.runs.notice.title");
        _tray.BalloonTipText = resumable.Count == 1
            ? Loc.T("acp.runs.notice.single", resumable[0].Title)
            : Loc.T("acp.runs.notice.several", resumable.Count);
        _tray.ShowBalloonTip(8000);

        // No status-line copy of the count. The task band is directly above and
        // says it with more context; a second figure in the status bar counted a
        // different subset, so the window stated two totals for the same thing
        // and pointed at the weaker surface to resolve them.
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(Loc.T("tray.refresh"), null, (_, _) => Reload());
        menu.Items.Add(new ToolStripSeparator());

        // The tray is where someone returning to their machine actually looks,
        // and the main window is usually closed at that moment.
        var resume = new ToolStripMenuItem(Loc.T("tray.resume"));
        resume.DropDownOpening += (_, _) => FillResumeMenu(resume.DropDownItems);
        // A submenu with no children never opens, so seed one placeholder.
        resume.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        menu.Items.Add(resume);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) =>
        {
            // Just ask to exit. `Application.Exit` raises FormClosing with
            // ApplicationExitCall, and that handler already hides the tray icon,
            // saves prefs, stops live runs and disposes the engine — in an order
            // that works. Doing any of it here first disposed the engine out
            // from under the shutdown that still needs it.
            Application.Exit();
        });
        return menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Dynamic primary CTA:
    /// 未选中 → 请选择账号
    /// 选中当前 → 当前正在使用
    /// 选中其他 → 切换到该账号
    /// 切换中 → 正在切换…
    /// </summary>
    private void UpdateSwitchEnabled()
    {
        if (_switching)
        {
            _btnSwitch.Enabled = false;
            _btnSwitch.Text = Loc.T("toolbar.switch.busy");
            _btnSwitch.ToolTipText = Loc.T("action.switching");
            return;
        }
        if (_selected is null)
        {
            _btnSwitch.Enabled = false;
            _btnSwitch.Text = Loc.T("toolbar.switch.select");
            _btnSwitch.ToolTipText = Loc.T("action.selectFirst");
            return;
        }
        if (_selected.Model.Active)
        {
            _btnSwitch.Enabled = false;
            _btnSwitch.Text = Loc.T("toolbar.switch.current");
            _btnSwitch.ToolTipText = Loc.T("action.alreadyCurrent");
        }
        else if (_selected.Model.Disabled)
        {
            _btnSwitch.Enabled = true;
            _btnSwitch.Text = Loc.T("toolbar.switch.go");
            _btnSwitch.ToolTipText = Loc.T("switch.toDisabled");
        }
        else
        {
            _btnSwitch.Enabled = true;
            _btnSwitch.Text = Loc.T("toolbar.switch.go");
            _btnSwitch.ToolTipText = Loc.T("toolbar.switch.tip");
        }
    }

    /// <summary>Called by onboarding wizard after add-account attempt.</summary>
    public void ReloadPublic() => Reload();

    private void ToggleDrawer()
    {
        if (DetailOpen)
            CloseDetailWindow();
        else
            OpenDetailSafe();
    }

    private void EnsureDetailForm()
    {
        if (_detailForm is { IsDisposed: false })
            return;

        _detailForm = new Form
        {
            // Empty caption + no system chrome — close is the soft × in the panel header.
            Text = "",
            FormBorderStyle = FormBorderStyle.Sizable,
            ControlBox = false,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowIcon = false,
            Width = UsageDrawer.DrawerWidth + 16,
            // Content-sized default — avoid a tall empty middle with footer stuck at bottom.
            Height = UsageDrawer.PreferredHostHeight,
            MinimumSize = new Size(460, 420),
            Owner = this,
            Font = Theme.FontBody,
            BackColor = Theme.BgDrawer,
            Padding = new Padding(0),
        };

        _drawer.Dock = DockStyle.Fill;
        if (_drawer.Parent is not null)
            _drawer.Parent.Controls.Remove(_drawer);
        _detailForm.Controls.Add(_drawer);

        _detailForm.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                CloseDetailWindow();
            }
        };
        // After first show / resize, re-measure text and keep footer height.
        _detailForm.Shown += (_, _) => _drawer.Relayout();
        _detailForm.Resize += (_, _) =>
        {
            if (_detailForm is { Visible: true })
                _drawer.Relayout();
        };
    }

    private void PositionDetailForm()
    {
        if (_detailForm is null || _detailForm.IsDisposed) return;

        var screen = Screen.FromControl(this).WorkingArea;
        FitDetailFormToContent();
        int w = _detailForm.Width;
        int h = _detailForm.Height;

        // Prefer immediately to the right of the main window (no overlap).
        int x = Right + 8;
        int y = Top;
        if (x + w > screen.Right)
        {
            x = Left - w - 8;
            if (x < screen.Left)
                x = screen.Left + 8;
        }
        if (y + h > screen.Bottom)
            y = Math.Max(screen.Top, screen.Bottom - h);
        if (y < screen.Top)
            y = screen.Top;

        _detailForm.Location = new Point(x, y);
    }

    /// <summary>
    /// Size the detail tool window to content (~520–600px typical) instead of
    /// stretching to the main window height with a large empty middle.
    /// </summary>
    private void FitDetailFormToContent()
    {
        if (_detailForm is null || _detailForm.IsDisposed) return;

        var screen = Screen.FromControl(this).WorkingArea;
        int chrome = Math.Max(0, _detailForm.Height - _detailForm.ClientSize.Height);
        int clientH = _drawer.PreferredHostClientHeight;
        if (clientH < 280)
            clientH = UsageDrawer.PreferredHostHeight - chrome;
        // Preferred band ~480–760; grow only when content or DPI needs it. The
        // upper bound covers usage + subscription facts without scrolling on a
        // 1080p screen; taller content still scrolls inside the drawer.
        int h = Math.Clamp(clientH + chrome, 480, Math.Min(760, screen.Height - 24));
        if (_detailForm.Width < 470)
            _detailForm.Width = UsageDrawer.DrawerWidth + 16;
        if (Math.Abs(_detailForm.Height - h) > 6)
            _detailForm.Height = h;
    }

    private void CloseDetailWindow()
    {
        _drawer.Clear();
        if (_detailForm is { IsDisposed: false, Visible: true })
            _detailForm.Hide();
    }

    private void ShowDetailForSelection()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, Loc.T("action.selectFirst.detail"), Loc.T("detail.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        EnsureDetailForm();
        if (_detailForm is null) return;

        PositionDetailForm();
        // Show first so the drawer handle exists, then bind (avoids BeginInvoke-before-handle).
        if (!_detailForm.Visible)
            _detailForm.Show(this);
        else
            _detailForm.BringToFront();

        _drawer.Bind(_selected.Model);
        _drawer.Relayout();
        _detailForm.Activate();
    }

    /// <summary>One shared instance: reopening should return to where you were.</summary>
    private ProjectsWindow? _projectsWindow;

    private OverviewWindow? _overviewWindow;

    /// <summary>
    /// Fill the activity strip off the UI thread.
    /// </summary>
    /// <remarks>
    /// The scan reads every transcript (~85 MB here). It is cached against a
    /// stat-only fingerprint, so this is usually a file read — but the first run
    /// after new sessions is the full ~500 ms, which must not land on the UI
    /// thread. The native engine serialises calls behind its own mutex, so a poll
    /// tick during the scan waits rather than racing.
    /// </remarks>
    /// <summary>
    /// Fill the activity strip: the stored figures at once, then the fresh ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan behind this reads every transcript byte on the machine — over a
    /// minute on a working developer's history. Worse, the cache it fills is
    /// keyed on total bytes and newest mtime, and this app's users are by
    /// definition running Claude Code, which appends continuously: the key
    /// changes between launches, so the cache missed nearly every time and the
    /// strip sat in its loading state indefinitely.
    /// </para>
    /// <para>
    /// So the stored answer is painted first, however old, and the rescan
    /// replaces it when it lands. A figure from the last launch, visible now,
    /// is worth more than an exact one that never arrives.
    /// </para>
    /// </remarks>
    private void LoadActivityStripAsync()
    {
        _ = Task.Run(() =>
        {
            bool needsRefresh = true;
            try
            {
                var peek = _engine.Call("overview_peek");
                if (peek["found"]?.GetValue<bool>() == true)
                {
                    ApplyActivityStrip(peek["stats"]);
                    needsRefresh = peek["stale"]?.GetValue<bool>() != false;
                }
            }
            catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
            {
                // An engine without the method, or one on its way out: fall
                // through to the full scan.
            }

            if (!needsRefresh) return;

            // Probe hook: the peek makes the loading state almost unreachable,
            // so it needs forcing to be reviewed.
            if (int.TryParse(
                    Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_STRIP_DELAY_MS"),
                    out var delay) && delay > 0)
            {
                Thread.Sleep(delay);
            }

            JsonNode? stats = null;
            try
            {
                stats = _engine.Call("overview_stats");
            }
            catch
            {
                // No transcripts, or an unreadable store: the strip stays hidden.
            }
            ApplyActivityStrip(stats);
        });
    }

    private void ApplyActivityStrip(JsonNode? stats)
    {
        if (IsDisposed) return;

        // The cache peek answers in milliseconds — before the window has a
        // handle, since this starts in the constructor. Dropping the result
        // there left the strip loading forever, because a fresh cache also
        // means no rescan follows to try again.
        if (!IsHandleCreated)
        {
            void OnReady(object? sender, EventArgs e)
            {
                HandleCreated -= OnReady;
                ApplyActivityStrip(stats);
            }
            HandleCreated += OnReady;
            // The handle can appear between the test and the subscription.
            if (IsHandleCreated)
            {
                HandleCreated -= OnReady;
                ApplyActivityStrip(stats);
            }
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) _activityStrip.Apply(stats);
            }));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Window closed while the scan was running.
        }
    }

    /// <summary>
    /// Scan every transcript and show the machine-wide picture.
    /// </summary>
    /// <remarks>
    /// Reads every byte of every transcript, so it runs on click and never on a
    /// timer. The wait is stated up front rather than left as a frozen window.
    /// </remarks>
    private void ShowOverviewWindow()
    {
        if (_overviewWindow is { IsDisposed: false })
        {
            _overviewWindow.Activate();
            return;
        }

        var previous = Cursor;
        Cursor = Cursors.WaitCursor;
        _baseStatus = Loc.T("status.overviewRunning");
        ComposeStatusLine();
        Application.DoEvents();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var stats = _engine.Call("overview_stats");
            sw.Stop();
            _overviewWindow = new OverviewWindow(stats, sw.ElapsedMilliseconds);
            _overviewWindow.FormClosed += (_, _) => _overviewWindow = null;
            _overviewWindow.Show(this);
            _baseStatus = Loc.T("status.overviewReady", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("status.overviewFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _baseStatus = Loc.T("status.overviewFailed");
        }
        finally
        {
            Cursor = previous;
            ComposeStatusLine();
        }
    }

    private void ShowProjectsWindow()
    {
        if (_projectsWindow is { IsDisposed: false })
        {
            _projectsWindow.Activate();
            return;
        }
        _projectsWindow = new ProjectsWindow(_engine);
        _projectsWindow.FormClosed += (_, _) => _projectsWindow = null;
        _projectsWindow.Show(this);
    }

    private void OnCardReorder(CardDrop drop)
    {
        int from = _models.FindIndex(m => m.Number == drop.Source.Model.Number);
        int to = _models.FindIndex(m => m.Number == drop.Target.Model.Number);
        if (from < 0 || to < 0) return;

        if (CardReorder.Apply(_models, from, to, drop.After) is not { } reordered)
            return; // dropped back where it started — nothing to persist

        // Persist before showing it: on failure nothing has changed, so there is
        // no wrong order on screen to walk back.
        try
        {
            _engine.Call("reorder_accounts", new { order = reordered.Select(m => m.Number).ToArray() });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("reorder.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Reload();
            return;
        }

        // Render from what we already have. The old path called Reload(), which
        // fetches usage over the network on the UI thread — every drop froze the
        // window for the length of an HTTP round trip.
        _models = reordered;
        RebuildCards();
        _baseStatus = Loc.T("status.reorder");
        ComposeStatusLine();
    }

    /// <summary>
    /// Demo accounts for `--fixture`. Plans deliberately vary across the tiers —
    /// including Max/Team/Enterprise, which no real account here can exercise —
    /// so the badge and detail section can be eyeballed for every branch.
    /// </summary>
    private static void SeedFixture(string root)
    {
        using var eng = new Engine(root);

        static string Cred(string email, string token, string? sub, string? rate)
        {
            // Shaped like a real credential, expiry included: a session profile
            // seeded from this is what Claude Code would actually be handed, and
            // without `expiresAt` it reads the profile as logged out — which
            // would make the demo misrepresent the feature.
            long expires = DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds();
            var oauth = new JsonObject
            {
                ["accessToken"] = token,
                ["refreshToken"] = "r",
                ["expiresAt"] = expires,
                ["refreshTokenExpiresAt"] = expires + 30L * 24 * 3600 * 1000,
                ["scopes"] = new JsonArray("user:inference", "user:profile"),
                ["emailAddress"] = email,
            };
            if (sub is not null) oauth["subscriptionType"] = sub;
            if (rate is not null) oauth["rateLimitTier"] = rate;
            return new JsonObject { ["claudeAiOauth"] = oauth }.ToJsonString();
        }

        // Expired, and no refresh token to renew it with — the shape a slot ends
        // up in after the account is signed out elsewhere.
        static string DeadCred(string email) =>
            new JsonObject
            {
                ["claudeAiOauth"] = new JsonObject
                {
                    ["accessToken"] = "tok-dead",
                    ["refreshToken"] = "",
                    ["expiresAt"] = 0,
                    ["scopes"] = new JsonArray("user:inference", "user:profile"),
                    ["emailAddress"] = email,
                    ["subscriptionType"] = "pro",
                },
            }.ToJsonString();

        static string Cfg(
            string email,
            string? orgType = null,
            string? billing = null,
            string? subscribedAt = null,
            string? orgName = null,
            string? role = null,
            string? seat = null,
            bool extraUsage = false)
        {
            var acct = new JsonObject
            {
                ["emailAddress"] = email,
                // Distinct per account: this is what identifies which slot the
                // live login belongs to, so a shared uuid would make the demo
                // ambiguous in a way real accounts never are.
                ["accountUuid"] = $"uuid-{email}",
                ["organizationUuid"] = $"org-{email}",
                ["organizationName"] = orgName ?? $"{email}'s Organization",
                ["displayName"] = email,
                ["accountCreatedAt"] = "2025-11-02T08:15:00Z",
                ["hasExtraUsageEnabled"] = extraUsage,
                ["profileFetchedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            if (orgType is not null) acct["organizationType"] = orgType;
            if (billing is not null) acct["billingType"] = billing;
            if (subscribedAt is not null) acct["subscriptionCreatedAt"] = subscribedAt;
            if (role is not null) acct["organizationRole"] = role;
            if (seat is not null) acct["seatTier"] = seat;
            return new JsonObject { ["oauthAccount"] = acct }.ToJsonString();
        }

        eng.Call("add_raw", new
        {
            number = 1,
            email = "alice@example.com",
            credentials = Cred("alice@example.com", "tok-a", "pro", "default_claude_ai"),
            config = Cfg("alice@example.com", "claude_pro", "google_play_subscription",
                "2026-05-24T05:45:13Z", role: "admin"),
        });
        eng.Call("add_raw", new
        {
            number = 2,
            email = "bob@example.com",
            credentials = Cred("bob@example.com", "tok-b", "max", "default_claude_max_20x"),
            config = Cfg("bob@example.com", "claude_max", "stripe_subscription",
                "2026-07-25T09:57:13Z", role: "admin", extraUsage: true),
        });
        eng.Call("add_raw", new
        {
            number = 3,
            email = "team@example.com",
            credentials = Cred("team@example.com", "tok-a", "team", null),
            config = Cfg("team@example.com", "claude_team", "invoice",
                "2026-02-01T00:00:00Z", orgName: "Acme Inc", role: "admin", seat: "standard"),
            alias = "work",
        });
        // Extra rows force vertical scroll in layout probe (list must not clip).
        eng.Call("add_raw", new
        {
            number = 4,
            email = "carol@example.com",
            credentials = Cred("carol@example.com", "tok-c", "max", "default_claude_max_5x"),
            config = Cfg("carol@example.com", "claude_max", "stripe_subscription",
                "2026-06-11T12:00:00Z"),
            alias = "side",
        });
        // Legacy slot: backups predating the plan fields — must render no badge.
        eng.Call("add_raw", new
        {
            number = 5,
            email = "dave@example.com",
            credentials = Cred("dave@example.com", "tok-d", null, null),
            config = Cfg("dave@example.com"),
        });
        eng.Call("add_raw", new
        {
            number = 6,
            email = "erin@example.com",
            credentials = Cred("erin@example.com", "tok-e", "enterprise", null),
            config = Cfg("erin@example.com", "claude_enterprise", "invoice",
                "2025-12-20T00:00:00Z", orgName: "Globex", role: "member", seat: "premium"),
            alias = "lab",
        });
        // A slot whose credential can no longer be renewed. The attention state
        // is the one card layout with a different shape — an amber badge and a
        // button where the plan tag goes — and without a fixture for it the only
        // way to see it rendered is to break a real account.
        eng.Call("add_raw", new
        {
            number = 7,
            email = "frank@example.com",
            credentials = DeadCred("frank@example.com"),
            config = Cfg("frank@example.com"),
            alias = "old",
        });

        SeedFixtureSession(eng, Path.Combine(root, ".claude"));
    }

    /// <summary>
    /// A session profile with a live terminal, so the demo shows that state.
    /// </summary>
    /// <remarks>
    /// Liveness is read from Claude Code's own <c>sessions/&lt;pid&gt;.json</c>
    /// files and verified against the running process, so a made-up pid would be
    /// filtered out immediately. The fixture claims its own process id — the one
    /// pid guaranteed to still be alive when the window paints.
    /// </remarks>
    private static void SeedFixtureSession(Engine eng, string defaultHome)
    {
        try
        {
            var launch = eng.Call("session_prepare", new { id = "3" });
            if (launch["configDir"]?.GetValue<string>() is not { Length: > 0 } dir) return;

            var pids = Path.Combine(dir, "sessions");
            Directory.CreateDirectory(pids);
            int pid = Environment.ProcessId;
            File.WriteAllText(
                Path.Combine(pids, $"{pid}.json"),
                new JsonObject
                {
                    ["pid"] = pid,
                    ["sessionId"] = "demo-session",
                    ["cwd"] = @"D:\work\acme",
                    ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["entrypoint"] = "cli",
                }.ToJsonString());

            eng.Call("mapping_set", new { path = @"D:\work\acme", id = "3" });

            // One directory worked in from both the default login and this
            // session profile — the case the directory list has to merge rather
            // than show twice, and the one that regresses silently if the
            // scanners ever go back to a single root.
            SeedTranscript(defaultHome, @"D:\work\acme", "s-default");
            SeedTranscript(dir, @"D:\work\acme", "s-session");
            SeedTranscript(defaultHome, @"D:\personal\notes", "s-notes");
        }
        catch (Exception)
        {
            // The fixture is a demo; a slot that cannot host a session profile
            // just shows the app without one.
        }
    }

    /// <summary>Write one transcript under a config home, as Claude Code would.</summary>
    private static void SeedTranscript(string configHome, string cwd, string sessionId)
    {
        // Claude Code encodes the directory into the folder name; the scanners
        // read the real path out of the transcript, so any stable slug will do.
        string slug = cwd.Replace('\\', '-').Replace(':', '-').Replace('/', '-');
        string dir = Path.Combine(configHome, "projects", slug);
        Directory.CreateDirectory(dir);

        var lines = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            lines.Add(new JsonObject
            {
                ["type"] = "user",
                ["cwd"] = cwd,
                ["sessionId"] = sessionId,
                ["timestamp"] = DateTimeOffset.UtcNow.AddDays(-i).ToString("o"),
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"demo prompt {i} in {Path.GetFileName(cwd)}",
                },
            }.ToJsonString());
        }
        File.WriteAllLines(Path.Combine(dir, $"{sessionId}.jsonl"), lines);
    }

    private void LoadSettingsUi()
    {
        _settingsLoading = true;
        try
        {
            var s = _engine.Call("get_settings");
            var auto = s["autoswitch"];
            if (auto is not null)
            {
                _autoEnabled.Checked = auto["enabled"]?.GetValue<bool>() ?? false;
                if (auto["threshold"] is JsonValue t && t.TryGetValue<double>(out var th))
                    _threshold.Value = (decimal)Math.Clamp(th, 50, 99);
            }
            var warmup = s["warmup"];
            if (warmup is not null)
            {
                _warmupEnabled.Checked = warmup["enabled"]?.GetValue<bool>() ?? false;
                _workStartHour.Value = Math.Min(23, ParseHour(warmup["workStart"]?.GetValue<string>(), 9));
                _workEndHour.Value = Math.Clamp(ParseHour(warmup["workEnd"]?.GetValue<string>(), 18), 1, 23);
                if (_workEndHour.Value <= _workStartHour.Value)
                    _workEndHour.Value = Math.Min(23, _workStartHour.Value + 1);
            }
            ApplyThresholdMarker();
            _hideEmail.Checked = UiPrefs.HideEmail;
            _warmupTask.Checked = WarmupTaskHelper.IsEnabled();
            SyncAutoSwitchUiEnabled();
            SyncWarmupUiEnabled();
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("settings.readFailed", ex.Message);
        }
        finally
        {
            _settingsLoading = false;
        }
    }

    /// <summary>Read the hour from a core <c>HH:MM</c> string; fall back on bad input.</summary>
    private static decimal ParseHour(string? hhmm, int fallback)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return fallback;
        var part = hhmm.Split(':')[0];
        if (!int.TryParse(part, out int h)) return fallback;
        return Math.Clamp(h, 0, 24);
    }

    private void SaveSettings(bool auto = false)
    {
        try
        {
            _engine.Call("set_autoswitch", new
            {
                threshold = (double)_threshold.Value,
                intervalSeconds = 60.0,
                cooldownSeconds = 300.0,
                hysteresisPct = 10.0,
                strategy = "best",
                includeApiKeyAccounts = false,
                unhealthyTicks = 3,
                enabled = _autoEnabled.Checked,
            });
            int startH = (int)_workStartHour.Value;
            int endH = (int)_workEndHour.Value;
            if (endH <= startH) endH = Math.Min(23, startH + 1);
            _engine.Call("set_warmup", new
            {
                enabled = _warmupEnabled.Checked,
                workStart = $"{startH:D2}:00",
                workEnd = $"{endH:D2}:00",
            });
            // Keep the daily task in sync: create/update when both toggles are on,
            // otherwise delete so unchecking never leaves an orphan schtask.
            SyncWarmupScheduledTask();
            if (!_warmupTask.Checked || !_warmupEnabled.Checked)
            {
                // Checkbox can lag if guardian was turned off above; force UI.
                if (_warmupTask.Checked && !_warmupEnabled.Checked)
                {
                    _settingsLoading = true;
                    _warmupTask.Checked = false;
                    _settingsLoading = false;
                }
            }
            ApplyThresholdMarker();
            SyncWarmupUiEnabled();
            if (DetailOpen && _selected is not null)
                _drawer.Bind(_selected.Model);
            string msg = SettingsSavedMessage();
            if (auto)
                FlashSettingsSaved(msg);
            else
            {
                _baseStatus = msg;
                ComposeStatusLine();
            }
            // After enabling autoswitch or warmup, pull usage (+ guardian) once.
            if (_autoEnabled.Checked || _warmupEnabled.Checked)
                RunEnginePoll(force: true);
            else
                ComposeStatusLine();
            WriteStatusFile();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("settings.saveFailed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string SettingsSavedMessage()
    {
        string auto = _autoEnabled.Checked
            ? Loc.T("settings.saved.on", _threshold.Value)
            : Loc.T("settings.saved.off");
        string warm = _warmupEnabled.Checked
            ? Loc.T("settings.warmup.saved.on", (int)_workStartHour.Value, (int)_workEndHour.Value)
            : Loc.T("settings.warmup.saved.off");
        return $"{auto} · {warm}";
    }

    /// <summary>
    /// 1s UI tick: refresh countdown text; when due, run engine poll cycle.
    /// </summary>
    private void OnPollUiTick()
    {
        if (IsDisposed) return;
        try
        {
            if (DateTime.UtcNow >= _nextPollUtc)
                RunEnginePoll(force: false);
            else
                ComposeStatusLine();
            WriteStatusFile();
        }
        catch (Exception ex)
        {
            _baseStatus = Loc.T("status.refreshFailed", ex.Message);
            ComposeStatusLine();
        }
    }

    /// <summary>
    /// Announce an autoswitch the moment it happens.
    /// </summary>
    /// <remarks>
    /// This is the product's one moment of proof: the limit was hit, the account
    /// moved, and the user never stopped typing. Done silently, the work is
    /// indistinguishable from nothing happening — the notification is what turns
    /// an invisible feature into a visible one.
    /// </remarks>
    private void NotifyIfSwitched(JsonNode? result, AccountCardModel? wasActive)
    {
        if (!SwitchNotice.DidSwitch(result)) return;

        var to = _models.FirstOrDefault(m => m.Number == SwitchNotice.TargetNumber(result));
        string target = to is null
            ? result?["to"]?["email"]?.GetValue<string>() ?? Loc.T("switch.otherAccount")
            : Pii.MaskAccountLabel(to.Alias, to.Email);

        double? fromPct = wasActive is null || (wasActive.FiveHour is null && wasActive.SevenDay is null)
            ? null
            : Math.Max(wasActive.FiveHour ?? 0, wasActive.SevenDay ?? 0);

        _tray.ShowBalloonTip(
            6000,
            SwitchNotice.Title,
            SwitchNotice.Body(
                target,
                wasActive is null ? null : Pii.MaskAccountLabel(wasActive.Alias, wasActive.Email),
                fromPct),
            ToolTipIcon.Info);

        _baseStatus = Loc.T("status.switchedTo", target);
        ComposeStatusLine();
    }

    /// <summary>
    /// Real engine path used by the timer (and save-settings kick).
    /// </summary>
    private void RunEnginePoll(bool force)
    {
        try
        {
            var result = _poll.RunTick(_engine, _autoEnabled.Checked);
            // Read the pre-switch usage before the snapshot overwrites it: the
            // notification's whole point is saying *why* it moved.
            var wasActive = _models.FirstOrDefault(m => m.Active);
            ApplySnapshotFromEngine(refreshAlreadyDone: true);
            ArmNextPoll(_poll.LastNextPollSeconds, forceShort: force);
            _baseStatus = _autoEnabled.Checked
                ? Loc.T("status.polled.auto", _poll.TickCount)
                : Loc.T("status.polled", _poll.TickCount);
            ComposeStatusLine();
            NotifyIfSwitched(result, wasActive);
            // Guardian fires live inside refresh_usage / autoswitch_tick; surface
            // them the same way as a manual Warm now so a closed main window still
            // explains the morning open.
            NotifyIfWarmed(result);
        }
        catch (Exception ex)
        {
            _baseStatus = Loc.T("status.pollFailed", ex.Message);
            ArmNextPoll(30, forceShort: false);
            ComposeStatusLine();
        }
    }

    private void ArmNextPoll(double plannedSeconds, bool forceShort)
    {
        _nextPollSeconds = plannedSeconds > 0 ? plannedSeconds : 60;
        int forceMs = 0;
        var env = Environment.GetEnvironmentVariable("CLAUDE_SWITCH_POLL_FORCE_MS");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var parsed) && parsed > 0)
            forceMs = parsed;
        if (forceShort && forceMs <= 0)
            forceMs = 800; // kick after save/settings without waiting full adaptive band
        if (forceMs > 0)
            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(forceMs);
        else
            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(
                PollCoordinator.IntervalMsFromSeconds(_nextPollSeconds));
    }

    /// <summary>
    /// Fills the status band: what is going on, and when the next refresh lands.
    /// </summary>
    /// <remarks>
    /// The countdown is written to its own right-hand cell rather than appended
    /// to the sentence. It is the one part that changes every second, and in a
    /// single label it dragged the whole line sideways as the message beside it
    /// grew or shrank.
    /// </remarks>
    private void ComposeStatusLine()
    {
        int remain = (int)Math.Max(0, Math.Ceiling((_nextPollUtc - DateTime.UtcNow).TotalSeconds));
        _statusRight.Text = Loc.T("status.nextRefresh", FormatClock(remain));

        string q = _search.Text.Trim();
        var filtered = AccountListFilter.Filter(_models, q);
        var filterMsg = AccountListFilter.FormatFilterMessage(filtered.Count, _models.Count, q);
        if (filterMsg is not null)
        {
            _status.Text = filterMsg;
            return;
        }

        if (!string.IsNullOrEmpty(_baseStatus) && remain > 0 && _poll.TickCount == 0)
        {
            // Keep one-shot messages visible until first poll completes.
            _status.Text = _baseStatus;
            return;
        }

        var active = _models.FirstOrDefault(m => m.Active);
        var masked = ActiveLabel();
        string auto = _autoEnabled.Checked
            ? Loc.T("status.autoswitch.on", _threshold.Value)
            : Loc.T("status.autoswitch.off");
        int total = Math.Max(1, _models.Count);
        int pos = active is null ? 0 : Math.Max(1, _models.FindIndex(m => m.Active) + 1);
        string pollPos = active is null
            ? Loc.T("status.poll.count", _poll.TickCount)
            : Loc.T("status.poll.position", pos, total);
        // Separators for scanability. No slot number when no slot is live.
        string who = active is null ? masked : $"#{active.Number} {masked}";
        _status.Text = $"{Loc.T("status.active", who)}   ·   {auto}   ·   {pollPos}";
    }

    /// <summary>Seconds as mm:ss — a clock that never changes width as it ticks.</summary>
    private static string FormatClock(int seconds)
    {
        if (seconds < 0) seconds = 0;
        if (seconds >= 3600) return Theme.FormatDuration(seconds);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private void ClearSearch()
    {
        // Assigning the same value raises no TextChanged, so rebuild explicitly
        // rather than relying on the event to have fired.
        _search.Text = "";
        RebuildCards();
    }

    private void Reload()
    {
        try
        {
            try { _engine.Call("refresh_usage"); } catch { /* offline */ }
            ApplySnapshotFromEngine(refreshAlreadyDone: true);
            ArmNextPoll(_nextPollSeconds, forceShort: false);
            ComposeStatusLine();
            WriteStatusFile();
        }
        catch (Exception ex)
        {
            _status.Text = Loc.T("status.loadFailed", ex.Message);
        }
        finally
        {
            UpdateSwitchEnabled();
        }
    }

    private void ApplySnapshotFromEngine(bool refreshAlreadyDone)
    {
        _ = refreshAlreadyDone;
        // Write the verified active slot back first, so sequence.json stays
        // truthful for the cswap CLI even when the login moved behind our back.
        try { _engine.Call("reconcile_active"); } catch { /* read-only view still works */ }
        var snap = _engine.Snapshot();
        _models = [];
        string activeEmail = Loc.T("status.none");
        var accounts = snap["accounts"]?.AsArray();
        if (accounts is not null)
        {
            foreach (var a in accounts)
            {
                if (a is null) continue;
                int number = a["number"]?.GetValue<int>() ?? 0;
                string email = a["email"]?.GetValue<string>() ?? "";
                string? alias = a["alias"]?.GetValue<string>();
                bool active = a["active"]?.GetValue<bool>() ?? false;
                bool disabled = a["disabled"]?.GetValue<bool>() ?? false;
                var u = a["usage"];
                double? five = u?["fiveHour"]?["pct"]?.GetValue<double>();
                double? seven = u?["sevenDay"]?["pct"]?.GetValue<double>();
                string? fiveReset = u?["fiveHour"]?["resetsAt"]?.GetValue<string>();
                string? sevenReset = u?["sevenDay"]?["resetsAt"]?.GetValue<string>();
                // `plan` is snapshot v2 and is omitted entirely for slots whose
                // backups predate the fields — every read below stays optional.
                var p = a["plan"];
                if (active)
                    activeEmail = string.IsNullOrEmpty(alias) ? email : $"{alias} · {email}";
                _models.Add(new AccountCardModel
                {
                    Number = number,
                    Email = email,
                    Alias = alias,
                    Active = active,
                    Disabled = disabled,
                    LiveSessions = a["liveSessions"]?.GetValue<int>() ?? 0,
                    FiveHour = five,
                    SevenDay = seven,
                    FiveHourResetsAt = fiveReset,
                    SevenDayResetsAt = sevenReset,
                    WarmupAnchor = a["warmupAnchor"]?.GetValue<string>(),
                    UsageStatus = a["usageStatus"]?.GetValue<string>(),
                    PlanLabel = p?["label"]?.GetValue<string>(),
                    PlanTier = p?["tier"]?.GetValue<string>(),
                    PlanPersonal = p?["personal"]?.GetValue<bool>() ?? false,
                    SubscriptionCreatedAt = p?["subscriptionCreatedAt"]?.GetValue<string>(),
                    AccountCreatedAt = p?["accountCreatedAt"]?.GetValue<string>(),
                    BillingType = p?["billingType"]?.GetValue<string>(),
                    RateLimitTier = p?["rateLimitTier"]?.GetValue<string>(),
                    OrganizationName = p?["organizationName"]?.GetValue<string>(),
                    OrganizationRole = p?["organizationRole"]?.GetValue<string>(),
                    SeatTier = p?["seatTier"]?.GetValue<string>(),
                    TrialEndsAt = p?["trialEndsAt"]?.GetValue<string>(),
                    ExtraUsageEnabled = p?["extraUsageEnabled"]?.GetValue<bool>() ?? false,
                    ProfileFetchedAt = p?["profileFetchedAt"]?.GetValue<long>(),
                });
            }
        }

        if (snap["nextPollSeconds"] is JsonValue np && np.TryGetValue<double>(out var nps))
            _nextPollSeconds = nps;

        // Claude Code is logged in as somebody no slot holds (a bare `/login`,
        // or an account never added here). No row is current, and saying "—"
        // alone would leave the user guessing why.
        _unmanagedLogin = snap["unmanagedLoginEmail"]?.GetValue<string>();

        RebuildCards();
        ApplyHeaderTexts();
    }

    /// <summary>Header chips and tray title, from the current models.</summary>
    private void ApplyHeaderTexts()
    {
        var maskedActive = ActiveLabel();
        // The chip keeps meaning "the default login". Session terminals are
        // counted beside it rather than folded in: with sessions open there is
        // no single "current account" any more, and quietly redefining the chip
        // would make the header lie in exactly the case it matters most.
        int sessions = _models.Sum(m => m.LiveSessions);
        _activeChip.Text = sessions > 0
            ? Loc.T("header.activeWithSessions", maskedActive, Loc.Plural("header.sessions", sessions))
            : Loc.T("header.active", maskedActive);
        _activeDot.ForeColor = _models.Any(a => a.Active) ? Theme.Primary : Theme.TextMuted;
        _countLabel.Text = CountText(_models.Count);
        var trayLabel = maskedActive.Length > 40
            ? maskedActive[..37] + "…"
            : maskedActive;
        _tray.Text = Loc.T("tray.title", trayLabel);
    }

    /// <summary>
    /// Masked label for the live login: a managed slot, an unmanaged account, or
    /// nobody. Never falls back to a stale slot — that was the bug.
    /// </summary>
    private string ActiveLabel()
    {
        var active = _models.FirstOrDefault(m => m.Active);
        if (active is not null)
            return Pii.MaskAccountLabel(active.Alias, active.Email);
        if (!string.IsNullOrWhiteSpace(_unmanagedLogin))
            return Loc.T("status.unmanaged", Pii.MaskEmail(_unmanagedLogin!));
        return _models.Count == 0 ? Loc.T("status.none") : Loc.T("status.loggedOut");
    }

    private void RebuildCards()
    {
        // DoDragDrop pumps messages, so a poll tick can land mid-drag. Rebuilding
        // there would dispose the control the OS is dragging — the drag dies and
        // the list jumps. Defer until the drag ends.
        if (AccountCard.DragActive)
        {
            _rebuildDeferred = true;
            return;
        }

        string q = _search.Text.Trim();
        var filtered = AccountListFilter.Filter(_models, q);

        int? keepSelected = _selected?.Model.Number;
        bool detailWasOpen = DetailOpen;
        _cardHost.SuspendLayout();
        _cardHost.Controls.Clear();
        _selected = null;

        int width = Math.Max(280, _cardHost.ClientSize.Width > 20
            ? _cardHost.ClientSize.Width - 4
            : (_listOuter.ClientSize.Width > 40 ? _listOuter.ClientSize.Width - 40 : 800));
        foreach (var model in filtered)
        {
            var card = new AccountCard { Width = width };
            card.Bind(model);
            card.Click += (_, _) => SelectCard(card);
            card.CardActivated += (_, _) =>
            {
                SelectCard(card);
                DoSwitch();
            };
            card.DragReorderRequested += (_, drop) => OnCardReorder(drop);
            card.DragSessionEnded += (_, _) =>
            {
                if (!_rebuildDeferred) return;
                _rebuildDeferred = false;
                RebuildCards();
            };
            card.MoreMenuRequested += (_, _) =>
            {
                SelectCard(card);
                ShowAccountMenu(card);
            };
            card.FixRequested += (_, _) =>
            {
                SelectCard(card);
                DoFixCredential(card.Model);
            };
            if (keepSelected == model.Number || (keepSelected is null && model.Active))
            {
                card.Selected = true;
                _selected = card;
            }
            _cardHost.Controls.Add(card);
        }
        _cardHost.ResumeLayout(true);
        LayoutCardHost();
        // The list's scrollbars belong to windows that were just recreated, so
        // the dark variant has to be asked for again.
        NativeScrollbars.Apply(_listOuter);

        bool noData = _models.Count == 0;
        bool filterEmpty = !noData && filtered.Count == 0 && q.Length > 0;
        _emptyState.Visible = noData || filterEmpty;
        _cardHost.Visible = !noData && !filterEmpty;
        if (filterEmpty)
        {
            foreach (Control c in _emptyState.Controls)
            {
                if (c is Label lbl)
                    lbl.Text = Loc.T("empty.noMatch", q, _models.Count);
            }
        }
        else if (noData)
        {
            foreach (Control c in _emptyState.Controls)
            {
                if (c is Label lbl)
                    lbl.Text = Loc.T("empty.noAccounts");
            }
        }

        // Match chrome
        var filterMsg = AccountListFilter.FormatFilterMessage(filtered.Count, _models.Count, q);
        if (_searchClear is not null) _searchClear.Visible = q.Length > 0;
        _searchMatchLabel.Text = q.Length == 0
            ? ""
            : filtered.Count == 0
                ? Loc.T("filter.zero")
                : $"{filtered.Count}/{_models.Count}";
        if (filterMsg is not null)
            _status.Text = filterMsg;
        else
            ComposeStatusLine();

        if (_selected is not null && detailWasOpen)
            _drawer.Bind(_selected.Model);
        else if (_selected is null && detailWasOpen)
            CloseDetailWindow();

        UpdateSwitchEnabled();
    }

    private void SelectCard(AccountCard card)
    {
        if (_selected is not null && !ReferenceEquals(_selected, card))
            _selected.Selected = false;
        _selected = card;
        card.Selected = true;
        UpdateSwitchEnabled();
        if (DetailOpen)
            _drawer.Bind(card.Model);
    }

    private void WriteStatusFile()
    {
        var statusPath = Environment.GetEnvironmentVariable("CLAUDE_SWITCH_STATUS_FILE");
        if (string.IsNullOrWhiteSpace(statusPath)) return;
        var rows = new System.Text.StringBuilder();
        rows.AppendLine(_status.Text);
        rows.AppendLine($"poll_ticks={_poll.TickCount}");
        rows.AppendLine($"next_poll_seconds={_nextPollSeconds:0.###}");
        rows.AppendLine($"remain_s={Math.Max(0, (_nextPollUtc - DateTime.UtcNow).TotalSeconds):0.###}");
        rows.AppendLine($"autoswitch={_autoEnabled.Checked}");
        rows.AppendLine($"last_used_autoswitch_tick={_poll.LastUsedAutoswitchTick}");
        string q = _search.Text.Trim();
        var filtered = AccountListFilter.Filter(_models, q);
        rows.AppendLine($"filter_q={q}");
        rows.AppendLine($"filter_match={filtered.Count}");
        rows.AppendLine($"filter_total={_models.Count}");
        foreach (var m in _models)
        {
            rows.AppendLine(string.Join("|",
                m.Number,
                m.Email,
                m.Alias ?? "",
                m.Active ? "yes" : "",
                Theme.UsageLabel(m.FiveHour),
                Theme.UsageLabel(m.SevenDay),
                m.Disabled ? "yes" : ""));
        }
        File.WriteAllText(statusPath, rows.ToString());
    }

    private void ShowAccountMenu(AccountCard card)
    {
        if (card.IsDisposed) return;
        var m = card.Model;
        var menu = new ContextMenuStrip
        {
            Font = Theme.FontBody,
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(),
        };

        // Defer actions so the menu can close cleanly first (avoids disposed-item races).
        void Run(Action action)
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (IsDisposed || card.IsDisposed) return;
                    SelectCard(card);
                    action();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        ex.Message,
                        Loc.T("disable.failed.title"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            });
        }

        var switchItem = new ToolStripMenuItem(
            m.Active ? Loc.T("toolbar.switch.current") : Loc.T("toolbar.switch.go"))
        {
            Enabled = !m.Active && !_switching,
        };
        switchItem.Click += (_, _) => Run(DoSwitch);

        // Opening a terminal is a peer of switching, not a variant of it: one
        // moves the default login, the other leaves it alone. It sits in the
        // menu rather than on the card's main button so the primary action
        // stays unambiguous.
        var sessionItem = new ToolStripMenuItem(Loc.T("menu.openTerminal"))
        {
            Enabled = !m.Disabled,
            ToolTipText = Loc.T("menu.openTerminal.tip"),
        };
        sessionItem.Click += (_, _) => Run(() => DoOpenSessionTerminal(m));

        // Supervised run: same account, same working directory, but the agent
        // stays a child of this app so an interruption can be resumed instead of
        // stalling a terminal nobody is watching.
        var agentItem = new ToolStripMenuItem(Loc.T("menu.openAgent"))
        {
            Enabled = !m.Disabled,
            ToolTipText = Loc.T("menu.openAgent.tip"),
        };
        agentItem.Click += (_, _) => Run(() => DoOpenAgentWindow(m));

        var warmItem = new ToolStripMenuItem(Loc.T("menu.warmup"))
        {
            // Only subscribed + healthy (usageStatus ok) participate in N / fires.
            Enabled = !m.Disabled && m.UsageStatus is "ok",
            ToolTipText = Loc.T("menu.warmup.tip"),
        };
        warmItem.Click += (_, _) => Run(() => DoWarmupNow(m.Number));

        var aliasItem = new ToolStripMenuItem(Loc.T("menu.alias"));
        aliasItem.Click += (_, _) => Run(DoEditAlias);

        var detailItem = new ToolStripMenuItem(Loc.T("menu.detail"));
        detailItem.Click += (_, _) => Run(OpenDetailSafe);

        var disableItem = new ToolStripMenuItem(m.Disabled ? Loc.T("menu.enable") : Loc.T("menu.disableOne"));
        disableItem.Click += (_, _) => Run(DoToggleDisable);

        var deleteItem = new ToolStripMenuItem(Loc.T("menu.delete"))
        {
            ForeColor = Theme.Danger,
        };
        deleteItem.Click += (_, _) => Run(DoDelete);

        menu.Items.Add(switchItem);
        menu.Items.Add(sessionItem);
        menu.Items.Add(agentItem);
        menu.Items.Add(warmItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(aliasItem);
        menu.Items.Add(detailItem);
        menu.Items.Add(disableItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(deleteItem);

        var pt = card.PointToScreen(new Point(Math.Max(0, card.Width - 8), card.Height / 2));
        menu.Show(pt);
        // Dispose after close without re-entering the click stack.
        menu.Closed += (_, _) => BeginInvoke(() =>
        {
            try { menu.Dispose(); } catch { /* ignore */ }
        });
    }

    private void OpenDetailSafe()
    {
        try
        {
            ShowDetailForSelection();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.T("detail.openFailed", ex.Message),
                Loc.T("menu.detail"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void DoSwitch()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, Loc.T("action.selectFirst.switch"), Loc.T("switch.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_selected.Model.Active)
            return;

        var to = _selected.Model;
        var from = _models.FirstOrDefault(m => m.Active);
        if (!SwitchConfirmDialog.Confirm(this, from, to))
            return;

        int num = to.Number;
        try
        {
            _switching = true;
            UpdateSwitchEnabled();
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
            _engine.SwitchTo(num.ToString());
            var masked = Pii.MaskAccountLabel(to.Alias, to.Email);
            _status.Text = Loc.T("switch.done", num, masked);
            Reload();
            _tray.ShowBalloonTip(
                2000,
                Loc.T("switch.ok.title"),
                Loc.T("switch.current", num, masked),
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("switch.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _switching = false;
            Cursor = Cursors.Default;
            UpdateSwitchEnabled();
        }
    }

    /// <summary>
    /// The card's amber "sign in again" button.
    /// </summary>
    /// <remarks>
    /// There is no way to re-authenticate a slot from here: the credential lives
    /// in Claude Code, so the only real fix is to sign in there and overwrite
    /// this slot from the live login. The dialog says exactly that and then
    /// offers the overwrite, rather than leaving the user to work out that
    /// "Add account" is what repairs an existing one.
    /// </remarks>
    private void DoFixCredential(AccountCardModel m)
    {
        string who = Pii.MaskAccountLabel(m.Alias, m.Email);
        var answer = MessageBox.Show(
            this,
            Loc.T("card.relogin.body", who, Theme.UsageStatusLong(m.UsageStatus)),
            Loc.T("card.relogin"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (answer == DialogResult.OK) DoAdd();
    }

    private void DoAdd()
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            _engine.Call("add_current", new { });
            _status.Text = Loc.T("add.done");
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                Loc.T("add.failed", ex.Message + Loc.T("add.failed.hint")),
                Loc.T("toolbar.add"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void DoToggleDisable()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, Loc.T("action.selectFirst.alias"), Loc.T("menu.disable"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var m = _selected.Model;
        try
        {
            _engine.Call("set_disabled", new { id = m.Number.ToString(), disabled = !m.Disabled });
            _status.Text = m.Disabled ? Loc.T("enable.done", m.Number) : Loc.T("disable.done", m.Number);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("disable.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DoEditAlias()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, Loc.T("action.selectFirst.alias"), Loc.T("menu.alias"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var m = _selected.Model;
        if (!AliasEditDialog.TryEdit(this, m, out var alias))
            return;
        try
        {
            // null or empty clears alias — send empty string for clear.
            _engine.Call("set_alias", new { id = m.Number.ToString(), alias = alias ?? "" });
            _status.Text = string.IsNullOrEmpty(alias)
                ? Loc.T("alias.cleared", m.Number)
                : Loc.T("alias.set", alias);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("alias.saveFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DoDelete()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, Loc.T("action.selectFirst.delete"), Loc.T("menu.delete"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var m = _selected.Model;
        if (!DeleteConfirmDialog.Confirm(this, m))
            return;
        try
        {
            Cursor = Cursors.WaitCursor;
            _engine.Call("remove_account", new { id = m.Number.ToString() });
            CloseDetailWindow();
            _selected = null;
            var masked = Pii.MaskAccountLabel(m.Alias, m.Email);
            _status.Text = Loc.T("delete.done", m.Number, masked);
            _tray.ShowBalloonTip(1800, Loc.T("delete.title"), Loc.T("delete.removed", m.Number, masked), ToolTipIcon.Info);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("delete.failed.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}

/// <summary>
/// AutoScroll panel that clips children so tall card stacks cannot paint over
/// sibling chrome (status bar). Default Panel does not set WS_CLIPCHILDREN.
/// </summary>
internal sealed class ClipScrollPanel : Panel
{
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    public ClipScrollPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WsClipChildren | WsClipSiblings;
            return cp;
        }
    }
}

/// <summary>
/// FlowLayout list host with clipping so partially-offscreen cards never paint
/// their text over the status bar (reads as “文字显示不完整”).
/// </summary>
internal sealed class ClipFlowPanel : FlowLayoutPanel
{
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    public ClipFlowPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WsClipChildren | WsClipSiblings;
            return cp;
        }
    }
}
