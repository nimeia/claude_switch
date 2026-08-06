using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeSwitch.App;

public sealed class AccountCardModel
{
    public int Number { get; init; }
    public string Email { get; init; } = "";
    public string? Alias { get; init; }
    public bool Active { get; init; }
    public bool Disabled { get; init; }
    public double? FiveHour { get; init; }
    public double? SevenDay { get; init; }
    /// <summary>ISO timestamp when the 5h window resets (if known).</summary>
    public string? FiveHourResetsAt { get; init; }
    /// <summary>ISO timestamp when the 7d window resets (if known).</summary>
    public string? SevenDayResetsAt { get; init; }

    /// <summary>
    /// Why usage numbers are missing: <c>ok</c> · <c>unknown</c> ·
    /// <c>no-credential</c> · <c>needs-login</c> · <c>api-key</c> ·
    /// <c>unavailable</c>. A blank cell that can't say why is a support ticket.
    /// </summary>
    public string? UsageStatus { get; init; }

    // ── Subscription facts (snapshot v2 `accounts[].plan`) ────────────────
    // Read back from the slot's own backups; nothing here is fetched or
    // predicted. There is deliberately no renewal date: no API reachable with
    // an OAuth token exposes one, so only the subscription *start* is shown.

    /// <summary>Plan badge text ("Pro" / "Max 20×" / "API Key"); empty when unknown.</summary>
    public string? PlanLabel { get; init; }
    /// <summary>Kebab tier id (`pro` / `max-20x` / `api-key` / `unknown`).</summary>
    public string? PlanTier { get; init; }
    /// <summary>Personal plan — org name is the synthesized "…'s Organization".</summary>
    public bool PlanPersonal { get; init; }
    /// <summary>ISO start of the current subscription. Not a renewal date.</summary>
    public string? SubscriptionCreatedAt { get; init; }
    public string? AccountCreatedAt { get; init; }
    public string? BillingType { get; init; }
    public string? RateLimitTier { get; init; }
    public string? OrganizationName { get; init; }
    public string? OrganizationRole { get; init; }
    public string? SeatTier { get; init; }
    public string? TrialEndsAt { get; init; }
    public bool ExtraUsageEnabled { get; init; }
    /// <summary>Epoch ms of Claude Code's last profile fetch (how stale this is).</summary>
    public long? ProfileFetchedAt { get; init; }

    /// <summary>True when a plan badge should be drawn.</summary>
    public bool HasPlanBadge => !string.IsNullOrWhiteSpace(PlanLabel);

    /// <summary>
    /// True when the snapshot carried a `plan` node worth a detail section —
    /// false for slots whose backups predate these fields.
    /// </summary>
    public bool HasPlanInfo =>
        HasPlanBadge
        || !string.IsNullOrWhiteSpace(SubscriptionCreatedAt)
        || !string.IsNullOrWhiteSpace(BillingType)
        || !string.IsNullOrWhiteSpace(AccountCreatedAt);
}

/// <summary>Where a dragged card was released: onto <paramref name="Target"/>'s
/// upper half (insert before) or lower half (insert after).</summary>
internal sealed record CardDrop(AccountCard Source, AccountCard Target, bool After);

/// <summary>
/// Compact account row. Active (in use) and selected (focus for actions) are independent:
///   · selected → slate/blue border + soft selection fill
///   · active   → green badge + "当前" pill (never green border alone)
/// </summary>
internal sealed class AccountCard : Control
{
    private bool _hover;
    private bool _selected;
    private bool _moreHover;
    private Point _dragStart;
    private bool _dragArmed;
    /// <summary>This card is the one being dragged (drawn faded).</summary>
    private bool _dragSource;
    /// <summary>Insertion line to draw, or null when the pointer is elsewhere.</summary>
    private bool? _dropAfter;
    private readonly ToolTip _tip = new() { ShowAlways = true, AutoPopDelay = 4500 };

    /// <summary>
    /// True while any card is mid-drag.
    ///
    /// <see cref="Control.DoDragDrop"/> runs a modal loop that keeps pumping
    /// messages, so a poll tick can land in the middle of a drag and rebuild the
    /// list — disposing the very control the OS is dragging. Hosts check this to
    /// defer the rebuild instead.
    /// </summary>
    public static bool DragActive { get; private set; }

    /// <summary>Paces drag auto-scroll (see <see cref="AutoScrollHost"/>).</summary>
    private static long s_lastAutoScrollMs;

    public AccountCardModel Model { get; private set; } = new();

    /// <summary>Autoswitch threshold % drawn as a tick on usage bars (0 = hide).</summary>
    public static double ThresholdMarkerPct { get; set; } = 90;

    [Browsable(false)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    public event EventHandler? CardActivated;
    /// <summary>A card was dropped onto this one — see <see cref="CardDrop"/>.</summary>
    public event EventHandler<CardDrop>? DragReorderRequested;
    /// <summary>A drag finished (dropped or cancelled); safe to rebuild again.</summary>
    public event EventHandler? DragSessionEnded;
    public event EventHandler? MoreMenuRequested;

    private static int s_nameH;
    private static int s_subH;
    private static int s_cardH;
    private static int s_labelW;
    private static int s_valueW;
    private static int s_meterW;
    private static bool s_metricsReady;

    private const int GripW = 18;
    private const int MoreW = 28;

    private const TextFormatFlags TfLeft =
        TextFormatFlags.Left
        | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPadding
        | TextFormatFlags.NoPrefix
        | TextFormatFlags.EndEllipsis
        | TextFormatFlags.SingleLine;

    private const TextFormatFlags TfRight =
        TextFormatFlags.Right
        | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPadding
        | TextFormatFlags.NoPrefix
        | TextFormatFlags.SingleLine;

    private const TextFormatFlags TfCenter =
        TextFormatFlags.HorizontalCenter
        | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPadding
        | TextFormatFlags.NoPrefix
        | TextFormatFlags.SingleLine;

    public AccountCard()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);
        EnsureMetrics();
        Height = s_cardH;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 0, 2);
        TabStop = true;
        AllowDrop = true;
        _tip.SetToolTip(this, Loc.T("card.tip.short"));
        Theme.Changed += (_, _) =>
        {
            s_metricsReady = false;
            EnsureMetrics();
            Height = s_cardH;
            Invalidate();
        };
    }

    /// <summary>
    /// Discards the cached measurements so the next card re-measures.
    /// </summary>
    /// <remarks>
    /// The meter column is sized from the widest label it will hold. Those
    /// labels are translated, so the cache computed under one language sizes
    /// the column wrong for the next: "5 小时" fitted where "5 hours" rendered
    /// as "5 ho…".
    /// </remarks>
    public static void InvalidateMetrics() => s_metricsReady = false;

    private static void EnsureMetrics()
    {
        if (s_metricsReady) return;

        // Generous line boxes so glyphs never clip under DPI / ClearType.
        s_nameH = Math.Max(20, Theme.FontName.Height + 4);
        s_subH = Math.Max(17, Theme.FontSmall.Height + 3);

        // Measure widest strings so meter column never ellipsizes labels/values.
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        s_labelW = Math.Max(
            52,
            TextRenderer.MeasureText(Loc.T("card.window.5h"), Theme.FontSmall, new Size(int.MaxValue, s_subH), flags).Width + 4);
        s_valueW = Math.Max(
            96,
            TextRenderer.MeasureText(Loc.T("usage.used", "100", "100"), Theme.FontSmall, new Size(int.MaxValue, s_subH), flags).Width + 6);
        // label + gap + bar area + value
        s_meterW = s_labelW + 8 + s_valueW + 8 + 72; // bar min ~72

        int identityH = s_nameH + s_subH + 2;
        int meterRow = s_subH + 8; // text line + bar + gap
        int meterH = meterRow * 2 + 2;
        s_cardH = Math.Max(86, Math.Max(identityH, meterH) + Theme.Space2 * 2);
        s_metricsReady = true;
    }

    public void Bind(AccountCardModel model)
    {
        Model = model;
        string title = DisplayTitle(model);
        string email = DisplayEmail(model.Email);
        string five = Theme.UsageLabelFull(model.FiveHour, model.UsageStatus);
        string seven = Theme.UsageLabelFull(model.SevenDay, model.UsageStatus);
        _tip.SetToolTip(this,
            $"{title}\n{email}\n{PlanTooltipLines(model)}"
            + Loc.T("card.tip.windows", five, seven)
            + (model.Active ? Loc.T("card.tip.inUse") : "")
            + Loc.T("card.tip.short"));
        Invalidate();
    }

    /// <summary>Plan lines for the card tooltip (each ends in \n; empty when unknown).</summary>
    private static string PlanTooltipLines(AccountCardModel m)
    {
        var sb = new System.Text.StringBuilder();
        if (m.HasPlanBadge)
        {
            sb.Append(Loc.T("card.tip.plan")).Append(m.PlanLabel);
            if (!string.IsNullOrWhiteSpace(m.BillingType))
                sb.Append(" · ").Append(Theme.BillingTypeLabel(m.BillingType));
            sb.Append('\n');
        }
        string? started = Theme.FormatDate(m.SubscriptionCreatedAt);
        if (started is not null)
            sb.Append(Loc.T("card.tip.subscribed")).Append(started).Append('\n');
        return sb.ToString();
    }

    public static string DisplayTitle(AccountCardModel m)
    {
        if (!string.IsNullOrWhiteSpace(m.Alias))
            return m.Alias!;
        var email = m.Email ?? "";
        int at = email.IndexOf('@');
        if (at > 0) return email[..at];
        return string.IsNullOrWhiteSpace(email) ? Loc.T("card.noAlias") : email;
    }

    public static string DisplayEmail(string email) =>
        UiPrefs.HideEmail ? Pii.MaskEmail(email) : email;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _moreHover = false;
        // Keep a pending drag armed while the button is still down: a quick
        // flick leaves the card's bounds before the drag threshold is crossed,
        // and disarming here made fast drags simply not start.
        if (MouseButtons != MouseButtons.Left)
            _dragArmed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    private int MoreLeft => Math.Max(0, Width - MoreW - 2);
    private int GripLeft => Math.Max(0, MoreLeft - GripW);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
            _dragArmed = e.X >= GripLeft && e.X < MoreLeft;
            Selected = true;

            if (e.X >= MoreLeft)
            {
                MoreMenuRequested?.Invoke(this, EventArgs.Empty);
                _dragArmed = false;
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            Selected = true;
            MoreMenuRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragArmed && e.Button == MouseButtons.Left)
        {
            // The OS drag threshold, not a hand-picked one: matching it makes
            // the grip feel like every other draggable thing on the desktop.
            var slop = SystemInformation.DragSize;
            if (Math.Abs(e.X - _dragStart.X) > slop.Width / 2
                || Math.Abs(e.Y - _dragStart.Y) > slop.Height / 2)
            {
                _dragArmed = false;
                BeginDragSession();
            }
        }

        bool wasMore = _moreHover;
        _moreHover = e.X >= MoreLeft;
        if (wasMore != _moreHover) Invalidate();

        // Assign only on change — every set of Cursor round-trips to the OS.
        var want = e.X >= GripLeft && e.X < MoreLeft ? Cursors.SizeAll : Cursors.Hand;
        if (Cursor != want) Cursor = want;
        base.OnMouseMove(e);
    }

    /// <summary>
    /// Run the modal drag loop, marking the session so the host defers rebuilds
    /// and this card can draw itself as the one in flight.
    /// </summary>
    private void BeginDragSession()
    {
        DragActive = true;
        _dragSource = true;
        Invalidate();
        try
        {
            DoDragDrop(this, DragDropEffects.Move);
        }
        finally
        {
            DragActive = false;
            _dragSource = false;
            _dropAfter = null;
            if (!IsDisposed) Invalidate();
            DragSessionEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragArmed = false;
        base.OnMouseUp(e);
    }

    private static bool CarriesCard(DragEventArgs e) =>
        e.Data?.GetDataPresent(typeof(AccountCard)) == true;

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        if (CarriesCard(drgevent))
            drgevent.Effect = DragDropEffects.Move;
        base.OnDragEnter(drgevent);
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        if (!CarriesCard(drgevent))
        {
            base.OnDragOver(drgevent);
            return;
        }
        drgevent.Effect = DragDropEffects.Move;

        // Which half the pointer is over decides above-vs-below. Without it a
        // drop had no expressible "after", so the last position was unreachable
        // and the landing spot was a guess.
        var local = PointToClient(new Point(drgevent.X, drgevent.Y));
        bool after = local.Y > Height / 2;
        bool? want = ReferenceEquals(drgevent.Data?.GetData(typeof(AccountCard)), this)
            ? null // no line on the card being dragged
            : after;
        if (_dropAfter != want)
        {
            _dropAfter = want;
            Invalidate();
        }

        AutoScrollHost(drgevent.Y);
        base.OnDragOver(drgevent);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        if (_dropAfter is not null)
        {
            _dropAfter = null;
            Invalidate();
        }
        base.OnDragLeave(e);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        bool after = _dropAfter ?? false;
        _dropAfter = null;
        Invalidate();
        if (drgevent.Data?.GetData(typeof(AccountCard)) is AccountCard source
            && !ReferenceEquals(source, this))
        {
            DragReorderRequested?.Invoke(this, new CardDrop(source, this, after));
        }
        base.OnDragDrop(drgevent);
    }

    /// <summary>
    /// Scroll the list when the pointer nears its top or bottom edge, so a card
    /// can be dragged past the visible window instead of stopping at it.
    /// </summary>
    private void AutoScrollHost(int screenY)
    {
        if (Parent is not ScrollableControl host || !host.VerticalScroll.Visible) return;
        int localY = host.PointToClient(new Point(0, screenY)).Y;
        const int Zone = 32;
        const int Step = 26;
        int delta = localY < Zone ? -Step
            : localY > host.ClientSize.Height - Zone ? Step
            : 0;
        if (delta == 0) return;

        // DragOver fires per mouse message; scrolling on every one sends the
        // list flying. Pace it so the speed is the same however fast the events
        // arrive.
        long now = Environment.TickCount64;
        if (now - s_lastAutoScrollMs < 60) return;
        s_lastAutoScrollMs = now;

        // AutoScrollPosition reads negative and writes positive, and clamps for us.
        host.AutoScrollPosition = new Point(
            -host.AutoScrollPosition.X,
            -host.AutoScrollPosition.Y + delta);
    }

    protected override void OnClick(EventArgs e)
    {
        Selected = true;
        base.OnClick(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        var pt = PointToClient(Cursor.Position);
        if (pt.X < GripLeft)
            CardActivated?.Invoke(this, EventArgs.Empty);
        base.OnDoubleClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            CardActivated?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10))
        {
            MoreMenuRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureMetrics();
        if (Height != s_cardH) Height = s_cardH;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.BgApp);

        var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));

        Color fill = Model.Disabled
            ? Theme.BgDisabled
            : _selected
                ? Theme.BgSelected
                : _hover
                    ? Theme.BgHover
                    : Theme.BgSurface;

        using (var path = RoundRect(bounds, Theme.CardRadius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        Color border = _selected ? Theme.SelectionBorder : Theme.BorderSoft;
        float borderW = _selected ? 2f : 1f;
        using (var path = RoundRect(bounds, Theme.CardRadius))
        using (var pen = new Pen(border, borderW))
            g.DrawPath(pen, path);

        // The card in flight reads as a placeholder for where it came from.
        if (_dragSource)
        {
            using var veil = new SolidBrush(Color.FromArgb(150, Theme.BgApp));
            using var path = RoundRect(bounds, Theme.CardRadius);
            g.FillPath(veil, path);
        }

        // Columns: identity | meters | grip | more
        int moreLeft = bounds.Right - MoreW;
        int gripLeft = moreLeft - GripW;
        int meterW = Math.Min(s_meterW, Math.Max(160, gripLeft - bounds.X - 200));
        // Prefer full measured meter width when space allows.
        if (gripLeft - Theme.Space3 - s_meterW > bounds.X + 160)
            meterW = s_meterW;
        int meterLeft = gripLeft - meterW - Theme.Space2;
        int identityRight = meterLeft - Theme.Space3;

        DrawGrip(g, gripLeft, bounds.Y, GripW, bounds.Height);
        DrawMore(g, moreLeft, bounds.Y, MoreW, bounds.Height, _moreHover);

        int padX = Theme.Space3;
        int padY = Theme.Space2;
        int left = bounds.X + padX;
        int top = bounds.Y + padY;

        int badgeSize = 22;
        int badgeY = top + Math.Max(0, (s_nameH + s_subH - badgeSize) / 2);
        var badge = new Rectangle(left, badgeY, badgeSize, badgeSize);
        using (var b = new SolidBrush(Model.Active ? Theme.Primary : Theme.BgRowAlt))
            g.FillEllipse(b, badge);
        using (var p = new Pen(Model.Active ? Theme.PrimaryDark : Theme.Border, 1f))
            g.DrawEllipse(p, badge);
        TextRenderer.DrawText(
            g,
            Model.Number.ToString(),
            Theme.FontBody,
            badge,
            Model.Active ? Theme.TextOnPrimary : Theme.TextSecondary,
            TfCenter);

        int textLeft = left + badgeSize + Theme.Space2;
        int textW = Math.Max(48, identityRight - textLeft);

        string status = Model.Disabled ? Loc.T("card.status.disabled")
            : Model.Active ? Loc.T("card.status.current") : Loc.T("card.status.ready");
        Color statusBg = Model.Disabled
            ? Theme.BgDisabled
            : Model.Active
                ? Theme.PrimarySoft
                : Theme.BgRowAlt;
        Color statusFg = Model.Disabled
            ? Theme.TextMuted
            : Model.Active
                ? Theme.PrimaryDark
                : Theme.TextSecondary;
        int pillW = PillWidth(status);
        int pillH = Math.Min(s_nameH - 2, Math.Max(16, Theme.FontSmall.Height + 4));

        // Plan badge sits after the status pill, in neutral colors so it never
        // competes with the green "当前". Dropped first when the row is tight —
        // identity and state matter more than the tier.
        string plan = Model.HasPlanBadge ? Model.PlanLabel! : "";
        int planW = plan.Length == 0 ? 0 : PillWidth(plan);
        if (planW > 0 && textW - pillW - planW - Theme.Space1 * 2 < MinNameW)
            planW = 0;

        int nameW = Math.Max(
            40,
            textW - pillW - Theme.Space2 - (planW > 0 ? planW + Theme.Space1 : 0));

        string title = DisplayTitle(Model);
        string emailLine = BuildSubLine(Model, textW);

        TextRenderer.DrawText(
            g,
            title,
            Theme.FontName,
            new Rectangle(textLeft, top, nameW, s_nameH),
            Model.Disabled ? Theme.TextMuted : Theme.TextPrimary,
            TfLeft);

        int pillY = top + Math.Max(0, (s_nameH - pillH) / 2);
        int pillX = textLeft + nameW + Theme.Space1;
        DrawPill(g, new Rectangle(pillX, pillY, pillW, pillH), status, statusBg, statusFg);

        if (planW > 0)
        {
            DrawPill(
                g,
                new Rectangle(pillX + pillW + Theme.Space1, pillY, planW, pillH),
                plan,
                Model.Disabled ? Theme.BgDisabled : Theme.BgRowAlt,
                Model.Disabled ? Theme.TextMuted : Theme.TextSecondary);
        }

        TextRenderer.DrawText(
            g,
            emailLine,
            Theme.FontSmall,
            new Rectangle(textLeft, top + s_nameH, textW, s_subH),
            Theme.TextSecondary,
            TfLeft);

        // Usage meters — full labels, no ellipsis
        int meterRowH = s_subH + 8;
        int meterBlockH = meterRowH * 2 + 2;
        int meterTop = bounds.Y + Math.Max(padY, (bounds.Height - meterBlockH) / 2);
        if (meterLeft > left && meterW > 80)
        {
            DrawMeter(g, meterLeft, meterTop, meterW, Loc.T("card.window.5h"), Model.FiveHour, Model.UsageStatus);
            DrawMeter(
                g, meterLeft, meterTop + meterRowH, meterW, Loc.T("card.window.7d"), Model.SevenDay, Model.UsageStatus);
        }

        DrawDropIndicator(g, bounds);
    }

    /// <summary>
    /// Insertion line showing exactly where the drop will land. Without it the
    /// drag is blind — the user only learns the outcome after releasing.
    /// </summary>
    private void DrawDropIndicator(Graphics g, Rectangle bounds)
    {
        if (_dropAfter is { } after)
            DrawDropIndicator(g, bounds, after);
    }

    /// <summary>Pure drawing half — no control state, so it renders anywhere.</summary>
    internal static void DrawDropIndicator(Graphics g, Rectangle bounds, bool after)
    {
        int y = after ? bounds.Bottom - 1 : bounds.Y + 1;
        using var pen = new Pen(Theme.Primary, 3f);
        g.DrawLine(pen, bounds.X + Theme.Space2, y, bounds.Right - Theme.Space2, y);
        // End caps make the line read as an insertion point, not a border.
        using var cap = new SolidBrush(Theme.Primary);
        g.FillEllipse(cap, bounds.X + Theme.Space2 - 3, y - 3, 6, 6);
        g.FillEllipse(cap, bounds.Right - Theme.Space2 - 3, y - 3, 6, 6);
    }

    private const int MinNameW = 56;

    private static readonly TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private static int PillWidth(string text) =>
        TextRenderer.MeasureText(text, Theme.FontSmall, new Size(int.MaxValue, s_nameH), MeasureFlags)
            .Width + 12;

    private static void DrawPill(Graphics g, Rectangle pill, string text, Color bg, Color fg)
    {
        if (pill.Width <= 0 || pill.Height <= 0) return;
        using (var path = RoundRect(pill, Math.Max(2, pill.Height / 2)))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, text, Theme.FontSmall, pill, fg, TfCenter);
    }

    /// <summary>
    /// Sub-line under the title: email, plus the subscription start date when it
    /// fits. Appended only if the whole line fits — ellipsizing here would eat
    /// the email, and a truncated address is worse than no date.
    /// </summary>
    public static string BuildSubLine(AccountCardModel m, int availWidth)
    {
        EnsureMetrics();
        string email = DisplayEmail(m.Email ?? "");
        string? started = Theme.FormatShortDate(m.SubscriptionCreatedAt);
        if (started is null) return email;

        string full = Loc.T("card.subLine", email, started);
        int w = TextRenderer
            .MeasureText(full, Theme.FontSmall, new Size(int.MaxValue, s_subH), MeasureFlags)
            .Width;
        return w <= availWidth ? full : email;
    }

    private static void DrawMeter(
        Graphics g, int x, int y, int width, string window, double? pct, string? status)
    {
        EnsureMetrics();
        int labelW = s_labelW;
        int valueW = s_valueW;
        int rowH = s_subH;
        // Keep bar usable even on narrow cards.
        if (width < labelW + valueW + 40)
        {
            valueW = Math.Max(72, width / 3);
            labelW = Math.Max(40, Math.Min(labelW, width / 4));
        }

        TextRenderer.DrawText(
            g,
            window,
            Theme.FontSmall,
            new Rectangle(x, y, labelW, rowH),
            Theme.TextMuted,
            TfLeft);

        string value = Theme.UsageLabelCompact(pct, status);
        // A reason the user must act on reads as a warning, not as muted "no data".
        Color vc = pct is null && status is "needs-login" or "no-credential" or "no-subscription"
            ? Theme.Warning
            : Theme.UsageColor(pct);
        TextRenderer.DrawText(
            g,
            value,
            Theme.FontSmall,
            new Rectangle(x + width - valueW, y, valueW, rowH),
            vc,
            TfRight);

        int barX = x + labelW + 6;
        int barW = Math.Max(24, width - labelW - valueW - 12);
        int barY = y + rowH + 1;
        int barH = 5;
        if (barW < 8 || barH < 1) return;

        var barRect = new Rectangle(barX, barY, barW, barH);
        using (var path = RoundRect(barRect, 2))
        using (var bg = new SolidBrush(Theme.BorderSoft))
            g.FillPath(bg, path);

        if (pct is not null and > 0)
        {
            int fillW = Math.Max(3, (int)(barW * Math.Clamp(pct.Value / 100.0, 0, 1)));
            using var path = RoundRect(new Rectangle(barX, barY, fillW, barH), 2);
            using var fg = new SolidBrush(vc);
            g.FillPath(fg, path);
        }

        double thr = ThresholdMarkerPct;
        if (thr is > 0 and < 100)
        {
            int tx = barX + (int)(barW * thr / 100.0);
            using var pen = new Pen(Theme.TextMuted, 1f);
            g.DrawLine(pen, tx, barY - 1, tx, barY + barH + 1);
        }
    }

    private static void DrawGrip(Graphics g, int left, int top, int w, int h)
    {
        using var brush = new SolidBrush(Theme.TextMuted);
        int cx = left + w / 2 - 4;
        int cy = top + h / 2 - 7;
        for (int row = 0; row < 3; row++)
        {
            g.FillEllipse(brush, cx, cy + row * 6, 2, 2);
            g.FillEllipse(brush, cx + 6, cy + row * 6, 2, 2);
        }
    }

    private static void DrawMore(Graphics g, int left, int top, int w, int h, bool hover)
    {
        if (hover)
        {
            var bg = new Rectangle(left + 2, top + h / 2 - 12, Math.Max(4, w - 4), 24);
            using var path = RoundRect(bg, 4);
            using var b = new SolidBrush(Theme.BgHover);
            g.FillPath(b, path);
        }

        using var brush = new SolidBrush(Theme.TextSecondary);
        int cx = left + w / 2 - 1;
        int cy = top + h / 2 - 8;
        for (int i = 0; i < 3; i++)
            g.FillEllipse(brush, cx, cy + i * 7, 3, 3);
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        // Guard against zero/negative rects (can throw in AddArc).
        if (r.Width < 2 || r.Height < 2)
        {
            var empty = new GraphicsPath();
            empty.AddRectangle(new Rectangle(
                r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)));
            return empty;
        }
        int d = Math.Min(Math.Max(2, radius * 2), Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
