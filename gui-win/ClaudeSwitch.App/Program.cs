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
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--fixture" or "-f" && i + 1 < args.Length)
                fixture = args[++i];
            if (args[i] is "--onboarding")
                forceOnboarding = true;
            if (args[i] is "--skip-onboarding")
                skipOnboarding = true;
        }

        // Layout probe mode always skips onboarding dialog.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LAYOUT_DIR")))
            skipOnboarding = true;

        using var mutex = new Mutex(true, @"Local\ClaudeSwitch.Gui", out bool created);
        if (!created)
        {
            MessageBox.Show(
                "Claude Switch 已在运行。\n请从系统托盘打开主窗口。",
                "Claude Switch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
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
            MessageBox.Show(ex.ToString(), "Claude Switch 启动失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
    private readonly FlowLayoutPanel _cardHost;
    private readonly Panel _emptyState;
    private readonly Panel _listOuter;
    private readonly ActivityStrip _activityStrip;
    private readonly ToolStripDropDownButton _btnResume;
    private readonly Panel _header;
    private readonly Panel _actionBar;
    private readonly Panel _settingsStrip;
    private readonly Label _status;
    private readonly Label _activeChip;
    private readonly Label _countLabel;
    private readonly Label _brand;
    private readonly Label _subtitle;
    private readonly NotifyIcon _tray;
    private readonly CheckBox _autoEnabled;
    private readonly CheckBox _startupEnabled;
    private readonly NumericUpDown _threshold;
    private readonly Label _thrLabel;
    private readonly Label _thrUnit;
    private readonly ToolStripButton _btnSwitch;
    private readonly ToolStripButton _btnTheme;
    private readonly ToolStripTextBox _search;
    private readonly ToolStripButton _btnSearchClear;
    private readonly ToolStripLabel _searchMatchLabel;
    private readonly ToolStrip _toolStrip;
    private readonly UsageDrawer _drawer;
    private Form? _detailForm;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly PollCoordinator _poll = new();
    private readonly Label _autoHint;
    private readonly CheckBox _hideEmail;
    private readonly Label _settingsSaved;
    private AccountCard? _selected;
    private List<AccountCardModel> _models = [];
    /// <summary>Watermark shown while the search box is empty (never in Text).</summary>
    private const string SearchHint = "搜索邮箱或别名…";
    private bool _switching;
    private bool _settingsLoading;
    private double _nextPollSeconds = 60;
    private DateTime _nextPollUtc = DateTime.UtcNow.AddSeconds(60);
    private string _baseStatus = "就绪";
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
        Width = 1200;
        Height = 800;
        MinimumSize = new Size(1000, 640);
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

        // ═══════════════════════════════════════════════════════════
        // Root chrome: ONE TableLayout (5 rows). No stacked Dock.Top —
        // eliminates band bleed/overlap under DPI.
        // ═══════════════════════════════════════════════════════════
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        // Bands sized for measured control height (30) + padding — no text clip.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));  // header
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));  // toolbar
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));  // settings (taller so checkbox glyph not clipped)
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // content (list)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));  // status

        // ── Row 0: Header — brand | spring | count + current chip ──
        _header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space4, 0, Theme.Space3, 0), Margin = new Padding(0) };
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

        _brand = new Label
        {
            Text = "Claude Switch",
            Font = Theme.FontBrand,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(Theme.Space1, 14, Theme.Space2, 0),
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
            Font = Theme.FontBody,
            Text = "0 个账号",
            Margin = new Padding(0, 16, Theme.Space3, 0),
            TextAlign = ContentAlignment.MiddleRight,
        };
        _activeChip = new Label
        {
            AutoSize = true,
            Font = Theme.FontBody,
            Padding = new Padding(Theme.Space3, 5, Theme.Space3, 5),
            Margin = new Padding(0, 10, 0, 0),
            Text = "当前账号：—",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        headerGrid.Controls.Add(_brand, 0, 0);
        headerGrid.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 1, 0);
        var metaCol = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        metaCol.Controls.Add(_countLabel);
        metaCol.Controls.Add(_activeChip);
        headerGrid.Controls.Add(metaCol, 2, 0);
        _header.Controls.Add(headerGrid);

        // ── Row 1: Toolbar — primary switch | global ops | theme | search ──
        // Per-account ops (alias / disable / detail / delete) live on each card's ⋮ menu.
        _actionBar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2), Margin = new Padding(0) };
        _toolStrip = new ToolStrip
        {
            Dock = DockStyle.Fill,
            GripStyle = ToolStripGripStyle.Hidden,
            CanOverflow = true,
            Stretch = true,
            Font = Theme.FontBody,
            Padding = new Padding(2),
            AutoSize = false,
            Height = Theme.ControlHeight + 4,
        };
        _btnSwitch = MakeStripBtn("请选择账号", "primary");
        _btnSwitch.ToolTipText = "将选中的账号设为 Claude Code 当前登录";
        var btnRefresh = MakeStripBtn("刷新全部", "secondary");
        btnRefresh.ToolTipText = "刷新全部账号的用量与列表";
        var btnAdd = MakeStripBtn("添加账号", "secondary");
        btnAdd.ToolTipText = "托管本机当前 Claude Code 登录（全局）";
        _btnResume = new ToolStripDropDownButton("继续会话")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, Theme.Space1, 0),
            Padding = new Padding(Theme.Space3, 5, Theme.Space3, 5),
            Font = Theme.FontBody,
            Tag = "secondary",
            Overflow = ToolStripItemOverflow.Never,
            ToolTipText = "回到最近的会话（在你自己的终端里打开）",
        };
        // Built on open, not on a timer: the list is only interesting at the
        // moment it is read, and building it costs a directory listing.
        _btnResume.DropDownOpening += (_, _) => FillResumeMenu(_btnResume.DropDownItems);

        var btnProjects = MakeStripBtn("目录", "secondary");
        btnProjects.ToolTipText = "Claude Code 用过的目录与会话（与账号无关）";
        var btnOverview = MakeStripBtn("用量总览", "secondary");
        btnOverview.ToolTipText = "统计本机所有目录的会话与 token 用量（按需扫描）";
        _btnTheme = MakeStripBtn(ThemeToggleText(), "secondary");
        _btnTheme.ToolTipText = "切换浅色 / 深色外观";
        _search = new ToolStripTextBox("search")
        {
            AutoSize = false,
            // Wider so placeholder and queries are readable without clipping.
            Width = 280,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.FontBody,
            // Hint is a native watermark, never Text — see SearchBox.
            Text = "",
            Alignment = ToolStripItemAlignment.Right,
            Overflow = ToolStripItemOverflow.Never,
            ToolTipText = "按邮箱或别名过滤列表",
        };
        _btnSearchClear = new ToolStripButton("清除")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Alignment = ToolStripItemAlignment.Right,
            Overflow = ToolStripItemOverflow.Never,
            Font = Theme.FontBody,
            Tag = "secondary",
            ToolTipText = "清空搜索并显示全部账号",
            Enabled = false,
            Margin = new Padding(0, 0, Theme.Space1, 0),
            Padding = new Padding(Theme.Space2, 5, Theme.Space2, 5),
        };
        _searchMatchLabel = new ToolStripLabel("")
        {
            Alignment = ToolStripItemAlignment.Right,
            Overflow = ToolStripItemOverflow.Never,
            Font = Theme.FontCaption,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, Theme.Space2, 0),
        };
        foreach (var item in new ToolStripItem[]
                 { _btnSwitch, btnRefresh, btnAdd, _btnResume, btnProjects, btnOverview, _btnTheme,
                   _search, _btnSearchClear, _searchMatchLabel })
            item.Overflow = ToolStripItemOverflow.Never;

        _btnSwitch.Click += (_, _) => DoSwitch();
        btnRefresh.Click += (_, _) => Reload();
        btnAdd.Click += (_, _) => DoAdd();
        btnProjects.Click += (_, _) => ShowProjectsWindow();
        btnOverview.Click += (_, _) => ShowOverviewWindow();
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
        _btnSearchClear.Click += (_, _) => ClearSearch();

        // Right cluster first, then left: primary switch + global actions only.
        _toolStrip.Items.Add(_searchMatchLabel);
        _toolStrip.Items.Add(_btnSearchClear);
        _toolStrip.Items.Add(_search);
        _toolStrip.Items.AddRange([
            _btnSwitch,
            new ToolStripSeparator(),
            btnRefresh, btnAdd,
            new ToolStripSeparator(),
            _btnResume, btnProjects, btnOverview,
            new ToolStripSeparator(),
            _btnTheme,
        ]);
        _actionBar.Controls.Add(_toolStrip);

        // ── Row 2: Auto-switch settings (natural sentence + auto-save) ──
        // Taller band + padding so WinForms checkbox glyphs are not clipped.
        _settingsStrip = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Theme.Space4, 10, Theme.Space4, 10),
            Margin = new Padding(0),
        };
        var settingsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 2, 0, 2),
        };

        _autoEnabled = new CheckBox
        {
            Text = "自动切换：",
            AutoSize = true,
            Margin = new Padding(0, 4, Theme.Space1, 4),
            Padding = new Padding(0, 2, 0, 2),
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
            UseVisualStyleBackColor = true,
        };
        _thrLabel = new Label
        {
            Text = "5小时或7天用量任一达到",
            AutoSize = true,
            Margin = new Padding(0, 8, Theme.Space1, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
        };
        _threshold = new NumericUpDown
        {
            Minimum = 50,
            Maximum = 99,
            Value = 90,
            Width = 52,
            Height = Theme.ControlHeight - 2,
            Margin = new Padding(0, 6, Theme.Space1, 0),
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.FontBody,
            TextAlign = HorizontalAlignment.Center,
        };
        _thrUnit = new Label
        {
            Text = "% 时切换到余量最多的可用账号",
            AutoSize = true,
            Margin = new Padding(0, 8, Theme.Space3, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
        };
        _autoHint = new Label
        {
            Text = "",
            AutoSize = true,
            Visible = false,
            Margin = new Padding(0),
        };
        _settingsSaved = new Label
        {
            Text = "",
            AutoSize = true,
            Margin = new Padding(Theme.Space2, 8, Theme.Space3, 0),
            Font = Theme.FontSmall,
            ForeColor = SystemColors.GrayText,
        };
        _startupEnabled = new CheckBox
        {
            Text = "开机自启",
            AutoSize = true,
            Margin = new Padding(0, 4, Theme.Space3, 4),
            Padding = new Padding(0, 2, 0, 2),
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
            UseVisualStyleBackColor = true,
            Checked = StartupHelper.IsEnabled(),
        };
        _startupEnabled.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupHelper.SetEnabled(_startupEnabled.Checked);
                FlashSettingsSaved(_startupEnabled.Checked
                    ? "已开启开机自启"
                    : "已关闭开机自启");
            }
            catch (Exception ex)
            {
                _startupEnabled.Checked = StartupHelper.IsEnabled();
                MessageBox.Show(this, ex.Message, "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _hideEmail = new CheckBox
        {
            Text = "隐藏邮箱",
            AutoSize = true,
            Margin = new Padding(0, 4, Theme.Space2, 4),
            Padding = new Padding(0, 2, 0, 2),
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
            Font = Theme.FontBody,
            UseVisualStyleBackColor = true,
            Checked = UiPrefs.HideEmail,
        };
        _hideEmail.CheckedChanged += (_, _) =>
        {
            if (_settingsLoading) return;
            UiPrefs.HideEmail = _hideEmail.Checked;
            UiPrefs.Save();
            RebuildCards();
            if (DetailOpen && _selected is not null)
                _drawer.Bind(_selected.Model);
            FlashSettingsSaved(_hideEmail.Checked ? "已隐藏邮箱" : "已显示完整邮箱");
        };

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

        settingsFlow.Controls.Add(_autoEnabled);
        settingsFlow.Controls.Add(_thrLabel);
        settingsFlow.Controls.Add(_threshold);
        settingsFlow.Controls.Add(_thrUnit);
        settingsFlow.Controls.Add(_startupEnabled);
        settingsFlow.Controls.Add(_hideEmail);
        settingsFlow.Controls.Add(_settingsSaved);
        settingsFlow.Controls.Add(_autoHint);
        _settingsStrip.Controls.Add(settingsFlow);

        // ── Row 3: Content (account list only — detail is a separate tool window) ──
        _drawer = new UsageDrawer();
        _drawer.SwitchRequested += (_, _) => DoSwitch();
        _drawer.AliasRequested += (_, _) => DoEditAlias();
        _drawer.DeleteRequested += (_, _) => DoDelete();
        _drawer.ToggleDisableRequested += (_, _) => DoToggleDisable();
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
            Text = "还没有托管账号\n\n请先在本机登录 Claude Code，然后点击「添加账号」。\n或使用 --fixture 打开演示数据。",
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
        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Theme.Space4, 0, Theme.Space3, 0),
            Font = Theme.FontSmall,
            Text = "就绪",
            Margin = new Padding(0),
        };

        root.Controls.Add(_header, 0, 0);
        root.Controls.Add(_actionBar, 0, 1);
        root.Controls.Add(_settingsStrip, 0, 2);
        root.Controls.Add(body, 0, 3);
        root.Controls.Add(_status, 0, 4);
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
                _tray.ShowBalloonTip(1600, "Claude Switch", "已最小化到托盘，双击图标重新打开。", ToolTipIcon.Info);
            }
        };

        // Live poll loop: 1s UI timer drives countdown + due refresh/autoswitch_tick.
        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += (_, _) => OnPollUiTick();

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
            _pollTimer.Stop();
            _pollTimer.Dispose();
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

        ApplyShellTheme();
        LoadSettingsUi();
        Reload();
        LoadActivityStripAsync();
        UpdateSwitchEnabled();
        _pollTimer.Start();

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
        // Extra text checks for ToolStrip primary label.
        Shown += (_, _) =>
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_SWITCH_LAYOUT_DIR");
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var path = Path.Combine(dir, "gui-layout-rects.txt");
                File.AppendAllText(
                    path,
                    $"switch_toolstrip_text=\"{_btnSwitch.Text}\" " +
                    $"switch_visible={_btnSwitch.Visible} " +
                    $"switch_w={_btnSwitch.Width}\n" +
                    (IsValidSwitchLabel(_btnSwitch.Text)
                        ? "OK switch_label_complete\n"
                        : "FAIL switch_label_incomplete\n") +
                    $"search_text=\"{_search.Text}\" search_w={_search.Width}\n" +
                    (_search.Width >= 120 ? "OK search_visible\n" : "FAIL search_not_visible\n") +
                    $"items={_toolStrip.Items.Count} " +
                    $"theme_visible={_btnTheme.Visible}\n");
            }
            catch { /* ignore */ }
        };
    }

    private static bool IsValidSwitchLabel(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains("请选择账号", StringComparison.Ordinal)
            || text.Contains("当前正在使用", StringComparison.Ordinal)
            || text.Contains("切换到该账号", StringComparison.Ordinal)
            || text.Contains("正在切换", StringComparison.Ordinal));

    private static string ThemeToggleText() =>
        Theme.Mode == ThemeMode.Dark ? "浅色模式" : "深色模式";

    /// <param name="role">primary | secondary | danger — read by SageToolStripRenderer.</param>
    private static ToolStripButton MakeStripBtn(string text, string role = "secondary")
    {
        return new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, Theme.Space1, 0),
            Padding = new Padding(Theme.Space3, 5, Theme.Space3, 5),
            Font = Theme.FontBody,
            Tag = role,
            Overflow = ToolStripItemOverflow.Never,
        };
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
        _status.BackColor = Theme.BgHeader;
        _status.ForeColor = Theme.TextSecondary;
        _brand.ForeColor = Theme.PrimaryDark;
        _subtitle.ForeColor = Theme.TextSecondary;
        _activeChip.ForeColor = Theme.PrimaryDark;
        _activeChip.BackColor = Theme.PrimarySoft;
        _countLabel.ForeColor = Theme.TextMuted;
        _autoEnabled.ForeColor = Theme.TextPrimary;
        _startupEnabled.ForeColor = Theme.TextPrimary;
        _hideEmail.ForeColor = Theme.TextPrimary;
        _settingsSaved.ForeColor = Theme.TextMuted;
        SyncAutoSwitchUiEnabled();
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
        _activeChip.BackColor = Theme.PrimarySoft;
        _activeChip.ForeColor = Theme.PrimaryDark;
        Icon = AppIcon.Get();
        _tray.Icon = AppIcon.Get();
        Invalidate(true);
    }

    private void SyncAutoSwitchUiEnabled()
    {
        bool on = _autoEnabled.Checked;
        _threshold.Enabled = on;
        _thrLabel.Enabled = on;
        _thrUnit.Enabled = on;
        _thrLabel.ForeColor = on ? Theme.TextSecondary : Theme.TextMuted;
        _thrUnit.ForeColor = on ? Theme.TextSecondary : Theme.TextMuted;
        _threshold.BackColor = on ? Theme.BgSurface : Theme.BgDisabled;
        _threshold.ForeColor = on ? Theme.TextPrimary : Theme.TextMuted;
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
            items.Add(new ToolStripMenuItem("还没有可继续的会话") { Enabled = false });
        }
        else
        {
            foreach (var session in recent)
            {
                var item = new ToolStripMenuItem(RecentSessions.MenuLabel(session))
                {
                    ToolTipText = $"{session.Path}{Environment.NewLine}会话 {session.SessionId}",
                };
                item.Click += (_, _) => ResumeSession(session);
                items.Add(item);
            }
        }

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("全部目录与会话…", null, (_, _) => ShowProjectsWindow()));
    }

    private void ResumeSession(RecentSession session)
    {
        if (ClaudeCli.Resume(session.Path, session.SessionId) is { } problem)
        {
            MessageBox.Show(this, problem, "无法继续会话", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _baseStatus = $"已在 {session.Name} 恢复会话";
        ComposeStatusLine();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("刷新用量", null, (_, _) => Reload());
        menu.Items.Add(new ToolStripSeparator());

        // The tray is where someone returning to their machine actually looks,
        // and the main window is usually closed at that moment.
        var resume = new ToolStripMenuItem("继续会话");
        resume.DropDownOpening += (_, _) => FillResumeMenu(resume.DropDownItems);
        // A submenu with no children never opens, so seed one placeholder.
        resume.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        menu.Items.Add(resume);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出（Shift+关闭）", null, (_, _) =>
        {
            _tray.Visible = false;
            Theme.SavePrefs();
            _engine.Dispose();
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
            _btnSwitch.Text = "正在切换…";
            _btnSwitch.ToolTipText = "正在切换账号，请稍候";
            return;
        }
        if (_selected is null)
        {
            _btnSwitch.Enabled = false;
            _btnSwitch.Text = "请选择账号";
            _btnSwitch.ToolTipText = "请先单击选中一张账号卡片";
            return;
        }
        if (_selected.Model.Active)
        {
            _btnSwitch.Enabled = false;
            _btnSwitch.Text = "当前正在使用";
            _btnSwitch.ToolTipText = "该账号已是 Claude Code 当前登录，无需切换";
        }
        else if (_selected.Model.Disabled)
        {
            _btnSwitch.Enabled = true;
            _btnSwitch.Text = "切换到该账号";
            _btnSwitch.ToolTipText = "切换到已停用账号（仍可手动切换；自动切换不会选中它）";
        }
        else
        {
            _btnSwitch.Enabled = true;
            _btnSwitch.Text = "切换到该账号";
            _btnSwitch.ToolTipText = "将选中的账号设为 Claude Code 当前登录";
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
            MessageBox.Show(this, "请先选择一张账号卡片。", "用量详情",
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
    private void LoadActivityStripAsync()
    {
        _ = Task.Run(() =>
        {
            JsonNode? stats = null;
            // Probe hook: the scan usually finishes before the window finishes
            // opening, so the loading state needs forcing to be reviewed.
            if (int.TryParse(
                    Environment.GetEnvironmentVariable("CLAUDE_SWITCH_PROBE_STRIP_DELAY_MS"),
                    out var delay) && delay > 0)
            {
                Thread.Sleep(delay);
            }
            try
            {
                stats = _engine.Call("overview_stats");
            }
            catch
            {
                // No transcripts, or an unreadable store: the strip stays hidden.
            }
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed) _activityStrip.Apply(stats);
                }));
            }
            catch (ObjectDisposedException)
            {
                // Window closed while the scan was running.
            }
        });
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
        _baseStatus = "正在读取全部会话记录…";
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
            _baseStatus = $"用量总览已生成（{sw.ElapsedMilliseconds} ms）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "统计失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _baseStatus = "统计失败";
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
            MessageBox.Show(this, ex.Message, "排序失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Reload();
            return;
        }

        // Render from what we already have. The old path called Reload(), which
        // fetches usage over the network on the UI thread — every drop froze the
        // window for the length of an HTTP round trip.
        _models = reordered;
        RebuildCards();
        _baseStatus = "已调整账号顺序（拖拽）";
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
            var oauth = new JsonObject
            {
                ["accessToken"] = token,
                ["refreshToken"] = "r",
                ["emailAddress"] = email,
            };
            if (sub is not null) oauth["subscriptionType"] = sub;
            if (rate is not null) oauth["rateLimitTier"] = rate;
            return new JsonObject { ["claudeAiOauth"] = oauth }.ToJsonString();
        }

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
    }

    private void LoadSettingsUi()
    {
        _settingsLoading = true;
        try
        {
            var s = _engine.Call("get_settings");
            var auto = s["autoswitch"];
            if (auto is null) return;
            _autoEnabled.Checked = auto["enabled"]?.GetValue<bool>() ?? false;
            if (auto["threshold"] is JsonValue t && t.TryGetValue<double>(out var th))
                _threshold.Value = (decimal)Math.Clamp(th, 50, 99);
            ApplyThresholdMarker();
            _hideEmail.Checked = UiPrefs.HideEmail;
            SyncAutoSwitchUiEnabled();
        }
        catch (Exception ex)
        {
            _status.Text = "读取设置失败：" + ex.Message;
        }
        finally
        {
            _settingsLoading = false;
        }
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
            ApplyThresholdMarker();
            if (DetailOpen && _selected is not null)
                _drawer.Bind(_selected.Model);
            string msg = _autoEnabled.Checked
                ? $"已保存：自动切换 · 阈值 {_threshold.Value}%"
                : "已保存：自动切换已关闭";
            if (auto)
                FlashSettingsSaved(msg);
            else
            {
                _baseStatus = msg;
                ComposeStatusLine();
            }
            // After enabling autoswitch, pull usage + tick path once so status updates soon.
            if (_autoEnabled.Checked)
                RunEnginePoll(force: true);
            else
                ComposeStatusLine();
            WriteStatusFile();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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
            _baseStatus = "自动刷新失败：" + ex.Message;
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
            ? result?["to"]?["email"]?.GetValue<string>() ?? "另一个账号"
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

        _baseStatus = $"已自动切换到 {target}";
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
                ? $"自动轮询 #{_poll.TickCount}（含 autoswitch）"
                : $"用量已刷新 · 轮询 #{_poll.TickCount}";
            ComposeStatusLine();
            NotifyIfSwitched(result, wasActive);
        }
        catch (Exception ex)
        {
            _baseStatus = "轮询失败：" + ex.Message;
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

    private void ComposeStatusLine()
    {
        string q = _search.Text.Trim();
        var filtered = AccountListFilter.Filter(_models, q);
        var filterMsg = AccountListFilter.FormatFilterMessage(filtered.Count, _models.Count, q);
        if (filterMsg is not null)
        {
            _status.Text = filterMsg;
            return;
        }

        var active = _models.FirstOrDefault(m => m.Active);
        var masked = ActiveLabel();
        int remain = (int)Math.Max(0, Math.Ceiling((_nextPollUtc - DateTime.UtcNow).TotalSeconds));
        string pollText = $"下次刷新：{Theme.FormatDuration(remain)}";
        string auto = _autoEnabled.Checked
            ? $"自动切换：开 · {_threshold.Value}%"
            : "自动切换：关闭";
        int total = Math.Max(1, _models.Count);
        int pos = active is null ? 0 : Math.Max(1, _models.FindIndex(m => m.Active) + 1);
        string pollPos = active is null
            ? $"轮询：{_poll.TickCount}"
            : $"轮询位置：{pos}/{total}";
        // Separators for scanability. No slot number when no slot is live.
        string who = active is null ? masked : $"#{active.Number} {masked}";
        _status.Text = $"当前账号：{who}  ｜  {auto}  ｜  {pollPos}  ｜  {pollText}";
        if (!string.IsNullOrEmpty(_baseStatus) && _baseStatus != "就绪"
            && remain > 0 && _poll.TickCount == 0)
        {
            // Keep one-shot messages visible until first poll completes.
            _status.Text = $"{_baseStatus}  ｜  {pollText}";
        }
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
            _status.Text = "加载失败：" + ex.Message;
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
        string activeEmail = "—";
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
                    FiveHour = five,
                    SevenDay = seven,
                    FiveHourResetsAt = fiveReset,
                    SevenDayResetsAt = sevenReset,
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
        var maskedActive = ActiveLabel();
        _activeChip.Text = $"当前账号：{maskedActive}";
        _countLabel.Text = $"{_models.Count} 个账号";
        var trayLabel = maskedActive.Length > 40
            ? maskedActive[..37] + "…"
            : maskedActive;
        _tray.Text = "Claude Switch · " + trayLabel;
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
            return $"未托管 · {Pii.MaskEmail(_unmanagedLogin!)}";
        return _models.Count == 0 ? "—" : "未登录";
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
            if (keepSelected == model.Number || (keepSelected is null && model.Active))
            {
                card.Selected = true;
                _selected = card;
            }
            _cardHost.Controls.Add(card);
        }
        _cardHost.ResumeLayout(true);
        LayoutCardHost();

        bool noData = _models.Count == 0;
        bool filterEmpty = !noData && filtered.Count == 0 && q.Length > 0;
        _emptyState.Visible = noData || filterEmpty;
        _cardHost.Visible = !noData && !filterEmpty;
        if (filterEmpty)
        {
            foreach (Control c in _emptyState.Controls)
            {
                if (c is Label lbl)
                    lbl.Text = $"没有匹配「{q}」的账号\n\n点工具栏「清除」或清空搜索框以显示全部 {_models.Count} 个账号。";
            }
        }
        else if (noData)
        {
            foreach (Control c in _emptyState.Controls)
            {
                if (c is Label lbl)
                    lbl.Text = "还没有托管账号\n\n请先在本机登录 Claude Code，然后点击「添加账号」。\n或使用 --fixture 打开演示数据。";
            }
        }

        // Match chrome
        var filterMsg = AccountListFilter.FormatFilterMessage(filtered.Count, _models.Count, q);
        _btnSearchClear.Enabled = q.Length > 0;
        _searchMatchLabel.Text = q.Length == 0
            ? ""
            : filtered.Count == 0
                ? "0 匹配"
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
                        "操作失败",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            });
        }

        var switchItem = new ToolStripMenuItem(
            m.Active ? "当前正在使用" : "切换到该账号")
        {
            Enabled = !m.Active && !_switching,
        };
        switchItem.Click += (_, _) => Run(DoSwitch);

        var aliasItem = new ToolStripMenuItem("编辑别名");
        aliasItem.Click += (_, _) => Run(DoEditAlias);

        var detailItem = new ToolStripMenuItem("查看详情");
        detailItem.Click += (_, _) => Run(OpenDetailSafe);

        var disableItem = new ToolStripMenuItem(m.Disabled ? "启用账号" : "停用账号");
        disableItem.Click += (_, _) => Run(DoToggleDisable);

        var deleteItem = new ToolStripMenuItem("删除账号")
        {
            ForeColor = Theme.Danger,
        };
        deleteItem.Click += (_, _) => Run(DoDelete);

        menu.Items.Add(switchItem);
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
                "无法打开详情：\n" + ex.Message,
                "查看详情",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void DoSwitch()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, "请先点击选择一个账号卡片。", "切换账号",
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
            _status.Text = $"已切换到 #{num} · {masked}";
            Reload();
            _tray.ShowBalloonTip(
                2000,
                "切换成功",
                $"当前使用 #{num} · {masked}",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _switching = false;
            Cursor = Cursors.Default;
            UpdateSwitchEnabled();
        }
    }

    private void DoAdd()
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            _engine.Call("add_current", new { });
            _status.Text = "已添加当前登录账号";
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "无法添加当前账号。\n\n" + ex.Message +
                "\n\n请先登录 Claude Code，或使用 --fixture 打开演示数据。",
                "添加账号",
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
            MessageBox.Show(this, "请先选择账号卡片。", "停用 / 启用",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var m = _selected.Model;
        try
        {
            _engine.Call("set_disabled", new { id = m.Number.ToString(), disabled = !m.Disabled });
            _status.Text = m.Disabled ? $"已重新启用 #{m.Number}" : $"已停用 #{m.Number}（仍可手动切换）";
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DoEditAlias()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, "请先选择账号卡片。", "编辑别名",
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
                ? $"已清除 #{m.Number} 的别名"
                : $"已设置别名：{alias}";
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "别名保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DoDelete()
    {
        if (_selected is null)
        {
            MessageBox.Show(this, "请先选择要删除的账号。", "删除账号",
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
            _status.Text = $"已删除 #{m.Number} · {masked}";
            _tray.ShowBalloonTip(1800, "账号已删除", $"已移除 #{m.Number} · {masked}", ToolTipIcon.Info);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
