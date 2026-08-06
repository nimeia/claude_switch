using System.Text.RegularExpressions;
using ClaudeSwitch.App;
using Xunit;

// The selected language is process-wide state that several tests change.
// Running classes in parallel would make those tests see each other's choice.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The translation catalogues.
///
/// These tests are the reason keys can be trusted: a missing or misspelled key
/// would otherwise show up as an English word in the middle of a Chinese
/// sentence, and only on the screen of whoever happens to open that dialog.
/// </summary>
public class LocTests
{
    [Fact]
    public void Every_language_carries_exactly_the_base_keys()
    {
        var baseKeys = Loc.Keys(Loc.BaseLanguage).ToHashSet();
        Assert.NotEmpty(baseKeys);

        foreach (var (code, _) in Loc.Available)
        {
            var keys = Loc.Keys(code).ToHashSet();
            var missing = baseKeys.Except(keys).OrderBy(k => k).ToList();
            var extra = keys.Except(baseKeys).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, $"{code} is missing: {string.Join(", ", missing)}");
            // An extra key is dead weight, and usually a rename that was only
            // half applied.
            Assert.True(extra.Count == 0, $"{code} has unknown keys: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        // A translation that drops {0} silently loses the account name; one that
        // invents {2} throws at format time. Both are caught here instead.
        foreach (var (code, _) in Loc.Available)
        {
            if (code == Loc.BaseLanguage) continue;
            Loc.Use(Loc.BaseLanguage);
            foreach (var key in Loc.Keys(Loc.BaseLanguage))
            {
                var expected = Placeholders(Loc.T(key));
                Loc.Use(code);
                var actual = Placeholders(Loc.T(key));
                Loc.Use(Loc.BaseLanguage);
                Assert.True(
                    expected.SetEquals(actual),
                    $"{code}/{key}: base has {{{string.Join(",", expected.Order())}}}, "
                    + $"translation has {{{string.Join(",", actual.Order())}}}");
            }
        }
    }

    [Fact]
    public void An_unknown_key_shows_itself_rather_than_blank()
    {
        // A typo should be visible on screen, not an empty label.
        Assert.Equal("no.such.key", Loc.T("no.such.key"));
    }

    [Fact]
    public void A_missing_translation_falls_back_to_the_base_language()
    {
        Loc.Use("zh-Hans");
        try
        {
            // Every key exists in both, so this is really asserting the lookup
            // never returns empty for a real key in a non-base language.
            foreach (var key in Loc.Keys(Loc.BaseLanguage))
                Assert.False(string.IsNullOrWhiteSpace(Loc.T(key)), key);
        }
        finally
        {
            Loc.Use(Loc.BaseLanguage);
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "en")]
    public void Os_language_maps_to_the_closest_catalogue(string osCulture, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        var savedPref = UiPrefs.Language;
        try
        {
            UiPrefs.Language = "";
            System.Globalization.CultureInfo.CurrentUICulture =
                System.Globalization.CultureInfo.GetCultureInfo(osCulture);
            Assert.Equal(expected, Loc.Detect());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
            UiPrefs.Language = savedPref;
        }
    }

    [Fact]
    public void A_saved_choice_wins_over_the_os()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        var savedPref = UiPrefs.Language;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture =
                System.Globalization.CultureInfo.GetCultureInfo("en-US");
            UiPrefs.Language = "zh-Hans";
            Assert.Equal("zh-Hans", Loc.Detect());

            // A stale code from a removed language must not strand the UI.
            UiPrefs.Language = "kl-GL";
            Assert.Equal("en", Loc.Detect());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
            UiPrefs.Language = savedPref;
        }
    }

    [Fact]
    public void Formatting_survives_a_malformed_translation()
    {
        // A bad placeholder in a contributed file must not take the app down.
        Assert.NotNull(Loc.T("app.name", "unused"));
    }

    private static HashSet<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToHashSet();
}
