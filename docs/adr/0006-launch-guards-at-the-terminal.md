# 6. Launch guards run at the terminal, before a sale exists

Status: accepted

**Context.** ADR 1 stops one sale id from being booked twice. It cannot stop an operator from creating a wrong sale: ringing the same basket twice under two ids, typing a code instead of scanning it, keying 100 instead of 10, or selling with the drawer still open from the last customer. By the time the worker sees such a sale, the POS has already committed it, and ADR 2 explains why the worker must not reject what the POS sold. So the check has to happen at the terminal, while the sale is still an attempt.

Retail device standards and POS products give the vocabulary. UnifiedPOS defines device classes for the cash drawer, keylock, scanner and POS keyboard ([OMG UnifiedPOS](https://www.omg.org/spec/UPOS/); 1.16.1 is the formal version). Its keylock has three positions, Locked = 1, Normal = 2, Supervisor = 3 ([POS for .NET `Keylock`](https://learn.microsoft.com/previous-versions/windows/embedded/ms884534(v=winembedded.1002))). Windows exposes the drawer state as `CashDrawer.IsDrawerOpen` ([Windows.Devices.PointOfService.CashDrawer](https://learn.microsoft.com/uwp/api/windows.devices.pointofservice.cashdrawer)) and the scanner as [`BarcodeScanner`](https://learn.microsoft.com/uwp/api/windows.devices.pointofservice.barcodescanner). Commercial POS software ships "require cash drawer to be closed before new transaction" as a per-workstation option ([Oracle Simphony, cash drawer workstation options](https://docs.oracle.com/en/industries/food-beverage/simphony/simcg/t_peripherals_cash_drawer.htm)), and some use a keylock or electronic key for staff logon ([LS Central keylock device](https://help.lsretail.com/lscentral130/Content/LS%20Retail/POS/Hardware/Keylock%20Device.htm)).

**Decision.** A `LaunchGuardPipeline` in `PosIntegration.Core` evaluates a `SaleAttempt` against the `TerminalState` (session, drawer, keylock, last posted basket) and returns every block at once. Each guard is an `ISaleGuard`, switched on per terminal by `LaunchGuardOptions`. The fake POS hosts it on simulated terminals; there is no hardware.

| Guard | Blocks | Supervisor can release |
|---|---|---|
| `session` | no open cash session, or the operator is not the one who opened it | no |
| `drawer` | the drawer sensor reports open (a posted sale kicks it open) | no |
| `keylock` | the keylock is Locked; with `RequiredKeylock: Supervisor`, any position below Supervisor | no |
| `item` | a code not in the item file (`item-unknown`); a typed line when `RequireScan` is on (`item-keyed`) | keyed only |
| `quantity` | a line above `ConfirmQuantityAbove` without the operator's confirmation; a line above `SupervisorQuantityAbove` | above the limit only |
| `repeat` | the same basket as the previous sale on that terminal within `RepeatWindowSeconds` | yes |

The rule for "can release" is that a supervisor may accept a risk but cannot change a fact. An open drawer, an unknown code or an unconfirmed quantity is fixed by the operator; a keyed item, a big quantity or a repeat purchase can be legitimate, so a supervisor may let it through.

An override is accepted only when the attempt names a configured supervisor id and the keylock is in the Supervisor position. The physical key is the authorisation, so the repository stores no PIN or other secret. Every attempt goes to the terminal journal (`GET /terminals/{id}/journal`) and to the log; a blocked attempt never becomes a sale. Each released block travels with the sale as `Overrides` and the worker writes it to `dbo.SaleOverride` in the same transaction as the booking, so a duplicate delivery adds no second row and the back office can see who released what.

**Left out, and why.**

- *Guards in the worker.* The sale already happened; ADR 2 stands.
- *Void and refund with a supervisor key.* There are no voids or returns in this repository yet (see Limitations); the override mechanism is ready for them.
- *Supervisor PIN.* It would mean storing a secret in configuration. The keylock covers the demo; a real deployment would use the POS vendor's authentication.
- *Duplicate sale id.* Already handled by ADR 1; the `repeat` guard covers the case that ADR 1 cannot see, a second sale with a new id.
- *Price, discount and tender limits.* The model has no prices.
- *Real devices (OPOS, JavaPOS, Windows.Devices.PointOfService).* An adapter would read `IsDrawerOpen`, the keylock position and scanner events into `TerminalState`; nothing here talks to hardware.
- *Brazilian fiscal rules (NFC-e, PAF-ECF).* They govern fiscal documents, not these interlocks. Nothing here claims conformity with them.

**Consequences.** Guards are pure functions of the attempt, the state and the clock, so each one has unit tests for block, allow and override. The quantity limits and the repeat window are design judgement, not taken from a standard; they are configuration so a store can tune them. The journal in the fake is in memory and lost on restart; a real terminal would write it to durable local storage. Only overrides reach SQL Server, not blocked attempts.
