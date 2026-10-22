namespace DcTech.PosIntegration.Tests.Unit;

public class LaunchGuardTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-05T10:00:00Z");
    private static readonly HashSet<string> Items = ["SKU-0001", "SKU-0002"];

    private static LaunchGuardPipeline Guards(Action<LaunchGuardOptions> configure)
    {
        var o = new LaunchGuardOptions();
        configure(o);
        return LaunchGuardPipeline.Build(o, Items, new HashSet<string> { "SUP-01" });
    }

    private static TerminalState Ready(KeylockPosition keylock = KeylockPosition.Normal, bool drawerOpen = false, LastLaunch? last = null) =>
        new("OP-01", drawerOpen, keylock, last);

    private static SaleAttempt Attempt(params AttemptLine[] lines) => new("OP-01", lines);

    private static AttemptLine Line(int qty = 1, EntryMethod entry = EntryMethod.Scanned, bool confirmed = false, string sku = "SKU-0001") =>
        new(sku, qty, entry, confirmed);

    private static IEnumerable<string> Codes(LaunchDecision d) => d.Blocks.Select(b => b.Code);

    [Fact]
    public void With_every_guard_off_anything_is_allowed()
    {
        var d = Guards(_ => { }).Evaluate(Attempt(Line(999, EntryMethod.Keyed, sku: "NOPE")), new TerminalState(null, true, KeylockPosition.Locked, null), Now);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Session_guard_blocks_without_a_session_and_for_another_operator()
    {
        var guards = Guards(o => o.RequireOpenSession = true);
        Assert.Equal(["no-session"], Codes(guards.Evaluate(Attempt(Line()), Ready() with { SessionOperator = null }, Now)));
        Assert.Equal(["operator-mismatch"], Codes(guards.Evaluate(Attempt(Line()), Ready() with { SessionOperator = "OP-02" }, Now)));
        Assert.True(guards.Evaluate(Attempt(Line()), Ready(), Now).Allowed);
    }

    [Fact]
    public void Drawer_guard_blocks_an_open_drawer_and_a_supervisor_cannot_release_it()
    {
        var guards = Guards(o => o.RequireDrawerClosed = true);
        Assert.True(guards.Evaluate(Attempt(Line()), Ready(), Now).Allowed);

        var d = guards.Evaluate(Attempt(Line()) with { SupervisorId = "SUP-01" }, Ready(KeylockPosition.Supervisor, drawerOpen: true), Now);
        Assert.False(d.Allowed);
        Assert.Equal(["drawer-open"], Codes(d));
    }

    [Theory]
    [InlineData(KeylockPosition.Normal, KeylockPosition.Locked, "keylock-locked")]
    [InlineData(KeylockPosition.Normal, KeylockPosition.Normal, null)]
    [InlineData(KeylockPosition.Supervisor, KeylockPosition.Normal, "keylock-supervisor-required")]
    [InlineData(KeylockPosition.Supervisor, KeylockPosition.Supervisor, null)]
    public void Keylock_guard_follows_the_required_position(KeylockPosition required, KeylockPosition actual, string? code)
    {
        var d = Guards(o => o.RequiredKeylock = required).Evaluate(Attempt(Line()), Ready(actual), Now);
        Assert.Equal(code is null, d.Allowed);
        if (code is not null) Assert.Equal([code], Codes(d));
    }

    [Fact]
    public void An_unknown_code_is_never_posted_even_with_a_supervisor()
    {
        var d = Guards(o => o.RequireKnownItem = true)
            .Evaluate(Attempt(Line(sku: "SKU-4242")) with { SupervisorId = "SUP-01" }, Ready(KeylockPosition.Supervisor), Now);
        Assert.False(d.Allowed);
        Assert.Equal(["item-unknown"], Codes(d));
    }

    [Fact]
    public void A_keyed_line_needs_a_supervisor_when_scanning_is_required()
    {
        var guards = Guards(o => o.RequireScan = true);
        Assert.True(guards.Evaluate(Attempt(Line()), Ready(), Now).Allowed);
        Assert.Equal(["item-keyed"], Codes(guards.Evaluate(Attempt(Line(entry: EntryMethod.Keyed)), Ready(), Now)));

        var d = guards.Evaluate(Attempt(Line(entry: EntryMethod.Keyed)) with { SupervisorId = "SUP-01" }, Ready(KeylockPosition.Supervisor), Now);
        Assert.True(d.Allowed);
        Assert.Equal([new SaleOverride("item", "item-keyed", "SUP-01")], d.Overrides);
    }

    [Fact]
    public void A_large_quantity_needs_confirmation_and_above_the_limit_a_supervisor()
    {
        var guards = Guards(o => { o.ConfirmQuantityAbove = 10; o.SupervisorQuantityAbove = 50; });
        Assert.True(guards.Evaluate(Attempt(Line(10)), Ready(), Now).Allowed);
        Assert.Equal(["quantity-unconfirmed"], Codes(guards.Evaluate(Attempt(Line(12)), Ready(), Now)));
        Assert.True(guards.Evaluate(Attempt(Line(12, confirmed: true)), Ready(), Now).Allowed);
        Assert.Equal(["quantity-over-limit"], Codes(guards.Evaluate(Attempt(Line(80, confirmed: true)), Ready(), Now)));

        // An unconfirmed quantity is the operator's to fix: the supervisor key does not skip the confirmation.
        Assert.False(guards.Evaluate(Attempt(Line(80)) with { SupervisorId = "SUP-01" }, Ready(KeylockPosition.Supervisor), Now).Allowed);
        Assert.True(guards.Evaluate(Attempt(Line(80, confirmed: true)) with { SupervisorId = "SUP-01" }, Ready(KeylockPosition.Supervisor), Now).Allowed);
    }

    [Fact]
    public void The_same_basket_inside_the_window_is_held_until_a_supervisor_releases_it()
    {
        var guards = Guards(o => o.RepeatWindowSeconds = 30);
        var last = new LastLaunch([new SaleLine("SKU-0001", 2), new SaleLine("SKU-0002", 1)], Now.AddSeconds(-10));
        // Same basket in another order and split over two lines still counts as the same basket.
        var again = Attempt(Line(1, sku: "SKU-0002"), Line(1), Line(1));

        Assert.Equal(["repeat-basket"], Codes(guards.Evaluate(again, Ready(last: last), Now)));
        Assert.True(guards.Evaluate(again, Ready(last: last with { At = Now.AddSeconds(-31) }), Now).Allowed);
        Assert.True(guards.Evaluate(Attempt(Line(3)), Ready(last: last), Now).Allowed);
        Assert.True(guards.Evaluate(again with { SupervisorId = "SUP-01" }, Ready(KeylockPosition.Supervisor, last: last), Now).Allowed);
    }

    [Theory]
    [InlineData("SUP-99", KeylockPosition.Supervisor)]
    [InlineData("SUP-01", KeylockPosition.Normal)]
    public void An_override_needs_a_known_supervisor_and_the_supervisor_key(string supervisor, KeylockPosition keylock)
    {
        var d = Guards(o => o.RequireScan = true)
            .Evaluate(Attempt(Line(entry: EntryMethod.Keyed)) with { SupervisorId = supervisor }, Ready(keylock), Now);
        Assert.False(d.Allowed);
        Assert.Equal(["item-keyed", "override-denied"], Codes(d));
        Assert.Empty(d.Overrides);
    }

    [Fact]
    public void Every_block_is_reported_at_once()
    {
        var guards = Guards(o => { o.RequireOpenSession = true; o.RequireDrawerClosed = true; o.RequireScan = true; o.ConfirmQuantityAbove = 5; });
        var d = guards.Evaluate(Attempt(Line(9, EntryMethod.Keyed)), new TerminalState(null, true, KeylockPosition.Normal, null), Now);
        Assert.Equal(["no-session", "drawer-open", "item-keyed", "quantity-unconfirmed"], Codes(d));
    }
}
