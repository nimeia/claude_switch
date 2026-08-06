using ClaudeSwitch.App;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// Drop-position arithmetic. The insertion index is stated against the list
/// before the drag item is removed, so anything below it shifts by one — the
/// off-by-one that makes a dragged card land one slot away from where the
/// insertion line promised.
/// </summary>
public class CardReorderTests
{
    private static List<string> Items() => ["A", "B", "C", "D"];

    [Theory]
    // Downward moves.
    [InlineData(0, 2, true, "B,C,A,D")]   // A below C
    [InlineData(0, 2, false, "B,A,C,D")]  // A above C
    [InlineData(0, 3, true, "B,C,D,A")]   // A to the very end
    // Upward moves.
    [InlineData(3, 1, false, "A,D,B,C")]  // D above B
    [InlineData(3, 1, true, "A,B,D,C")]   // D below B
    [InlineData(2, 0, false, "C,A,B,D")]  // C to the very front
    public void Drop_lands_where_the_indicator_promised(int from, int to, bool after, string expected)
    {
        var got = CardReorder.Apply(Items(), from, to, after);
        Assert.NotNull(got);
        Assert.Equal(expected, string.Join(",", got!));
    }

    [Theory]
    [InlineData(1, 1, false)] // onto itself, above
    [InlineData(1, 1, true)]  // onto itself, below
    [InlineData(1, 0, true)]  // below A — where B already is
    [InlineData(1, 2, false)] // above C — where B already is
    public void Seams_it_already_occupies_are_no_ops(int from, int to, bool after)
    {
        // These must report "no change" rather than persisting an identical
        // order and flashing a "reordered" status the user did not earn.
        Assert.Null(CardReorder.TargetIndex(from, to, after));
        Assert.Null(CardReorder.Apply(Items(), from, to, after));
    }

    [Fact]
    public void Every_drop_is_a_permutation_of_the_same_items()
    {
        var items = Items();
        for (int from = 0; from < items.Count; from++)
            for (int to = 0; to < items.Count; to++)
                foreach (bool after in new[] { false, true })
                {
                    var got = CardReorder.Apply(items, from, to, after);
                    if (got is null) continue;
                    Assert.Equal(items.Count, got.Count);
                    Assert.Equal(items.OrderBy(x => x), got.OrderBy(x => x));
                    // A real move, never a silent no-op reported as a change.
                    Assert.NotEqual(items, got);
                }
    }

    [Fact]
    public void Out_of_range_indices_are_refused_not_clamped()
    {
        Assert.Null(CardReorder.TargetIndex(-1, 2, true));
        Assert.Null(CardReorder.TargetIndex(1, -1, true));
        Assert.Null(CardReorder.Apply(Items(), 9, 1, true));
    }

    [Fact]
    public void Two_item_list_can_swap_in_both_directions()
    {
        List<string> pair = ["A", "B"];
        Assert.Equal("B,A", string.Join(",", CardReorder.Apply(pair, 0, 1, true)!));
        Assert.Equal("B,A", string.Join(",", CardReorder.Apply(pair, 1, 0, false)!));
    }
}
