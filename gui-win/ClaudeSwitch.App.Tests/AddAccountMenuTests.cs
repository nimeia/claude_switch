using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public sealed class AddAccountMenuTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Mouse_click_finishes_closing_before_running_the_selected_action(int index)
    {
        using var owner = NewOwner();
        int signedIn = 0, captured = 0;
        using var menu = AddAccountMenu.Create(owner, () => signedIn++, () => captured++);
        Show(menu, owner);

        // Use ToolStrip's mouse handlers, including OnItemClicked -> SetVisibleCore,
        // rather than calling the feature callback directly.
        MouseClick(menu, index);
        Assert.False(menu.Visible);
        Assert.False(menu.IsDisposed);
        Assert.Equal(0, signedIn + captured);
        Application.DoEvents();
        Assert.Equal(index == 0 ? 1 : 0, signedIn);
        Assert.Equal(index == 1 ? 1 : 0, captured);
    }

    [Fact]
    public void Both_actions_can_be_selected_repeatedly_from_the_same_menu()
    {
        using var owner = NewOwner();
        int signedIn = 0, captured = 0;
        using var menu = AddAccountMenu.Create(owner, () => signedIn++, () => captured++);
        for (int i = 0; i < 6; i++)
        {
            Show(menu, owner);
            MouseClick(menu, i % 2);
            Application.DoEvents();
            Assert.False(menu.Visible);
            Assert.False(menu.IsDisposed);
        }
        Assert.Equal(3, signedIn);
        Assert.Equal(3, captured);
    }

    [Fact]
    public void Dismissal_runs_no_action_and_allows_reopening()
    {
        using var owner = NewOwner();
        int calls = 0;
        using var menu = AddAccountMenu.Create(owner, () => calls++, () => calls++);
        Show(menu, owner);
        menu.Close(ToolStripDropDownCloseReason.Keyboard);
        Application.DoEvents();
        Assert.Equal(0, calls);
        Assert.False(menu.IsDisposed);
        Show(menu, owner);
        MouseClick(menu, 0);
        Application.DoEvents();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Owner_disposal_releases_menu_and_cancels_a_queued_action()
    {
        using var owner = NewOwner();
        int calls = 0;
        using var menu = AddAccountMenu.Create(owner, () => calls++, () => calls++);
        Show(menu, owner);
        MouseClick(menu, 0);
        owner.Dispose();
        Application.DoEvents();
        Assert.True(menu.IsDisposed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Modal_action_runs_after_menu_closes_and_menu_can_be_used_again()
    {
        using var owner = NewOwner();
        ContextMenuStrip? selectedMenu = null;
        int calls = 0;
        using var menu = AddAccountMenu.Create(owner, () =>
        {
            Assert.False(selectedMenu!.Visible);
            Assert.False(selectedMenu.IsDisposed);
            using var dialog = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000) };
            dialog.Shown += (_, _) => dialog.BeginInvoke(() => dialog.Close());
            dialog.ShowDialog(owner);
            calls++;
        }, () => calls++);
        selectedMenu = menu;
        Show(menu, owner);
        MouseClick(menu, 0);
        Application.DoEvents();
        Assert.Equal(1, calls);
        Show(menu, owner);
        MouseClick(menu, 1);
        Application.DoEvents();
        Assert.Equal(2, calls);
    }

    private static Form NewOwner()
    {
        var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
        owner.Show();
        Application.DoEvents();
        return owner;
    }

    private static void Show(ContextMenuStrip menu, Control owner)
    {
        menu.Show(owner, Point.Empty);
        menu.Location = new Point(-32000, -32000);
    }

    private static void MouseClick(ContextMenuStrip menu, int index)
    {
        var item = menu.Items[index];
        item.Select();
        var args = new MouseEventArgs(MouseButtons.Left, 1,
            item.Bounds.Left + item.Width / 2, item.Bounds.Top + item.Height / 2, 0);
        foreach (string method in new[] { "OnMouseDown", "OnMouseUp" })
            typeof(ToolStrip).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, [args]);
    }
}
