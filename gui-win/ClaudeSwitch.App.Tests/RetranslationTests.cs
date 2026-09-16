using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Chrome that outlives a language change. Anything composed once and stored
/// keeps the language it was born in, which is how "运行" and a Chinese stats
/// line survived on an English window.
/// </summary>
public class RetranslationTests
{
    [Fact]
    public void The_bands_see_all_link_follows_a_language_change()
    {
        using var board = new TaskBoard(
            _ => "recent", () => false, _ => { }, () => { }, () => Loc.T("recent.seeAll"));

        string chinese;
        using (var zh = Loc.Scoped("zh-Hans"))
        {
            board.Show([
                new TaskEntry(TaskState.Updated, "one", "acct", "D:/work", "2m", () => { }, "go"),
            ]);
            chinese = board.SeeAllTextForTest;
            Assert.Equal(Loc.T("recent.seeAll"), chinese);
        }

        using (var en = Loc.Scoped("en"))
        {
            board.ApplyTexts();
            Assert.Equal(Loc.T("recent.seeAll"), board.SeeAllTextForTest);
            Assert.NotEqual(chinese, board.SeeAllTextForTest);
        }
    }

    [Fact]
    public void The_activity_summary_is_recomposed_rather_than_replayed()
    {
        using var strip = new ActivityStrip();
        // Parsed rather than composed: the engine's numbers arrive as JSON
        // text, and a hand-built JsonObject holds Int32 where the strip reads
        // Int64 — the test would fail on a shape that never occurs.
        var stats = JsonNode.Parse("""
            {
              "outputTokens": 20900000,
              "sessions": 53,
              "projects": 9,
              "longestStreak": 6,
              "daily": [{ "day": "2026-09-15", "userMessages": 12 }]
            }
            """);

        using (var zh = Loc.Scoped("zh-Hans"))
        {
            strip.Apply(stats);
            Assert.Contains("个会话", strip.SummaryForTest);
        }

        using (var en = Loc.Scoped("en"))
        {
            strip.ApplyTexts();
            // The numbers survive the change; only the sentence around them moves.
            Assert.Contains("53 sessions", strip.SummaryForTest);
            Assert.Contains("20.9M", strip.SummaryForTest);
            Assert.DoesNotContain("个会话", strip.SummaryForTest);
        }
    }
}
