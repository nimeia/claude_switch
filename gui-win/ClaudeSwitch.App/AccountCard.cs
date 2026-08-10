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
    /// <summary>
    /// Terminals running this account in session mode.
    /// </summary>
    /// <remarks>
    /// A session terminal is invisible once opened — the user can easily forget
    /// three of them are running. The count is drawn on the card so the state is
    /// discoverable, and so "this account has a live session" is already on
    /// screen when a delete or an auto-switch is refused for that reason.
    /// </remarks>
    public int LiveSessions { get; init; }
    public double? FiveHour { get; init; }
    public double? SevenDay { get; init; }
    /// <summary>ISO timestamp when the 5h window resets (if known).</summary>
    public string? FiveHourResetsAt { get; init; }
    /// <summary>ISO timestamp when the 7d window resets (if known).</summary>
    public string? SevenDayResetsAt { get; init; }
    /// <summary>
    /// Next local time the window guardian would open the 5h bucket (when
    /// warmup is on and the window is not already live).
    /// </summary>
    public string? WarmupAnchor { get; init; }

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
    /// The account cannot report usage until the user does something about it.
    /// </summary>
    /// <remarks>
    /// One card-level state, not a per-window one: the credential is broken for
    /// the whole account, so repeating the reason on the 5h row and again on the
    /// 7d row read as two independent quota faults.
    /// </remarks>
    public bool NeedsAttention =>
        UsageStatus is "needs-login" or "no-credential" or "no-subscription";

    /// <summary>Numbers are simply not in yet — not an error, not a final answer.</summary>
    public bool UsageUnknownYet =>
        FiveHour is null && SevenDay is null && UsageStatus is null or "ok" or "unknown";

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
/// One account, drawn as five aligned columns:
/// <c>identity │ state │ 5h │ 7d │ actions</c>. Every card computes the same
/// column geometry from the same measurements, so the numbers line up down the
/// list instead of drifting with the length of each name.
/// </summary>
/// <remarks>
/// Colour roles are fixed and do not overlap: green says <em>this account is the
/// one in use</em>, slate/blue says <em>this row has the keyboard focus</em>,
/// amber says <em>this account needs you</em>. A card can be all three at once,
/// which is exactly why each gets its own channel — fill, border and badge —
/// rather than all three fighting over the background.
/// </remarks>
internal sealed class AccountCard : Control
{
    private bool _hover;
    private bool _selected;
    private bool _moreHover;
    private bool _fixHover;
    private Point _dragStart;
    private bool _dragArmed;
    /// <summary>This card is the one being dragged (drawn faded).</summary>
    private bool _dragSource;
    /// <summary>Insertion line to draw, or null when the pointer is elsewhere.</summary>
    private bool? _dropAfter;
    /// <summary>Where the "sign in again" button was last painted (empty when absent).</summary>
    private Rectangle _fixRect;
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
    /// <summary>The user pressed the card's "sign in again" button.</summary>
    public event EventHandler? FixRequested;

    private static int s_row1H;
    private static int s_row2H;
    private static int s_cardH;
    private static int s_statusW;
    private static int s_meterValueW;
    private static int s_meterLabelW;
    private static bool s_metricsReady;
    /// <summary>Display DPI the cached metrics were measured at.</summary>
    private static int s_dpi = 96;

    /// <summary>Design pixels → device pixels for the display the cards are on.</summary>
    private static int Sc(int designPx) => (int)Math.Round(designPx * s_dpi / 96.0);

    // ── Column geometry (96-DPI design px; run through Sc for the display) ──
    //
    // Every length here is a design pixel. The fonts scale themselves with the
    // display, so a raw constant is a column that keeps its 96-DPI width while
    // the text inside it grows — which is how "alice@exa…" ended up ellipsised
    // on a 125% screen at a width where it fits comfortably at 100%.
    private const int PadX = 14;
    private const int PadY = 9;
    private const int AvatarD = 30;
    private const int GapAvatar = 10;
    private const int GapCol = 14;
    private const int IdentityMinW = 150;
    private const int MeterBarW = 120;
    private const int MeterBarMinW = 72;
    private const int GripW = 22;
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
        SyncDpi();
        Height = s_cardH;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 0, 4);
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
    /// The state and meter columns are sized from the widest label they will
    /// hold. Those labels are translated, so the cache computed under one
    /// language sizes the column wrong for the next: "5 小时" fitted where
    /// "5 hours" rendered as "5 ho…".
    /// </remarks>
    public static void InvalidateMetrics() => s_metricsReady = false;

    /// <summary>
    /// Re-measure when this card lands on a display with a different scaling.
    /// </summary>
    /// <remarks>
    /// The measurements are cached statically because every card shares them,
    /// which is only sound while every card is on the same display. Checking the
    /// DPI here is what keeps that true after a drag to a second monitor.
    /// </remarks>
    private void SyncDpi()
    {
        if (s_dpi != DeviceDpi)
        {
            s_dpi = DeviceDpi;
            s_metricsReady = false;
        }
        EnsureMetrics();
    }

    private static readonly TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private static int TextW(string s, Font f) =>
        TextRenderer.MeasureText(s, f, new Size(int.MaxValue, 200), MeasureFlags).Width;

    private static void EnsureMetrics()
    {
        if (s_metricsReady) return;

        // Generous line boxes so glyphs never clip under DPI / ClearType.
        s_row1H = Math.Max(20, Theme.FontName.Height + 4);
        s_row2H = Math.Max(17, Theme.FontSmall.Height + 3);

        // The state column holds one badge above one badge, and must fit the
        // longest thing either can say — including the attention badge, which is
        // the widest and the one that must never be the one that gets clipped.
        int widestState = 0;
        foreach (var key in new[]
                 {
                     "card.status.current", "card.status.ready", "card.status.disabled",
                     "usage.needsLogin", "usage.noSubscription", "usage.noCredential",
                 })
            widestState = Math.Max(widestState, TextW(Loc.T(key), Theme.FontSmall));
        widestState = Math.Max(widestState, TextW(Loc.T("card.relogin"), Theme.FontSmall) + Sc(8));
        s_statusW = Math.Clamp(widestState + Sc(PillPadX) * 2, Sc(64), Sc(160));

        s_meterLabelW = Math.Max(
            TextW(Loc.T("card.window.5h"), Theme.FontSmall),
            TextW(Loc.T("card.window.7d"), Theme.FontSmall)) + Sc(4);
        s_meterValueW = Math.Max(
            Sc(52), TextW(Loc.T("usage.remain", "100"), Theme.FontSmall) + Sc(4));

        s_cardH = Math.Max(Sc(62), s_row1H + Sc(3) + s_row2H + Sc(PadY) * 2);
        s_metricsReady = true;
    }

    public void Bind(AccountCardModel model)
    {
        Model = model;
        string title = DisplayTitle(model);
        string email = DisplayEmail(model.Email);
        string five = Theme.UsageLabelFull(model.FiveHour, model.UsageStatus);
        string seven = Theme.UsageLabelFull(model.SevenDay, model.UsageStatus);
        string warmTip = "";
        if (Theme.FormatWarmupAnchor(model.WarmupAnchor) is { } warm)
            warmTip = Loc.T("card.tip.warmup", warm);
        _tip.SetToolTip(this,
            $"{title}\n{Loc.T("card.slot", model.Number)} · {email}\n{PlanTooltipLines(model)}"
            + Loc.T("card.tip.windows", five, seven)
            + warmTip
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

    /// <summary>Single glyph for the avatar disc — the account's own initial.</summary>
    private static string Initial(AccountCardModel m)
    {
        string t = DisplayTitle(m).TrimStart();
        return t.Length == 0 ? "?" : t[..1].ToUpperInvariant();
    }

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
        _fixHover = false;
        // Keep a pending drag armed while the button is still down: a quick
        // flick leaves the card's bounds before the drag threshold is crossed,
        // and disarming here made fast drags simply not start.
        if (MouseButtons != MouseButtons.Left)
            _dragArmed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    private int MoreLeft => Math.Max(0, Width - Sc(MoreW) - Sc(4));
    private int GripLeft => Math.Max(0, MoreLeft - Sc(GripW) - Sc(Theme.Space2));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
            _dragArmed = e.X >= GripLeft && e.X < MoreLeft;
            Selected = true;

            if (!_fixRect.IsEmpty && _fixRect.Contains(e.Location))
            {
                FixRequested?.Invoke(this, EventArgs.Empty);
                _dragArmed = false;
            }
            else if (e.X >= MoreLeft)
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
        bool wasFix = _fixHover;
        _moreHover = e.X >= MoreLeft;
        _fixHover = !_fixRect.IsEmpty && _fixRect.Contains(e.Location);
        if (wasMore != _moreHover || wasFix != _fixHover) Invalidate();

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
        if (pt.X < GripLeft && !_fixRect.Contains(pt))
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

    /// <summary>Resolved column geometry for one paint.</summary>
    private readonly record struct Columns(
        int IdentityLeft, int IdentityW,
        int StatusLeft, int StatusW,
        int Meter5Left, int Meter7Left, int MeterW, int BarW,
        int GripLeft, int MoreLeft);

    /// <summary>
    /// Fit the five columns into <paramref name="bounds"/>, giving width back in
    /// the order the user can most afford to lose it: the bars narrow first,
    /// then the state column folds into the identity row. Identity never drops
    /// below <see cref="IdentityMinW"/> — a card whose name is unreadable
    /// identifies nothing.
    /// </summary>
    private static Columns Resolve(Rectangle bounds)
    {
        int gapCol = Sc(GapCol);
        int gapVal = Sc(Theme.Space2);
        int minBarW = Sc(MeterBarMinW);
        int minIdentityW = Sc(IdentityMinW);

        int moreLeft = bounds.Right - Sc(MoreW) - Sc(4);
        int gripLeft = moreLeft - Sc(GripW) - Sc(Theme.Space2);
        int opsLeft = gripLeft - Sc(Theme.Space2);
        int identityLeft = bounds.X + Sc(PadX) + Sc(AvatarD) + Sc(GapAvatar);

        int barW = Sc(MeterBarW);
        int statusW = s_statusW;
        for (; ; )
        {
            int meterW = s_meterValueW + gapVal + barW;
            int meter7Left = opsLeft - meterW;
            int meter5Left = meter7Left - gapCol - meterW;
            int statusLeft = meter5Left - gapCol - statusW;
            int identityW = (statusW > 0 ? statusLeft : meter5Left) - gapCol - identityLeft;

            if (identityW >= minIdentityW || (barW <= minBarW && statusW == 0))
            {
                return new Columns(
                    identityLeft, Math.Max(Sc(40), identityW),
                    statusLeft, statusW,
                    meter5Left, meter7Left, meterW, barW,
                    gripLeft, moreLeft);
            }

            if (barW > minBarW) barW = Math.Max(minBarW, barW - Sc(12));
            else statusW = 0;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SyncDpi();
        if (Height != s_cardH) Height = s_cardH;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Theme.BgApp);

        var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        var m = Model;

        // Fill says "selected" (slate) or "in use" (green tint) — never both, so
        // the two never have to be told apart by shade.
        Color fill = m.Disabled ? Theme.BgDisabled
            : _selected ? Theme.BgSelected
            : m.Active ? Theme.PrimarySoft
            : _hover ? Theme.BgHover
            : Theme.BgSurface;

        using (var path = RoundRect(bounds, Theme.CardRadius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        // The in-use account keeps a green edge even while selected: the accent
        // and the border are different channels, so "current" survives focus.
        if (m.Active && !m.Disabled)
            DrawAccentEdge(g, bounds);

        Color border = _selected ? Theme.SelectionBorder
            : m.NeedsAttention ? Theme.Warning
            : Theme.BorderSoft;
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

        var col = Resolve(bounds);
        int row1Y = bounds.Y + Sc(PadY);
        int row2Y = row1Y + s_row1H + Sc(3);

        int avatarD = Sc(AvatarD);
        DrawAvatar(
            g,
            new Rectangle(
                bounds.X + Sc(PadX), bounds.Y + (bounds.Height - avatarD) / 2, avatarD, avatarD),
            m);
        // The grip only appears when the row is under the pointer or has focus:
        // a permanent one on every card reads as clutter, and it competes with
        // the ⋮ it sits beside.
        if (_hover || _selected)
            DrawGrip(g, col.GripLeft, bounds.Y, Sc(GripW), bounds.Height);
        DrawMore(g, col.MoreLeft, bounds.Y, Sc(MoreW), bounds.Height, _moreHover);

        // When the row is too narrow for a state column, the badge moves into
        // the identity line rather than disappearing. Dropping it would take the
        // amber "needs you" with it — the one thing on the card that must never
        // be the casualty of a narrow window.
        DrawIdentity(g, col, row1Y, row2Y, m, inlineState: col.StatusW == 0);
        _fixRect = col.StatusW > 0
            ? DrawStatusColumn(g, col, row1Y, row2Y, m)
            : Rectangle.Empty;
        DrawMeters(g, col, row1Y, row2Y, m);

        DrawDropIndicator(g, bounds);
    }

    /// <summary>A 3px green bar down the left edge: "this is the live login".</summary>
    private static void DrawAccentEdge(Graphics g, Rectangle bounds)
    {
        var edge = new Rectangle(
            bounds.X, bounds.Y + Sc(6), Sc(3), Math.Max(2, bounds.Height - Sc(12)));
        using var path = RoundRect(edge, Sc(2));
        using var b = new SolidBrush(Theme.Primary);
        g.FillPath(b, path);
    }

    private static void DrawAvatar(Graphics g, Rectangle disc, AccountCardModel m)
    {
        // An initial, not a rank. The slot number lives in the sub-line, where
        // it reads as the identifier it is instead of a position in a list the
        // user can freely reorder.
        Color bg = m.Disabled ? Theme.BgDisabled : m.Active ? Theme.Primary : Theme.BgRowAlt;
        Color fg = m.Disabled ? Theme.TextMuted : m.Active ? Theme.TextOnPrimary : Theme.TextSecondary;
        using (var b = new SolidBrush(bg))
            g.FillEllipse(b, disc);
        using (var p = new Pen(m.Active ? Theme.PrimaryDark : Theme.Border, 1f))
            g.DrawEllipse(p, disc);
        TextRenderer.DrawText(g, Initial(m), Theme.FontName, disc, fg, TfCenter);
    }

    private static void DrawIdentity(
        Graphics g, Columns col, int row1Y, int row2Y, AccountCardModel m, bool inlineState)
    {
        int w = col.IdentityW;
        int gap = Sc(Theme.Space2);

        // Live terminals ride with the name: they belong to this account's
        // identity right now, and they are the one thing here that can change
        // between two polls.
        string sessions = m.LiveSessions > 0 ? Loc.Plural("card.terminals", m.LiveSessions) : "";
        int sessW = sessions.Length == 0 ? 0 : PillWidth(sessions);
        if (sessW > 0 && w - sessW - gap < Sc(60)) sessW = 0;

        var state = StateBadge(m);
        int stateW = inlineState ? PillWidth(state.Text) : 0;
        if (stateW > 0 && w - stateW - sessW - gap * 2 < Sc(50)) sessW = 0;

        int nameW = Math.Max(
            Sc(40),
            w - (sessW > 0 ? sessW + gap : 0) - (stateW > 0 ? stateW + gap : 0));
        TextRenderer.DrawText(
            g, DisplayTitle(m), Theme.FontName,
            new Rectangle(col.IdentityLeft, row1Y, nameW, s_row1H),
            m.Disabled ? Theme.TextMuted : Theme.TextPrimary,
            TfLeft);

        int pillH = PillHeight();
        int pillY = row1Y + Math.Max(0, (s_row1H - pillH) / 2);
        int pillX = col.IdentityLeft + nameW + gap;
        if (sessW > 0)
        {
            DrawPill(
                g, new Rectangle(pillX, pillY, sessW, pillH),
                sessions, Theme.BgActive, Theme.Accent);
            pillX += sessW + gap;
        }
        if (stateW > 0)
            DrawPill(g, new Rectangle(pillX, pillY, stateW, pillH), state.Text, state.Bg, state.Fg);

        TextRenderer.DrawText(
            g, BuildSubLine(m, w), Theme.FontSmall,
            new Rectangle(col.IdentityLeft, row2Y, w, s_row2H),
            m.Disabled ? Theme.TextDisabled : Theme.TextSecondary,
            TfLeft);
    }

    /// <summary>
    /// The one badge that says what this account is doing.
    /// </summary>
    /// <remarks>
    /// Attention outranks "in use": an account that cannot report its quota is
    /// the more urgent fact about it, even when it happens to be the one Claude
    /// Code is logged into. One state, one colour — the badge never has to be
    /// read together with a second one to be understood.
    /// </remarks>
    internal static (string Text, Color Bg, Color Fg) StateBadge(AccountCardModel m) =>
        m.NeedsAttention
            ? (Theme.UsageStatusShort(m.UsageStatus), Theme.WarningSoft, Theme.Warning)
        : m.Disabled ? (Loc.T("card.status.disabled"), Theme.BgDisabled, Theme.TextMuted)
        : m.Active ? (Loc.T("card.status.current"), Theme.PrimarySoft, Theme.PrimaryDark)
        : (Loc.T("card.status.ready"), Theme.BgRowAlt, Theme.TextSecondary);

    /// <summary>
    /// State above, plan below. Returns the "sign in again" hit box, or an empty
    /// rectangle when the account has nothing to fix.
    /// </summary>
    private Rectangle DrawStatusColumn(
        Graphics g, Columns col, int row1Y, int row2Y, AccountCardModel m)
    {
        int pillH = PillHeight();
        int pillY = row1Y + Math.Max(0, (s_row1H - pillH) / 2);
        var state = StateBadge(m);

        DrawPill(
            g, new Rectangle(col.StatusLeft, pillY, col.StatusW, pillH),
            state.Text, state.Bg, state.Fg);

        if (m.NeedsAttention)
        {
            var btn = new Rectangle(col.StatusLeft, row2Y, col.StatusW, s_row2H + Sc(2));
            DrawFixButton(g, btn, _fixHover);
            return btn;
        }

        if (m.HasPlanBadge)
        {
            // Neutral on purpose: a tier that never changes must not look like
            // live state sitting right above it.
            int planW = Math.Min(col.StatusW, PillWidth(m.PlanLabel!));
            DrawPill(
                g, new Rectangle(col.StatusLeft, row2Y, planW, s_row2H),
                m.PlanLabel!,
                m.Disabled ? Theme.BgDisabled : Theme.BgRowAlt,
                m.Disabled ? Theme.TextMuted : Theme.TextMuted);
        }
        return Rectangle.Empty;
    }

    private static void DrawFixButton(Graphics g, Rectangle r, bool hover)
    {
        if (r.Width < Sc(20) || r.Height < Sc(8)) return;
        using (var path = RoundRect(r, Theme.ControlRadius))
        using (var b = new SolidBrush(hover ? Theme.WarningSoft : Theme.BgSurface))
            g.FillPath(b, path);
        using (var path = RoundRect(r, Theme.ControlRadius))
        using (var p = new Pen(Theme.Warning, 1f))
            g.DrawPath(p, path);
        TextRenderer.DrawText(g, Loc.T("card.relogin"), Theme.FontSmall, r, Theme.Warning, TfCenter);
    }

    private static void DrawMeters(
        Graphics g, Columns col, int row1Y, int row2Y, AccountCardModel m)
    {
        if (col.MeterW < Sc(60)) return;
        DrawMeter(g, col.Meter5Left, row1Y, row2Y, col, Loc.T("card.window.5h"), m.FiveHour, m);
        DrawMeter(g, col.Meter7Left, row1Y, row2Y, col, Loc.T("card.window.7d"), m.SevenDay, m);
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
        int inset = Sc(Theme.Space2);
        int capD = Sc(6);
        using var pen = new Pen(Theme.Primary, Sc(3));
        g.DrawLine(pen, bounds.X + inset, y, bounds.Right - inset, y);
        // End caps make the line read as an insertion point, not a border.
        using var cap = new SolidBrush(Theme.Primary);
        g.FillEllipse(cap, bounds.X + inset - capD / 2, y - capD / 2, capD, capD);
        g.FillEllipse(cap, bounds.Right - inset - capD / 2, y - capD / 2, capD, capD);
    }

    private const int PillPadX = 7;

    private static int PillWidth(string text) =>
        TextW(text, Theme.FontSmall) + Sc(PillPadX) * 2;

    /// <summary>Badge height — one value, so every badge on the card matches.</summary>
    private static int PillHeight() =>
        Math.Min(s_row1H - Sc(2), Math.Max(Sc(16), Theme.FontSmall.Height + Sc(4)));

    private static void DrawPill(Graphics g, Rectangle pill, string text, Color bg, Color fg)
    {
        if (pill.Width <= 0 || pill.Height <= 0) return;
        using (var path = RoundRect(pill, Math.Max(2, pill.Height / 2)))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, text, Theme.FontSmall, pill, fg, TfCenter);
    }

    /// <summary>
    /// Sub-line under the title: the slot number, the email, and the
    /// subscription start when it fits. The number lives here rather than in a
    /// badge because it identifies the slot — it is not a rank, and the list
    /// order beside it is the user's own.
    /// </summary>
    public static string BuildSubLine(AccountCardModel m, int availWidth)
    {
        EnsureMetrics();
        string head = Loc.T("card.slot", m.Number);
        string email = DisplayEmail(m.Email ?? "");
        string baseLine = $"{head} · {email}";
        string? started = Theme.FormatShortDate(m.SubscriptionCreatedAt);
        if (started is null) return baseLine;

        string full = Loc.T("card.subLine", baseLine, started);
        return TextW(full, Theme.FontSmall) <= availWidth ? full : baseLine;
    }

    /// <summary>
    /// One quota window: label on the upper line, bar and remaining figure on
    /// the lower one. The figure sits against the bar it belongs to, and says
    /// only what is left — the bar already shows what is spent, and printing
    /// both numbers made the pair read as two different measurements.
    /// </summary>
    private static void DrawMeter(
        Graphics g, int x, int row1Y, int row2Y, Columns col,
        string window, double? pct, AccountCardModel m)
    {
        int w = col.MeterW;
        TextRenderer.DrawText(
            g, window, Theme.FontSmall,
            new Rectangle(x, row1Y, Math.Min(w, s_meterLabelW + Sc(40)), s_row1H),
            Theme.TextMuted, TfLeft);

        int barW = col.BarW;
        int barH = Sc(6);
        int gap = Sc(Theme.Space2);
        int barY = row2Y + Math.Max(0, (s_row2H - barH) / 2);
        var barRect = new Rectangle(x, barY, barW, barH);
        var valueRect = new Rectangle(x + barW + gap, row2Y, w - barW - gap, s_row2H);

        // Nothing to say yet: a skeleton of the shape that is coming, in place,
        // rather than a spinner somewhere else on the window.
        if (m.UsageUnknownYet)
        {
            using (var path = RoundRect(barRect, barH / 2))
            using (var b = new SolidBrush(Theme.BorderSoft))
                g.FillPath(b, path);
            int ghostW = Math.Min(valueRect.Width, Sc(34));
            var ghost = new Rectangle(valueRect.Right - ghostW, barY, ghostW, barH);
            using (var path = RoundRect(ghost, barH / 2))
            using (var b = new SolidBrush(Theme.BorderSoft))
                g.FillPath(b, path);
            return;
        }

        // A broken credential is stated once, on the card. Here the cell just
        // says it has no number, so two windows do not read as two faults.
        if (pct is null)
        {
            TextRenderer.DrawText(
                g, "—", Theme.FontSmall,
                new Rectangle(x, row2Y, w, s_row2H),
                Theme.TextMuted, TfLeft);
            return;
        }

        Color vc = Theme.UsageColor(pct);
        using (var path = RoundRect(barRect, barH / 2))
        using (var bg = new SolidBrush(Theme.BorderSoft))
            g.FillPath(bg, path);

        if (pct > 0)
        {
            int fillW = Math.Max(Sc(3), (int)(barW * Math.Clamp(pct.Value / 100.0, 0, 1)));
            using var path = RoundRect(new Rectangle(x, barY, fillW, barH), barH / 2);
            using var fg = new SolidBrush(vc);
            g.FillPath(fg, path);
        }

        double thr = ThresholdMarkerPct;
        if (thr is > 0 and < 100)
        {
            int tx = x + (int)(barW * thr / 100.0);
            using var pen = new Pen(Theme.TextMuted, 1f);
            g.DrawLine(pen, tx, barY - Sc(2), tx, barY + barH + Sc(2));
        }

        TextRenderer.DrawText(
            g, Theme.UsageLabelRemain(pct), Theme.FontSmall, valueRect,
            m.Disabled ? Theme.TextMuted : vc, TfLeft);
    }

    /// <summary>Standard six-dot drag handle, clearly apart from the ⋮ menu.</summary>
    private static void DrawGrip(Graphics g, int left, int top, int w, int h)
    {
        int dot = Sc(3);
        int pitch = Sc(6);
        using var brush = new SolidBrush(Theme.TextMuted);
        int cx = left + w / 2 - pitch / 2 - dot / 2;
        int cy = top + h / 2 - pitch - dot / 2;
        for (int row = 0; row < 3; row++)
        {
            g.FillEllipse(brush, cx, cy + row * pitch, dot, dot);
            g.FillEllipse(brush, cx + pitch, cy + row * pitch, dot, dot);
        }
    }

    private static void DrawMore(Graphics g, int left, int top, int w, int h, bool hover)
    {
        if (hover)
        {
            int hh = Sc(24);
            var bg = new Rectangle(left + Sc(2), top + (h - hh) / 2, Math.Max(4, w - Sc(4)), hh);
            using var path = RoundRect(bg, Sc(4));
            using var b = new SolidBrush(Theme.BgHover);
            g.FillPath(b, path);
        }

        int dot = Sc(3);
        int pitch = Sc(7);
        using var brush = new SolidBrush(Theme.TextSecondary);
        int cx = left + w / 2 - dot / 2;
        int cy = top + h / 2 - pitch - dot / 2;
        for (int i = 0; i < 3; i++)
            g.FillEllipse(brush, cx, cy + i * pitch, dot, dot);
    }

    /// <summary>Local name for the shared shape; this file draws a dozen of them.</summary>
    private static GraphicsPath RoundRect(Rectangle r, int radius) => Shapes.Rounded(r, radius);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
