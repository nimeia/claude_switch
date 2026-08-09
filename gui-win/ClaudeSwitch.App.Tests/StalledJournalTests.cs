using ClaudeSwitch.App;
using ClaudeSwitch.Core;
using Xunit;

namespace ClaudeSwitch.App.Tests;

/// <summary>
/// The journal has to agree with what the user is told.
/// </summary>
/// <remarks>
/// A takeover whose turn was swallowed still ends with ACP reporting
/// <c>stopReason: end_turn</c>, so <see cref="BackgroundRuns"/> writes the run
/// down as <c>completed</c>. Verified against a live agent: the protocol cannot
/// tell work done from work swallowed. If the correction never lands, the
/// dropdown says the session needs attention while the journal says the work is
/// finished — and the run is never offered for resume, which is the one thing
/// still worth doing with it.
/// </remarks>
public class StalledJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cswitch-stalled-journal-" + Guid.NewGuid().ToString("N"));

    private readonly Engine _engine;
    private readonly AgentRunStore _store;

    public StalledJournalTests()
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

    private void SaveRun(string id, string status) =>
        _store.Save(
            id,
            sessionId: id,
            cwd: "D:/work/proj",
            accountNumber: 2,
            configDir: "D:/backup/sessions/2-a_x.com",
            mode: "acceptEdits",
            prompt: "refactor the parser",
            status: status,
            stopCause: status == "completed" ? "completed" : null);

    private AgentRunRecord? Find(string id) => _store.List().FirstOrDefault(r => r.Id == id);

    [Fact]
    public void A_swallowed_takeover_is_corrected_from_completed_to_resumable()
    {
        // What BackgroundRuns writes when the protocol claims success.
        SaveRun("sess-swallowed", "completed");
        Assert.Equal("completed", Find("sess-swallowed")!.Status);
        Assert.False(Find("sess-swallowed")!.IsResumable);

        // What the verification step then discovers.
        _store.Patch("sess-swallowed", status: "interrupted", lastError: "no real output");

        var after = Find("sess-swallowed")!;
        Assert.Equal("interrupted", after.Status);
        Assert.Contains("no real output", after.LastError);
        Assert.True(after.IsResumable, "the work is unfinished, so it must be offered back");
    }

    [Fact]
    public void Patching_keeps_the_account_and_profile()
    {
        // The trap this method exists to avoid: the engine's upsert replaces a
        // record whole, so a caller that only knows the new status would erase
        // the account and profile — and the run would then resume as the
        // default login, which cannot see its own conversation.
        SaveRun("sess-fields", "completed");
        _store.Patch("sess-fields", status: "interrupted", lastError: "x");

        var after = Find("sess-fields")!;
        Assert.Equal(2, after.AccountNumber);
        Assert.Equal("D:/backup/sessions/2-a_x.com", after.ConfigDir);
        Assert.Equal("acceptEdits", after.Mode);
        Assert.Equal("refactor the parser", after.Prompt);
        Assert.Equal("sess-fields", after.SessionId);
    }

    [Fact]
    public void Patching_a_run_that_is_gone_is_not_an_error()
    {
        // A takeover of a session with no journal record at all is normal: the
        // missing-working-directory case fails before a run is ever started.
        _store.Patch("never-existed", status: "interrupted", lastError: "x");
        Assert.Null(Find("never-existed"));
    }

    [Fact]
    public void A_verified_takeover_is_left_alone()
    {
        // Only failures are corrected; a real reply means the journal was right.
        SaveRun("sess-ok", "completed");
        var after = Find("sess-ok")!;
        Assert.Equal("completed", after.Status);
        Assert.Equal("completed", after.StopCause);
    }
}
