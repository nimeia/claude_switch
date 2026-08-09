using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// What dismissing a task on the board actually does.
/// </summary>
/// <remarks>
/// "Ignore" sits next to unfinished work, so the one thing it must not do is
/// destroy any: it clears the reminder and leaves the conversation on disk.
/// </remarks>
public class TaskBoardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-board-" + Guid.NewGuid().ToString("N"));

    private readonly Engine _engine;
    private readonly AgentRunStore _store;

    public TaskBoardTests()
    {
        Directory.CreateDirectory(_root);
        _engine = new Engine(_root);
        _store = new AgentRunStore(_engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Save(string id, string status) =>
        _store.Save(id, sessionId: id, cwd: "D:/work/proj", accountNumber: 1,
            configDir: null, mode: null, prompt: "do the thing", status: status);

    [Fact]
    public void Ignoring_a_run_removes_only_that_record()
    {
        Save("keep", "interrupted");
        Save("drop", "interrupted");
        Assert.Equal(2, _store.Resumable().Count);

        _store.Remove("drop");

        var left = _store.Resumable();
        Assert.Single(left);
        Assert.Equal("keep", left[0].Id);
    }

    [Fact]
    public void Ignoring_is_idempotent()
    {
        // The board can be rebuilt between the click and the write; dismissing
        // something already gone must not throw.
        Save("gone", "interrupted");
        _store.Remove("gone");
        _store.Remove("gone");
        Assert.Empty(_store.Resumable());
    }

    private static TaskEntry Entry(
        TaskState state, string title = "t", Action? ignore = null, string cwd = "D:/work") =>
        new(state, title, "acct", cwd, "1m", () => { }, "go", Ignore: ignore);

    [Fact]
    public void A_live_run_offers_no_way_to_dismiss_it()
    {
        // Hiding work that is still happening is how a supervised run quietly
        // becomes an unsupervised one.
        Assert.Null(Entry(TaskState.Running).Ignore);
        Assert.NotNull(Entry(TaskState.NeedsAttention, ignore: () => { }).Ignore);
    }

    [Fact]
    public void The_board_shows_at_most_its_row_cap()
    {
        using var board = new TaskBoard();
        var many = Enumerable
            .Range(0, TaskBoard.MaxRows + 3)
            .Select(i => Entry(TaskState.Interrupted, $"t{i}", () => { }))
            .ToList();

        board.Show(many);

        Assert.Equal(TaskBoard.MaxRows, board.RowCount);

        // And it collapses to nothing when there is nothing to report, so the
        // band costs no height on a machine that never uses supervised runs.
        board.Show([]);
        Assert.Equal(0, board.RowCount);
    }

    [Fact]
    public void Expanding_shows_a_page_instead_of_the_collapsed_few()
    {
        using var board = new TaskBoard();
        var many = Enumerable
            .Range(0, TaskBoard.PageSize + 2)
            .Select(i => Entry(TaskState.Interrupted, $"t{i}", () => { }))
            .ToList();

        board.Show(many);
        Assert.Equal(TaskBoard.MaxRows, board.RowCount);
        float collapsed = board.DesiredHeight;

        board.ToggleForTest();
        Assert.Equal(TaskBoard.PageSize, board.RowCount);

        // Expanding must ask the form for more room, or the extra rows would be
        // laid out inside a height that never changed and get clipped.
        Assert.True(board.DesiredHeight > collapsed);

        board.ToggleForTest();
        Assert.Equal(TaskBoard.MaxRows, board.RowCount);
    }

    [Fact]
    public void The_last_page_holds_the_remainder()
    {
        using var board = new TaskBoard();
        var many = Enumerable
            .Range(0, TaskBoard.PageSize + 2)
            .Select(i => Entry(TaskState.Interrupted, $"t{i}", () => { }))
            .ToList();

        board.Show(many);
        board.ToggleForTest();
        board.TurnPageForTest(1);

        Assert.Equal(2, board.RowCount);

        // And it does not run off the end.
        board.TurnPageForTest(1);
        Assert.Equal(2, board.RowCount);
    }

    [Fact]
    public void A_list_that_shrinks_under_the_reader_does_not_leave_an_empty_page()
    {
        // A refresh can drop tasks while someone is on the last page.
        using var board = new TaskBoard();
        board.Show([.. Enumerable.Range(0, TaskBoard.PageSize + 2)
            .Select(i => Entry(TaskState.Interrupted, $"t{i}", () => { }))]);
        board.ToggleForTest();
        board.TurnPageForTest(1);
        Assert.Equal(2, board.RowCount);

        board.Show([Entry(TaskState.Interrupted, "only", () => { })]);
        Assert.Equal(1, board.RowCount);
    }

    [Fact]
    public void A_short_list_never_stays_expanded()
    {
        // Nothing is hidden, so there is nothing to expand into.
        using var board = new TaskBoard();
        board.Show([.. Enumerable.Range(0, TaskBoard.PageSize + 2)
            .Select(i => Entry(TaskState.Interrupted, $"t{i}", () => { }))]);
        board.ToggleForTest();
        Assert.Equal(TaskBoard.PageSize, board.RowCount);

        board.Show([Entry(TaskState.Running, "one")]);
        Assert.Equal(1, board.RowCount);
        Assert.False(board.IsExpandedForTest);
    }

    [Fact]
    public void A_short_window_shows_fewer_rows_rather_than_clipping_one()
    {
        // A clipped half-row reads as a rendering fault, and scrolling to fix it
        // brings a native scrollbar the theme cannot recolour.
        using var board = new TaskBoard();
        board.Show([.. Enumerable.Range(0, 8)
            .Select(i => Entry(TaskState.Interrupted, $"t{i}", () => { }))]);
        Assert.Equal(TaskBoard.MaxRows, board.RowCount);

        board.SetMaxVisibleRows(2);
        Assert.Equal(2, board.RowCount);

        // Expanding cannot exceed the window either.
        board.ToggleForTest();
        Assert.Equal(2, board.RowCount);
    }

    [Fact]
    public void At_least_one_row_survives_any_window()
    {
        // Better one row than an empty band that says nothing.
        Assert.True(TaskBoard.RowsThatFit(0) >= 1);
        Assert.True(TaskBoard.RowsThatFit(-500) >= 1);
    }

    [Fact]
    public void Row_capacity_grows_with_the_room_given()
    {
        int small = TaskBoard.RowsThatFit(
            TaskBoard.HeadingHeight + TaskBoard.PagerHeight + 2 * (TaskBoard.RowHeight + TaskBoard.RowGap) + 8);
        int large = TaskBoard.RowsThatFit(
            TaskBoard.HeadingHeight + TaskBoard.PagerHeight + 5 * (TaskBoard.RowHeight + TaskBoard.RowGap) + 8);
        Assert.Equal(2, small);
        Assert.True(large >= 5);
    }

    [Fact]
    public void Urgent_states_sort_above_healthy_ones()
    {
        // The top of the band must be what wants a person, not whatever was
        // created last. The enum order is the priority order.
        var states = new[]
        {
            TaskState.AwaitingPermission, TaskState.NeedsAttention, TaskState.Failed,
            TaskState.Running, TaskState.Interrupted, TaskState.Cancelled,
        };
        var shuffled = states.Reverse().Select(s => Entry(s)).ToList();
        shuffled.Sort((a, b) => a.State.CompareTo(b.State));

        Assert.Equal(states, shuffled.Select(e => e.State).ToArray());
    }

    [Theory]
    [InlineData(@"C:\Users\huang\AppData\Local\Temp\cswitch-acp-e2e\restart", @"…\cswitch-acp-e2e\restart")]
    [InlineData(@"D:\work\payments-service", @"…\work\payments-service")]
    [InlineData(@"D:\work", @"D:\work")]
    [InlineData("", "")]
    public void A_path_is_shortened_to_the_part_that_identifies_it(string full, string expected)
    {
        // A full Windows path is mostly prefix nobody reads, and it would push
        // the account and the status off the row.
        Assert.Equal(expected, TaskBoard.ShortPath(full));
    }

    [Fact]
    public void A_trailing_separator_does_not_eat_the_last_segment()
    {
        Assert.Equal(@"…\work\payments-service", TaskBoard.ShortPath(@"D:\work\payments-service\"));
    }
}
