namespace DcTech.PosIntegration;

/// <summary>Keylock positions, numbered as in the UPOS Keylock device class (Locked = 1, Normal = 2, Supervisor = 3).</summary>
public enum KeylockPosition { Locked = 1, Normal = 2, Supervisor = 3 }

/// <summary>How a line reached the terminal: read by the scanner, or typed on the keyboard.</summary>
public enum EntryMethod { Scanned, Keyed }

public sealed record AttemptLine(string Sku, int Quantity, EntryMethod Entry = EntryMethod.Scanned, bool QuantityConfirmed = false);

/// <summary>What an operator asks a terminal to post. A <see cref="SupervisorId"/> asks for a supervisor override of the blocks that allow one.</summary>
public sealed record SaleAttempt(string OperatorId, IReadOnlyList<AttemptLine> Lines, string? SupervisorId = null);

public sealed record LastLaunch(IReadOnlyList<SaleLine> Lines, DateTimeOffset At);

/// <summary>The (simulated) devices of one terminal at the moment a sale is attempted.</summary>
public sealed record TerminalState(string? SessionOperator, bool DrawerOpen, KeylockPosition Keylock, LastLaunch? Last);

/// <summary>Why a launch was stopped. <see cref="Overridable"/> blocks can be released by a supervisor; the others need the cause fixed.</summary>
public sealed record GuardBlock(string Guard, string Code, string Message, bool Overridable);

public sealed record LaunchDecision(bool Allowed, IReadOnlyList<GuardBlock> Blocks, IReadOnlyList<SaleOverride> Overrides);

/// <summary>Launch guards for one terminal. Zero or false turns a guard off; null <see cref="RequiredKeylock"/> ignores the keylock.</summary>
public sealed class LaunchGuardOptions
{
    /// <summary>A sale needs an open cash session, and the operator on the attempt must be the one who opened it.</summary>
    public bool RequireOpenSession { get; set; }

    /// <summary>The next sale cannot start while the drawer sensor reports open.</summary>
    public bool RequireDrawerClosed { get; set; }

    /// <summary>Normal: a locked terminal cannot sell. Supervisor: every sale needs the supervisor key turned.</summary>
    public KeylockPosition? RequiredKeylock { get; set; }

    /// <summary>A code that is not in the terminal's item file is never posted.</summary>
    public bool RequireKnownItem { get; set; }

    /// <summary>Lines must come from the scanner; a keyed line needs a supervisor override.</summary>
    public bool RequireScan { get; set; }

    /// <summary>A line above this quantity must be confirmed by the operator (a second keystroke).</summary>
    public int ConfirmQuantityAbove { get; set; }

    /// <summary>A line above this quantity needs a supervisor override even when confirmed.</summary>
    public int SupervisorQuantityAbove { get; set; }

    /// <summary>The same basket as the previous sale on this terminal, within this many seconds, needs a supervisor override.</summary>
    public int RepeatWindowSeconds { get; set; }
}

public interface ISaleGuard
{
    IEnumerable<GuardBlock> Check(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now);
}

/// <summary>
/// Runs the enabled guards and reports every block at once, so the operator fixes them in one go.
/// Overridable blocks are released only when a known supervisor id is given and the keylock is in the Supervisor position:
/// the physical key is the authorisation, so no PIN or secret is stored.
/// </summary>
public sealed class LaunchGuardPipeline(IReadOnlyList<ISaleGuard> guards, IReadOnlySet<string> supervisors)
{
    public static LaunchGuardPipeline Build(LaunchGuardOptions o, IReadOnlySet<string> itemFile, IReadOnlySet<string> supervisors)
    {
        var guards = new List<ISaleGuard>();
        if (o.RequireOpenSession) guards.Add(new SessionGuard());
        if (o.RequireDrawerClosed) guards.Add(new DrawerGuard());
        if (o.RequiredKeylock is { } position) guards.Add(new KeylockGuard(position));
        if (o.RequireKnownItem || o.RequireScan) guards.Add(new ItemGuard(o.RequireKnownItem ? itemFile : null, o.RequireScan));
        if (o.ConfirmQuantityAbove > 0 || o.SupervisorQuantityAbove > 0) guards.Add(new QuantityGuard(o.ConfirmQuantityAbove, o.SupervisorQuantityAbove));
        if (o.RepeatWindowSeconds > 0) guards.Add(new RepeatGuard(TimeSpan.FromSeconds(o.RepeatWindowSeconds)));
        return new LaunchGuardPipeline(guards, supervisors);
    }

    public LaunchDecision Evaluate(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now)
    {
        var blocks = guards.SelectMany(g => g.Check(attempt, terminal, now)).ToList();
        if (blocks.Count == 0) return new LaunchDecision(true, [], []);
        if (attempt.SupervisorId is not { } supervisor || blocks.Any(b => !b.Overridable)) return new LaunchDecision(false, blocks, []);

        if (!supervisors.Contains(supervisor))
            return Denied(blocks, $"{supervisor} is not a supervisor on this terminal");
        if (terminal.Keylock != KeylockPosition.Supervisor)
            return Denied(blocks, "an override needs the keylock in the Supervisor position");

        return new LaunchDecision(true, [], blocks.DistinctBy(b => b.Code).Select(b => new SaleOverride(b.Guard, b.Code, supervisor)).ToList());
    }

    private static LaunchDecision Denied(List<GuardBlock> blocks, string why) =>
        new(false, [.. blocks, new GuardBlock("override", "override-denied", why, false)], []);
}

public sealed class SessionGuard : ISaleGuard
{
    public IEnumerable<GuardBlock> Check(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now)
    {
        if (terminal.SessionOperator is null)
            yield return new("session", "no-session", "no cash session is open on this terminal", false);
        else if (!string.Equals(terminal.SessionOperator, attempt.OperatorId, StringComparison.Ordinal))
            yield return new("session", "operator-mismatch", $"the session belongs to {terminal.SessionOperator}, not {attempt.OperatorId}", false);
    }
}

public sealed class DrawerGuard : ISaleGuard
{
    public IEnumerable<GuardBlock> Check(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now)
    {
        if (terminal.DrawerOpen)
            yield return new("drawer", "drawer-open", "close the cash drawer before the next sale", false);
    }
}

public sealed class KeylockGuard(KeylockPosition required) : ISaleGuard
{
    public IEnumerable<GuardBlock> Check(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now)
    {
        if (terminal.Keylock == KeylockPosition.Locked)
            yield return new("keylock", "keylock-locked", "the terminal is locked", false);
        else if (terminal.Keylock < required)
            yield return new("keylock", "keylock-supervisor-required", "this terminal sells only with the supervisor key turned", false);
    }
}

/// <param name="itemFile">Known codes; null skips the lookup.</param>
public sealed class ItemGuard(IReadOnlySet<string>? itemFile, bool requireScan) : ISaleGuard
{
    public IEnumerable<GuardBlock> Check(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now)
    {
        foreach (var line in attempt.Lines)
        {
            if (itemFile is not null && !itemFile.Contains(line.Sku))
                yield return new("item", "item-unknown", $"{line.Sku} is not in the item file", false);
            else if (requireScan && line.Entry == EntryMethod.Keyed)
                yield return new("item", "item-keyed", $"{line.Sku} was typed, not scanned", true);
        }
    }
}

public sealed class QuantityGuard(int confirmAbove, int supervisorAbove) : ISaleGuard
{
    public IEnumerable<GuardBlock> Check(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now)
    {
        foreach (var line in attempt.Lines)
        {
            if (confirmAbove > 0 && line.Quantity > confirmAbove && !line.QuantityConfirmed)
                yield return new("quantity", "quantity-unconfirmed", $"confirm {line.Quantity} x {line.Sku}", false);
            if (supervisorAbove > 0 && line.Quantity > supervisorAbove)
                yield return new("quantity", "quantity-over-limit", $"{line.Quantity} x {line.Sku} is above the limit of {supervisorAbove}", true);
        }
    }
}

/// <summary>
/// Catches the same basket rung up twice as two sales. Idempotency by sale id cannot see this: the second sale has its own id.
/// A customer who really buys the same thing again is let through by a supervisor.
/// </summary>
public sealed class RepeatGuard(TimeSpan window) : ISaleGuard
{
    public IEnumerable<GuardBlock> Check(SaleAttempt attempt, TerminalState terminal, DateTimeOffset now)
    {
        if (terminal.Last is not { } last || now - last.At > window) yield break;
        var basket = SaleValidator.Normalize(attempt.Lines.Select(l => new SaleLine(l.Sku, l.Quantity)));
        if (basket.SequenceEqual(last.Lines))
            yield return new("repeat", "repeat-basket", $"same basket as the sale {(int)(now - last.At).TotalSeconds}s ago", true);
    }
}
