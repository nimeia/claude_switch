using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The policy JSON the agent window actually sends.
/// </summary>
/// <remarks>
/// Auto-continue off used to set only <c>maxAttempts: 0</c>. The engine's
/// long-retry ladder and quota-wait budget still defaulted on, so unchecking
/// the box still retried a network blip for half an hour and waited out a
/// quota wall. These facts pin the payload the window builds, which is what
/// <c>acp_decide</c> deserialises.
/// </remarks>
public class AcpPolicyTests
{
    [Fact]
    public void Auto_continue_off_is_a_one_shot_policy()
    {
        var policy = AgentWindow.BuildPolicy(autoContinue: false, onQuotaIndex: 0);
        Assert.NotNull(policy);
        Assert.Equal(0, policy!["maxAttempts"]!.GetValue<int>());
        Assert.False(policy["continueOnTruncation"]!.GetValue<bool>());
        Assert.Equal(0, policy["maxLongRetries"]!.GetValue<int>());
        Assert.Equal(0, policy["maxRateLimitWaits"]!.GetValue<int>());
        // Wait is the engine default; do not send a redundant override.
        Assert.Null(policy["onRateLimit"]);
    }

    [Fact]
    public void Auto_continue_on_with_default_quota_sends_no_override()
    {
        // Engine defaults stay the single definition of normal behaviour.
        Assert.Null(AgentWindow.BuildPolicy(autoContinue: true, onQuotaIndex: 0));
    }

    [Fact]
    public void Quota_dropdown_still_overrides_when_auto_continue_is_off()
    {
        var policy = AgentWindow.BuildPolicy(autoContinue: false, onQuotaIndex: 2);
        Assert.Equal("stop", policy!["onRateLimit"]!.GetValue<string>());
        Assert.Equal(0, policy["maxRateLimitWaits"]!.GetValue<int>());
    }
}
