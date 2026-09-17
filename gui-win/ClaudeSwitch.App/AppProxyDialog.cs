using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// App-wide proxy for Claude Switch itself and for accounts set to "system".
/// </summary>
internal sealed class AppProxyDialog : Form
{
    private readonly Engine? _engine;
    private readonly RadioButton _system = new();
    private readonly RadioButton _direct = new();
    private readonly RadioButton _custom = new();
    private readonly TextBox _url = new();
    private readonly Label _detectStatus = new();
    private readonly SecondaryButton _detect = new();
    private readonly System.Windows.Forms.Timer _urlDebounce = new() { Interval = 600 };
    private bool _ready;
    private int _detectGen;

    public string? ProxyResult { get; private set; }

    public AppProxyDialog(Engine? engine, string? stored)
    {
        _engine = engine;
        Text = Loc.T("appProxy.title");
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

        var title = layout.Text(Loc.T("appProxy.title"), Theme.FontHeading, Theme.TextPrimary);
        var hint = layout.Text(Loc.T("appProxy.hint"), Theme.FontSmall, Theme.TextMuted);
        var when = layout.Text(Loc.T("appProxy.when"), Theme.FontSmall, Theme.TextMuted);

        layout.Radio(_system, Loc.T("appProxy.choice.system"));
        layout.Radio(_direct, Loc.T("appProxy.choice.direct"));
        layout.Radio(_custom, Loc.T("appProxy.choice.custom"));

        int indent = layout.Scale(22);
        _url.Name = "proxyUrl";
        _url.Location = new Point(layout.Left + indent, layout.Y);
        _url.Width = layout.TextWidth - indent;
        _url.Height = layout.Scale(Theme.ControlHeight);
        _url.Font = Theme.FontBody;
        _url.BorderStyle = BorderStyle.FixedSingle;
        _url.BackColor = Theme.BgApp;
        _url.ForeColor = Theme.TextPrimary;
        _url.PlaceholderText = Loc.T("proxy.url.placeholder");
        layout.Advance(_url);

        _detectStatus.Name = "detectStatus";
        _detectStatus.Font = Theme.FontSmall;
        _detectStatus.ForeColor = Theme.TextMuted;
        _detectStatus.AutoSize = true;
        _detectStatus.MaximumSize = new Size(layout.TextWidth, 0);
        _detectStatus.Location = new Point(layout.Left, layout.Y);
        _detectStatus.Text = Loc.T("locale.detect.ok", "255.255.255.255", "United States", "America/Los_Angeles", "english");
        _detectStatus.Size = _detectStatus.GetPreferredSize(new Size(layout.TextWidth, 0));
        _detectStatus.MinimumSize = new Size(0, _detectStatus.Height);
        _detectStatus.Text = "";
        layout.Advance(_detectStatus);

        stored ??= "";
        if (string.Equals(stored, "direct", StringComparison.OrdinalIgnoreCase))
            _direct.Checked = true;
        else if (stored.Length > 0)
        {
            _custom.Checked = true;
            _url.Text = stored;
        }
        else
            _system.Checked = true;

        void SyncUrlEnabled() => _url.Enabled = _custom.Checked;
        _system.CheckedChanged += (_, _) => { SyncUrlEnabled(); if (_system.Checked) OnProxyChoiceChanged(); };
        _direct.CheckedChanged += (_, _) => { SyncUrlEnabled(); if (_direct.Checked) OnProxyChoiceChanged(); };
        _custom.CheckedChanged += (_, _) => { SyncUrlEnabled(); if (_custom.Checked) OnProxyChoiceChanged(); };
        SyncUrlEnabled();

        _url.TextChanged += (_, _) =>
        {
            if (!_ready || !_custom.Checked) return;
            _urlDebounce.Stop();
            _urlDebounce.Start();
        };
        _urlDebounce.Tick += (_, _) =>
        {
            _urlDebounce.Stop();
            BeginDetect();
        };

        _detect.Name = "detect";
        _detect.Text = Loc.T("locale.detect");
        _detect.Enabled = _engine is not null;
        _detect.Click += (_, _) => BeginDetect();

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
            ProxyResult = proxy;
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.AddRange([
            title, hint, when,
            _system, _direct, _custom, _url,
            _detectStatus, _detect, btnOk, btnCancel,
        ]);
        layout.ActionRow(_detect, btnOk, btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        ActiveControl = _custom.Checked ? _url : _system;
        _ready = true;
        if (_engine is not null)
            Shown += (_, _) => BeginDetect();
        FormClosed += (_, _) =>
        {
            _detectGen++;
            _urlDebounce.Stop();
            _urlDebounce.Dispose();
        };
    }

    public static bool TryEdit(IWin32Window owner, Engine engine, string? stored, out string? proxy)
    {
        proxy = stored;
        using var dlg = new AppProxyDialog(engine, stored);
        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;
        proxy = dlg.ProxyResult;
        return true;
    }

    private void OnProxyChoiceChanged()
    {
        if (!_ready) return;
        _urlDebounce.Stop();
        BeginDetect();
    }

    private void BeginDetect()
    {
        if (_engine is not { } engine || IsDisposed) return;
        if (!TryReadProxy(out string? proxy, out string? problem))
        {
            _detectStatus.Text = problem ?? Loc.T("locale.detect.needUrl");
            _detectStatus.ForeColor = Theme.TextMuted;
            return;
        }
        int gen = ++_detectGen;
        _detect.Enabled = false;
        _detectStatus.ForeColor = Theme.TextMuted;
        _detectStatus.Text = Loc.T("locale.detect.working");
        bool osOnly = proxy is null;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var node = engine.Call("locale_detect", new { proxy = proxy ?? "", osOnly });
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
        _detect.Enabled = true;
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
        string timezone = node["timezone"]?.GetValue<string>() ?? "";
        string language = node["language"]?.GetValue<string>() ?? "";
        _detectStatus.ForeColor = Theme.TextSecondary;
        _detectStatus.Text = Loc.T("locale.detect.ok", ip, country, timezone, language);
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
}
