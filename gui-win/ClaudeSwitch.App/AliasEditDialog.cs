namespace ClaudeSwitch.App;

/// <summary>Edit or clear a short display alias for an account.</summary>
internal sealed class AliasEditDialog : Form
{
    private readonly TextBox _input;

    public string? AliasResult { get; private set; }

    public AliasEditDialog(AccountCardModel model)
    {
        Text = Loc.T("alias.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        // Text measures itself and the dialog is sized from it — a fixed pixel box
        // would clip the last lines once Windows scaling is above 100%.
        var layout = new DialogLayout(this, textWidth: 356, pad: 20, gap: 8);

        var title = layout.Text(Loc.T("alias.account", model.Number), Theme.FontHeading, Theme.TextPrimary);
        var email = layout.Text(model.Email, Theme.FontSmall, Theme.TextSecondary);
        var hint = layout.Text(
            Loc.T("alias.hint"),
            Theme.FontSmall,
            Theme.TextMuted);

        _input = new TextBox
        {
            Location = new Point(layout.Left, layout.Y),
            Width = layout.TextWidth,
            Font = Theme.FontBody,
            Text = model.Alias ?? "",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BgApp,
            ForeColor = Theme.TextPrimary,
        };
        layout.Advance(_input);

        var btnOk = new PrimaryButton { Text = Loc.T("alias.save") };
        var btnCancel = new SecondaryButton
        {
            Text = Loc.T("common.cancel"),
            DialogResult = DialogResult.Cancel,
        };
        btnOk.Click += (_, _) =>
        {
            var raw = _input.Text.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                AliasResult = null;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            if (!IsValidAlias(raw, out var err))
            {
                MessageBox.Show(this, err, Loc.T("alias.invalid"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AliasResult = raw.ToLowerInvariant();
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange([title, email, hint, _input, btnOk, btnCancel]);
        layout.ActionRow(btnOk, btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        ActiveControl = _input;
    }

    private static bool IsValidAlias(string name, out string error)
    {
        error = "";
        if (name.Length == 0)
        {
            error = Loc.T("alias.err.empty");
            return false;
        }
        if (name.All(char.IsDigit))
        {
            error = Loc.T("alias.err.numeric");
            return false;
        }
        if (name.StartsWith('-'))
        {
            error = Loc.T("alias.err.dash");
            return false;
        }
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                continue;
            error = Loc.T("alias.err.chars");
            return false;
        }
        return true;
    }

    public static string? Prompt(IWin32Window owner, AccountCardModel model)
    {
        using var dlg = new AliasEditDialog(model);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.AliasResult : model.Alias;
    }

    /// <returns>true if user confirmed; alias may be null to clear.</returns>
    public static bool TryEdit(IWin32Window owner, AccountCardModel model, out string? alias)
    {
        alias = model.Alias;
        using var dlg = new AliasEditDialog(model);
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;
        alias = dlg.AliasResult;
        return true;
    }
}
