using System.Text.Json.Nodes;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

internal sealed class LoginWorkflow(Engine engine, ILoginProcess process)
{
    internal async Task<JsonNode> RunAsync(bool activate, JsonNode? recovery,
        Action<string> progress, CancellationToken token)
    {
        LoginLaunch? launch = null;
        bool committed = false;
        try
        {
            progress("login.checking");
            await process.CheckAvailableAsync(token);
            token.ThrowIfCancellationRequested();
            if (recovery is not null) LoginProcess.EnsureStopped(recovery["configDir"]!.GetValue<string>());
            progress("login.backingUp");
            var prepared = await Task.Run(() => engine.Call("login_begin", new { id = recovery?["id"]?.GetValue<string>() }));
            launch = LoginLaunch.Parse(prepared);
            token.ThrowIfCancellationRequested();
            if (recovery is null)
            {
                progress(prepared["backedUp"]?.GetValue<bool>() == true ? "login.waitingBackedUp" : "login.waiting");
                await process.LoginAsync(launch, token);
            }
            progress("login.verifying");
            var status = await process.StatusAsync(launch, token);
            token.ThrowIfCancellationRequested();
            progress("login.saving");
            // Cancellation stops at the commit boundary: an atomic import/switch
            // must finish and report its actual outcome, not an ambiguous cancel.
            var result = await Task.Run(() => engine.Call("login_commit", new { id = launch.Id, activate, status }));
            committed = true;
            return result;
        }
        finally
        {
            if (launch is not null && !committed)
            {
                // Preserve a completed authorization on verification/storage
                // failure for recovery. Explicit cancellation discards only
                // when its process is confirmed stopped.
                bool discard = (token.IsCancellationRequested && recovery is null)
                    || !File.Exists(Path.Combine(launch.ConfigDir, ".credentials.json"));
                try { LoginProcess.EnsureStopped(launch.ConfigDir); }
                catch { discard = false; }
                await Task.Run(() => engine.Call("login_cancel", new { id = launch.Id, discard }));
            }
        }
    }
}
