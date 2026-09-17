using System.Windows.Forms;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public class AppProxyDialogTests
{
    [Fact]
    public void Choices_stack_without_overlapping()
    {
        using var lang = Loc.Scoped("zh-Hans");
        using var dlg = new AppProxyDialog(engine: null!, stored: null);
        var radios = dlg.Controls.OfType<RadioButton>().OrderBy(c => c.Top).ToList();
        Assert.Equal(3, radios.Count);
        var url = dlg.Controls.OfType<TextBox>().Single(t => t.Name == "proxyUrl");
        for (int i = 1; i < radios.Count; i++)
        {
            Assert.True(
                radios[i].Top >= radios[i - 1].Bottom,
                $"radio {i} overlaps previous");
        }
        Assert.True(url.Top >= radios[^1].Bottom);
        Assert.True(url.Left > radios[^1].Left);
        Assert.Contains(
            dlg.Controls.OfType<Label>(),
            l => l.Text.Contains("本软件", StringComparison.Ordinal));
    }

    [Fact]
    public void Stored_url_selects_custom()
    {
        using var dlg = new AppProxyDialog(engine: null!, stored: "http://127.0.0.1:7897");
        var custom = dlg.Controls.OfType<RadioButton>()
            .Single(r => r.Text == Loc.T("appProxy.choice.custom"));
        Assert.True(custom.Checked);
        Assert.Equal("http://127.0.0.1:7897", dlg.Controls["proxyUrl"]!.Text);
        Assert.True(dlg.Controls["proxyUrl"]!.Enabled);
    }
}
