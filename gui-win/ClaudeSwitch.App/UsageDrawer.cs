using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeSwitch.App;

/// <summary>
/// Compact account-detail panel (tool window). Content-sized height; footer actions
/// follow account state rather than always showing the same four buttons.
/// </summary>
public sealed class UsageDrawer : Panel
{
    private AccountCardModel? _model;
    private readonly Panel _header;
    private readonly Label _headerTitle;
    private readonly IconCloseButton _btnIconClose;
    private readonly Panel _scrollHost;
    private readonly Panel _body;
    private readonly Panel _footer;
    private readonly FlowLayoutPanel _footerLeft;
    private readonly FlowLayoutPanel _footerRight;

    private readonly NumberBadge _badge;
    private readonly Label _alias;
    private readonly StatusTag _statusTag;
    private readonly Label _email;

    private readonly Panel _healthBanner;
    private readonly StatusTag _healthTag;
    private readonly Label _healthText;

    private readonly Label _fiveTitle;
    private readonly StatusTag _fiveLevel;
    private readonly Label _fiveUsed;
    private readonly Label _fiveRemain;
    private readonly MeterBar _fiveBar;
    private readonly Label _fiveThresholdHint;
    private readonly Label _fiveReset;

    private readonly Label _sevenTitle;
    private readonly StatusTag _sevenLevel;
    private readonly Label _sevenUsed;
    private readonly Label _sevenRemain;
    private readonly MeterBar _sevenBar;
    private readonly Label _sevenThresholdHint;
    private readonly Label _sevenReset;

    private readonly Label _planTitle;
    private readonly StatusTag _planTag;
    private readonly Label _planFoot;
    /// <summary>Key/value label pool, grown on demand and reused across binds.</summary>
    private readonly List<Label> _planKeys = [];
    private readonly List<Label> _planVals = [];
    private int _planRowCount;
    private bool _planVisible;

    private readonly Label _rulesLink;
    private readonly ToolTip _toolTip = new() { ShowAlways = true, AutoPopDelay = 12000 };

    private readonly ThemedButton _btnAlias;
    private readonly ThemedButton _btnMore;
    private readonly ThemedButton _btnPrimary;
    private readonly ContextMenuStrip _moreMenu;

    private bool _layoutBusy;
    /// <summary>Scroll-host width the last layout pass was computed against.</summary>
    private int _lastLaidOutWidth = -1;
    private Point _dragStart;
    private bool _dragging;

    public const int DrawerWidth = 480;
    public const int HeaderHeight = 44;
    /// <summary>Single action row + padding, at 96 DPI.</summary>
    public const int FooterHeight = 56;
    /// <summary>Breathing room around the action row, at 96 DPI.</summary>
    private const int FooterPadV = FooterHeight - Theme.ControlHeight;
    /// <summary>Typical host form height when content is compact (no tall empty middle).</summary>
    public const int PreferredHostHeight = 560;

    public const string UsageLegendText =
        "百分比 = 已用额度。绿 0–69% 充足 · 橙 70–89% 注意 · 红 ≥90% 临界。竖线 = 自动切换阈值。";

    public const string UsageRulesShort =
        "绿色：充足　橙色：注意　红色：临界";

    public event EventHandler? SwitchRequested;
    public event EventHandler? AliasRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? ToggleDisableRequested;
    public event EventHandler? ClosedByUser;
    public event EventHandler? PreferredSizeChanged;

    /// <summary>Measured body content height after last Relayout (excluding header/footer).</summary>
    public int ContentHeight { get; private set; }

    /// <summary>Converts a 96-DPI design length to device pixels.</summary>
    private int Scale(int designPx) => (int)Math.Round(designPx * DeviceDpi / 96.0);

    /// <summary>
    /// Footer height in device pixels. Driven by the action row rather than a fixed
    /// 56px band, so the buttons keep their padding at 125/150/200% scaling.
    /// </summary>
    private int FooterH => _btnPrimary.RowHeight + Scale(FooterPadV);

    /// <summary>Scrollable extent of the body — must match what the host reserves for it.</summary>
    private int ScrollContentHeight => ContentHeight + 4;

    /// <summary>Host form client height that fits content without a large empty middle.</summary>
    public int PreferredHostClientHeight =>
        HeaderHeight + Math.Max(ScrollContentHeight, 200) + FooterH;

    public UsageDrawer()
    {
        Dock = DockStyle.Fill;
        MinimumSize = new Size(360, 240);
        DoubleBuffered = true;
        Padding = new Padding(0);

        // ── Header ────────────────────────────────────────────────
        _header = new Panel
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            Padding = new Padding(18, 0, 10, 0),
            Cursor = Cursors.SizeAll,
        };
        _headerTitle = new Label
        {
            Text = "账号详情",
            Font = Theme.FontHeading,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.SizeAll,
        };
        _btnIconClose = new IconCloseButton
        {
            Dock = DockStyle.Right,
            Width = 32,
            Cursor = Cursors.Hand,
        };
        _toolTip.SetToolTip(_btnIconClose, "关闭");
        _btnIconClose.Click += (_, _) => ClosedByUser?.Invoke(this, EventArgs.Empty);
        _header.Controls.Add(_headerTitle);
        _header.Controls.Add(_btnIconClose);
        WireHeaderDrag();

        // ── Footer: left actions + right primary ──────────────────
        _footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = FooterHeight,
            Padding = new Padding(16, 10, 16, 12),
        };
        _footerLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        _footerRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };

        _btnAlias = MakeBtn(secondary: true, "编辑别名");
        _btnMore = MakeBtn(secondary: true, "更多操作");
        _btnPrimary = MakeBtn(secondary: false, "完成");
        _btnPrimary.MinWidth = 100;

        _btnAlias.Click += (_, _) => AliasRequested?.Invoke(this, EventArgs.Empty);
        _btnMore.Click += BtnMore_Click;
        _btnPrimary.Click += BtnPrimary_Click;

        foreach (var b in new[] { _btnAlias, _btnMore, _btnPrimary })
        {
            b.AutoSize = true;
            b.Margin = new Padding(0, 0, 8, 0);
        }
        _btnPrimary.Margin = new Padding(0);

        _footerLeft.Controls.Add(_btnAlias);
        _footerLeft.Controls.Add(_btnMore);
        _footerRight.Controls.Add(_btnPrimary);
        _footer.Controls.Add(_footerRight);
        _footer.Controls.Add(_footerLeft);

        _moreMenu = new ContextMenuStrip
        {
            Font = Theme.FontBody,
            ShowImageMargin = false,
        };

        // ── Scrollable body ───────────────────────────────────────
        _scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0),
        };
        _body = new Panel
        {
            Location = Point.Empty,
            Size = new Size(DrawerWidth, 360),
            AutoSize = false,
        };

        _badge = new NumberBadge();
        _alias = MakeLabel(Theme.FontName);
        _statusTag = new StatusTag();
        _email = MakeLabel(Theme.FontBody);

        _healthBanner = new Panel { Height = 36 };
        _healthTag = new StatusTag();
        _healthText = MakeLabel(Theme.FontBody);
        _healthBanner.Controls.Add(_healthTag);
        _healthBanner.Controls.Add(_healthText);

        _fiveTitle = MakeLabel(Theme.FontBody);
        _fiveTitle.Text = "5 小时额度";
        _fiveLevel = new StatusTag();
        _fiveUsed = MakeLabel(Theme.FontBody);
        _fiveRemain = MakeLabel(Theme.FontBody);
        _fiveRemain.TextAlign = ContentAlignment.MiddleRight;
        _fiveBar = new MeterBar { Height = 12 };
        _fiveThresholdHint = MakeLabel(Theme.FontCaption);
        _fiveThresholdHint.TextAlign = ContentAlignment.MiddleRight;
        _fiveReset = MakeLabel(Theme.FontCaption);

        _sevenTitle = MakeLabel(Theme.FontBody);
        _sevenTitle.Text = "7 天额度";
        _sevenLevel = new StatusTag();
        _sevenUsed = MakeLabel(Theme.FontBody);
        _sevenRemain = MakeLabel(Theme.FontBody);
        _sevenRemain.TextAlign = ContentAlignment.MiddleRight;
        _sevenBar = new MeterBar { Height = 12 };
        _sevenThresholdHint = MakeLabel(Theme.FontCaption);
        _sevenThresholdHint.TextAlign = ContentAlignment.MiddleRight;
        _sevenReset = MakeLabel(Theme.FontCaption);

        _planTitle = MakeLabel(Theme.FontBody);
        _planTitle.Text = "订阅";
        _planTag = new StatusTag();
        _planFoot = MakeLabel(Theme.FontCaption);

        _rulesLink = MakeLabel(Theme.FontCaption);
        _rulesLink.Text = "ⓘ  查看用量和自动切换规则";
        _rulesLink.Cursor = Cursors.Hand;
        _toolTip.SetToolTip(_rulesLink, UsageLegendText);
        _rulesLink.Click += (_, _) =>
        {
            MessageBox.Show(
                FindForm(),
                UsageLegendText + "\n\n" + UsageRulesShort,
                "用量及自动切换规则",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        };

        _body.Controls.AddRange([
            _badge, _alias, _statusTag, _email,
            _healthBanner,
            _fiveTitle, _fiveLevel, _fiveUsed, _fiveRemain, _fiveBar, _fiveThresholdHint, _fiveReset,
            _sevenTitle, _sevenLevel, _sevenUsed, _sevenRemain, _sevenBar, _sevenThresholdHint, _sevenReset,
            _planTitle, _planTag, _planFoot,
            _rulesLink,
        ]);
        _scrollHost.Controls.Add(_body);

        Controls.Add(_scrollHost);
        Controls.Add(_header);
        Controls.Add(_footer);

        Resize += (_, _) => Relayout();
        _scrollHost.Resize += (_, _) => Relayout();
        HandleCreated += (_, _) => Relayout();

        ApplyTheme();
        Theme.Changed += (_, _) =>
        {
            ApplyTheme();
            if (_model is not null) Bind(_model);
            else Relayout();
        };
    }

    private void WireHeaderDrag()
    {
        void HeaderMouseDown(object? _, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragStart = e.Location;
        }
        void HeaderMouseMove(object? _, MouseEventArgs e)
        {
            if (!_dragging || e.Button != MouseButtons.Left) return;
            var form = FindForm();
            if (form is null) return;
            form.Left += e.X - _dragStart.X;
            form.Top += e.Y - _dragStart.Y;
        }
        void HeaderMouseUp(object? _, MouseEventArgs e) => _dragging = false;
        _header.MouseDown += HeaderMouseDown;
        _header.MouseMove += HeaderMouseMove;
        _header.MouseUp += HeaderMouseUp;
        _headerTitle.MouseDown += HeaderMouseDown;
        _headerTitle.MouseMove += HeaderMouseMove;
        _headerTitle.MouseUp += HeaderMouseUp;
    }

    private static Label MakeLabel(Font font) =>
        new()
        {
            AutoSize = false,
            Font = font,
        };

    private static ThemedButton MakeBtn(bool secondary, string text)
    {
        ThemedButton b = secondary
            ? new SecondaryButton { Text = text }
            : new PrimaryButton { Text = text };
        b.AutoSize = true;
        b.TextAlign = ContentAlignment.MiddleCenter;
        return b;
    }

    private void BtnMore_Click(object? sender, EventArgs e)
    {
        if (_model is null) return;
        _moreMenu.Items.Clear();

        var disableItem = new ToolStripMenuItem(
            _model.Disabled ? "启用账号" : "停用账号");
        disableItem.Click += (_, _) => ToggleDisableRequested?.Invoke(this, EventArgs.Empty);

        var deleteItem = new ToolStripMenuItem("删除账号")
        {
            ForeColor = Theme.Danger,
        };
        deleteItem.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

        _moreMenu.Items.Add(disableItem);
        _moreMenu.Items.Add(new ToolStripSeparator());
        _moreMenu.Items.Add(deleteItem);
        _moreMenu.Show(_btnMore, new Point(0, _btnMore.Height));
    }

    private void BtnPrimary_Click(object? sender, EventArgs e)
    {
        if (_model is null)
        {
            ClosedByUser?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_model.Disabled)
        {
            ToggleDisableRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_model.Active)
        {
            ClosedByUser?.Invoke(this, EventArgs.Empty);
            return;
        }

        SwitchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Safe layout: never BeginInvoke before handle exists.</summary>
    public void Relayout()
    {
        if (_layoutBusy || IsDisposed || _body.IsDisposed) return;
        _layoutBusy = true;
        try
        {
            LayoutOnce();
            // Content tall enough to need a vertical scrollbar loses that
            // scrollbar's width; without a corrective pass the body stays at the
            // old width and a *horizontal* scrollbar appears under it. The
            // Resize this triggers is swallowed by _layoutBusy, so re-run here.
            if (_scrollHost.ClientSize.Width != _lastLaidOutWidth)
                LayoutOnce();
        }
        finally
        {
            _layoutBusy = false;
        }

        PreferredSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LayoutOnce()
    {
        {
            if (_header.Height != HeaderHeight)
                _header.Height = HeaderHeight;

            // Footer follows the action row so button labels are never clipped at
            // >100% scaling; the leftover band is split evenly above/below the row.
            int footerH = FooterH;
            int slack = Math.Max(0, footerH - _btnPrimary.RowHeight);
            var footerPad = new Padding(Scale(16), slack / 2, Scale(16), slack - slack / 2);
            if (_footer.Padding != footerPad)
                _footer.Padding = footerPad;
            if (_footer.Height != footerH)
                _footer.Height = footerH;

            int hostW = _scrollHost.ClientSize.Width;
            if (hostW < 40)
                hostW = Math.Max(200, ClientSize.Width > 40 ? ClientSize.Width : DrawerWidth);
            _lastLaidOutWidth = _scrollHost.ClientSize.Width;

            const int padL = 20;
            const int padR = 20;
            const int padT = 16;
            const int padB = 12;
            int innerW = Math.Max(200, hostW - padL - padR);
            int x = padL;
            int y = padT;
            const int section = 18;

            // Identity: badge + alias/tag row + email
            const int badgeSize = 28;
            _badge.SetBounds(x, y + 2, badgeSize, badgeSize);

            int textLeft = x + badgeSize + 10;
            int textW = Math.Max(80, innerW - badgeSize - 10);

            // Status tag on the right of alias row
            int tagW = _statusTag.PreferredWidth;
            int tagH = 22;
            int aliasW = Math.Max(40, textW - tagW - 8);
            _alias.SetBounds(textLeft, y, aliasW, 24);
            _statusTag.SetBounds(textLeft + aliasW + 8, y + 1, tagW, tagH);
            y += 26;
            _email.SetBounds(textLeft, y, textW, 20);
            y += 20 + section;

            // Health banner (full width soft strip)
            int bannerH = 34;
            _healthBanner.SetBounds(x, y, innerW, bannerH);
            int htW = _healthTag.PreferredWidth;
            _healthTag.SetBounds(8, (bannerH - 20) / 2, htW, 20);
            _healthText.SetBounds(8 + htW + 8, 0, Math.Max(40, innerW - htW - 24), bannerH);
            _healthText.TextAlign = ContentAlignment.MiddleLeft;
            y += bannerH + section;

            // Five-hour block
            y = LayoutUsageBlock(
                y, x, innerW,
                _fiveTitle, _fiveLevel, _fiveUsed, _fiveRemain,
                _fiveBar, _fiveThresholdHint, _fiveReset);
            y += section;

            // Seven-day block
            y = LayoutUsageBlock(
                y, x, innerW,
                _sevenTitle, _sevenLevel, _sevenUsed, _sevenRemain,
                _sevenBar, _sevenThresholdHint, _sevenReset);
            y += section;

            // Subscription facts (hidden entirely for slots with no plan data)
            if (_planVisible)
            {
                int planTagW = _planTag.PreferredWidth;
                _planTitle.SetBounds(x, y, Math.Max(60, innerW - planTagW - 8), 22);
                _planTag.SetBounds(x + innerW - planTagW, y + 1, planTagW, 20);
                y += 24 + 2;

                // Row box must clear descenders and underscores — values like
                // `default_claude_ai` lose their underscores in a tight box.
                const int keyW = 76;
                int rowH = Math.Max(20, Theme.FontCaption.Height + 6);
                for (int i = 0; i < _planRowCount; i++)
                {
                    _planKeys[i].SetBounds(x, y, keyW, rowH);
                    _planVals[i].SetBounds(x + keyW, y, Math.Max(40, innerW - keyW), rowH);
                    y += rowH;
                }

                if (_planFoot.Visible)
                {
                    y += 4;
                    _planFoot.SetBounds(x, y, innerW, rowH);
                    y += rowH;
                }
                y += section;
            }

            // Rules link
            _rulesLink.SetBounds(x, y, innerW, 20);
            y += 20 + padB;

            ContentHeight = y;
            _body.SetBounds(0, 0, hostW, ContentHeight);
            _scrollHost.AutoScrollMinSize = new Size(0, ScrollContentHeight);
            _scrollHost.HorizontalScroll.Enabled = false;
            _scrollHost.HorizontalScroll.Visible = false;
        }
    }

    private static int LayoutUsageBlock(
        int y, int x, int innerW,
        Label title, StatusTag level, Label used, Label remain,
        MeterBar bar, Label thrHint, Label reset)
    {
        int levelW = level.PreferredWidth;
        title.SetBounds(x, y, Math.Max(80, innerW - levelW - 8), 22);
        level.SetBounds(x + innerW - levelW, y + 1, levelW, 20);
        y += 24 + 2;

        int half = innerW / 2;
        used.SetBounds(x, y, half, 20);
        remain.SetBounds(x + half, y, half, 20);
        y += 20 + 4;

        bar.SetBounds(x, y, innerW, 12);
        y += 12 + 2;

        if (thrHint.Visible)
        {
            thrHint.SetBounds(x, y, innerW, 16);
            y += 16 + 2;
        }

        if (reset.Visible && !string.IsNullOrEmpty(reset.Text))
        {
            reset.SetBounds(x, y, innerW, 16);
            y += 16;
        }

        return y;
    }

    private void ScheduleRelayout()
    {
        Relayout();
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) Relayout();
            }));
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed between check and invoke.
        }
    }

    private void ApplyTheme()
    {
        BackColor = Theme.BgDrawer;
        _header.BackColor = Theme.BgDrawer;
        _headerTitle.ForeColor = Theme.TextPrimary;
        _headerTitle.BackColor = Theme.BgDrawer;
        _scrollHost.BackColor = Theme.BgDrawer;
        _body.BackColor = Theme.BgDrawer;
        _footer.BackColor = Theme.BgDrawer;
        _footerLeft.BackColor = Theme.BgDrawer;
        _footerRight.BackColor = Theme.BgDrawer;

        _alias.ForeColor = Theme.TextPrimary;
        _email.ForeColor = Theme.TextSecondary;

        _healthBanner.BackColor = Theme.BgRowAlt;
        _healthText.ForeColor = Theme.TextSecondary;

        _fiveTitle.ForeColor = Theme.TextPrimary;
        _sevenTitle.ForeColor = Theme.TextPrimary;
        _fiveUsed.ForeColor = Theme.TextPrimary;
        _sevenUsed.ForeColor = Theme.TextPrimary;
        _fiveRemain.ForeColor = Theme.TextSecondary;
        _sevenRemain.ForeColor = Theme.TextSecondary;
        _fiveThresholdHint.ForeColor = Theme.TextMuted;
        _sevenThresholdHint.ForeColor = Theme.TextMuted;
        _fiveReset.ForeColor = Theme.TextMuted;
        _sevenReset.ForeColor = Theme.TextMuted;
        _planTitle.ForeColor = Theme.TextPrimary;
        _planFoot.ForeColor = Theme.TextMuted;
        foreach (var k in _planKeys) k.ForeColor = Theme.TextMuted;
        foreach (var v in _planVals) v.ForeColor = Theme.TextSecondary;
        _rulesLink.ForeColor = Theme.Primary;

        _btnIconClose.Invalidate();
        _badge.Invalidate();
        _statusTag.Invalidate();
        _healthTag.Invalidate();
        _fiveLevel.Invalidate();
        _sevenLevel.Invalidate();
        _planTag.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        using var pen = new Pen(Theme.BorderSoft);
        int hy = _header.Bottom - 1;
        if (hy > 0)
            e.Graphics.DrawLine(pen, 0, hy, Width, hy);
        int fy = _footer.Top;
        if (fy > 1)
            e.Graphics.DrawLine(pen, 0, fy, Width, fy);
    }

    public void Bind(AccountCardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;

        string name = AccountCard.DisplayTitle(model);
        _badge.Number = model.Number;
        _badge.Active = model.Active;
        _alias.Text = name;
        _email.Text = AccountCard.DisplayEmail(model.Email ?? "");

        // Account status tag (identity)
        if (model.Disabled)
            _statusTag.Set("已停用", Theme.BgDisabled, Theme.TextMuted);
        else if (model.Active)
            _statusTag.Set("当前账号", Theme.PrimarySoft, Theme.PrimaryDark);
        else
            _statusTag.Set("就绪", Theme.BgRowAlt, Theme.TextSecondary);

        BindHealth(model);
        BindUsageWindow(
            model.FiveHour, model.FiveHourResetsAt, model.UsageStatus,
            _fiveLevel, _fiveUsed, _fiveRemain, _fiveBar, _fiveThresholdHint, _fiveReset);
        BindUsageWindow(
            model.SevenDay, model.SevenDayResetsAt, model.UsageStatus,
            _sevenLevel, _sevenUsed, _sevenRemain, _sevenBar, _sevenThresholdHint, _sevenReset);
        BindPlan(model);

        ConfigureFooter(model);
        ScheduleRelayout();
        Invalidate(true);
    }

    private void BindHealth(AccountCardModel model)
    {
        var tag = Theme.UsageHealthTag(model.FiveHour, model.SevenDay);
        double? max = model.FiveHour is null && model.SevenDay is null
            ? null
            : Math.Max(model.FiveHour ?? 0, model.SevenDay ?? 0);
        Color bg = max is null
            ? Theme.BgRowAlt
            : max >= 90 ? Theme.DangerSoft
            : max >= 70 ? Color.FromArgb(
                Theme.Mode == ThemeMode.Dark ? 0x3A : 0xF8,
                Theme.Mode == ThemeMode.Dark ? 0x34 : 0xF0,
                Theme.Mode == ThemeMode.Dark ? 0x28 : 0xE0)
            : Theme.PrimarySoft;
        Color fg = max is null
            ? Theme.TextMuted
            : max >= 90 ? Theme.Danger
            : max >= 70 ? Theme.Warning
            : Theme.PrimaryDark;

        string label = tag switch
        {
            "健康" => "用量健康",
            "注意" => "用量注意",
            "临界" => "用量临界",
            _ => Theme.UsageStatusShort(model.UsageStatus),
        };
        _healthTag.Set(label, bg, fg);

        _healthText.Text = BuildHealthAdvice(model, max);
        _healthBanner.BackColor = Theme.Mode == ThemeMode.Dark
            ? Theme.BgRowAlt
            : Color.FromArgb(0xF4, 0xF7, 0xF5);
    }

    private static string BuildHealthAdvice(AccountCardModel m, double? max)
    {
        if (max is null)
        {
            // "Click refresh" is useless advice when refreshing cannot help.
            return m.UsageStatus is not (null or "ok" or "unknown")
                ? Theme.UsageStatusLong(m.UsageStatus)
                : "暂无用量数据，可点击主窗口「刷新」拉取。";
        }
        if (max >= 90)
            return m.Active
                ? "接近限流，建议尽快切换到余量更充足的账号"
                : "接近限流，不建议作为切换目标";
        if (max >= 70)
            return m.Active
                ? "用量偏高，适合短任务；长会话可考虑换号"
                : "用量偏高，短任务可用";
        if (m.Active)
            return "当前账号暂时不需要切换";
        return "余量充足，适合作为切换目标";
    }

    /// <summary>
    /// Subscription facts, exactly as stored — no renewal date is shown because
    /// none is obtainable: an OAuth token carries no billing scope, so the only
    /// dated fact available is when the subscription started.
    /// </summary>
    private void BindPlan(AccountCardModel m)
    {
        _planVisible = m.HasPlanInfo;
        _planTitle.Visible = _planVisible;
        _planTitle.Text = _planVisible ? "订阅" : "";
        _planTag.Visible = _planVisible && m.HasPlanBadge;

        var rows = new List<(string Key, string Value)>();
        if (_planVisible)
        {
            if (!string.IsNullOrWhiteSpace(m.BillingType))
                rows.Add(("付费方式", Theme.BillingTypeLabel(m.BillingType)));

            if (Theme.FormatDate(m.SubscriptionCreatedAt) is { } started)
                rows.Add(("订阅开始", started));
            if (Theme.FormatDate(m.TrialEndsAt) is { } trialEnds)
                rows.Add(("试用到期", trialEnds));
            if (Theme.FormatDate(m.AccountCreatedAt) is { } created)
                rows.Add(("账号创建", created));

            // Personal plans synthesize "<email>'s Organization" — noise, skip it.
            if (!m.PlanPersonal && !string.IsNullOrWhiteSpace(m.OrganizationName))
            {
                string org = m.OrganizationName!;
                if (!string.IsNullOrWhiteSpace(m.OrganizationRole))
                    org += $"（{m.OrganizationRole}）";
                rows.Add(("组织", org));
            }
            if (!string.IsNullOrWhiteSpace(m.SeatTier))
                rows.Add(("席位", m.SeatTier!));

            rows.Add(("额外用量", m.ExtraUsageEnabled ? "已开启" : "未开启"));

            if (!string.IsNullOrWhiteSpace(m.RateLimitTier))
                rows.Add(("限速档", m.RateLimitTier!));
        }

        EnsurePlanRows(rows.Count);
        for (int i = 0; i < _planKeys.Count; i++)
        {
            bool used = _planVisible && i < rows.Count;
            _planKeys[i].Visible = used;
            _planVals[i].Visible = used;
            // Clear unused rows too: a hidden label keeps its old text, which
            // would resurface as stale content on the next bind.
            _planKeys[i].Text = used ? rows[i].Key : "";
            _planVals[i].Text = used ? rows[i].Value : "";
        }
        _planRowCount = _planVisible ? rows.Count : 0;

        if (m.HasPlanBadge)
            _planTag.Set(m.PlanLabel!, Theme.BgRowAlt, Theme.TextSecondary);

        // These facts are a snapshot taken when the slot was last live, so say
        // when — an account upgraded elsewhere still reports its old tier here.
        string? fetched = Theme.FormatEpochMs(m.ProfileFetchedAt);
        _planFoot.Visible = _planVisible && fetched is not null;
        _planFoot.Text = fetched is null ? "" : $"资料同步于 {fetched}（切换到该账号时更新）";
    }

    /// <summary>Grow the key/value label pool to at least <paramref name="count"/> rows.</summary>
    private void EnsurePlanRows(int count)
    {
        while (_planKeys.Count < count)
        {
            var key = MakeLabel(Theme.FontCaption);
            key.ForeColor = Theme.TextMuted;
            var val = MakeLabel(Theme.FontCaption);
            val.ForeColor = Theme.TextSecondary;
            _planKeys.Add(key);
            _planVals.Add(val);
            _body.Controls.Add(key);
            _body.Controls.Add(val);
        }
    }

    private void BindUsageWindow(
        double? pct,
        string? resetsAt,
        string? status,
        StatusTag level,
        Label used,
        Label remain,
        MeterBar bar,
        Label thrHint,
        Label reset)
    {
        string levelText = Theme.UsageLevel(pct);
        Color levelFg = Theme.UsageColor(pct);
        Color levelBg = pct is null
            ? Theme.BgRowAlt
            : pct >= 90 ? Theme.DangerSoft
            : pct >= 70 ? (Theme.Mode == ThemeMode.Dark
                ? Color.FromArgb(0x3A, 0x34, 0x28)
                : Color.FromArgb(0xF8, 0xF0, 0xE0))
            : Theme.PrimarySoft;
        level.Set(levelText, levelBg, levelFg);

        if (pct is null)
        {
            // The short reason on this line; the full "what to do" goes to the
            // reset line below, which has the whole drawer width.
            used.Text = Theme.UsageStatusShort(status);
            remain.Text = "";
        }
        else
        {
            double rem = Math.Max(0, 100.0 - pct.Value);
            used.Text = $"已使用 {pct.Value:0.#}%";
            remain.Text = $"剩余 {rem:0.#}%";
        }
        used.ForeColor = pct is null && status is "needs-login" or "no-credential" or "no-subscription"
            ? Theme.Warning
            : Theme.UsageColor(pct);
        remain.ForeColor = Theme.TextSecondary;

        double thr = AccountCard.ThresholdMarkerPct;
        bool showThr = thr is > 0 and < 100;
        bar.Set(pct, showThr ? thr : 0, showThreshold: showThr);

        if (showThr)
        {
            thrHint.Visible = true;
            thrHint.Text = $"自动切换 {thr:0.#}%";
            thrHint.ForeColor = Theme.TextMuted;
            _toolTip.SetToolTip(bar, $"达到 {thr:0.#}% 后自动切换账号");
            _toolTip.SetToolTip(thrHint, $"达到 {thr:0.#}% 后自动切换账号");
        }
        else
        {
            thrHint.Visible = false;
            thrHint.Text = "";
            _toolTip.SetToolTip(bar, "自动切换已关闭，不显示切换阈值");
        }

        string? resetRel = Theme.FormatResetsIn(resetsAt);
        if (resetRel is not null)
        {
            reset.Visible = true;
            reset.Text = $"重置时间：{resetRel}";
        }
        else if (pct is null && status is not (null or "ok" or "unknown"))
        {
            reset.Visible = true;
            reset.Text = Theme.UsageStatusLong(status);
        }
        else
        {
            reset.Visible = false;
            reset.Text = "";
        }
    }

    private void ConfigureFooter(AccountCardModel model)
    {
        _btnAlias.Visible = true;
        _btnMore.Visible = true;

        if (model.Disabled)
        {
            _btnPrimary.Text = "启用账号";
            // Promote enable; keep edit; more for delete.
        }
        else if (model.Active)
        {
            _btnPrimary.Text = "完成";
        }
        else
        {
            _btnPrimary.Text = "切换到此账号";
        }

        // PrimaryButton is always primary green — correct for switch/enable/done.
        _btnPrimary.Enabled = true;
    }

    public void Clear()
    {
        _model = null;
    }

    public bool HasModel => _model is not null;
}

/// <summary>Circular account index matching the main list badge.</summary>
internal sealed class NumberBadge : Control
{
    private int _number = 1;
    private bool _active;

    public NumberBadge()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.ResizeRedraw,
            true);
        Size = new Size(28, 28);
        TabStop = false;
    }

    public int Number
    {
        get => _number;
        set { _number = value; Invalidate(); }
    }

    public bool Active
    {
        get => _active;
        set { _active = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.BgDrawer);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(_active ? Theme.Primary : Theme.BgRowAlt))
            g.FillEllipse(b, r);
        using (var p = new Pen(_active ? Theme.PrimaryDark : Theme.Border, 1f))
            g.DrawEllipse(p, r);

        TextRenderer.DrawText(
            g,
            _number.ToString(),
            Theme.FontBody,
            r,
            _active ? Theme.TextOnPrimary : Theme.TextSecondary,
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine);
    }
}

/// <summary>Compact rounded status / level pill.</summary>
internal sealed class StatusTag : Control
{
    private string _text = "";
    private Color _bg = Color.Gray;
    private Color _fg = Color.Black;

    public StatusTag()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.ResizeRedraw,
            true);
        Size = new Size(64, 20);
        TabStop = false;
    }

    public int PreferredWidth
    {
        get
        {
            if (string.IsNullOrEmpty(_text)) return 48;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int w = TextRenderer.MeasureText(_text, Theme.FontSmall, new Size(int.MaxValue, 20), flags).Width;
            return Math.Max(40, w + 14);
        }
    }

    public void Set(string text, Color bg, Color fg)
    {
        _text = text ?? "";
        _bg = bg;
        _fg = fg;
        Width = PreferredWidth;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.BgDrawer);

        var r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        int radius = Math.Max(2, Height / 2);
        using (var path = Round(r, radius))
        using (var b = new SolidBrush(_bg))
            g.FillPath(b, path);

        TextRenderer.DrawText(
            g,
            _text,
            Theme.FontSmall,
            r,
            _fg,
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine);
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = Math.Min(Math.Max(2, radius * 2), Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

internal sealed class MeterBar : Control
{
    private double? _pct;
    private double _thresholdPct = 90;
    private bool _showThreshold = true;

    public MeterBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        Height = 12;
        Cursor = Cursors.Default;
    }

    public void Set(double? pct, double thresholdPct = 90, bool showThreshold = true)
    {
        _pct = pct;
        _thresholdPct = thresholdPct;
        _showThreshold = showThreshold && thresholdPct is > 0 and < 100;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 2) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Round(r, Math.Min(4, Height / 2)))
        using (var bg = new SolidBrush(Theme.BorderSoft))
            g.FillPath(bg, path);

        if (_pct is not null and > 0)
        {
            int w = Math.Max(4, (int)((Width - 1) * Math.Clamp(_pct.Value / 100.0, 0, 1)));
            w = Math.Min(w, Width - 1);
            using var path = Round(new Rectangle(0, 0, w, Height - 1), Math.Min(4, Height / 2));
            using var fg = new SolidBrush(Theme.UsageColor(_pct));
            g.FillPath(fg, path);
        }

        if (_showThreshold && Width > 4)
        {
            int tx = Math.Clamp((int)((Width - 1) * _thresholdPct / 100.0), 0, Width - 1);
            using var pen = new Pen(Theme.TextMuted, 1.2f);
            g.DrawLine(pen, tx, 0, tx, Height - 1);
        }
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        if (r.Width < 2 || r.Height < 2)
        {
            var empty = new GraphicsPath();
            empty.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)));
            return empty;
        }
        int d = Math.Min(Math.Max(2, radius * 2), Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
