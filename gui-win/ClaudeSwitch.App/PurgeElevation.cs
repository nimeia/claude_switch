using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using ClaudeSwitch.Core;

namespace ClaudeSwitch.App;

internal enum PurgeRetryResult
{
    Completed = 0,
    Failed = 1,
    InvalidRequest = 2,
    ClaudeRunning = 3,
    PermissionDenied = 4,
    FileInUse = 5,
    Cancelled = 6,
}

/// <summary>A short-lived UAC helper that never opens an account store.</summary>
internal static class PurgeElevation
{
    internal const string Command = "--purge-machine-data";
    private static readonly (string Key, string Relative)[] Targets =
    [
        ("desktop-data", "Claude"),
        ("desktop-logs", Path.Combine("Claude", "Logs")),
        ("managed", "ClaudeCode"),
        ("managed-spaced", "Claude Code"),
    ];

    internal static string MachineRoot { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    internal static bool ValidTargets(IReadOnlyList<string> keys) =>
        keys.Count is > 0 and <= 4 && keys.All(key => Targets.Any(t => t.Key == key));

    internal static string? TargetForPath(string path)
    {
        // Compare exact known paths. A matching filename or a descendant is
        // insufficient to authorize an elevated operation.
        var root = MachineRoot;
        if (string.IsNullOrWhiteSpace(root)) return null;
        return Targets.FirstOrDefault(t => string.Equals(
            Path.Combine(root, t.Relative), path, StringComparison.OrdinalIgnoreCase)).Key;
    }

    internal static IReadOnlyList<string> RetryTargets(PurgeOutcomeView outcome) =>
        outcome.Failed.Where(f => f.Reason == "permission-denied")
            .Select(f => TargetForPath(f.Path)).OfType<string>().Distinct().ToArray();

    /// <summary>Called before normal startup and the single-instance mutex.</summary>
    internal static int RunCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] != Command || !ValidTargets(args.Skip(1).ToArray()))
            return (int)PurgeRetryResult.InvalidRequest;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return (int)PurgeRetryResult.PermissionDenied;
        try
        {
            var outcome = PurgeData.ParseOutcome(Engine.PurgeMachine(args.Skip(1).ToArray()));
            if (outcome.Failed.Count == 0) return (int)PurgeRetryResult.Completed;
            if (outcome.Failed.Any(f => f.Reason == "permission-denied"))
                return (int)PurgeRetryResult.PermissionDenied;
            if (outcome.Failed.Any(f => f.Reason == "file-in-use"))
                return (int)PurgeRetryResult.FileInUse;
            return (int)PurgeRetryResult.Failed;
        }
        catch (EngineException ex) when (ex.Code == 9) { return (int)PurgeRetryResult.ClaudeRunning; }
        catch (Exception) { return (int)PurgeRetryResult.Failed; }
    }

    internal static ProcessStartInfo StartInfo(IReadOnlyList<string> keys)
    {
        if (!ValidTargets(keys)) throw new ArgumentException("Invalid machine cleanup selection", nameof(keys));
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add(Command);
        foreach (var key in keys.Distinct()) start.ArgumentList.Add(key);
        return start;
    }

    internal static Task<PurgeRetryResult> RetryAsync(IReadOnlyList<string> keys) => Task.Run(async () =>
    {
        try
        {
            using var process = Process.Start(StartInfo(keys));
            if (process is null) return PurgeRetryResult.Failed;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return Enum.IsDefined(typeof(PurgeRetryResult), process.ExitCode)
                ? (PurgeRetryResult)process.ExitCode : PurgeRetryResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return PurgeRetryResult.Cancelled; }
        catch (Exception) { return PurgeRetryResult.Failed; }
    });

    internal static PurgeOutcomeView Reconcile(PurgeOutcomeView original,
        IReadOnlyList<string> retried, PurgeRetryResult result)
    {
        var deleted = original.Deleted.ToList();
        var failed = new List<(string Id, string Path, string Reason)>();
        foreach (var item in original.Failed)
        {
            if (item.Reason != "permission-denied" || TargetForPath(item.Path) is not { } key || !retried.Contains(key))
            {
                failed.Add(item);
                continue;
            }
            if (result == PurgeRetryResult.Completed || (result != PurgeRetryResult.Cancelled && Removed(item.Path)))
                deleted.Add((item.Id, item.Path));
            else
                failed.Add((item.Id, item.Path, result switch
                {
                    PurgeRetryResult.Cancelled => "admin-cancelled",
                    PurgeRetryResult.ClaudeRunning => "claude-running",
                    PurgeRetryResult.PermissionDenied => "permission-denied",
                    PurgeRetryResult.FileInUse => "file-in-use",
                    _ => "admin-failed",
                }));
        }
        return original with { Deleted = deleted, Failed = failed };
    }

    private static bool Removed(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception) { return false; } // Inaccessible does not mean deleted.
    }
}
