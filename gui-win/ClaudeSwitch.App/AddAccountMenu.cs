namespace ClaudeSwitch.App;

internal static class AddAccountMenu
{
    internal static ContextMenuStrip Create(Control owner, Action signIn, Action captureCurrent)
    {
        var menu = new ContextMenuStrip { Renderer = new SageToolStripRenderer() };
        menu.Items.Add(Loc.T("login.title"), null, (_, _) => Queue(signIn));
        menu.Items.Add(Loc.T("login.captureCurrent"), null, (_, _) => Queue(captureCurrent));

        // Closed fires inside ToolStripDropDown.SetVisibleCore. WinForms still
        // needs the handle after that event returns, so keep the menu for the
        // owner's lifetime and reuse it on the next click.
        void DisposeWithOwner(object? sender, EventArgs e) => menu.Dispose();
        owner.Disposed += DisposeWithOwner;
        menu.Disposed += (_, _) => owner.Disposed -= DisposeWithOwner;
        return menu;

        void Queue(Action action)
        {
            if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated) return;
            owner.BeginInvoke(() =>
            {
                if (!owner.IsDisposed && !owner.Disposing) action();
            });
        }
    }
}
