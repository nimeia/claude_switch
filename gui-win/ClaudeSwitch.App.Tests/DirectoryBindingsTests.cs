using System.Text.Json.Nodes;
using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

public class DirectoryBindingsTests
{
    private static DirectoryBindings Parse(params (string Path, int? Number)[] entries)
    {
        var arr = new JsonArray();
        foreach (var (path, number) in entries)
        {
            arr.Add(new JsonObject
            {
                ["key"] = DirectoryBindings.Key(path),
                ["path"] = path,
                ["email"] = $"user{number}@example.com",
                ["number"] = number is null ? null : JsonValue.Create(number.Value),
            });
        }
        return DirectoryBindings.Parse(new JsonObject { ["mappings"] = arr });
    }

    [Fact]
    public void Key_folds_separators_and_case()
    {
        Assert.Equal(DirectoryBindings.Key(@"D:\Work\App"), DirectoryBindings.Key("d:/work/app"));
        // A trailing separator is how a directory is often pasted.
        Assert.Equal(DirectoryBindings.Key("D:/work"), DirectoryBindings.Key(@"D:\work\"));
    }

    [Fact]
    public void An_exact_binding_is_not_inherited()
    {
        var bindings = Parse((@"D:\work", 2));
        var match = bindings.Resolve("d:/work");
        Assert.Equal(2, match.Binding?.Number);
        Assert.False(match.Inherited);
    }

    [Fact]
    public void Subdirectories_inherit_the_nearest_bound_ancestor()
    {
        var bindings = Parse((@"D:\work", 2), (@"D:\work\client", 3));

        var deep = bindings.Resolve(@"D:\work\src\lib");
        Assert.Equal(2, deep.Binding?.Number);
        Assert.True(deep.Inherited);

        // The more specific binding wins over its parent.
        var nested = bindings.Resolve(@"D:\work\client\api");
        Assert.Equal(3, nested.Binding?.Number);
        Assert.True(nested.Inherited);
    }

    [Fact]
    public void A_sibling_sharing_a_prefix_does_not_inherit()
    {
        // Without a separator check, "D:/workshop" matches a binding on
        // "D:/work" and silently runs as an account it has nothing to do with.
        var bindings = Parse((@"D:\work", 2));
        Assert.False(bindings.Resolve(@"D:\workshop").Found);
        Assert.False(bindings.Resolve(@"D:\work-old\src").Found);
    }

    [Fact]
    public void An_unbound_directory_matches_nothing()
    {
        var bindings = Parse((@"D:\work", 2));
        Assert.False(bindings.Resolve(@"C:\elsewhere").Found);
        Assert.Equal("", bindings.Label(@"C:\elsewhere"));
    }

    [Fact]
    public void A_binding_whose_account_was_removed_is_kept_and_labelled()
    {
        using var lang = Loc.Scoped("en");
        // Bindings store an identity, so one can outlive the slot that held it.
        // Dropping it would leave a blank cell that reads as "never bound".
        var bindings = Parse((@"D:\work", null));
        var match = bindings.Resolve(@"D:\work");
        Assert.True(match.Found);
        Assert.Null(match.Binding?.Number);
        Assert.Contains("removed", bindings.Label(@"D:\work"));
    }

    [Fact]
    public void Inherited_bindings_are_labelled_differently_from_exact_ones()
    {
        using var lang = Loc.Scoped("en");
        var bindings = Parse((@"D:\work", 2));
        string exact = bindings.Label(@"D:\work");
        string inherited = bindings.Label(@"D:\work\src");

        Assert.Contains("2", exact);
        Assert.Contains("2", inherited);
        Assert.DoesNotContain("inherited", exact);
        Assert.Contains("inherited", inherited);
    }

    [Fact]
    public void A_malformed_or_absent_response_yields_no_bindings()
    {
        Assert.Equal(0, DirectoryBindings.Parse(null).Count);
        Assert.Equal(0, DirectoryBindings.Parse(new JsonObject()).Count);
        // An entry with no key cannot be matched against anything.
        var noKey = new JsonObject
        {
            ["mappings"] = new JsonArray(new JsonObject { ["path"] = @"D:\work" }),
        };
        Assert.Equal(0, DirectoryBindings.Parse(noKey).Count);
    }
}
