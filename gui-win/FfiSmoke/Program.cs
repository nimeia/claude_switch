using ClaudeSwitch.Core;

// Gating smoke: load cdylib, add two fixture accounts, refresh usage (5h/7d),
// switch, snapshot with numeric windows + adaptive nextPollSeconds.
var root = Path.Combine(Path.GetTempPath(), "claude-switch-ffi-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Directory.CreateDirectory(Path.Combine(root, ".claude"));

Console.WriteLine($"isolated root: {root}");

static string Cred(string email, string token) =>
    "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\",\"refreshToken\":\"r-" + token + "\",\"emailAddress\":\"" + email + "\"}}";

static string Cfg(string email) =>
    "{\"oauthAccount\":{\"emailAddress\":\"" + email + "\",\"accountUuid\":\"u\",\"organizationUuid\":\"o\",\"organizationName\":\"O\",\"displayName\":\"" + email + "\"}}";

using var eng = new Engine(root);

eng.Call("add_raw", new
{
    number = 1,
    email = "a@x.com",
    credentials = Cred("a@x.com", "tok-a"),
    config = Cfg("a@x.com"),
});
eng.Call("add_raw", new
{
    number = 2,
    email = "b@x.com",
    credentials = Cred("b@x.com", "tok-b"),
    config = Cfg("b@x.com"),
});

var sw1 = eng.SwitchTo("1");
Console.WriteLine("switch_to 1: " + sw1.ToJsonString());

var refresh = eng.Call("refresh_usage");
Console.WriteLine("refresh_usage: " + refresh.ToJsonString());
if (refresh["accountsRefreshed"]?.GetValue<int>() is not > 0)
{
    Console.Error.WriteLine("FAIL: expected accountsRefreshed > 0");
    return 3;
}

var snapAfterRefresh = eng.Snapshot();
Console.WriteLine("snapshot_after_refresh: " + snapAfterRefresh.ToJsonString());
var acc1 = snapAfterRefresh["accounts"]?.AsArray()?.FirstOrDefault(a => a?["number"]?.GetValue<int>() == 1);
var five = acc1?["usage"]?["fiveHour"]?["pct"]?.GetValue<double>();
var seven = acc1?["usage"]?["sevenDay"]?["pct"]?.GetValue<double>();
if (five is null or <= 0 || seven is null)
{
    Console.Error.WriteLine($"FAIL: expected numeric 5h/7d on account 1, got five={five} seven={seven}");
    return 4;
}
Console.WriteLine($"account1_usage fiveHour={five} sevenDay={seven}");

var nextPoll = snapAfterRefresh["nextPollSeconds"]?.GetValue<double>();
if (nextPoll is null or <= 0)
{
    Console.Error.WriteLine("FAIL: missing nextPollSeconds");
    return 5;
}
Console.WriteLine($"nextPollSeconds={nextPoll}");

var sw2 = eng.SwitchTo("2");
Console.WriteLine("switch_to 2: " + sw2.ToJsonString());
if (sw2["switched"]?.GetValue<bool>() != true)
{
    Console.Error.WriteLine("FAIL: expected switched=true");
    return 1;
}

var snap = eng.Snapshot();
Console.WriteLine("snapshot: " + snap.ToJsonString());
var schema = snap["schemaVersion"]?.GetValue<int>() ?? 0;
var accounts = snap["accounts"]?.AsArray()?.Count ?? 0;
var active = snap["activeAccountNumber"]?.GetValue<int>() ?? -1;

// Snapshot schema 2 added accounts[].plan; older 1 is still accepted here since
// the smoke only reads fields both versions carry.
if (schema is not (1 or 2) || accounts < 2 || active != 2)
{
    Console.Error.WriteLine($"FAIL: schema={schema} accounts={accounts} active={active}");
    return 2;
}

// After switch to tok-b (80%), refresh should replan to 60s band.
var refresh2 = eng.Call("refresh_usage");
Console.WriteLine("refresh_after_switch: " + refresh2.ToJsonString());
var poll2 = refresh2["nextPollSeconds"]?.GetValue<double>() ?? 0;
if (Math.Abs(poll2 - 60.0) > 0.01)
{
    Console.Error.WriteLine($"FAIL: expected nextPollSeconds=60 after high-usage active, got {poll2}");
    return 6;
}

var events = eng.Call("drain_events");
Console.WriteLine("events: " + events.ToJsonString());
Console.WriteLine("OK: FFI consumer smoke passed (switch + 5h/7d usage + adaptive poll)");
return 0;
