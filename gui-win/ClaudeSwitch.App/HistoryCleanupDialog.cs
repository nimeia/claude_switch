using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// Batch cleanup by condition, and Claude Code's own retention setting.
/// </summary>
/// <remarks>
/// One plan is taken with every rule switched on, so each row can say what it
/// would select before it is ticked; the ticked rows are then combined here. A
/// session two rows both select counts once in the total, which is why the rows'
/// figures can add up to more than the preview.
/// </remarks>
internal sealed class HistoryCleanupDialog : Form
{
    private sealed record RuleRow(string Reason, ThemedCheckBox Check, Label Tally);

    private readonly Engine _engine;
    private readonly List<RuleRow> _rules = [];
    private readonly ThemedNumericUpDown _olderDays;
    private readonly ThemedNumericUpDown _largerMb;
    private readonly Label _preview;
    private readonly Label _note;
    private readonly ThemedNumericUpDown _retentionDays;
    private readonly SecondaryButton _saveRetention;
    private readonly Label _retentionStatus;
    private readonly DangerButton _delete;
    private readonly System.Windows.Forms.Timer _replan = new() { Interval = 300 };
    private CleanupPlanView _plan = CleanupPlanView.Empty;
    private RetentionView? _retention;

    /// <summary>Whether anything was deleted, so the caller knows to list again.</summary>
    public bool Changed { get; private set; }

    public HistoryCleanupDialog(Engine engine)
    {
        _engine = engine;
        Text = Loc.T("history.cleanup.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        _retention = LoadRetention();
        int maxDays = _retention?.MaxDays ?? 3650;

        var layout = new DialogLayout(this, textWidth: 520);
        Controls.Add(layout.Text(Loc.T("history.cleanup.intro"), Theme.FontBody, Theme.TextSecondary));

        // Defaults to the retention Claude Code already applies: whatever is
        // older than that has escaped the sweep — typically a profile nobody
        // launches any more.
        _olderDays = Number(layout, 1, maxDays, _retention?.Effective ?? 30);
        _largerMb = Number(layout, 1, 102_400, 10);
        AddRule(layout, HistoryCleanup.ReasonOlderThan, "history.cleanup.rule.olderThan", true,
            _olderDays, "history.cleanup.rule.olderThan.unit");
        AddRule(layout, HistoryCleanup.ReasonLargerThan, "history.cleanup.rule.largerThan", false,
            _largerMb, "history.cleanup.rule.largerThan.unit");
        // Off by default: a directory that was moved or renamed looks exactly
        // like one that was deleted, and its conversations still resume by id.
        AddRule(layout, HistoryCleanup.ReasonMissingDirectory, "history.cleanup.rule.missingDirectory", false);
        AddRule(layout, HistoryCleanup.ReasonRemovedAccount, "history.cleanup.rule.removedAccount", true);
        AddRule(layout, HistoryCleanup.ReasonOrphan, "history.cleanup.rule.orphan", true);

        layout.Y += layout.Scale(6);
        _preview = layout.Text(" ", Theme.FontHeading, Theme.TextPrimary);
        _note = layout.Text(" ", Theme.FontSmall, Theme.TextMuted);
        Controls.AddRange([_preview, _note]);

        var divider = new Panel
        {
            Location = new Point(layout.Left, layout.Y),
            Size = new Size(layout.TextWidth, 1),
            BackColor = Theme.BorderSoft,
        };
        Controls.Add(divider);
        layout.Advance(divider);

        Controls.Add(layout.Text(Loc.T("history.retention.heading"), Theme.FontHeading, Theme.TextPrimary));
        Controls.Add(layout.Text(
            Loc.T("history.retention.body", _retention?.DefaultDays ?? 30),
            Theme.FontSmall,
            Theme.TextMuted));
        _retentionDays = Number(layout, 1, maxDays, _retention?.Effective ?? 30);
        _saveRetention = new SecondaryButton { Text = Loc.T("history.retention.save") };
        _saveRetention.Click += (_, _) => SaveRetention();
        _retentionStatus = Caption("");
        PlaceRow(
            layout,
            Caption(Loc.T("history.retention.label")),
            _retentionDays,
            Caption(Loc.T("history.retention.unit")),
            _saveRetention,
            _retentionStatus);
        ShowRetention(message: null);

        _delete = new DangerButton { Text = Loc.T("history.cleanup.delete"), Enabled = false };
        _delete.Click += (_, _) => DeleteSelected();
        var close = new SecondaryButton
        {
            Text = Loc.T("history.cleanup.close"),
            DialogResult = DialogResult.Cancel,
        };
        Controls.AddRange([_delete, close]);
        layout.ActionRow(_delete, close);
        CancelButton = close;

        _replan.Tick += (_, _) =>
        {
            _replan.Stop();
            Replan();
        };
        Shown += (_, _) => Replan();
    }

    private static ThemedNumericUpDown Number(DialogLayout layout, int min, int max, int value) =>
        new()
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = layout.Scale(72),
            Height = layout.Scale(Theme.ControlHeight - 2),
            Font = Theme.FontBody,
            TextAlign = HorizontalAlignment.Center,
        };

    private static Label Caption(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.FontBody,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };
        label.Size = label.GetPreferredSize(Size.Empty);
        return label;
    }

    /// <summary>Lay controls out left to right on one row, centred on its height.</summary>
    private Rectangle PlaceRow(DialogLayout layout, params Control[] parts)
    {
        foreach (var b in parts.OfType<ThemedButton>())
            b.Width = b.GetPreferredSize(Size.Empty).Width;

        int top = layout.Y;
        int height = Math.Max(layout.Scale(Theme.ControlHeight), parts.Max(p => p.Height));
        int x = layout.Left;
        foreach (var part in parts)
        {
            part.Location = new Point(x, top + (height - part.Height) / 2);
            x += part.Width + layout.Scale(6);
            Controls.Add(part);
        }
        layout.Y = top + height + layout.Scale(4);
        return new Rectangle(layout.Left, top, layout.TextWidth, height);
    }

    private void AddRule(
        DialogLayout layout,
        string reason,
        string textKey,
        bool on,
        ThemedNumericUpDown? value = null,
        string? unitKey = null)
    {
        var check = new ThemedCheckBox
        {
            Text = Loc.T(textKey),
            Checked = on,
            AutoSize = true,
            Font = Theme.FontBody,
            ForeColor = Theme.TextPrimary,
        };
        check.Size = check.GetPreferredSize(Size.Empty);

        var parts = new List<Control> { check };
        if (value is not null)
        {
            value.Enabled = on;
            value.ValueChanged += (_, _) => SchedulePlan();
            parts.Add(value);
        }
        if (unitKey is not null)
            parts.Add(Caption(Loc.T(unitKey)));
        var row = PlaceRow(layout, [.. parts]);

        var tally = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.FontSmall,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Size = new Size(layout.Scale(130), row.Height),
        };
        tally.Location = new Point(row.Right - tally.Width, row.Top);
        Controls.Add(tally);

        check.CheckedChanged += (_, _) =>
        {
            if (value is not null) value.Enabled = check.Checked;
            ShowSelection();
        };
        _rules.Add(new RuleRow(reason, check, tally));
    }

    private bool IsOn(string reason) => _rules.Any(r => r.Reason == reason && r.Check.Checked);

    /// <summary>The ticked rules, at the values entered — what Delete applies.</summary>
    private CleanupRules TickedRules() =>
        new(
            IsOn(HistoryCleanup.ReasonOlderThan) ? (int)_olderDays.Value : null,
            IsOn(HistoryCleanup.ReasonLargerThan) ? (int)_largerMb.Value : null,
            IsOn(HistoryCleanup.ReasonMissingDirectory),
            IsOn(HistoryCleanup.ReasonRemovedAccount),
            IsOn(HistoryCleanup.ReasonOrphan));

    private void SchedulePlan()
    {
        _replan.Stop();
        _replan.Start();
    }

    /// <summary>One plan with every rule on, so every row has a figure.</summary>
    private void Replan()
    {
        try
        {
            _plan = HistoryCleanup.Plan(
                _engine,
                HistoryCleanup.ForRules(new CleanupRules(
                    (int)_olderDays.Value, (int)_largerMb.Value, true, true, true)));
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            _plan = CleanupPlanView.Empty;
            _preview.Text = Loc.T("history.cleanup.previewFailed", ex.Message);
            _note.Text = "";
            _delete.Enabled = false;
            return;
        }

        foreach (var row in _rules)
        {
            var (count, bytes) = _plan.For(row.Reason);
            row.Tally.Text = count > 0
                ? Loc.T("history.cleanup.tally", count, HistoryCleanup.FormatBytes(bytes))
                : Loc.T("common.dash");
        }
        ShowSelection();
    }

    /// <summary>What the ticked rows select together.</summary>
    private CleanupPlanView Selection()
    {
        var on = _rules.Where(r => r.Check.Checked).Select(r => r.Reason).ToHashSet();
        return _plan.Where(item => item.Reasons.Any(on.Contains));
    }

    private void ShowSelection()
    {
        var selection = Selection();
        _preview.Text = selection.DeletableCount > 0
            ? Loc.T(
                "history.cleanup.preview",
                selection.DeletableCount,
                HistoryCleanup.FormatBytes(selection.DeletableBytes))
            : Loc.T("history.cleanup.previewNone");
        _note.Text = selection.BlockedCount > 0
            ? Loc.T("history.cleanup.previewBlocked", selection.BlockedCount)
            : "";
        _delete.Enabled = selection.DeletableCount > 0;
    }

    private void DeleteSelected()
    {
        var selection = Selection();
        if (!HistoryDeleteDialog.Confirm(this, selection)) return;

        CleanupOutcomeView outcome;
        UseWaitCursor = true;
        try
        {
            outcome = HistoryCleanup.Apply(
                _engine,
                HistoryCleanup.ForRules(TickedRules()),
                selection.DeletableIds);
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            MessageBox.Show(
                this,
                Loc.T("history.delete.failed", ex.Message),
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        Changed = true;
        Replan();
        _note.Text = Loc.T(
            "history.delete.done",
            outcome.DeletedCount,
            HistoryCleanup.FormatBytes(outcome.FreedBytes));
        HistoryDeleteDialog.ReportFailures(this, outcome);
    }

    private RetentionView? LoadRetention()
    {
        try
        {
            return HistoryCleanup.GetRetention(_engine);
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            return null;
        }
    }

    private void ShowRetention(string? message)
    {
        if (_retention is null || _retention.Error is not null)
        {
            // An unreadable settings.json is the user's file to fix; writing a
            // fresh one over it would lose whatever else it held.
            _retentionDays.Enabled = false;
            _saveRetention.Enabled = false;
            string why = _retention?.Error ?? Loc.T("common.dash");
            _retentionStatus.Text = Loc.T("history.retention.failed", RecentSessions.Truncate(why, 48));
        }
        else
        {
            _retentionStatus.Text = message
                ?? (_retention.Days is null ? Loc.T("history.retention.default") : "");
        }
        _retentionStatus.Size = _retentionStatus.GetPreferredSize(Size.Empty);
    }

    private void SaveRetention()
    {
        if (_retention is null) return;
        int days = (int)_retentionDays.Value;
        try
        {
            // The default is written as no value at all, so a later change to
            // Claude Code's default reaches this user too.
            _retention = HistoryCleanup.SetRetention(_engine, days == _retention.DefaultDays ? null : days);
            ShowRetention(Loc.T("history.retention.saved"));
        }
        catch (Exception ex) when (ex is EngineException or ObjectDisposedException)
        {
            _retentionStatus.Text = Loc.T("history.retention.saveFailed", RecentSessions.Truncate(ex.Message, 48));
            _retentionStatus.Size = _retentionStatus.GetPreferredSize(Size.Empty);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _replan.Dispose();
        base.Dispose(disposing);
    }
}
