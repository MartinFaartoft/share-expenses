# Slice 8 — View balances

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Balances — opened from the group page: everyone in the group and where they stand |
| Read model | `GroupBalancesReadModel` — **live**: the group stream folded per request, nothing stored |
| Query | `ViewBalances(userId, now)` — the signed-in user, and the clock for invite status; the group from the route selects the stream |
| Code | `src/SplitIt/Slices/ViewBalances/` |
| Endpoint | `GET /groups/{group}/balances` — the screen; sign-in required |

Every member slot of the group — joined, invited or placeholder — with its balance
(spec §9): who is owed, who owes, how much.

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
    THEN   { g1, "Lisbon trip", GBP, you: m1,
             members: [Alice joined 0, Bob joined 0, Carol placeholder 0] }

2 - an expense moves balances: the payer is owed, the sharers owe
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                                   {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined +6000, Bob joined -3000, Carol placeholder -3000]

3 - a payer who does not share is owed it all
    GIVEN  ... AND ExpenseRecorded(e1, "Bob's ticket", 4500, m1, equal [m2], {m2: 4500}, …)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined +4500, Bob joined -4500, Carol placeholder 0]

4 - balances add up across expenses, and always to zero
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
           AND ExpenseRecorded(e2, "Taxi", 3000, m2, …, {m1: 1500, m2: 1500}, …)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined +4500, Bob joined -1500, Carol placeholder -3000]

5 - a settlement moves balances: the payer's up, the recipient's down
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
           AND SettlementRecorded(s1, m2, m1, 3000, 2026-10-02, bob)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined +3000, Bob joined 0, Carol placeholder -3000]

6 - an invited slot shows as invited until its deadline
    GIVEN  ... AND MemberInvited(m3, i1, t0+1d, alice)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined 0, Bob joined 0, Carol invited 0]

7 - and as a placeholder again after it
    GIVEN  ... AND MemberInvited(m3, i1, t0, alice)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined 0, Bob joined 0, Carol placeholder 0]

8 - a claimed slot has joined, whatever its invite
    GIVEN  ... AND MemberInvited(m3, i1, t0+1d, alice) AND MemberClaimed(m3, carol)
    WHEN   ViewBalances(alice)
    THEN   members: [Alice joined 0, Bob joined 0, Carol joined 0]

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

## The screen

- **Everyone's standing,** in member-added order: name, "(you)" for the caller,
  status where not joined, and "owes £45.00", "is owed £60.00" or "settled up".
- **Settle up** (to `/groups/{group}/settle-up`), as on the group
  page, and a link back to the group.
- **Not a member, no such group, malformed id:** the same "not found" page, 404.

## Notes

- **Balances follow spec §9:** a member's balance is what they paid minus the sum of
  their splits; a settlement counts as paid by `from` and shared by `to` alone
  (spec §7). Positive is owed, negative owes. They always sum to zero — asserted
  over many random streams, not just these scenarios.
- **Live, not stored** (spec §11): the group stream — a few hundred events — is
  folded per request, so nothing is stored that could go stale. (It was the stored
  `group_ledger` until the group page became its own slice, View group, which took
  the history and the stored projection with it.)
- **Invite status is computed on reading** from the recorded deadline and `now`
  (spec §11).
- **Statuses:** `joined` (a user holds the slot), `invited` (an open, unexpired
  invite), `placeholder` (neither). A slot's balance does not depend on its status:
  placeholders owe and are owed like anyone (spec §4).
- **Members only:** a non-member, a missing group and a malformed group id are the
  same 404 — the group's existence is not disclosed (spec §4).

## Deferred to the slices that introduce the events

- **A removed expense or settlement** stops counting — with `ExpenseRemoved`,
  `SettlementRemoved`.
- **Corrected expenses** — with the correction events (§11).
- **Renamed members** show their new names — with `MemberRenamed`.
- **A released claim** returns the slot to placeholder — with `MemberClaimReleased`.
- **Removed members** — shown or not — with `MemberRemoved`.
