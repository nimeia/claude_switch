using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// One account's proxy, timezone and language — kept together so they match
/// the proxy node's exit region. The local Clash URL cannot say which country
/// that is, so this window probes the exit IP through the chosen proxy.
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

    private readonly Engine? _engine;
    private readonly RadioButton _system = new();
    private readonly RadioButton _direct = new();
    private readonly RadioButton _custom = new();
    private readonly TextBox _url = new();
    private readonly ComboBox _region = new();
    private readonly TextBox _timezone = new();
    private readonly TextBox _language = new();
    private readonly Label _detectStatus = new();
    private readonly Label _osLine = new();
    private readonly Label _warn = new();
    private readonly Label _preview = new();
    private readonly SecondaryButton _detect = new();
    private readonly System.Windows.Forms.Timer _urlDebounce = new() { Interval = 600 };
    private ThemedButton[] _actions = [];
    private int _pad;
    private int _gap;
    private int _textW;
    private bool _reflowing;
    private bool _syncingRegion;
    private bool _userTouchedRegion;
    private bool _ready;
    private int _detectGen;
    private string? _exitCountry;
    private string? _exitTimezone;

    public Result EditResult { get; private set; }

    public ProxyEditDialog(AccountCardModel model, Engine? engine = null)
    {
        _engine = engine;
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
        _pad = layout.Scale(20);
        _gap = layout.Scale(8);
        _textW = layout.TextWidth;

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

        ReserveInfo(_osLine, layout, "osTimezone", Theme.TextMuted, Loc.T("locale.os", "America/Los_Angeles"));
        ReserveInfo(_warn, layout, "localeWarn", Theme.UsageHigh, Loc.T("locale.warn.follow-system-cn"));
        // The override line is longer than follow-system and wraps; reserve that
        // height so a later detect result is not drawn on top of it.
        ReserveInfo(
            _preview,
            layout,
            "localePreview",
            Theme.TextMuted,
            Loc.T(
                "locale.preview.override",
                "America/Argentina/Buenos_Aires",
                Loc.T("locale.preview.lang", "english", "en_US.UTF-8")));

        _detectStatus.Name = "detectStatus";
        _detectStatus.Font = Theme.FontSmall;
        _detectStatus.ForeColor = Theme.TextMuted;
        _detectStatus.AutoSize = true;
        _detectStatus.MaximumSize = new Size(layout.TextWidth, 0);
        _detectStatus.Location = new Point(layout.Left, layout.Y);
        // Reserve two lines so a later probe result does not sit on the buttons.
        _detectStatus.Text = Loc.T("locale.detect.ok", "255.255.255.255", "United States", "America/Los_Angeles", "english");
        _detectStatus.Size = _detectStatus.GetPreferredSize(new Size(layout.TextWidth, 0));
        _detectStatus.MinimumSize = new Size(0, _detectStatus.Height);
        _detectStatus.Text = "";
        layout.Advance(_detectStatus);

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
        _system.CheckedChanged += (_, _) => { SyncUrlEnabled(); if (_system.Checked) OnProxyChoiceChanged(); };
        _direct.CheckedChanged += (_, _) => { SyncUrlEnabled(); if (_direct.Checked) OnProxyChoiceChanged(); };
        _custom.CheckedChanged += (_, _) => { SyncUrlEnabled(); if (_custom.Checked) OnProxyChoiceChanged(); };
        SyncUrlEnabled();

        _region.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingRegion) _userTouchedRegion = true;
            ApplySelectedPreset();
        };
        _timezone.TextChanged += (_, _) => { MarkCustomIfEdited(); RefreshAlignment(); };
        _language.TextChanged += (_, _) => { MarkCustomIfEdited(); RefreshAlignment(); };
        _url.TextChanged += (_, _) =>
        {
            if (!_ready || !_custom.Checked) return;
            _urlDebounce.Stop();
            _urlDebounce.Start();
        };
        _urlDebounce.Tick += (_, _) =>
        {
            _urlDebounce.Stop();
            _userTouchedRegion = false;
            BeginDetect(force: false);
        };

        _detect.Name = "detect";
        _detect.Text = Loc.T("locale.detect");
        _detect.Enabled = _engine is not null;
        _detect.Click += (_, _) => BeginDetect(force: true);

        var btnOk = new PrimaryButton { Text = Loc.T("proxy.save") };
        var btnCancel = new SecondaryButton
        {
            Text = Loc.T("common.cancel"),
            DialogResult = DialogResult.Cancel,
        };
        btnOk.Click += (_, _) =>
        {
            if (!TryReadProxy(out string? proxy, out string? problem))
            {
                MessageBox.Show(this, problem, Loc.T("proxy.invalid"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
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
            _osLine, _warn, _preview,
            _detectStatus, _detect, btnOk, btnCancel,
        ]);
        _actions = [_detect, btnOk, btnCancel];
        layout.ActionRow(_actions);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        ActiveControl = _custom.Checked ? _url : _system;
        _osLine.TextChanged += (_, _) => ReflowFooter();
        _warn.TextChanged += (_, _) => ReflowFooter();
        _preview.TextChanged += (_, _) => ReflowFooter();
        _detectStatus.TextChanged += (_, _) => ReflowFooter();
        _ready = true;
        RefreshAlignment();
        ReflowFooter();

        if (_engine is not null)
            Shown += (_, _) => BeginDetect(force: false);

        FormClosed += (_, _) =>
        {
            _detectGen++;
            _urlDebounce.Stop();
            _urlDebounce.Dispose();
        };
    }

    public static bool TryEdit(IWin32Window owner, Engine engine, AccountCardModel model, out Result result)
    {
        result = new(model.Proxy, model.Timezone, model.Language);
        using var dlg = new ProxyEditDialog(model, engine);
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;
        result = dlg.EditResult;
        return true;
    }

    /// <summary>
    /// Stacks the machine-timezone / warning / preview / detect lines and the
    /// action row so a wrapping preview cannot sit on the probe result, and
    /// the buttons stay fully inside the client area.
    /// </summary>
    private void ReflowFooter()
    {
        if (!_ready || _reflowing || IsDisposed) return;
        _reflowing = true;
        try
        {
            int y = _language.Bottom + _gap;
            foreach (var line in new[] { _osLine, _warn, _preview, _detectStatus })
            {
                line.MaximumSize = new Size(_textW, 0);
                line.Location = new Point(_pad, y);
                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    line.AutoSize = false;
                    line.MinimumSize = Size.Empty;
                    line.Size = new Size(_textW, 0);
                    continue;
                }
                line.AutoSize = true;
                var sz = line.GetPreferredSize(new Size(_textW, 0));
                line.MinimumSize = new Size(0, sz.Height);
                line.Size = new Size(Math.Max(sz.Width, 1), sz.Height);
                y = line.Bottom + _gap;
            }

            int rowY = y + _gap;
            int rowH = 0;
            int right = _pad + _textW;
            int btnGap = Theme.Scale(this, 10);
            for (int i = _actions.Length - 1; i >= 0; i--)
            {
                var b = _actions[i];
                int w = b.GetPreferredSize(Size.Empty).Width;
                b.Width = w;
                b.Location = new Point(right - w, rowY);
                right -= w + btnGap;
                rowH = Math.Max(rowH, b.RowHeight);
            }
            ClientSize = new Size(_pad + _textW + _pad, rowY + rowH + _pad);
        }
        finally
        {
            _reflowing = false;
        }
    }

    private void ReserveInfo(Label label, DialogLayout layout, string name, Color color, string sample)
    {
        StyleInfo(label, layout, name, color);
        label.Text = sample;
        label.Size = label.GetPreferredSize(new Size(layout.TextWidth, 0));
        label.MinimumSize = new Size(0, label.Height);
        label.Text = "";
        layout.Advance(label);
    }

    private static void StyleInfo(Label label, DialogLayout layout, string name, Color color)
    {
        label.Name = name;
        label.Font = Theme.FontSmall;
        label.ForeColor = color;
        label.AutoSize = true;
        label.MaximumSize = new Size(layout.TextWidth, 0);
        label.Location = new Point(layout.Left, layout.Y);
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

    private void OnProxyChoiceChanged()
    {
        if (!_ready) return;
        _urlDebounce.Stop();
        _userTouchedRegion = false;
        BeginDetect(force: false);
    }

    private void BeginDetect(bool force)
    {
        if (_engine is null || IsDisposed) return;
        if (!TryReadProxy(out string? proxy, out string? problem))
        {
            _detectStatus.Text = problem ?? Loc.T("locale.detect.needUrl");
            _detectStatus.ForeColor = Theme.TextMuted;
            return;
        }
        if (force) _userTouchedRegion = false;
        int gen = ++_detectGen;
        _detect.Enabled = false;
        _detectStatus.ForeColor = Theme.TextMuted;
        _detectStatus.Text = Loc.T("locale.detect.working");
        var engine = _engine;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var node = engine.Call("locale_detect", new { proxy = proxy ?? "" });
                Post(() => ApplyDetect(gen, node, error: null));
            }
            catch (Exception ex)
            {
                string msg = DescribeEngine(ex);
                Post(() => ApplyDetect(gen, null, msg));
            }
        });
    }

    private void Post(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // Dialog closed while the probe was in flight.
        }
    }

    private void ApplyDetect(int gen, JsonNode? node, string? error)
    {
        if (IsDisposed || gen != _detectGen) return;
        _detect.Enabled = _engine is not null;
        if (error is not null)
        {
            _detectStatus.ForeColor = Theme.TextMuted;
            _detectStatus.Text = Loc.T("locale.detect.fail", error);
            return;
        }
        if (node is null) return;

        string ip = node["ip"]?.GetValue<string>() ?? "";
        string country = node["country"]?.GetValue<string>()
            ?? node["countryCode"]?.GetValue<string>()
            ?? "";
        _exitCountry = node["countryCode"]?.GetValue<string>() ?? "";
        string timezone = node["timezone"]?.GetValue<string>() ?? "";
        _exitTimezone = timezone;
        string language = node["language"]?.GetValue<string>() ?? "";
        _detectStatus.ForeColor = Theme.TextSecondary;
        _detectStatus.Text = Loc.T("locale.detect.ok", ip, country, timezone, language);
        if (_userTouchedRegion) return;
        if (timezone.Length == 0 && language.Length == 0) return;

        _syncingRegion = true;
        try
        {
            _timezone.Text = timezone;
            _language.Text = language;
            SelectMatchingPreset();
        }
        finally
        {
            _syncingRegion = false;
        }
        RefreshAlignment();
    }

    private void RefreshAlignment()
    {
        if (IsDisposed) return;
        string? os = OsTimezone.Iana();
        _osLine.Text = string.IsNullOrWhiteSpace(os)
            ? Loc.T("locale.os.unknown")
            : Loc.T("locale.os", os);

        string? tz = EmptyToNull(_timezone.Text);
        string? lang = EmptyToNull(_language.Text);

        if (_engine is null)
        {
            _warn.Text = FollowSystemCnWarning(os, tz);
            _warn.ForeColor = Theme.UsageHigh;
            _preview.Text = PreviewText(tz, lang, os, langEnv: null);
            return;
        }

        try
        {
            var node = _engine.Call("locale_align", new
            {
                osTimezone = os ?? "",
                timezone = tz ?? "",
                language = lang ?? "",
                exitCountry = _exitCountry ?? "",
                exitTimezone = _exitTimezone ?? "",
            });
            var warnings = new List<string>();
            if (node["warnings"] is JsonArray arr)
            {
                foreach (var w in arr)
                {
                    string? code = w?.GetValue<string>();
                    if (string.IsNullOrEmpty(code)) continue;
                    string key = "locale.warn." + code;
                    string text = Loc.T(key);
                    warnings.Add(text == key ? code : text);
                }
            }
            _warn.Text = warnings.Count == 0 ? "" : string.Join("\n", warnings);
            _warn.ForeColor = Theme.UsageHigh;

            bool follow = node["followSystem"]?.GetValue<bool>() ?? tz is null;
            string? timeZone = node["preview"]?["timeZone"]?.GetValue<string>();
            string? language = node["preview"]?["language"]?.GetValue<string>();
            string? langEnv = node["preview"]?["langEnv"]?.GetValue<string>();
            _preview.Text = follow
                ? Loc.T("locale.preview.system", os ?? "—")
                : PreviewText(timeZone, language, os, langEnv);
        }
        catch (Exception)
        {
            _warn.Text = FollowSystemCnWarning(os, tz);
            _preview.Text = PreviewText(tz, lang, os, langEnv: null);
        }
    }

    internal static string PreviewText(string? timeZone, string? language, string? os, string? langEnv)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
            return Loc.T("locale.preview.system", string.IsNullOrWhiteSpace(os) ? "—" : os);
        string langBit = string.IsNullOrWhiteSpace(language)
            ? ""
            : Loc.T("locale.preview.lang", language, string.IsNullOrWhiteSpace(langEnv) ? language : langEnv);
        return Loc.T("locale.preview.override", timeZone, langBit);
    }

    internal static string FollowSystemCnWarning(string? os, string? accountTz)
    {
        if (!string.IsNullOrWhiteSpace(accountTz)) return "";
        if (string.IsNullOrWhiteSpace(os)) return "";
        string tz = os.Trim();
        bool cn = tz.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase)
            || tz.Equals("Asia/Urumqi", StringComparison.OrdinalIgnoreCase)
            || tz.Equals("Asia/Chongqing", StringComparison.OrdinalIgnoreCase)
            || tz.Equals("China Standard Time", StringComparison.OrdinalIgnoreCase);
        return cn ? Loc.T("locale.warn.follow-system-cn") : "";
    }

    private bool TryReadProxy(out string? proxy, out string? problem)
    {
        problem = null;
        if (_direct.Checked)
        {
            proxy = "direct";
            return true;
        }
        if (_custom.Checked)
        {
            string raw = _url.Text.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                proxy = null;
                problem = Loc.T("proxy.err.empty");
                return false;
            }
            proxy = raw;
            return true;
        }
        proxy = null;
        return true;
    }

    private void SelectMatchingPreset()
    {
        bool was = _syncingRegion;
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
            _syncingRegion = was;
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
        _userTouchedRegion = true;
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

    private static string DescribeEngine(Exception ex)
    {
        if (ex is EngineException ee)
        {
            try
            {
                if (JsonNode.Parse(ee.Json)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } m)
                    return m;
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to the exception text.
            }
        }
        return ex.Message;
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
