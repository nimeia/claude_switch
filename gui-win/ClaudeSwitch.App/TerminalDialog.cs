namespace ClaudeSwitch.App;

/// <summary>
/// Which terminal app opens when this tool starts Claude Code.
/// </summary>
internal sealed class TerminalDialog : Form
{
    private readonly Dictionary<TerminalKind, RadioButton> _radios = [];
    private readonly Label _note = new();
    private TerminalKind _selected;

    public TerminalKind Selected => _selected;

    public TerminalDialog()
    {
        Text = Loc.T("terminal.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        var layout = new DialogLayout(this, textWidth: 460, pad: 20, gap: 8);
        var title = layout.Text(Loc.T("terminal.title"), Theme.FontHeading, Theme.TextPrimary);
        var hint = layout.Text(Loc.T("terminal.hint"), Theme.FontSmall, Theme.TextMuted);
        var when = layout.Text(Loc.T("terminal.when"), Theme.FontSmall, Theme.TextMuted);

        var catalog = TerminalHost.Catalog();
        var saved = TerminalHost.Preferred;
        bool savedOk = catalog.Any(c => c.Kind == saved && c.Available);
        _selected = savedOk ? saved : TerminalKind.Auto;

        var added = new List<Control> { title, hint, when };
        string? lastGroup = null;
        foreach (var choice in catalog)
        {
            if (choice.GroupKey != lastGroup)
            {
                lastGroup = choice.GroupKey;
                added.Add(layout.Text(Loc.T(choice.GroupKey), Theme.FontSmall, Theme.TextMuted));
            }

            var radio = new RadioButton { Name = "kind-" + choice.Kind.Id() };
            string label = TerminalHost.DisplayName(choice.Kind);
            if (!choice.Available)
                label += " — " + Loc.T("terminal.missing");
            layout.Radio(radio, label);
            radio.Enabled = choice.Available;
            radio.Checked = choice.Kind == _selected && choice.Available;
            if (choice.Available)
            {
                var kind = choice.Kind;
                radio.CheckedChanged += (_, _) =>
                {
                    if (!radio.Checked) return;
                    _selected = kind;
                    UpdateNote();
                };
            }
            _radios[choice.Kind] = radio;
            added.Add(radio);
        }

        if (!_radios.Values.Any(r => r.Checked) && _radios.TryGetValue(TerminalKind.Auto, out var auto))
        {
            auto.Checked = true;
            _selected = TerminalKind.Auto;
        }

        _note.Font = Theme.FontSmall;
        _note.ForeColor = Theme.TextMuted;
        _note.AutoSize = false;
        _note.Location = new Point(layout.Left, layout.Y);
        _note.Size = new Size(layout.TextWidth, layout.Scale(48));
        layout.Advance(_note);
        added.Add(_note);
        UpdateNote();

        var btnOk = new PrimaryButton { Text = Loc.T("terminal.save") };
        var btnCancel = new SecondaryButton
        {
            Text = Loc.T("common.cancel"),
            DialogResult = DialogResult.Cancel,
        };
        btnOk.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        added.Add(btnOk);
        added.Add(btnCancel);

        Controls.AddRange(added.ToArray());
        layout.ActionRow(btnOk, btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    public static bool TryEdit(IWin32Window owner, out TerminalKind kind)
    {
        kind = TerminalHost.Preferred;
        using var dlg = new TerminalDialog();
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;
        kind = dlg.Selected;
        return true;
    }

    private void UpdateNote()
    {
        _note.Text = TerminalHost.Note(_selected);
    }
}
