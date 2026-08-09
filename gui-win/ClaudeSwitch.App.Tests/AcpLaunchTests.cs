using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The launch environment and error mapping for a supervised ACP run.
/// </summary>
/// <remarks>
/// These are the parts that decide whether a run authenticates as the right
/// account and whether a failure is classified correctly — both were real bugs
/// during bring-up, not hypotheticals.
/// </remarks>
public class AcpLaunchTests
{
    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cswitch-acp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void BuildStartInfo_scrubs_auth_overrides()
    {
        // An inherited API key would silently override the account the launch
        // names — the one thing an account-scoped launch must not allow.
        string dir = MakeTempDir();
        try
        {
            var launch = new AcpLaunch { WorkingDirectory = dir, ConfigDir = dir };
            var psi = launch.BuildStartInfo();

            Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.False(psi.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
            Assert.False(psi.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_sets_the_account_profile()
    {
        string dir = MakeTempDir();
        try
        {
            var launch = new AcpLaunch { WorkingDirectory = dir, ConfigDir = dir };
            var psi = launch.BuildStartInfo();
            Assert.Equal(dir, psi.Environment["CLAUDE_CONFIG_DIR"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_omits_config_dir_for_the_default_login()
    {
        // Null means "this account already is the default login"; writing a
        // profile path there would point at a second copy of its token.
        string dir = MakeTempDir();
        try
        {
            var launch = new AcpLaunch { WorkingDirectory = dir, ConfigDir = null };
            var psi = launch.BuildStartInfo();
            Assert.False(psi.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_injects_the_proxy_in_both_cases()
    {
        // The adapter's SDK does not read Windows Internet Settings the way the
        // Claude Code CLI does; without this a proxied network answers 403
        // "Request not allowed", which reads exactly like an auth failure.
        string dir = MakeTempDir();
        try
        {
            var launch = new AcpLaunch
            {
                WorkingDirectory = dir,
                Proxy = "http://127.0.0.1:7890",
            };
            var psi = launch.BuildStartInfo();

            Assert.Equal("http://127.0.0.1:7890", psi.Environment["HTTPS_PROXY"]);
            Assert.Equal("http://127.0.0.1:7890", psi.Environment["https_proxy"]);
            Assert.Equal("http://127.0.0.1:7890", psi.Environment["HTTP_PROXY"]);
            Assert.Equal("http://127.0.0.1:7890", psi.Environment["http_proxy"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_injects_no_proxy_when_direct()
    {
        // The child inherits the parent environment, so "no proxy" cannot mean
        // "the variable is absent" — it means we did not *set* it. Comparing
        // against the parent is the only assertion that holds whether or not the
        // test host itself runs behind a proxy.
        string dir = MakeTempDir();
        try
        {
            var launch = new AcpLaunch { WorkingDirectory = dir, Proxy = null };
            var psi = launch.BuildStartInfo();

            string? inherited = Environment.GetEnvironmentVariable("HTTPS_PROXY");
            psi.Environment.TryGetValue("HTTPS_PROXY", out string? child);
            Assert.Equal(inherited, child);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_rejects_a_missing_working_directory()
    {
        var launch = new AcpLaunch
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid()),
        };
        Assert.Throws<DirectoryNotFoundException>(() => launch.BuildStartInfo());
    }

    [Fact]
    public void Adapter_search_prefers_a_repo_local_install()
    {
        // A dev checkout's tools/acp/node_modules must win over a global npm
        // install, so a version being tested is the one that runs.
        var roots = AcpLaunch.CandidateModuleRoots().ToList();
        int local = roots.FindIndex(r => r.Replace('\\', '/').EndsWith("tools/acp/node_modules", StringComparison.Ordinal));
        int global = roots.FindIndex(r => r.Replace('\\', '/').Contains("Roaming/npm/node_modules", StringComparison.Ordinal));

        Assert.True(local >= 0, "repo-local adapter root missing from the search list");
        Assert.True(global < 0 || local < global, "a global install must not shadow the repo-local one");
    }

    [Fact]
    public void RpcException_lifts_the_error_kind()
    {
        // Verbatim from a probe run with no proxy configured.
        var err = JsonNode.Parse("""
            {
              "code": -32603,
              "message": "Internal error: Failed to authenticate. API Error: 403 Request not allowed",
              "data": { "errorKind": "authentication_failed" }
            }
            """)!;

        var ex = AcpRpcException.FromErrorObject(err);
        Assert.Equal(-32603, ex.Code);
        Assert.Equal("authentication_failed", ex.ErrorKind);
        Assert.Contains("403 Request not allowed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RpcException_survives_an_error_object_without_data()
    {
        var err = JsonNode.Parse("""{ "code": -1, "message": "boom" }""")!;
        var ex = AcpRpcException.FromErrorObject(err);
        Assert.Null(ex.ErrorKind);
        Assert.Equal("boom", ex.Message);
    }
}

/// <summary>Reporting shape the window renders from.</summary>
public class AcpRunReportTests
{
    private static AcpTurn Turn(string state, string value) => new(state, value, null, TimeSpan.Zero);

    [Fact]
    public void A_clean_run_reports_no_continuations()
    {
        var report = new AcpRunReport("s1", [Turn("completed", "endTurn")], "completed", TimeSpan.Zero);
        Assert.Equal(0, report.Continuations);
        Assert.True(report.Succeeded);
    }

    [Fact]
    public void Continuations_count_turns_beyond_the_first()
    {
        var report = new AcpRunReport(
            "s1",
            [Turn("interrupted", "adapterCrash"), Turn("completed", "endTurn")],
            "completed",
            TimeSpan.FromSeconds(3));

        Assert.Equal(1, report.Continuations);
        Assert.True(report.Succeeded);
        Assert.Single(report.Turns, t => t.IsInterruption);
    }

    [Fact]
    public void An_auth_stop_is_not_a_success()
    {
        var report = new AcpRunReport(
            "s1",
            [Turn("interrupted", "auth")],
            "needsAuth",
            TimeSpan.Zero);

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.Continuations);
    }
}
