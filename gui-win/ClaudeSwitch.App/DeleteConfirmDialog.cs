namespace ClaudeSwitch.App;

/// <summary>Destructive confirm before removing a managed account slot.</summary>
internal sealed class DeleteConfirmDialog : Form
{
    public DeleteConfirmDialog(AccountCardModel model)
    {
        Text = Loc.T("confirm.delete.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        string name = string.IsNullOrWhiteSpace(model.Alias)
            ? model.Email
            : $"{model.Alias} · {model.Email}";

        // Text measures itself and the dialog is sized from it — a fixed pixel box
        // would clip the last lines once Windows scaling is above 100%.
        var layout = new DialogLayout(this, textWidth: 392);

        var title = layout.Text(
            Loc.T("confirm.delete.heading"),
            Theme.FontHeading,
            Theme.UsageHigh);
        var body = layout.Text(
            Loc.T("confirm.delete.body", model.Number, name),
            Theme.FontBody,
            Theme.TextSecondary);

        var btnDelete = new DangerButton
        {
            Text = Loc.T("confirm.delete.ok"),
            DialogResult = DialogResult.Yes,
        };
        var btnCancel = new SecondaryButton
        {
            Text = Loc.T("common.cancel"),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange([title, body, btnDelete, btnCancel]);
        layout.ActionRow(btnDelete, btnCancel);
        AcceptButton = btnCancel;
        CancelButton = btnCancel;
    }

    public static bool Confirm(IWin32Window owner, AccountCardModel model)
    {
        if (model.Active)
        {
            var r = MessageBox.Show(
                owner,
                Loc.T("confirm.delete.active"),
                Loc.T("confirm.delete.activeTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes)
                return false;
        }

        using var dlg = new DeleteConfirmDialog(model);
        return dlg.ShowDialog(owner) == DialogResult.Yes;
    }
}
