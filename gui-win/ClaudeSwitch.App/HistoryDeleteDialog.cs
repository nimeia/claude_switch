namespace ClaudeSwitch.App;

/// <summary>
/// The last question before history is deleted: how much goes, and what stays.
/// </summary>
/// <remarks>
/// Deletion is permanent on purpose — the point is to free disk space, which a
/// recycle bin would not do, and transcripts are plaintext that may hold
/// secrets. So the dialog states the count and the size, names what is kept,
/// and leaves Cancel as the default button.
/// </remarks>
internal sealed class HistoryDeleteDialog : Form
{
    private HistoryDeleteDialog(string heading, IEnumerable<string> lines, string okText)
    {
        Text = Loc.T("history.delete.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        var layout = new DialogLayout(this, textWidth: 420);
        var controls = new List<Control> { layout.Text(heading, Theme.FontHeading, Theme.UsageHigh) };
        foreach (var line in lines)
            controls.Add(layout.Text(line, Theme.FontBody, Theme.TextSecondary));

        var ok = new DangerButton { Text = okText, DialogResult = DialogResult.Yes };
        var cancel = new SecondaryButton { Text = Loc.T("common.cancel"), DialogResult = DialogResult.Cancel };
        Controls.AddRange([.. controls, ok, cancel]);
        layout.ActionRow(ok, cancel);
        AcceptButton = cancel;
        CancelButton = cancel;
    }

    /// <summary>
    /// Confirm deleting what a plan selected.
    /// </summary>
    /// <returns>False when declined, or when there was nothing to delete (and the user was told).</returns>
    public static bool Confirm(IWin32Window owner, CleanupPlanView plan)
    {
        if (plan.DeletableCount == 0)
        {
            MessageBox.Show(
                owner,
                plan.BlockedCount > 0
                    ? Loc.T("history.delete.allBlocked", plan.BlockedCount)
                    : Loc.T("history.delete.nothing"),
                Loc.T("history.delete.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        var lines = new List<string>
        {
            Loc.T("history.delete.body", HistoryCleanup.FormatBytes(plan.DeletableBytes)),
        };
        if (plan.BlockedCount > 0)
            lines.Add(Loc.T("history.delete.blocked", plan.BlockedCount));
        if (plan.RunRecords > 0)
            lines.Add(Loc.T("history.delete.runs", plan.RunRecords));

        using var dlg = new HistoryDeleteDialog(
            Loc.Plural("history.delete.heading", plan.DeletableCount),
            lines,
            Loc.T("history.delete.ok"));
        return dlg.ShowDialog(owner) == DialogResult.Yes;
    }

    /// <summary>Name the files a cleanup could not remove, when there were any.</summary>
    /// <remarks>
    /// Usually a file another process holds open. Listing a handful is enough to
    /// act on; a thousand-line message box is not.
    /// </remarks>
    public static void ReportFailures(IWin32Window owner, CleanupOutcomeView outcome)
    {
        if (outcome.Failures.Count == 0) return;
        var shown = outcome.Failures.Take(8).ToList();
        if (outcome.Failures.Count > shown.Count) shown.Add("…");
        MessageBox.Show(
            owner,
            Loc.T("history.delete.partial", outcome.Failures.Count, string.Join("\n", shown)),
            Loc.T("history.delete.partialTitle"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Confirm taking registered-only directories off the list.
    /// </summary>
    /// <remarks>
    /// Names the directories rather than only counting them: they are the user's
    /// own folders, and one they still use would be recognisable at a glance.
    /// </remarks>
    public static bool ConfirmEmptyDirectories(IWin32Window owner, RegistryPlanView plan)
    {
        if (plan.Paths.Count == 0)
        {
            MessageBox.Show(
                owner,
                plan.LiveDirectories > 0
                    ? Loc.T("history.emptyDirs.allLive")
                    : Loc.T("history.emptyDirs.nothing"),
                Loc.T("history.emptyDirs.title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        const int named = 6;
        var names = plan.Paths.Take(named).ToList();
        if (plan.Paths.Count > named)
            names.Add(Loc.T("history.emptyDirs.more", plan.Paths.Count - named));

        var lines = new List<string> { string.Join("\n", names), Loc.T("history.emptyDirs.body") };
        if (plan.WithMcp > 0)
            lines.Add(Loc.T("history.emptyDirs.mcp", plan.WithMcp));
        if (plan.LiveDirectories > 0)
            lines.Add(Loc.T("history.emptyDirs.live", plan.LiveDirectories));
        lines.Add(Loc.T("history.emptyDirs.kept"));
        if (plan.BackupDir is { Length: > 0 } backup)
            lines.Add(Loc.T("history.emptyDirs.backup", backup));

        using var dlg = new HistoryDeleteDialog(
            Loc.Plural("history.emptyDirs.heading", plan.Paths.Count),
            lines,
            Loc.T("history.emptyDirs.ok"));
        dlg.Text = Loc.T("history.emptyDirs.title");
        return dlg.ShowDialog(owner) == DialogResult.Yes;
    }

    /// <summary>Confirm removing everything Claude Code keeps for one directory.</summary>
    public static bool ConfirmPurge(IWin32Window owner, string name, PurgePreflightView pre)
    {
        var lines = new List<string>
        {
            Loc.T(
                "history.purge.body",
                pre.Roots.Sum(r => r.Sessions),
                HistoryCleanup.FormatBytes(pre.Roots.Sum(r => r.Bytes))),
            Loc.T("history.purge.list"),
        };
        if (pre.Roots.Count > 1)
            lines.Add(Loc.T("history.purge.homes", pre.Roots.Count));
        lines.Add(Loc.T("history.purge.kept"));

        using var dlg = new HistoryDeleteDialog(
            Loc.T("history.purge.heading", name),
            lines,
            Loc.T("history.purge.ok"));
        return dlg.ShowDialog(owner) == DialogResult.Yes;
    }
}
