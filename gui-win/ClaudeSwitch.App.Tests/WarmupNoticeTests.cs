using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public class WarmupNoticeTests
{
    [Fact]
    public void Empty_or_skip_only_payloads_raise_no_notice()
    {
        Assert.False(WarmupNotice.AnySuccess(null));
        Assert.False(WarmupNotice.AnySuccess(JsonNode.Parse("{}")));
        Assert.False(WarmupNotice.AnySuccess(JsonNode.Parse(
            """{"fired":[{"number":1,"email":"a@x.com","ok":false}],"skipped":[]}""")));
    }

    [Fact]
    public void Nested_warmup_on_refresh_is_read()
    {
        var payload = JsonNode.Parse(
            """
            {
              "ok": true,
              "warmup": {
                "fired": [
                  {"number": 2, "email": "b@x.com", "ok": true},
                  {"number": 3, "email": "c@x.com", "ok": false}
                ]
              }
            }
            """)!;
        var fires = WarmupNotice.SuccessfulFires(payload);
        Assert.Single(fires);
        Assert.Equal(2, fires[0].Number);
        Assert.Equal("b@x.com", fires[0].Email);
    }

    [Fact]
    public void Body_names_one_and_many()
    {
        using var lang = Loc.Scoped("zh-Hans");
        string one = WarmupNotice.Body(["#2 · work"]);
        Assert.Contains("#2 · work", one);
        Assert.Contains("5 小时", one);
        string many = WarmupNotice.Body(["#1 · a", "#2 · b"]);
        Assert.Contains("#1 · a", many);
        Assert.Contains("#2 · b", many);
    }

    [Fact]
    public void Body_truncates_long_lists()
    {
        using var lang = Loc.Scoped("en");
        var labels = Enumerable.Range(1, 5).Select(i => $"#{i}").ToList();
        string body = WarmupNotice.Body(labels);
        Assert.Contains("#1", body);
        Assert.Contains("#3", body);
        Assert.Contains("2 more", body);
    }
}
