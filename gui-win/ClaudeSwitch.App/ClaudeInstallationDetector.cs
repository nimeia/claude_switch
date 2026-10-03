using System.Text.Json.Nodes;

namespace ClaudeSwitch.App;

internal enum ClaudeInstallSource { None, Native, Npm, Volta, WinGet, Unknown }

internal sealed record ClaudeInstallation(ClaudeInstallSource Source, string? Executable,
    string? Manager, Version? Version, IReadOnlyList<string> Copies, string? Problem = null)
{
    internal bool CanChange => Problem is null && Source != ClaudeInstallSource.Unknown;
}

internal sealed record ClaudeInstallPaths(string Home, string Local, string Roaming,
    string ProgramFiles, string VoltaHome, string SearchPath)
{
    internal static ClaudeInstallPaths Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetEnvironmentVariable("VOLTA_HOME") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta"),
        Environment.GetEnvironmentVariable("PATH") ?? "");
    internal string Native => Path.Combine(Home, ".local", "bin", "claude.exe");

    internal IEnumerable<string> FindAll(string name)
    {
        var directories = SearchPath.Split(Path.PathSeparator).Concat(name == "claude"
            ? new[] { Path.GetDirectoryName(Native)!, Path.Combine(VoltaHome, "bin"), Path.Combine(Roaming, "npm"),
                Path.Combine(Local, "Microsoft", "WinGet", "Links") } : []);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (string ext in new[] { ".exe", ".cmd", ".bat", ".com" })
            {
                string path;
                try { path = Path.GetFullPath(Path.Combine(dir.Trim().Trim('"'), name + ext)); }
                catch (ArgumentException) { continue; }
                if (File.Exists(path) && seen.Add(path)) { yield return path; break; }
            }
        }
    }
}

internal sealed class ClaudeInstallationDetector(IClaudeCommandRunner runner, ClaudeInstallPaths? paths = null)
{
    internal async Task<ClaudeInstallation> DetectAsync(CancellationToken token)
    {
        var p = paths ?? ClaudeInstallPaths.Current;
        var copies = p.FindAll("claude").ToList();
        string? launchable = copies.FirstOrDefault();
        string? npm = p.FindAll("npm").FirstOrDefault();
        string? prefix = null;
        if (npm is not null)
        {
            var result = await ProbeAsync(new(npm, "prefix", "-g"), token);
            if (result?.ExitCode == 0 && Path.IsPathFullyQualified(result.Output.Trim()))
            {
                prefix = result.Output.Trim();
                foreach (var path in new[] { Path.Combine(prefix, "claude.cmd"), Path.Combine(prefix, "claude.exe") })
                    if (File.Exists(path) && !copies.Contains(path, StringComparer.OrdinalIgnoreCase)) copies.Add(path);
            }
        }
        string? exe = copies.FirstOrDefault();
        if (exe is null) return new(ClaudeInstallSource.None, null, null, null, copies);
        var source = ClaudeInstallSource.Unknown;
        string? manager = null;
        string target = ResolveTarget(exe);
        if (IsWithin(target, Path.Combine(p.Local, "Microsoft", "WinGet", "Packages"))
            || IsWithin(target, Path.Combine(p.ProgramFiles, "WinGet", "Packages")))
        {
            if (target.Contains("Anthropic.ClaudeCode_", StringComparison.OrdinalIgnoreCase))
            {
                source = ClaudeInstallSource.WinGet;
                manager = p.FindAll("winget").FirstOrDefault();
            }
        }
        else if (IsWithin(exe, Path.Combine(p.VoltaHome, "bin")))
        {
            manager = p.FindAll("volta").FirstOrDefault();
            if (manager is not null)
            {
                var which = await ProbeAsync(new(manager, "which", "claude"), token);
                if (which?.ExitCode == 0 && File.Exists(which.Output.Trim()) && IsWithin(which.Output.Trim(),
                    Path.Combine(p.VoltaHome, "tools", "image", "packages", "@anthropic-ai", "claude-code")))
                    source = ClaudeInstallSource.Volta;
            }
        }
        else if (prefix is not null && SamePath(Path.GetDirectoryName(exe)!, prefix)
            && IsClaudePackage(Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code", "package.json")))
        { source = ClaudeInstallSource.Npm; manager = npm; }
        else if (SamePath(exe, p.Native)) source = ClaudeInstallSource.Native;

        var versionResult = await ProbeAsync(new(exe, "--version"), token);
        var version = versionResult?.ExitCode == 0 ? ClaudeCli.ParseVersion(versionResult.Output) : null;
        string? problem = source == ClaudeInstallSource.Unknown ? Loc.T("install.unknownSource")
            : source == ClaudeInstallSource.WinGet && manager is null ? Loc.T("install.managerMissing")
            : launchable is null ? Loc.T("install.notOnPath") : null;
        return new(source, exe, manager, version, copies, problem);
    }

    private async Task<ClaudeCommandResult?> ProbeAsync(ClaudeCommand command, CancellationToken token)
    {
        try { return await runner.RunAsync(command, token, timeout: TimeSpan.FromSeconds(12)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    private static bool IsClaudePackage(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?["name"]?.GetValue<string>() == "@anthropic-ai/claude-code"; }
        catch (Exception) { return false; }
    }
    internal static string ResolveTarget(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path; }
        catch (IOException) { return path; }
        catch (UnauthorizedAccessException) { return path; }
    }
    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    internal static bool IsWithin(string path, string directory)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        return Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static void RefreshPath()
    {
        // Retain the app's launch environment (including custom toolchains), and
        // add directories registered since startup. Never write the user's PATH.
        var entries = new[] { Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) }
            .Where(s => s is not null).SelectMany(s => s!.Split(Path.PathSeparator))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, entries));
    }
}
