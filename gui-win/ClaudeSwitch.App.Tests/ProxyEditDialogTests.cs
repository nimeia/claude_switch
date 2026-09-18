using System.Drawing;
using System.Windows.Forms;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Per-account proxy dialog: radios and the URL box must stack, not sit on the
/// same y. RadioButton AutoSize + MaximumSize (width, 0) used to collapse
/// Height to 0 before the control was parented, which is how the choices
/// overlapped on screen.
/// </summary>
public class ProxyEditDialogTests
{
    [Fact]
    public void Choices_stack_without_overlapping()
    {
        using var lang = Loc.Scoped("zh-Hans");
        using var dlg = new ProxyEditDialog(new AccountCardModel
        {
            Number = 2,
            Email = "liutong.pub@gmail.com",
        });

        var radios = dlg.Controls.OfType<RadioButton>().OrderBy(c => c.Top).ToList();
        Assert.Equal(3, radios.Count);
        var url = dlg.Controls.OfType<TextBox>().Single(t => t.Name == "proxyUrl");

        for (int i = 0; i < radios.Count; i++)
        {
            Assert.True(
                radios[i].Height >= Theme.FontBody.Height,
                $"radio {i} height {radios[i].Height} is shorter than the font");
        }

        for (int i = 1; i < radios.Count; i++)
        {
            Assert.True(
                radios[i].Top >= radios[i - 1].Bottom,
                $"radio {i} top {radios[i].Top} overlaps previous bottom {radios[i - 1].Bottom}");
        }

        Assert.True(
            url.Top >= radios[^1].Bottom,
            $"URL top {url.Top} overlaps last radio bottom {radios[^1].Bottom}");
        Assert.True(url.Height > 0);
        Assert.True(
            url.Left > radios[^1].Left,
            "URL should sit indented under the custom option");

        var labels = dlg.Controls.OfType<Label>().OrderBy(c => c.Top).ToList();
        Assert.Contains(labels, l => l.Text.Contains("全局代理", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Text.Contains("生效时机", StringComparison.Ordinal));
        var intro = labels.Where(l => l.Bottom <= radios[0].Top).ToList();
        Assert.True(intro.Count >= 4, "title, email, hint and when must sit above the radios");
        Assert.Contains(labels, l => l.Bottom > radios[^1].Bottom && l.Text.Contains("出口地区"));
    }

    [Fact]
    public void Stored_url_selects_custom_and_fills_the_box()
    {
        using var dlg = new ProxyEditDialog(new AccountCardModel
        {
            Number = 1,
            Email = "a@b.c",
            Proxy = "http://127.0.0.1:7897",
        });

        var radios = dlg.Controls.OfType<RadioButton>().ToList();
        var custom = radios.Single(r => r.Text == Loc.T("proxy.choice.custom"));
        Assert.True(custom.Checked);
        var url = dlg.Controls.OfType<TextBox>().Single(t => t.Name == "proxyUrl");
        Assert.Equal("http://127.0.0.1:7897", url.Text);
        Assert.True(url.Enabled);
    }

    [Fact]
    public void Japan_preset_fills_tokyo_and_japanese()
    {
        using var dlg = new ProxyEditDialog(new AccountCardModel
        {
            Number = 1,
            Email = "a@b.c",
        });
        var combo = dlg.Controls.OfType<ComboBox>().Single();
        int jp = Enumerable.Range(0, combo.Items.Count)
            .First(i => combo.Items[i] is ProxyEditDialog.RegionChoice c && c.Key == "jp");
        combo.SelectedIndex = jp;
        Assert.Equal("Asia/Tokyo", dlg.Controls["timezone"]!.Text);
        Assert.Equal("japanese", dlg.Controls["language"]!.Text);
    }

    [Fact]
    public void Stored_tokyo_selects_the_japan_preset()
    {
        using var dlg = new ProxyEditDialog(new AccountCardModel
        {
            Number = 1,
            Email = "a@b.c",
            Timezone = "Asia/Tokyo",
            Language = "japanese",
        });
        var combo = dlg.Controls.OfType<ComboBox>().Single();
        var selected = Assert.IsType<ProxyEditDialog.RegionChoice>(combo.SelectedItem);
        Assert.Equal("jp", selected.Key);
    }

    [Fact]
    public void PreviewText_describes_follow_system_and_an_override()
    {
        using var lang = Loc.Scoped("en");
        Assert.Contains("this machine", ProxyEditDialog.PreviewText(null, null, "Asia/Shanghai", null), StringComparison.OrdinalIgnoreCase);
        string over = ProxyEditDialog.PreviewText("America/New_York", "english", "Asia/Shanghai", "en_US.UTF-8");
        Assert.Contains("America/New_York", over, StringComparison.Ordinal);
        Assert.Contains("english", over, StringComparison.Ordinal);
        Assert.Contains("en_US.UTF-8", over, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowSystemCnWarning_only_when_the_machine_is_china_and_unset()
    {
        using var lang = Loc.Scoped("en");
        Assert.NotEmpty(ProxyEditDialog.FollowSystemCnWarning("Asia/Shanghai", null));
        Assert.Empty(ProxyEditDialog.FollowSystemCnWarning("Asia/Shanghai", "America/New_York"));
        Assert.Empty(ProxyEditDialog.FollowSystemCnWarning("America/New_York", null));
    }

    [Fact]
    public void Dialog_shows_the_machine_timezone_and_a_preview()
    {
        using var lang = Loc.Scoped("zh-Hans");
        using var dlg = new ProxyEditDialog(new AccountCardModel
        {
            Number = 1,
            Email = "a@b.c",
            Timezone = "America/New_York",
            Language = "english",
        });
        var os = dlg.Controls.OfType<Label>().Single(l => l.Name == "osTimezone");
        Assert.Contains("本机时区", os.Text, StringComparison.Ordinal);
        var preview = dlg.Controls.OfType<Label>().Single(l => l.Name == "localePreview");
        Assert.Contains("timeZone=America/New_York", preview.Text, StringComparison.Ordinal);
        var warn = dlg.Controls.OfType<Label>().Single(l => l.Name == "localeWarn");
        Assert.Equal("", warn.Text);
    }

    [Fact]
    public void Long_preview_and_detect_result_stay_stacked_inside_the_dialog()
    {
        using var lang = Loc.Scoped("zh-Hans");
        using var dlg = new ProxyEditDialog(new AccountCardModel
        {
            Number = 2,
            Email = "liutong.pub@gmail.com",
            Timezone = "America/New_York",
            Language = "english",
        });

        var preview = dlg.Controls.OfType<Label>().Single(l => l.Name == "localePreview");
        var detect = dlg.Controls.OfType<Label>().Single(l => l.Name == "detectStatus");
        var os = dlg.Controls.OfType<Label>().Single(l => l.Name == "osTimezone");
        detect.Text = Loc.T(
            "locale.detect.ok",
            "130.130.102.112",
            "United States",
            "America/New_York",
            "english");

        Assert.Contains("timeZone=America/New_York", preview.Text, StringComparison.Ordinal);
        Assert.True(preview.Height > 0);
        Assert.True(detect.Height > 0);
        Assert.True(
            detect.Top >= preview.Bottom,
            $"detect top {detect.Top} overlaps preview bottom {preview.Bottom}");
        Assert.True(
            preview.Top >= os.Bottom,
            $"preview top {preview.Top} overlaps os bottom {os.Bottom}");

        // Child.Visible is false until the form is shown; Location is still set.
        var buttons = dlg.Controls.OfType<Button>().Where(b => b.Name == "detect" || b is PrimaryButton or SecondaryButton).ToList();
        Assert.NotEmpty(buttons);
        int buttonsTop = buttons.Min(b => b.Top);
        Assert.True(
            buttonsTop >= detect.Bottom,
            $"buttons top {buttonsTop} overlap detect bottom {detect.Bottom}");
        foreach (var b in buttons)
        {
            Assert.True(
                b.Bottom <= dlg.ClientSize.Height,
                $"{b.Name} bottom {b.Bottom} is clipped by client {dlg.ClientSize.Height}");
            Assert.True(
                b.Right <= dlg.ClientSize.Width,
                $"{b.Name} right {b.Right} is clipped by client {dlg.ClientSize.Width}");
        }
        Assert.True(preview.Bottom <= dlg.ClientSize.Height);
        Assert.True(detect.Bottom <= dlg.ClientSize.Height);
    }

    [Fact]
    public void Detect_button_is_on_the_dialog()
    {
        using var dlg = new ProxyEditDialog(new AccountCardModel { Number = 1, Email = "a@b.c" });
        var detect = dlg.Controls.OfType<Button>().First(b => b.Name == "detect");
        Assert.Equal(Loc.T("locale.detect"), detect.Text);
        Assert.False(detect.Enabled, "no engine in this test, so probing is off");
    }

    [Fact]
    public void LooksLikeIana_accepts_area_location_and_utc()
    {
        Assert.True(ProxyEditDialog.LooksLikeIana("America/Los_Angeles"));
        Assert.True(ProxyEditDialog.LooksLikeIana("UTC"));
        Assert.True(ProxyEditDialog.LooksLikeIana("America/Argentina/Buenos_Aires"));
        Assert.False(ProxyEditDialog.LooksLikeIana("PST"));
        Assert.False(ProxyEditDialog.LooksLikeIana("America/Los Angeles"));
    }

    [Fact]
    public void DialogLayout_Radio_measures_before_the_control_is_parented()
    {
        using var form = new Form
        {
            Font = Theme.FontBody,
            BackColor = Theme.BgSurface,
        };
        var layout = new DialogLayout(form, textWidth: 400, pad: 20, gap: 8);
        var a = layout.Radio(new RadioButton(), "系统代理（环境变量或 Windows 设置）");
        var b = layout.Radio(new RadioButton(), "直连（不使用代理）");
        var c = layout.Radio(new RadioButton(), "自定义 HTTP 代理");

        Assert.True(a.Height >= Theme.FontBody.Height);
        Assert.True(b.Top >= a.Bottom);
        Assert.True(c.Top >= b.Bottom);
        Assert.Empty(form.Controls);
    }
}
