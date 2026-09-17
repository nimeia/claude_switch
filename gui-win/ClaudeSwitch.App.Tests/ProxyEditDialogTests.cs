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
