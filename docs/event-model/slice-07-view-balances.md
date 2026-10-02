# Slice 7 — View balances

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Balances — the group page: who is owed, who owes, and the expenses behind it |
| Read model | `GroupLedgerReadModel`, from the **stored, inline** `GroupLedger` projection |
| Query | `ViewBalances(userId, now)` — the signed-in user, and the clock for invite status; the group from the route selects the stream |
| Code | `src/ShareExpenses/Slices/ViewBalances/` |
| Endpoint | `GET /api/groups/{group}` — sign-in required |

The group as its members see it: each member slot with its balance, and the
expenses, newest first. The first **stored** read model (spec §11): the slice's
state is projected *inline* — in the same transaction that appends the events — so
a balance moves the moment an expense is saved. No window where you enter £120 and
the balance has not moved.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a query field (spec §13).

Amounts are in minor units (pence). Every query is made at `now = t0`; invites are
written with their deadline. A member is written `Alice joined +6000`: name,
status, balance. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
```

```
1 - a group with no expenses: everyone at zero
    WHEN   ViewBalances(alice)
    THEN   { "Lisbon trip", GBP, you: m1,
             members: [Alice joined 0, Bob joined 0, Carol placeholder 0],
             expenses: [] }

2 - an expense moves balances: the payer is owed, the sharers owe
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                                   {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined +6000, Bob joined -3000, Carol placeholder -3000]
           expenses: [e1 "Dinner" 9000 paid by m1 on 2026-10-01,
                      equal [m1, m2, m3], {m1: 3000, m2: 3000, m3: 3000}]

3 - a payer who does not share is owed it all
    GIVEN  ... AND ExpenseRecorded(e1, "Bob's ticket", 4500, m1, equal [m2], {m2: 4500}, …)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined +4500, Bob joined -4500, Carol placeholder 0]

4 - balances add up across expenses, and always to zero
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
           AND ExpenseRecorded(e2, "Taxi", 3000, m2, …, {m1: 1500, m2: 1500}, …)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined +4500, Bob joined -1500, Carol placeholder -3000]

5 - an invited slot shows as invited until its deadline
    GIVEN  ... AND MemberInvited(m3, i1, t0+1d, alice)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined 0, Bob joined 0, Carol invited 0]

6 - and as a placeholder again after it
    GIVEN  ... AND MemberInvited(m3, i1, t0, alice)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined 0, Bob joined 0, Carol placeholder 0]

7 - a claimed slot has joined, whatever its invite
    GIVEN  ... AND MemberInvited(m3, i1, t0+1d, alice) AND MemberClaimed(m3, carol)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined 0, Bob joined 0, Carol joined 0]

8 - expenses newest first: by date, then most recently recorded
    GIVEN  ... AND ExpenseRecorded(e1, "Flights", …, 2026-06-15, …)
           AND ExpenseRecorded(e2, "Dinner",  …, 2026-10-01, …)
           AND ExpenseRecorded(e3, "Coffee",  …, 2026-10-01, …)
    WHEN   ViewBalances(alice)
    THEN   expenses: [e3 "Coffee", e2 "Dinner", e1 "Flights"]

9 - "you" is the caller's own slot
    WHEN   ViewBalances(bob)
    THEN   you: m2

10 - the group must exist
    GIVEN  (empty stream)
    WHEN   ViewBalances(alice)
    THEN   not found

11 - only members may view
    WHEN   ViewBalances(mallory)
    THEN   not found
```

## Notes

- **Balances follow spec §9:** a member's balance is what they paid minus the sum of
  their splits. Positive is owed, negative owes. They always sum to zero — asserted
  over many random streams, not just these scenarios.
- **Stored, inline — the first projection of its kind** (spec §11). The slice's
  state is a Marten snapshot projected inline, saved as the `group_ledger` document
  in the same transaction as the events. The endpoint loads it; a pure `Reader`
  turns it into the read model.
- **Invite status is computed on reading,** not stored. Whether an invite is still
  open depends on the clock, and a stored document cannot change as time passes —
  so the projection stores each slot's invite deadline, and the reader compares it
  with `now`. As everywhere, the deadline is the recorded one (spec §11).
- **Statuses:** `joined` (a user holds the slot), `invited` (an open, unexpired
  invite), `placeholder` (neither). A slot's balance does not depend on its status:
  placeholders owe and are owed like anyone (spec §4).
- **Expenses carry their split as entered** (spec §7) and the amounts it produced,
  so a later edit form can show the split as it was entered.
- **Members only:** a non-member, a missing group and a malformed group id are the
  same 404 — the group's existence is not disclosed (spec §4).
- **Amounts stay in minor units,** with the group's currency code; the client
  formats them with the currency's decimal places.

## Deferred to the slices that introduce the events

- **Settlements** move balances — with `SettlementRecorded` (Settle up).
- **A removed expense** stops counting — with `ExpenseRemoved`.
- **Corrected expenses** — with the correction events (§11).
- **Renamed members and groups** show their new names — with `MemberRenamed`, `GroupRenamed`.
- **A released claim** returns the slot to placeholder — with `MemberClaimReleased`.
- **Removed members** — shown or not — with `MemberRemoved`.
- **An archived group** — read-only marker — with `GroupArchived`.
