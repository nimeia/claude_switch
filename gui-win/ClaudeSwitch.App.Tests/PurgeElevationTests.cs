using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public class PurgeElevationTests
{
    private static string SystemPath(string relative) => Path.Combine(PurgeElevation.MachineRoot, relative);

    [Fact]
    public void Retry_is_limited_to_exact_system_paths_with_permission_failures()
    {
        var outcome = new PurgeOutcomeView([], [],
        [
            ("desktop", SystemPath("Claude"), "permission-denied"),
            ("managed", SystemPath("ClaudeCode"), "permission-denied"),
            ("locked", SystemPath("Claude Code"), "file-in-use"),
            ("user-data", @"C:\Users\example\Claude", "permission-denied"),
            ("descendant", SystemPath("Claude/Logs/service.log"), "permission-denied"),
            ("sibling", SystemPath("Claude-unrelated"), "permission-denied"),
        ], 0);
        Assert.Equal(new[] { "desktop-data", "managed" }, PurgeElevation.RetryTargets(outcome));
        Assert.Null(PurgeElevation.TargetForPath(PurgeElevation.MachineRoot));
    }

    [Theory]
    [InlineData("C:\\ProgramData\\Claude")]
    [InlineData("../Claude")]
    [InlineData("desktop-data --fixture C:\\Users")]
    [InlineData("accounts")]
    public void Helper_rejects_paths_and_extra_commands(string token)
    {
        Assert.False(PurgeElevation.ValidTargets([token]));
        Assert.Equal((int)PurgeRetryResult.InvalidRequest,
            PurgeElevation.RunCommand([PurgeElevation.Command, token]));
        Assert.Throws<ArgumentException>(() => PurgeElevation.StartInfo([token]));
    }

    [Fact]
    public void Uac_request_starts_only_the_machine_helper_with_fixed_keys()
    {
        var start = PurgeElevation.StartInfo(["desktop-data", "managed", "desktop-data"]);
        Assert.Equal("runas", start.Verb);
        Assert.True(start.UseShellExecute);
        Assert.Equal(new[] { PurgeElevation.Command, "desktop-data", "managed" }, start.ArgumentList);
        Assert.Equal((int)PurgeRetryResult.InvalidRequest, PurgeElevation.RunCommand([PurgeElevation.Command]));
    }

    [Fact]
    public void Completed_retry_keeps_previous_results_and_unrelated_failures()
    {
        var original = new PurgeOutcomeView([("runtime", @"C:\fixture\.claude")], [],
        [
            ("system", SystemPath("Claude"), "permission-denied"),
            ("unrelated", @"C:\fixture\other", "file-in-use"),
        ], 20);
        var result = PurgeElevation.Reconcile(original, ["desktop-data"], PurgeRetryResult.Completed);
        Assert.Equal(2, result.Deleted.Count);
        Assert.Contains(original.Deleted[0], result.Deleted);
        Assert.Single(result.Failed);
        Assert.Equal("unrelated", result.Failed[0].Id);
        Assert.Equal(20, result.Bytes);
    }

    [Fact]
    public void Cancelling_uac_keeps_residuals_and_completed_cleanup()
    {
        var original = new PurgeOutcomeView([("runtime", @"C:\fixture\.claude")], [],
            [("system", SystemPath("Claude"), "permission-denied")], 20);
        var result = PurgeElevation.Reconcile(original, ["desktop-data"], PurgeRetryResult.Cancelled);
        Assert.Equal(original.Deleted, result.Deleted);
        Assert.Single(result.Failed);
        Assert.Equal("admin-cancelled", result.Failed[0].Reason);
    }

    [Fact]
    public void Native_helper_rejects_unknown_targets_through_pinvoke()
    {
        var error = Assert.Throws<EngineException>(() => Engine.PurgeMachine(["../other"]));
        Assert.Equal(2, error.Code);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    public void Access_errors_have_translated_guidance(string language)
    {
        using var scope = Loc.Scoped(language);
        foreach (var reason in new[] { "permission-denied", "file-in-use", "admin-cancelled", "admin-failed" })
        {
            Assert.NotEqual(reason, PurgeData.FailureText(reason));
            Assert.DoesNotContain("purge.error.", PurgeData.FailureText(reason));
        }
    }
}
