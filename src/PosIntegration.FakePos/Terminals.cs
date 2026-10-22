namespace DcTech.PosIntegration.FakePos;

/// <summary>Configuration section "LaunchGuards": defaults for every terminal, per-terminal changes on top, and the supervisor ids.</summary>
public sealed class LaunchGuardSettings
{
    public List<string> Supervisors { get; set; } = [];
}

/// <summary>One line of the terminal journal: every launch attempt, allowed or not, with the reasons.</summary>
public sealed record JournalEntry(string TerminalId, DateTimeOffset At, string OperatorId, string Outcome, string? SaleId,
    IReadOnlyList<GuardBlock> Blocks, IReadOnlyList<SaleOverride> Overrides);

/// <summary>
/// A simulated terminal: a cash session, a drawer sensor and a keylock, all set through the API instead of hardware.
/// A posted sale kicks the drawer open, as a real cash sale does, so the drawer guard has something to catch.
/// </summary>
public sealed class Terminal(string id, LaunchGuardPipeline guards)
{
    private readonly Lock _gate = new();
    private string? _session;
    private bool _drawerOpen;
    private KeylockPosition _keylock = KeylockPosition.Normal;
    private LastLaunch? _last;

    public string Id => id;

    public TerminalState State
    {
        get { lock (_gate) return new TerminalState(_session, _drawerOpen, _keylock, _last); }
    }

    public void OpenSession(string operatorId) { lock (_gate) _session = operatorId; }
    public void CloseSession() { lock (_gate) _session = null; }
    public void SetDrawer(bool open) { lock (_gate) _drawerOpen = open; }
    public void SetKeylock(KeylockPosition position) { lock (_gate) _keylock = position; }

    /// <summary>Evaluates and, when allowed, posts in one step under the terminal lock, so two attempts cannot both pass the repeat guard.</summary>
    public JournalEntry Launch(SaleAttempt attempt, PosGenerator pos, DateTimeOffset now)
    {
        lock (_gate)
        {
            var decision = guards.Evaluate(attempt, new TerminalState(_session, _drawerOpen, _keylock, _last), now);
            if (!decision.Allowed)
                return new JournalEntry(id, now, attempt.OperatorId, "blocked", null, decision.Blocks, []);

            var lines = SaleValidator.Normalize(attempt.Lines.Select(l => new SaleLine(l.Sku, l.Quantity)));
            var sale = pos.Append(lines, now, decision.Overrides);
            _last = new LastLaunch(lines, now);
            _drawerOpen = true;
            return new JournalEntry(id, now, attempt.OperatorId, decision.Overrides.Count > 0 ? "overridden" : "posted", sale.SaleId, [], decision.Overrides);
        }
    }
}

/// <summary>Creates terminals on first use. Options are LaunchGuards:Default with LaunchGuards:Terminals:{id} bound on top.</summary>
public sealed class TerminalRegistry(IConfiguration config, LaunchGuardSettings settings, ILogger<TerminalRegistry> log)
{
    private readonly Dictionary<string, Terminal> _terminals = new(StringComparer.Ordinal);
    private readonly List<JournalEntry> _journal = [];

    /// <summary>The fake's item file: the stock catalogue plus the price-lookup code the generator also sells.</summary>
    public static IReadOnlySet<string> ItemFile { get; } = PosGenerator.Catalog.Append("SKU-9999").ToHashSet(StringComparer.Ordinal);

    public Terminal Get(string id)
    {
        lock (_terminals)
        {
            if (_terminals.TryGetValue(id, out var t)) return t;
            var options = config.GetSection("LaunchGuards:Default").Get<LaunchGuardOptions>() ?? new LaunchGuardOptions();
            config.GetSection($"LaunchGuards:Terminals:{id}").Bind(options);
            var pipeline = LaunchGuardPipeline.Build(options, ItemFile, settings.Supervisors.ToHashSet(StringComparer.Ordinal));
            return _terminals[id] = new Terminal(id, pipeline);
        }
    }

    public void Record(JournalEntry entry)
    {
        lock (_journal) _journal.Add(entry);
        if (entry.Outcome == "blocked")
            log.LogWarning("Terminal {Terminal}: launch by {Operator} blocked: {Codes}", entry.TerminalId, entry.OperatorId,
                string.Join(", ", entry.Blocks.Select(b => b.Code)));
        else if (entry.Overrides.Count > 0)
            log.LogWarning("Terminal {Terminal}: sale {SaleId} posted with override by {Supervisor}: {Codes}", entry.TerminalId, entry.SaleId,
                entry.Overrides[0].SupervisorId, string.Join(", ", entry.Overrides.Select(o => o.Code).Distinct()));
    }

    public IReadOnlyList<JournalEntry> Journal(string terminalId)
    {
        lock (_journal) return _journal.Where(e => e.TerminalId == terminalId).ToList();
    }
}
