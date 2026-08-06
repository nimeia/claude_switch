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

        Text = "欢迎使用 Claude Switch";
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
            Text = "步骤 1 / 3",
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
            Text = "上一步",
            Enabled = false,
        };
        _btnSkip = new SecondaryButton { Text = "跳过引导" };
        _btnNext = new PrimaryButton { Text = "下一步" };

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
                    _resultLabel.Text = "已成功添加当前登录账号。可以开始使用了。";
                }
                _btnNext.Text = "开始使用";
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
                        "暂时无法添加：\n" + ex.Message +
                        "\n\n可先跳过，稍后在主界面点击「添加当前登录」。\n" +
                        "演示可用：启动参数 --fixture <目录>";
                }
                _btnNext.Text = "仍进入主界面";
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
                _stepLabel.Text = "步骤 1 / 3 · 欢迎";
                _btnNext.Text = "下一步";
                AddTitle("用更少心智成本切换 Claude 账号");
                AddBody(
                    "Claude Switch 把多账号登录、用量查看和自动切换放在一处。\n\n" +
                    "· 卡片一览 5 小时 / 7 天用量\n" +
                    "· 一键切换，接近限流时可自动换号\n" +
                    "· 托盘常驻，关掉窗口也不会退出（Shift+关闭可退出）");
                break;
            case 1:
                _stepLabel.Text = "步骤 2 / 3 · 使用方式";
                _btnNext.Text = "下一步";
                AddTitle("三步上手");
                AddBody(
                    "1. 在本机用 Claude Code 登录某个账号\n" +
                    "2. 回到这里点「添加当前登录」收入托管列表\n" +
                    "3. 选中账号后点绿色「切换到该账号」，或双击卡片；也可开启自动切换\n\n" +
                    "提示：拖拽卡片可调整列表顺序；托盘通知会隐藏邮箱中间字符。");
                break;
            case 2:
                _stepLabel.Text = "步骤 3 / 3 · 添加账号";
                _btnNext.Text = "添加当前登录";
                AddTitle("捕获本机当前登录");
                _resultLabel = MakeBody(
                    "请确认 Claude Code 已登录，然后点击右下角「添加当前登录」。\n\n" +
                    "若你只想先看演示界面，可点「跳过引导」，\n" +
                    "或用启动参数 --fixture 打开示例数据。");
                _content.Controls.Add(_resultLabel);
                break;
            default:
                _stepLabel.Text = "完成";
                _btnNext.Text = "开始使用";
                AddTitle("一切就绪");
                AddBody("主窗口将打开账号列表。可随时在右上角切换浅色 / 深色主题。");
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
