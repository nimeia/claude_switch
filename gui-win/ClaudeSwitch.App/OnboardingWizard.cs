using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

/// <summary>
/// First-run multi-step wizard: welcome → how it works → add account → done.
/// </summary>
internal sealed class OnboardingWizard : Form
{
    private int _step;
    private readonly Panel _content;
    private readonly Label _stepLabel;
    private readonly PrimaryButton _btnNext;
    private readonly SecondaryButton _btnBack;
    private readonly SecondaryButton _btnSkip;
    private readonly Engine _engine;
    private readonly Action _onFinished;
    private Label? _resultLabel;

    private readonly int _pad;
    private readonly int _gap;
    private readonly int _contentW;
    /// <summary>Grows to the tallest step visited so text never clips; never shrinks back.</summary>
    private int _contentH;

    public OnboardingWizard(Engine engine, Action onFinished)
    {
        _engine = engine;
        _onFinished = onFinished;

        Text = Loc.T("wizard.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        BackColor = Theme.BgSurface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.FontBody;
        Icon = AppIcon.Get();

        // Step text measures itself and the frame grows to fit — fixed pixel boxes
        // clip the last lines once Windows scaling is above 100%.
        _pad = Theme.Scale(this, 24);
        _gap = Theme.Scale(this, 20);
        _contentW = Theme.Scale(this, 470);
        _contentH = Theme.Scale(this, 240);

        _stepLabel = new Label
        {
            Location = new Point(_pad, Theme.Scale(this, 16)),
            AutoSize = true,
            Font = Theme.FontSmall,
            ForeColor = Theme.TextMuted,
            Text = Loc.T("wizard.step", 1),
        };
        // AutoSize only measures once the label joins a parent — measure now so the
        // content panel below can stack from Bottom.
        _stepLabel.Size = _stepLabel.GetPreferredSize(Size.Empty);

        _content = new Panel
        {
            Location = new Point(_pad, _stepLabel.Bottom + Theme.Scale(this, 12)),
            Size = new Size(_contentW, _contentH),
            BackColor = Theme.BgSurface,
        };

        _btnBack = new SecondaryButton
        {
            Text = Loc.T("wizard.back"),
            Enabled = false,
        };
        _btnSkip = new SecondaryButton { Text = Loc.T("wizard.skip") };
        _btnNext = new PrimaryButton { Text = Loc.T("wizard.next") };

        _btnBack.Click += (_, _) =>
        {
            if (_step > 0)
            {
                _step--;
                RenderStep();
            }
        };
        _btnSkip.Click += (_, _) => Finish(skipped: true);
        _btnNext.Click += (_, _) => OnNext();

        Controls.AddRange([_stepLabel, _content, _btnBack, _btnSkip, _btnNext]);
        RenderStep();
    }

    private void OnNext()
    {
        if (_step == 2)
        {
            // Try add current login.
            try
            {
                Cursor = Cursors.WaitCursor;
                _engine.Call("add_current", new { });
                if (_resultLabel is not null)
                {
                    _resultLabel.ForeColor = Theme.PrimaryDark;
                    _resultLabel.Text = Loc.T("wizard.added");
                }
                _btnNext.Text = Loc.T("wizard.start");
                _step = 3;
                _btnBack.Enabled = false;
                _btnSkip.Visible = false;
                FitFrame();
                return;
            }
            catch (Exception ex)
            {
                if (_resultLabel is not null)
                {
                    _resultLabel.ForeColor = Theme.UsageHigh;
                    _resultLabel.Text =
                        Loc.T("wizard.addFailed", ex.Message);
                }
                _btnNext.Text = Loc.T("wizard.continueAnyway");
                _step = 3;
                FitFrame();
                return;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        if (_step >= 3)
        {
            Finish(skipped: false);
            return;
        }

        _step++;
        RenderStep();
    }

    private void Finish(bool skipped)
    {
        UiPrefs.OnboardingDone = true;
        UiPrefs.Save();
        DialogResult = skipped ? DialogResult.Ignore : DialogResult.OK;
        _onFinished();
        Close();
    }

    private void RenderStep()
    {
        _content.Controls.Clear();
        _btnBack.Enabled = _step > 0 && _step < 3;
        _btnSkip.Visible = _step < 3;

        switch (_step)
        {
            case 0:
                _stepLabel.Text = Loc.T("wizard.step1");
                _btnNext.Text = Loc.T("wizard.next");
                AddTitle(Loc.T("wizard.step1.title"));
                AddBody(
                    Loc.T("wizard.step1.body"));
                break;
            case 1:
                _stepLabel.Text = Loc.T("wizard.step2");
                _btnNext.Text = Loc.T("wizard.next");
                AddTitle(Loc.T("wizard.step2.title"));
                AddBody(
                    Loc.T("wizard.step2.body"));
                break;
            case 2:
                _stepLabel.Text = Loc.T("wizard.step3");
                _btnNext.Text = Loc.T("wizard.addCurrent");
                AddTitle(Loc.T("wizard.step3.title"));
                _resultLabel = MakeBody(
                    Loc.T("wizard.step3.body"));
                _content.Controls.Add(_resultLabel);
                break;
            default:
                _stepLabel.Text = Loc.T("wizard.doneStep");
                _btnNext.Text = Loc.T("wizard.start");
                AddTitle(Loc.T("wizard.done.title"));
                AddBody(Loc.T("wizard.done.body"));
                break;
        }

        FitFrame();
    }

    private void AddTitle(string text) =>
        _content.Controls.Add(Measured(new Label
        {
            Text = text,
            Font = Theme.FontHeading,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new Point(0, 0),
            MaximumSize = new Size(_contentW, 0),
        }));

    private void AddBody(string text) => _content.Controls.Add(MakeBody(text));

    private Label MakeBody(string text) =>
        Measured(new Label
        {
            Text = text,
            Font = Theme.FontBody,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(_contentW, 0),
            Location = new Point(0, Theme.Scale(this, 40)),
        });

    /// <summary>AutoSize measures only once a label joins a parent — do it up front.</summary>
    private Label Measured(Label label)
    {
        label.Size = label.GetPreferredSize(new Size(_contentW, 0));
        return label;
    }

    /// <summary>
    /// Sizes the frame around the current step. Content height only grows, so the
    /// window settles on the tallest step instead of jumping back and forth.
    /// </summary>
    private void FitFrame()
    {
        int needed = 0;
        foreach (Control c in _content.Controls)
            needed = Math.Max(needed, c.Bottom);

        _contentH = Math.Max(_contentH, needed);
        _content.Height = _contentH;

        int rowY = _content.Bottom + _gap;
        int right = _pad + _contentW;
        foreach (var b in new ThemedButton[] { _btnNext, _btnSkip })
        {
            b.Width = b.GetPreferredSize(Size.Empty).Width;
            b.Location = new Point(right - b.Width, rowY);
            right -= b.Width + Theme.Scale(this, 10);
        }
        _btnBack.Width = _btnBack.GetPreferredSize(Size.Empty).Width;
        _btnBack.Location = new Point(_pad, rowY);

        ClientSize = new Size(_pad + _contentW + _pad, rowY + _btnNext.RowHeight + _pad);
    }
}

/// <summary>UI prefs beyond theme (onboarding flag, etc.).</summary>
internal static class UiPrefs
{
    public static bool OnboardingDone { get; set; }
    /// <summary>When true, mask emails in list cards (same policy as chrome/status).</summary>
    public static bool HideEmail { get; set; }

    /// <summary>
    /// Chosen UI language code, or empty to follow the OS.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="Loc"/> before any window exists, so it must survive a
    /// prefs file that predates this setting — an absent line means "follow the
    /// OS", which is what an upgrading user expects to keep happening.
    /// </remarks>
    public static string Language { get; set; } = "";

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeSwitch",
            "ui-prefs.ini");

    public static void Load()
    {
        Theme.LoadPrefs();
        try
        {
            if (!File.Exists(Path)) return;
            foreach (var line in File.ReadAllLines(Path))
            {
                if (line.StartsWith("onboarding_done=", StringComparison.OrdinalIgnoreCase))
                    OnboardingDone = line.Contains("1") || line.Contains("true", StringComparison.OrdinalIgnoreCase);
                if (line.StartsWith("hide_email=", StringComparison.OrdinalIgnoreCase))
                    HideEmail = line.Contains("1") || line.Contains("true", StringComparison.OrdinalIgnoreCase);
                if (line.StartsWith("language=", StringComparison.OrdinalIgnoreCase))
                    Language = line["language=".Length..].Trim();
            }
        }
        catch { /* ignore */ }
    }

    public static void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path,
                $"theme={(Theme.Mode == ThemeMode.Dark ? "dark" : "light")}\n" +
                $"onboarding_done={(OnboardingDone ? "1" : "0")}\n" +
                $"hide_email={(HideEmail ? "1" : "0")}\n");
        }
        catch { /* ignore */ }
    }
}
