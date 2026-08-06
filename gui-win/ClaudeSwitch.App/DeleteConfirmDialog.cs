namespace ClaudeSwitch.App;

/// <summary>Destructive confirm before removing a managed account slot.</summary>
internal sealed class DeleteConfirmDialog : Form
{
    public DeleteConfirmDialog(AccountCardModel model)
    {
        Text = "删除账号";
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
            "确定删除此托管账号？",
            Theme.FontHeading,
            Theme.UsageHigh);
        var body = layout.Text(
            $"将移除：#{model.Number}  {name}\n\n" +
            "• 删除的是 Claude Switch 中的备份与槽位记录\n" +
            "• 不会登出 Claude 官网账号本身\n" +
            "• 若该账号正在使用中，请先切换到其他账号",
            Theme.FontBody,
            Theme.TextSecondary);

        var btnDelete = new DangerButton
        {
            Text = "确认删除",
            DialogResult = DialogResult.Yes,
        };
        var btnCancel = new SecondaryButton
        {
            Text = "取消",
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
                "该账号当前正在使用中。\n建议先切换到其他账号再删除。\n\n仍要继续删除吗？",
                "删除当前账号",
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
