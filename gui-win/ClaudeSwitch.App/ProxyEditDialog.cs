namespace ClaudeSwitch.App;

/// <summary>
/// One account's proxy, timezone and language — kept together so they match
/// the proxy node's exit region. Clash's local URL cannot say which country
/// that is, so the region is chosen here next to the proxy.
/// </summary>
internal sealed class ProxyEditDialog : Form
{
    internal readonly record struct Result(string? Proxy, string? Timezone, string? Language);

    internal readonly record struct RegionChoice(string Key, string? Timezone, string? Language)
    {
        public override string ToString() => Loc.T("locale.preset." + Key);
    }

    private static readonly RegionChoice[] Presets =
    [
        new("system", null, null),
        new("us-west", "America/Los_Angeles", "english"),
        new("us-east", "America/New_York", "english"),
        new("uk", "Europe/London", "english"),
        new("jp", "Asia/Tokyo", "japanese"),
        new("sg", "Asia/Singapore", "english"),
        new("cn", "Asia/Shanghai", "chinese"),
        new("custom", null, null),
    ];

    private readonly RadioButton _system = new();
    private readonly RadioButton _direct = new();
    private readonly RadioButton _custom = new();
    private readonly TextBox _url = new();
    private readonly ComboBox _region = new();
    private readonly TextBox _timezone = new();
    private readonly TextBox _language = new();
    private bool _syncingRegion;

    public Result EditResult { get; private set; }

    public ProxyEditDialog(AccountCardModel model)
    {
        Text = Loc.T("proxy.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        var layout = new DialogLayout(this, textWidth: 420, pad: 20, gap: 8);

        var title = layout.Text(Loc.T("proxy.account", model.Number), Theme.FontHeading, Theme.TextPrimary);
        var email = layout.Text(model.Email, Theme.FontSmall, Theme.TextSecondary);
        var hint = layout.Text(Loc.T("proxy.hint"), Theme.FontSmall, Theme.TextMuted);
        var when = layout.Text(Loc.T("proxy.when"), Theme.FontSmall, Theme.TextMuted);

        layout.Radio(_system, Loc.T("proxy.choice.system"));
        layout.Radio(_direct, Loc.T("proxy.choice.direct"));
        layout.Radio(_custom, Loc.T("proxy.choice.custom"));

        int indent = layout.Scale(22);
        StyleBox(_url, layout, indent);
        _url.Name = "proxyUrl";
        _url.PlaceholderText = Loc.T("proxy.url.placeholder");
        layout.Advance(_url);

        var regionLabel = layout.Text(Loc.T("locale.region"), Theme.FontSmall, Theme.TextPrimary);
        var regionHint = layout.Text(Loc.T("locale.hint"), Theme.FontSmall, Theme.TextMuted);

        _region.Name = "region";
        _region.DropDownStyle = ComboBoxStyle.DropDownList;
        _region.FlatStyle = FlatStyle.Flat;
        _region.BackColor = Theme.BgApp;
        _region.ForeColor = Theme.TextPrimary;
        _region.Font = Theme.FontBody;
        _region.Location = new Point(layout.Left, layout.Y);
        _region.Width = layout.TextWidth;
        _region.Height = layout.Scale(Theme.ControlHeight);
        foreach (var preset in Presets)
            _region.Items.Add(preset);
        layout.Advance(_region);

        var tzLabel = layout.Text(Loc.T("locale.timezone"), Theme.FontSmall, Theme.TextMuted);
        StyleBox(_timezone, layout, indent: 0);
        _timezone.Name = "timezone";
        _timezone.PlaceholderText = Loc.T("locale.timezone.placeholder");
        layout.Advance(_timezone);

        var langLabel = layout.Text(Loc.T("locale.language"), Theme.FontSmall, Theme.TextMuted);
        StyleBox(_language, layout, indent: 0);
        _language.Name = "language";
        _language.PlaceholderText = Loc.T("locale.language.placeholder");
        layout.Advance(_language);

        string stored = model.Proxy ?? "";
        if (string.Equals(stored, "direct", StringComparison.OrdinalIgnoreCase))
        {
            _direct.Checked = true;
        }
        else if (stored.Length > 0)
        {
            _custom.Checked = true;
            _url.Text = stored;
        }
        else
        {
            _system.Checked = true;
        }

        _timezone.Text = model.Timezone ?? "";
        _language.Text = model.Language ?? "";
        SelectMatchingPreset();

        void SyncUrlEnabled() => _url.Enabled = _custom.Checked;
        _system.CheckedChanged += (_, _) => SyncUrlEnabled();
        _direct.CheckedChanged += (_, _) => SyncUrlEnabled();
        _custom.CheckedChanged += (_, _) => SyncUrlEnabled();
        SyncUrlEnabled();

        _region.SelectedIndexChanged += (_, _) => ApplySelectedPreset();
        _timezone.TextChanged += (_, _) => MarkCustomIfEdited();
        _language.TextChanged += (_, _) => MarkCustomIfEdited();

        var btnOk = new PrimaryButton { Text = Loc.T("proxy.save") };
        var btnCancel = new SecondaryButton
        {
            Text = Loc.T("common.cancel"),
            DialogResult = DialogResult.Cancel,
        };
        btnOk.Click += (_, _) =>
        {
            string? proxy;
            if (_direct.Checked)
            {
                proxy = "direct";
            }
            else if (_custom.Checked)
            {
                string raw = _url.Text.Trim();
                if (string.IsNullOrEmpty(raw))
                {
                    MessageBox.Show(this, Loc.T("proxy.err.empty"), Loc.T("proxy.invalid"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                proxy = raw;
            }
            else
            {
                proxy = null;
            }

            string? timezone = EmptyToNull(_timezone.Text);
            string? language = EmptyToNull(_language.Text);
            if (timezone is not null && !LooksLikeIana(timezone))
            {
                MessageBox.Show(this, Loc.T("locale.err.timezone"), Loc.T("proxy.invalid"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            EditResult = new Result(proxy, timezone, language);
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange([
            title, email, hint, when,
            _system, _direct, _custom, _url,
            regionLabel, regionHint, _region, tzLabel, _timezone, langLabel, _language,
            btnOk, btnCancel,
        ]);
        layout.ActionRow(btnOk, btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        ActiveControl = _custom.Checked ? _url : _system;
    }

    public static bool TryEdit(IWin32Window owner, AccountCardModel model, out Result result)
    {
        result = new(model.Proxy, model.Timezone, model.Language);
        using var dlg = new ProxyEditDialog(model);
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;
        result = dlg.EditResult;
        return true;
    }

    private void StyleBox(TextBox box, DialogLayout layout, int indent)
    {
        box.Location = new Point(layout.Left + indent, layout.Y);
        box.Width = layout.TextWidth - indent;
        box.Height = layout.Scale(Theme.ControlHeight);
        box.Font = Theme.FontBody;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = Theme.BgApp;
        box.ForeColor = Theme.TextPrimary;
    }

    private void SelectMatchingPreset()
    {
        _syncingRegion = true;
        try
        {
            string? tz = EmptyToNull(_timezone.Text);
            string? lang = EmptyToNull(_language.Text);
            int match = -1;
            for (int i = 0; i < Presets.Length; i++)
            {
                var p = Presets[i];
                if (p.Key is "custom") continue;
                if (Same(p.Timezone, tz) && Same(p.Language, lang))
                {
                    match = i;
                    break;
                }
            }
            _region.SelectedIndex = match >= 0 ? match : Presets.Length - 1;
        }
        finally
        {
            _syncingRegion = false;
        }
    }

    private void ApplySelectedPreset()
    {
        if (_syncingRegion) return;
        if (_region.SelectedItem is not RegionChoice choice) return;
        if (choice.Key == "custom") return;
        _syncingRegion = true;
        try
        {
            _timezone.Text = choice.Timezone ?? "";
            _language.Text = choice.Language ?? "";
        }
        finally
        {
            _syncingRegion = false;
        }
    }

    private void MarkCustomIfEdited()
    {
        if (_syncingRegion) return;
        if (_region.SelectedItem is RegionChoice choice
            && choice.Key != "custom"
            && Same(choice.Timezone, EmptyToNull(_timezone.Text))
            && Same(choice.Language, EmptyToNull(_language.Text)))
        {
            return;
        }
        _syncingRegion = true;
        try
        {
            _region.SelectedIndex = Presets.Length - 1;
        }
        finally
        {
            _syncingRegion = false;
        }
    }

    private static string? EmptyToNull(string? s)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same shape the core accepts: UTC/GMT, or Area/Location IANA names.
    /// </summary>
    internal static bool LooksLikeIana(string s)
    {
        s = s.Trim();
        if (s.Length is 0 or > 64) return false;
        if (s.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            || s.Equals("GMT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        int segs = 0;
        foreach (var seg in s.Split('/'))
        {
            segs++;
            if (seg.Length == 0) return false;
            if (!char.IsAsciiLetter(seg[0]) && seg[0] != '_') return false;
            foreach (char c in seg.AsSpan(1))
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '+' and not '-')
                    return false;
            }
        }
        return segs >= 2;
    }
}
