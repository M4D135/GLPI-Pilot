using GlpiPilot.Core;

var checks = new (string Name, Action Run)[]
{
    ("A native request and the real DOMContentLoaded barrier stay active beyond the inactivity threshold", () =>
    {
        var clock = new TestClock(); var state = new LibraryIndexActivityState(5, clock); var request = new object();
        state.RequestStarted(request); clock.Advance(TimeSpan.FromMinutes(20));
        Require(state.CanWait && state.HasPendingRequest && state.TakePendingNotice());
        Require(!state.TakePendingNotice()); clock.Advance(TimeSpan.FromSeconds(5)); Require(state.TakePendingNotice());
        state.RequestEnded(request); Require(state.CanWait && !state.HasPendingRequest);
        clock.Advance(TimeSpan.FromSeconds(5)); Require(!state.CanWait);
        state.BeginDocument(); clock.Advance(TimeSpan.FromMinutes(20));
        Require(state.DocumentLoading && !state.HasPendingRequest && state.CanWait && state.TakePendingNotice());
        state.DocumentLoaded(); Require(!state.DocumentLoading && state.CanWait);
        clock.Advance(TimeSpan.FromSeconds(5)); Require(!state.CanWait);
    }),
    ("Response headers do not mean that the native response has finished", () =>
    {
        var clock = new TestClock(); var state = new LibraryIndexActivityState(5, clock); var request = new object();
        state.RequestStarted(request); state.ResponseReceived(request); clock.Advance(TimeSpan.FromMinutes(3));
        Require(state.HasPendingRequest && state.CanWait);
        state.RequestEnded(request); clock.Advance(TimeSpan.FromSeconds(4)); Require(state.CanWait);
        clock.Advance(TimeSpan.FromSeconds(1)); Require(!state.CanWait);
    }),
    ("Unchanged DOM polling and unrelated request completions cannot manufacture progress", () =>
    {
        var clock = new TestClock(); var state = new LibraryIndexActivityState(5, clock);
        state.Observe("form:1;status:12;rows:0"); clock.Advance(TimeSpan.FromSeconds(4));
        state.Observe("form:1;status:12;rows:0"); state.RequestEnded(new object()); state.ResponseReceived(new object());
        clock.Advance(TimeSpan.FromSeconds(1)); Require(!state.CanWait);
        state.Observe("form:1;status:12;rows:20"); Require(state.CanWait);
    }),
    ("A new phase preserves a still-live request instead of resetting it away", () =>
    {
        var clock = new TestClock(); var state = new LibraryIndexActivityState(5, clock); var request = new object();
        state.RequestStarted(request); state.BeginPhase(); clock.Advance(TimeSpan.FromMinutes(1));
        Require(state.HasPendingRequest && state.CanWait); state.RequestEnded(request);
        clock.Advance(TimeSpan.FromSeconds(6)); Require(!state.CanWait);
    }),
    ("Only the same installation's main list GET is a native navigation", () =>
    {
        var source = new Uri("https://glpi.test/support/front/ticket.php");
        bool Native(string url, string method = "GET", string resource = "document", bool navigation = true)
            => LibraryIndexRequestPolicy.IsNative(source, url, method, resource, navigation, null);
        Require(Native(source + "?start=20"));
        Require(!Native("https://glpi.test/other/front/ticket.php") && !Native("https://foreign.test/support/front/ticket.php")
            && !Native("http://glpi.test/support/front/ticket.php") && !Native("https://user@glpi.test/support/front/ticket.php")
            && !Native(source.ToString(), "POST") && !Native(source.ToString(), navigation: false)
            && !Native("https://glpi.test/support/front/login.php") && !Native(source.ToString(), resource: "image"));
    }),
    ("Native GLPI 9.5/10 status and result requests are recognized from their action and item type", () =>
    {
        var source = new Uri("https://glpi.test/support/front/ticket.php"); var endpoint = "https://glpi.test/support/ajax/search.php";
        foreach (var action in new[] { "display_searchoption", "display_searchoption_value", "display_results" })
        {
            Require(LibraryIndexRequestPolicy.IsNative(source, endpoint, "POST", "xhr", false, "action=" + action + "&field=12&itemtype=Ticket&_idor_token=NOT_LOGGED"));
            Require(LibraryIndexRequestPolicy.IsNative(source, endpoint + "?action=" + action + "&field=12&itemtype=Ticket", "GET", "fetch", false, null));
        }
    }),
    ("Polling, preferences writes, other item types, ambiguous parameters and sibling installations do not extend waiting", () =>
    {
        var source = new Uri("https://glpi.test/support/front/ticket.php"); var endpoint = "https://glpi.test/support/ajax/search.php";
        foreach (var data in new[] { "action=fold_search&itemtype=Ticket", "action=display_results&itemtype=Computer", "action=display_results", "itemtype=Ticket", "action=display_results&itemtype=Ticket&itemtype=Ticket", "action=display_results&action=fold_search&itemtype=Ticket", "action=display_searchoption&itemtype=Ticket", "action=display_searchoption&field=19&itemtype=Ticket", "action=display_searchoption&field=12&field=12&itemtype=Ticket" })
            Require(!LibraryIndexRequestPolicy.IsNative(source, endpoint, "POST", "xhr", false, data));
        Require(!LibraryIndexRequestPolicy.IsNative(source, endpoint + "?action=display_results", "POST", "xhr", false, "action=display_results&itemtype=Ticket"));
        Require(!LibraryIndexRequestPolicy.IsNative(source, "https://glpi.test/other/ajax/search.php", "POST", "xhr", false, "action=display_results&itemtype=Ticket"));
        Require(!LibraryIndexRequestPolicy.IsNative(source, "https://glpi.test/support/ajax/displaypreference.php", "POST", "xhr", false, "action=display_results&itemtype=Ticket"));
    }),
    ("Inactivity configuration is bounded while an active request remains independent of those bounds", () =>
    {
        var clock = new TestClock(); var small = new LibraryIndexActivityState(0, clock); var large = new LibraryIndexActivityState(int.MaxValue, clock);
        clock.Advance(TimeSpan.FromSeconds(5)); Require(!small.CanWait && large.CanWait);
        clock.Advance(TimeSpan.FromSeconds(595)); Require(!large.CanWait);
        large.RequestStarted(new object()); clock.Advance(TimeSpan.FromHours(1)); Require(large.CanWait);
    })
};
var failures = 0;
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine("PASS " + check.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + check.Name + "\n" + error); }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} contrôles purs de progression de l’index réussis.");
return failures == 0 ? 0 : 1;

static void Require(bool value) { if (!value) throw new InvalidOperationException("Index progress assertion failed."); }
sealed class TestClock : TimeProvider
{
    private long _time;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _time;
    public void Advance(TimeSpan elapsed) => _time += elapsed.Ticks;
}
