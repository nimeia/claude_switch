namespace ClaudeSwitch.App;

/// <summary>
/// Modal confirm before switching away from the live account — reduces mis-clicks.
/// </summary>
internal sealed class SwitchConfirmDialog : Form
{
    public SwitchConfirmDialog(AccountCardModel from, AccountCardModel to)
    {
        Text = Loc.T("confirm.switch.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        Font = Theme.FontBody;
        ForeColor = Theme.TextPrimary;
        Icon = AppIcon.Get();

        // Text measures itself and the dialog is sized from it — a fixed pixel box
        // would clip the last lines once Windows scaling is above 100%.
        var layout = new DialogLayout(this, textWidth: 372);

        var title = layout.Text(
            Loc.T("confirm.switch.heading"),
            Theme.FontHeading,
            Theme.TextPrimary);

        string fromName = DisplayName(from);
        string toName = DisplayName(to);
        var body = layout.Text(
            Loc.T("confirm.switch.body", from.Number, fromName, to.Number, toName),
            Theme.FontBody,
            Theme.TextSecondary);

        var usageHint = layout.Text(
            UsageLine(to),
            Theme.FontSmall,
            Theme.UsageColor(MaxUsage(to)));

        var btnOk = new PrimaryButton
        {
            Text = Loc.T("confirm.switch.ok"),
            DialogResult = DialogResult.OK,
        };
        var btnCancel = new SecondaryButton
        {
            Text = Loc.T("common.cancel"),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange([title, body, usageHint, btnOk, btnCancel]);
        layout.ActionRow(btnOk, btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private static string DisplayName(AccountCardModel m) =>
        string.IsNullOrWhiteSpace(m.Alias) ? m.Email : $"{m.Alias} · {m.Email}";

    private static double? MaxUsage(AccountCardModel m)
    {
        if (m.FiveHour is null && m.SevenDay is null) return null;
        return Math.Max(m.FiveHour ?? 0, m.SevenDay ?? 0);
    }

    private static string UsageLine(AccountCardModel to)
    {
        var five = Theme.UsageLabel(to.FiveHour);
        var seven = Theme.UsageLabel(to.SevenDay);
        var level = Theme.UsageLevel(MaxUsage(to));
        return Loc.T("confirm.switch.target", five, seven, level);
    }

    public static bool Confirm(IWin32Window owner, AccountCardModel? from, AccountCardModel to)
    {
        // Same account — no need to confirm.
        if (from is not null && from.Number == to.Number && from.Active)
            return false;

        // Switching onto already-active is a no-op for UX; still rare.
        if (to.Active)
        {
            MessageBox.Show(owner, Loc.T("confirm.switch.same"), Loc.T("switch.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var fromModel = from ?? new AccountCardModel
        {
            Number = 0,
            Email = Loc.T("confirm.unrecorded"),
            Active = true,
        };

        using var dlg = new SwitchConfirmDialog(fromModel, to);
        return dlg.ShowDialog(owner) == DialogResult.OK;
    }
}
