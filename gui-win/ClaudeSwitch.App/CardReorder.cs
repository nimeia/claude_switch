namespace ClaudeSwitch.App;

/// <summary>
/// Where a dragged card lands in the list.
///
/// The arithmetic is small but easy to get subtly wrong: the insertion point is
/// expressed against the list *before* the removal, while the insert happens
/// after it, so every index past the source shifts down by one.
/// </summary>
internal static class CardReorder
{
    /// <summary>
    /// Index to insert the item at once it has been removed from
    /// <paramref name="from"/>, or <c>null</c> when the drop leaves the order
    /// unchanged (dropped back where it started, or onto the seam it already
    /// occupies).
    /// </summary>
    /// <param name="from">Current index of the dragged item.</param>
    /// <param name="to">Index of the card it was dropped on.</param>
    /// <param name="after">Dropped on the target's lower half.</param>
    public static int? TargetIndex(int from, int to, bool after)
    {
        if (from < 0 || to < 0) return null;

        int insert = after ? to + 1 : to;
        // Removing the source first pulls everything below it up one slot.
        if (insert > from) insert--;
        return insert == from ? null : insert;
    }

    /// <summary>Apply <see cref="TargetIndex"/> to a copy of <paramref name="items"/>.</summary>
    /// <returns>The reordered list, or <c>null</c> when nothing would change.</returns>
    public static List<T>? Apply<T>(IReadOnlyList<T> items, int from, int to, bool after)
    {
        if (TargetIndex(from, to, after) is not { } insert) return null;
        if (from >= items.Count || insert > items.Count - 1) return null;

        var next = new List<T>(items);
        var moved = next[from];
        next.RemoveAt(from);
        next.Insert(insert, moved);
        return next;
    }
}
