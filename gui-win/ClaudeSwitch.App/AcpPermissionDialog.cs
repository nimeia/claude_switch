using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

/// <summary>
/// Answers the agent's <c>session/request_permission</c>.
/// </summary>
/// <remarks>
/// <para>
/// This dialog is the reason a supervised ACP run can be trusted at all. In a
/// terminal, a permission prompt is a line of text a watching human answers; a
/// program driving that terminal would have to type into it blind and could
/// answer the wrong question. Over ACP the request is an addressed RPC carrying
/// its own option list, so the answer is unambiguous — and the agent's turn
/// simply waits until this returns.
/// </para>
/// <para>
/// Options come from the agent, not from us. Each carries an <c>optionId</c> we
/// echo back, a display <c>name</c>, and a <c>kind</c> (<c>allow_once</c>,
/// <c>allow_always</c>, <c>reject_once</c>, …) used only to colour the buttons.
/// Inventing an option id here would be answering a question the agent did not
/// ask.
/// </para>
/// </remarks>
internal sealed class AcpPermissionDialog : Form
{
    /// <summary>The chosen <c>optionId</c>, or null when denied.</summary>
    public string? SelectedOptionId { get; private set; }

    private AcpPermissionDialog(string title, string? detail, IReadOnlyList<PermissionOption> options)
    {
        Text = Loc.T("acp.perm.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgSurface;
        Font = Theme.FontBody;
        ForeColor = Theme.TextPrimary;
        Icon = AppIcon.Get();

        var layout = new DialogLayout(this, textWidth: 400);

        var heading = layout.Text(Loc.T("acp.perm.heading"), Theme.FontHeading, Theme.TextPrimary);
        var what = layout.Text(title, Theme.FontName, Theme.TextPrimary);

        var controls = new List<Control> { heading, what };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            controls.Add(layout.Text(Truncate(detail, 600), Theme.FontSmall, Theme.TextSecondary));
        }

        var buttons = new List<ThemedButton>();
        foreach (var option in options)
        {
            ThemedButton button = option.Kind switch
            {
                "allow_always" or "allow_once" => new PrimaryButton(),
                "reject_always" or "reject_once" => new DangerButton(),
                _ => new SecondaryButton(),
            };
            button.Text = option.Name;
            string id = option.OptionId;
            button.Click += (_, _) =>
            {
                SelectedOptionId = id;
                DialogResult = DialogResult.OK;
            };
            buttons.Add(button);
        }

        // A window closed with Esc or the title bar must deny, never approve —
        // so there is always a way out that does not grant anything.
        var deny = new SecondaryButton
        {
            Text = Loc.T("acp.perm.deny"),
            DialogResult = DialogResult.Cancel,
        };
        buttons.Add(deny);

        controls.AddRange(buttons);
        Controls.AddRange([.. controls]);
        layout.ActionRow([.. buttons]);
        CancelButton = deny;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private sealed record PermissionOption(string OptionId, string Name, string? Kind);

    /// <summary>
    /// Show the dialog for one request and return the chosen option id.
    /// </summary>
    /// <remarks>
    /// Marshals onto the UI thread itself: the caller is the ACP reader task, so
    /// this is always invoked from a background thread.
    /// </remarks>
    public static Task<string?> AskAsync(Control owner, JsonNode request)
    {
        string title = request["toolCall"]?["title"]?.GetValue<string>()
            ?? request["toolCall"]?["kind"]?.GetValue<string>()
            ?? Loc.T("acp.perm.unnamed");

        string? detail = DescribeToolCall(request["toolCall"]);

        var options = new List<PermissionOption>();
        if (request["options"] is JsonArray arr)
        {
            foreach (var o in arr)
            {
                if (o?["optionId"]?.GetValue<string>() is not { Length: > 0 } id) continue;
                string name = o["name"]?.GetValue<string>() ?? id;
                options.Add(new PermissionOption(id, name, o["kind"]?.GetValue<string>()));
            }
        }

        // No options means there is nothing we could legitimately answer with.
        if (options.Count == 0) return Task.FromResult<string?>(null);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Show()
        {
            try
            {
                using var dlg = new AcpPermissionDialog(title, detail, options);
                var result = dlg.ShowDialog(owner);
                tcs.TrySetResult(result == DialogResult.OK ? dlg.SelectedOptionId : null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // Window went away mid-prompt: deny rather than hang the turn.
                tcs.TrySetResult(null);
            }
        }

        if (owner.InvokeRequired) owner.BeginInvoke(Show);
        else Show();

        return tcs.Task;
    }

    /// <summary>Best-effort one-line summary of what the agent wants to do.</summary>
    private static string? DescribeToolCall(JsonNode? toolCall)
    {
        if (toolCall is null) return null;

        // Shell commands and file paths are the two that matter most to a human
        // deciding whether to approve, so surface them ahead of raw input JSON.
        if (toolCall["rawInput"] is { } raw)
        {
            if (raw["command"]?.GetValue<string>() is { Length: > 0 } command) return command;
            if (raw["file_path"]?.GetValue<string>() is { Length: > 0 } path) return path;
        }

        if (toolCall["locations"] is JsonArray locs && locs.Count > 0)
        {
            var paths = locs
                .Select(l => l?["path"]?.GetValue<string>())
                .Where(p => !string.IsNullOrEmpty(p));
            string joined = string.Join(", ", paths);
            if (joined.Length > 0) return joined;
        }

        return toolCall["rawInput"]?.ToJsonString();
    }
}
